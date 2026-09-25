using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using UnityEngine;

using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Modding
{
    public static class ILHookBackend
    {
        private static bool _initialized;
        private static bool _available;

        private sealed class ILHookState
        {
            public Assembly GhostAssembly;
            public MethodInfo GhostMethod;
            public Delegate Replacement;
        }

        private static readonly Dictionary<MethodBase, ILHookState> ActiveHooks =
            new Dictionary<MethodBase, ILHookState>();

        private static readonly object HookLock = new object();

        private static readonly object ReferenceAssemblyLock = new object();

        private static AssemblyDefinition CachedReferenceAssembly;

        private static MemoryStream CachedReferenceStream;

        public static bool IsAvailable
        {
            get
            {
                if (!_initialized)
                {
                    _initialized = true;

                    _available =
                        HybridCLRInitializer.IsIL2CPP() &&
                        HybridCLRInitializer.IsInitialized &&
                        DetourBridge.IsAvailable;

                    Logger.APILogger.Log(
                        _available
                            ? "IL hook backend available."
                            : "IL hook backend not available.");
                }

                return _available;
            }
        }

        public static bool TryApplyILHook(
            MethodBase method,
            Delegate handler,
            out string error)
        {
            error = null;

            if (!IsAvailable)
            {
                error =
                    "IL hook backend not available.";

                return false;
            }

            MethodInfo methodInfo =
                method as MethodInfo;

            if (methodInfo == null)
            {
                error =
                    "Only MethodInfo targets are supported.";

                return false;
            }

            if (handler == null)
            {
                error =
                    "IL hook handler is null.";

                return false;
            }

            MonoMod.Cil.ILContext.Manipulator manipulator =
                handler as MonoMod.Cil.ILContext.Manipulator;

            if (manipulator == null)
            {
                error =
                    "Handler is not an ILContext.Manipulator.";

                return false;
            }

            Logger.APILogger.Log(
                "[ILHOOK] Begin: " +
                methodInfo.DeclaringType?.FullName +
                "." +
                methodInfo.Name);

            lock (HookLock)
            {
                if (ActiveHooks.ContainsKey(method))
                {
                    error =
                        "An IL hook is already active for this method.";

                    Logger.APILogger.LogWarn(
                        "[ILHOOK] Already active: " +
                        methodInfo.Name);

                    return false;
                }
            }

            try
            {
                if (methodInfo.IsGenericMethod ||
                    methodInfo.ContainsGenericParameters)
                {
                    error =
                        "Generic IL hook targets are not supported.";

                    return false;
                }

                if (methodInfo.DeclaringType != null &&
                    methodInfo.DeclaringType.ContainsGenericParameters)
                {
                    error =
                        "Methods declared on generic types are not supported.";

                    return false;
                }

                Type origDelegateType =
                    DetourBridge.GetManagedDelegateTypeForMethod(
                    methodInfo,
                    out string delegateTypeError);

                if (origDelegateType == null)
                {
                    error =
                        "Could not create managed IL orig delegate type: " +
                        delegateTypeError;

                    Logger.APILogger.LogError(
                        "[ILHOOK] " + error);

                    return false;
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Managed IL orig delegate: " +
                    origDelegateType.FullName);

                AssemblyDefinition referenceAssembly =
                    LoadReferenceAssembly();

                if (referenceAssembly == null)
                {
                    error =
                        "Reference assembly not found.";

                    return false;
                }

                MethodDefinition cecilMethod =
                    ExtractMethodWithMonoCecil(
                        methodInfo,
                        referenceAssembly);

                if (cecilMethod == null)
                {
                    error =
                        "Could not extract target method with Mono.Cecil.";

                    return false;
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Extracted: " +
                    cecilMethod.FullName);

                NormalizeInstanceCallsForILMatchers(
                    cecilMethod);

                if (!ModifyILWithMonoMod(
                        cecilMethod,
                        manipulator))
                {
                    error =
                        "IL manipulation failed.";

                    return false;
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Manipulator completed.");

                byte[] ghostDll =
                    CreateGhostDll(
                        methodInfo,
                        cecilMethod,
                        origDelegateType,
                        out string ghostTypeName,
                        out string replacementDelegateTypeName);

                if (ghostDll == null)
                {
                    error =
                        "Failed to create IL ghost assembly.";

                    return false;
                }

                GhostInfo ghost =
                    LoadGhostMethod(
                        ghostDll,
                        ghostTypeName,
                        replacementDelegateTypeName);

                if (ghost == null)
                {
                    error =
                        "Failed to load IL ghost method.";

                    return false;
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Ghost method loaded: " +
                    ghost.GhostMethod.DeclaringType?.FullName +
                    "." +
                    ghost.GhostMethod.Name);

                Delegate replacement;

                try
                {
                    replacement =
                        Delegate.CreateDelegate(
                            ghost.ReplacementDelegateType,
                            ghost.GhostMethod);
                }
                catch (Exception ex)
                {
                    error =
                        "Could not create IL replacement delegate: " +
                        ex;

                    return false;
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Replacement delegate created: " +
                    replacement.GetType().FullName);

                if (!DetourBridge.TryCreateOrigDetour(
                        methodInfo,
                        replacement,
                        out Delegate trampoline,
                        out string detourError))
                {
                    error =
                        "DetourBridge failed: " +
                        detourError;

                    Logger.APILogger.LogError(
                        "[ILHOOK] " + error);

                    return false;
                }

                if (trampoline == null)
                {
                    error =
                        "DetourBridge returned null trampoline.";

                    return false;
                }

                lock (HookLock)
                {
                    ActiveHooks[method] =
                        new ILHookState
                        {
                            GhostAssembly =
                                ghost.GhostAssembly,

                            GhostMethod =
                                ghost.GhostMethod,

                            Replacement =
                                replacement
                        };
                }

                Logger.APILogger.Log(
                    "[ILHOOK] Installed successfully: " +
                    methodInfo.DeclaringType?.FullName +
                    "." +
                    methodInfo.Name);

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "IL hook failed: " +
                    ex;

                Logger.APILogger.LogError(
                    error);

                return false;
            }
        }

        public static bool TryRemoveILHook(
            MethodBase method,
            out string error)
        {
            error = null;

            MethodInfo methodInfo =
                method as MethodInfo;

            if (methodInfo == null)
            {
                error = "Only MethodInfo targets are supported.";
                return false;
            }

            lock (HookLock)
            {
                if (!ActiveHooks.ContainsKey(method))
                {
                    error =
                        "No active IL hook exists for this method.";
                    return false;
                }
            }

            try
            {
                if (!DetourBridge.RemoveDetour(methodInfo))
                {
                    error =
                        "DetourBridge.RemoveDetour returned false.";
                    return false;
                }

                lock (HookLock)
                {
                    ActiveHooks.Remove(method);
                }

                Logger.APILogger.Log(
                    "IL hook removed: " +
                    methodInfo.DeclaringType?.FullName +
                    "." +
                    methodInfo.Name);

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Failed to remove IL hook: " +
                    ex.Message;

                Logger.APILogger.LogError(error);

                return false;
            }
        }

        private static Type FindHookDelegateType(MethodInfo method)
        {
            if (method == null ||
                method.DeclaringType == null)
                return null;

            string declaringTypeName =
                "On." +
                method.DeclaringType.FullName;

            string hookMethodName =
                method.Name.StartsWith("orig_")
                    ? method.Name.Substring(5)
                    : method.Name;

            string nestedHookName =
                "hook_" + hookMethodName;

            Logger.APILogger.Log(
                "Searching HookGen type: " +
                declaringTypeName +
                " / " +
                nestedHookName);

            foreach (Assembly assembly
                     in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type onType =
                        assembly.GetType(
                            declaringTypeName,
                            false);

                    if (onType == null)
                        continue;

                    Type hookType =
                        onType.GetNestedType(
                            nestedHookName,
                            BindingFlags.Public |
                            BindingFlags.NonPublic);

                    if (hookType == null)
                        continue;

                    if (!typeof(Delegate).IsAssignableFrom(
                        hookType))
                    {
                        continue;
                    }

                    Logger.APILogger.Log(
                        "HookGen delegate resolved from assembly: " +
                        assembly.FullName);

                    return hookType;
                }
                catch
                {
                    // Continue searching remaining assemblies.
                }
            }

            Logger.APILogger.LogWarn(
                "HookGen delegate not found for " +
                method.DeclaringType.FullName +
                "." +
                method.Name);

            return null;
        }

        private static AssemblyDefinition LoadReferenceAssembly()
        {
            lock (ReferenceAssemblyLock)
            {
                try
                {
                    if (CachedReferenceAssembly != null)
                    {
                        return CachedReferenceAssembly;
                    }

                    if (!ReferenceAssemblyManager.EnsureReferenceAssembly(
                            out string referencePath,
                            out string error))
                    {
                        Logger.APILogger.LogError(
                            "[ILREF] " + error);

                        return null;
                    }

                    if (string.IsNullOrEmpty(referencePath) ||
                        !File.Exists(referencePath))
                    {
                        Logger.APILogger.LogError(
                            "[ILREF] Reference file does not exist: " +
                            referencePath);

                        return null;
                    }

                    Logger.APILogger.Log(
                        "[ILREF] Reading Cecil reference assembly: " +
                        referencePath);

                    byte[] bytes =
                        File.ReadAllBytes(referencePath);

                    if (bytes == null ||
                        bytes.Length == 0)
                    {
                        Logger.APILogger.LogError(
                            "[ILREF] Reference assembly is empty.");

                        return null;
                    }

                    DefaultAssemblyResolver resolver =
                        new DefaultAssemblyResolver();

                    string directory =
                        Path.GetDirectoryName(referencePath);

                    if (!string.IsNullOrEmpty(directory))
                    {
                        resolver.AddSearchDirectory(
                            directory);
                    }

                    CachedReferenceStream =
                        new MemoryStream(
                            bytes,
                            writable: false);

                    ReaderParameters readerParameters =
                        new ReaderParameters
                        {
                            AssemblyResolver = resolver,
                            ReadSymbols = false,
                            InMemory = true
                        };

                    // Do not wrap CachedReferenceStream in a using block. 
                    // It needs to remain open during the IL hooks.
                    CachedReferenceAssembly =
                        AssemblyDefinition.ReadAssembly(
                            CachedReferenceStream,
                            readerParameters);

                    if (CachedReferenceAssembly == null)
                    {
                        Logger.APILogger.LogError(
                            "[ILREF] Cecil returned null AssemblyDefinition.");

                        CachedReferenceStream.Dispose();
                        CachedReferenceStream = null;

                        return null;
                    }

                    Logger.APILogger.Log(
                        "[ILREF] Cecil loaded reference: " +
                        CachedReferenceAssembly.Name.FullName);

                    return CachedReferenceAssembly;
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogError(
                        "[ILREF] Failed to load Cecil reference: " +
                        ex);

                    return null;
                }
            }
        }

        private static MethodDefinition ExtractMethodWithMonoCecil(
            MethodInfo runtimeMethod,
            AssemblyDefinition referenceAssembly)
        {
            try
            {
                if (runtimeMethod == null)
                {
                    Logger.APILogger.LogWarn(
                        "[ILREF] Runtime method is null.");

                    return null;
                }

                if (referenceAssembly == null)
                {
                    Logger.APILogger.LogWarn(
                        "[ILREF] Cecil reference assembly is null.");

                    return null;
                }

                Type declaringType =
                    runtimeMethod.DeclaringType;

                if (declaringType == null)
                {
                    Logger.APILogger.LogWarn(
                        "[ILREF] Runtime declaring type is null.");

                    return null;
                }

                string typeName =
                    declaringType.FullName
                        .Replace('+', '/');

                Logger.APILogger.Log(
                    "[ILREF] Searching reference type: " +
                    typeName);

                TypeDefinition type =
                    referenceAssembly.MainModule.GetType(
                        typeName);

                if (type == null)
                {
                    Logger.APILogger.LogWarn(
                        "[ILREF] Reference type not found: " +
                        typeName);

                    return null;
                }

                ParameterInfo[] runtimeParameters =
                    runtimeMethod.GetParameters();

                MethodDefinition method =
                    null;

                foreach (MethodDefinition candidate
                         in type.Methods)
                {
                    if (candidate.Name !=
                        runtimeMethod.Name)
                    {
                        continue;
                    }

                    if (candidate.Parameters.Count !=
                        runtimeParameters.Length)
                    {
                        continue;
                    }

                    bool sameSignature = true;

                    for (int i = 0;
                         i < runtimeParameters.Length;
                         i++)
                    {
                        string runtimeType =
                            runtimeParameters[i]
                                .ParameterType
                                .FullName
                                .Replace('+', '/');

                        string cecilType =
                            candidate.Parameters[i]
                                .ParameterType
                                .FullName
                                .Replace('+', '/');

                        if (runtimeType != cecilType)
                        {
                            sameSignature = false;
                            break;
                        }
                    }

                    if (!sameSignature)
                    {
                        continue;
                    }

                    method = candidate;
                    break;
                }

                if (method == null)
                {
                    Logger.APILogger.LogWarn(
                        "[ILREF] Reference method not found: " +
                        declaringType.FullName +
                        "." +
                        runtimeMethod.Name);

                    return null;
                }

                Logger.APILogger.Log(
                    "[ILREF] Reference method resolved: " +
                    method.FullName);

                return method;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[ILREF] Failed to extract method with Cecil: " +
                    ex);

                return null;
            }
        }

        private static int NormalizeInstanceCallsForILMatchers(
            MethodDefinition targetMethod)
        {
            if (targetMethod == null ||
                targetMethod.Body == null)
            {
                return 0;
            }

            TypeDefinition declaringType =
                targetMethod.DeclaringType;

            if (declaringType == null)
            {
                return 0;
            }

            int converted = 0;

            foreach (Instruction instruction
                     in targetMethod.Body.Instructions)
            {
                if (instruction.OpCode.Code != Code.Call)
                {
                    continue;
                }

                MethodReference calledMethod =
                    instruction.Operand as MethodReference;

                if (calledMethod == null)
                {
                    continue;
                }

                // Constructors must remain CALL.
                if (calledMethod.Name == ".ctor" ||
                    calledMethod.Name == ".cctor")
                {
                    continue;
                }

                // Static methods must remain CALL.
                if (calledMethod.HasThis == false)
                {
                    continue;
                }

                // Only normalize calls to methods belonging to the
                // same declaring type as the target method.
                if (calledMethod.DeclaringType.FullName !=
                    declaringType.FullName)
                {
                    continue;
                }

                instruction.OpCode =
                    OpCodes.Callvirt;

                converted++;
            }

            if (converted > 0)
            {
                Logger.APILogger.Log(
                    "[ILHOOK] Normalized " +
                    converted +
                    " same-type instance CALL instruction(s) to CALLVIRT.");
            }

            return converted;
        }

        private static bool ModifyILWithMonoMod(
            MethodDefinition method,
            MonoMod.Cil.ILContext.Manipulator handler)
        {
            try
            {
                ILContext context =
                    new ILContext(method);

                Logger.APILogger.Log(
                    "Calling ILContext.Manipulator.");

                handler(context);

                Logger.APILogger.Log(
                    "ILContext.Manipulator completed.");

                return true;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "IL manipulator exception: " +
                    ex);

                return false;
            }
        }

        private sealed class GhostInfo
        {
            public Assembly GhostAssembly;
            public MethodInfo GhostMethod;
            public Type ReplacementDelegateType;
        }


        private static byte[] CreateGhostDll(
            MethodInfo originalMethod,
            MethodDefinition modifiedCecilMethod,
            Type origDelegateType,
            out string ghostTypeName,
            out string replacementDelegateTypeName)
        {
            ghostTypeName = null;
            replacementDelegateTypeName = null;

            try
            {
                AssemblyNameDefinition assemblyName =
                    new AssemblyNameDefinition(
                        "ILHookGhost_" +
                        Guid.NewGuid().ToString("N"),
                        new Version(1, 0, 0, 0));

                AssemblyDefinition assembly =
                    AssemblyDefinition.CreateAssembly(
                        assemblyName,
                        "ILHookGhostModule",
                        ModuleKind.Dll);

                ModuleDefinition module =
                    assembly.MainModule;

                TypeReference objectType =
                    module.ImportReference(
                        typeof(object));

                string ns =
                    string.IsNullOrEmpty(
                        originalMethod.DeclaringType?.Namespace)
                        ? "ILHook"
                        : originalMethod.DeclaringType.Namespace;

                string id =
                    Guid.NewGuid().ToString("N");

                string generatedGhostTypeName =
                    originalMethod.DeclaringType.Name +
                    "_ILHook_" +
                    id;

                string generatedDelegateTypeName =
                    originalMethod.DeclaringType.Name +
                    "_ILHookDelegate_" +
                    id;

                ghostTypeName =
                    ns +
                    "." +
                    generatedGhostTypeName;

                replacementDelegateTypeName =
                    ns +
                    "." +
                    generatedDelegateTypeName;

                TypeDefinition replacementDelegate =
                    new TypeDefinition(
                        ns,
                        generatedDelegateTypeName,
                        Mono.Cecil.TypeAttributes.Public |
                        Mono.Cecil.TypeAttributes.Sealed |
                        Mono.Cecil.TypeAttributes.Class,
                        module.ImportReference(
                            typeof(MulticastDelegate)));

                module.Types.Add(
                    replacementDelegate);

                MethodDefinition delegateCtor =
                    new MethodDefinition(
                        ".ctor",
                        Mono.Cecil.MethodAttributes.Public |
                        Mono.Cecil.MethodAttributes.HideBySig |
                        Mono.Cecil.MethodAttributes.SpecialName |
                        Mono.Cecil.MethodAttributes.RTSpecialName,
                        module.TypeSystem.Void);

                delegateCtor.ImplAttributes =
                    Mono.Cecil.MethodImplAttributes.Runtime |
                    Mono.Cecil.MethodImplAttributes.Managed;

                delegateCtor.Parameters.Add(
                    new ParameterDefinition(
                        "object",
                        Mono.Cecil.ParameterAttributes.None,
                        objectType));

                delegateCtor.Parameters.Add(
                    new ParameterDefinition(
                        "method",
                        Mono.Cecil.ParameterAttributes.None,
                        module.ImportReference(
                            typeof(IntPtr))));

                replacementDelegate.Methods.Add(
                    delegateCtor);

                TypeReference returnType =
                    module.ImportReference(
                        originalMethod.ReturnType);

                MethodDefinition delegateInvoke =
                    new MethodDefinition(
                        "Invoke",
                        Mono.Cecil.MethodAttributes.Public |
                        Mono.Cecil.MethodAttributes.HideBySig |
                        Mono.Cecil.MethodAttributes.NewSlot |
                        Mono.Cecil.MethodAttributes.Virtual,
                        returnType);

                delegateInvoke.ImplAttributes =
                    Mono.Cecil.MethodImplAttributes.Runtime |
                    Mono.Cecil.MethodImplAttributes.Managed;

                delegateInvoke.Parameters.Add(
                    new ParameterDefinition(
                        "orig",
                        Mono.Cecil.ParameterAttributes.None,
                        module.ImportReference(
                            origDelegateType)));

                if (!originalMethod.IsStatic)
                {
                    delegateInvoke.Parameters.Add(
                        new ParameterDefinition(
                            "self",
                            Mono.Cecil.ParameterAttributes.None,
                            module.ImportReference(
                                originalMethod.DeclaringType)));
                }

                foreach (ParameterInfo param
                         in originalMethod.GetParameters())
                {
                    if (param.ParameterType.IsByRef)
                    {
                        Logger.APILogger.LogWarn(
                            "[ILHOOK] By-ref parameters are not supported.");

                        return null;
                    }

                    delegateInvoke.Parameters.Add(
                        new ParameterDefinition(
                            param.Name,
                            (Mono.Cecil.ParameterAttributes)
                                param.Attributes,
                            module.ImportReference(
                                param.ParameterType)));
                }

                replacementDelegate.Methods.Add(
                    delegateInvoke);

                TypeDefinition ghostType =
                    new TypeDefinition(
                        ns,
                        generatedGhostTypeName,
                        Mono.Cecil.TypeAttributes.Public |
                        Mono.Cecil.TypeAttributes.Class,
                        objectType);

                module.Types.Add(
                    ghostType);

                MethodDefinition ghostMethod =
                    new MethodDefinition(
                        "Invoke",
                        Mono.Cecil.MethodAttributes.Public |
                        Mono.Cecil.MethodAttributes.Static |
                        Mono.Cecil.MethodAttributes.HideBySig,
                        returnType);

                ghostType.Methods.Add(
                    ghostMethod);

                foreach (ParameterDefinition param
                         in delegateInvoke.Parameters)
                {
                    ghostMethod.Parameters.Add(
                        new ParameterDefinition(
                            param.Name,
                            param.Attributes,
                            param.ParameterType));
                }

                ghostMethod.Body =
            new Mono.Cecil.Cil.MethodBody(
                ghostMethod);

                ghostMethod.Body.InitLocals =
                    modifiedCecilMethod.Body.InitLocals;

                ghostMethod.Body.MaxStackSize =
                    Math.Max(
                        modifiedCecilMethod.Body.MaxStackSize,
                        8);

                Dictionary<
                    VariableDefinition,
                    VariableDefinition> localMap =
                    new Dictionary<
                        VariableDefinition,
                        VariableDefinition>();

                foreach (VariableDefinition local
                         in modifiedCecilMethod.Body.Variables)
                {
                    VariableDefinition ghostLocal =
                        new VariableDefinition(
                            module.ImportReference(
                                local.VariableType));

                    ghostMethod.Body.Variables.Add(
                        ghostLocal);

                    localMap[local] =
                        ghostLocal;
                }

                Dictionary<
                    ParameterDefinition,
                    ParameterDefinition> parameterMap =
                    new Dictionary<
                        ParameterDefinition,
                        ParameterDefinition>();

                ParameterDefinition[] originalParameters =
                    modifiedCecilMethod.Parameters.ToArray();

                for (int i = 0;
                     i < originalParameters.Length;
                     i++)
                {
                    int ghostIndex =
                        i +
                        1 +
                        (originalMethod.IsStatic ? 0 : 1);

                    parameterMap[
                        originalParameters[i]] =
                        ghostMethod.Parameters[
                            ghostIndex];
                }

                Dictionary<
                    Instruction,
                    Instruction> instructionMap =
                    new Dictionary<
                        Instruction,
                        Instruction>();

                ILProcessor processor =
                    ghostMethod.Body.GetILProcessor();

                foreach (Instruction originalInstruction
                         in modifiedCecilMethod.Body.Instructions)
                {
                    Instruction clone;

                    int? argumentIndex =
                        GetArgumentIndex(
                            originalInstruction);

                    if (argumentIndex.HasValue &&
                        IsArgumentInstruction(
                            originalInstruction.OpCode))
                    {
                        int originalArg =
                            argumentIndex.Value;

                        int ghostArg;

                        if (originalMethod.IsStatic)
                        {
                            // ghost[0] = orig
                            // original[0] = ghost[1]
                            ghostArg =
                                originalArg + 1;
                        }
                        else
                        {
                            // ghost[0] = orig
                            // ghost[1] = self
                            // original[0] = self
                            if (originalArg == 0 &&
                                IsThisArgumentInstruction(
                                    originalInstruction.OpCode))
                            {
                                ghostArg = 1;
                            }
                            else
                            {
                                ghostArg =
                                    originalArg + 2;
                            }
                        }

                        clone =
                            CreateMappedArgumentInstruction(
                                originalInstruction,
                                ghostMethod.Parameters[
                                    ghostArg]);
                    }
                    else
                    {
                        clone =
                            Instruction.Create(
                                originalInstruction.OpCode);

                        clone.Operand = null;
                    }

                    instructionMap[
                        originalInstruction] =
                        clone;

                    processor.Append(clone);
                }

                foreach (Instruction originalInstruction
                         in modifiedCecilMethod.Body.Instructions)
                {
                    if (IsArgumentInstruction(
                            originalInstruction.OpCode))
                    {
                        continue;
                    }

                    Instruction clone =
                        instructionMap[
                            originalInstruction];

                    clone.Operand =
                        ImportOperand(
                            originalInstruction.Operand,
                            module,
                            parameterMap,
                            localMap,
                            instructionMap);
                }

                foreach (ExceptionHandler oldHandler
                         in modifiedCecilMethod.Body.ExceptionHandlers)
                {
                    ExceptionHandler newHandler =
                        new ExceptionHandler(
                            oldHandler.HandlerType);

                    if (oldHandler.CatchType != null)
                    {
                        newHandler.CatchType =
                            module.ImportReference(
                                oldHandler.CatchType);
                    }

                    newHandler.TryStart =
                        oldHandler.TryStart == null
                            ? null
                            : instructionMap[
                                oldHandler.TryStart];

                    newHandler.TryEnd =
                        oldHandler.TryEnd == null
                            ? null
                            : instructionMap[
                                oldHandler.TryEnd];

                    newHandler.HandlerStart =
                        oldHandler.HandlerStart == null
                            ? null
                            : instructionMap[
                                oldHandler.HandlerStart];

                    newHandler.HandlerEnd =
                        oldHandler.HandlerEnd == null
                            ? null
                            : instructionMap[
                                oldHandler.HandlerEnd];

                    newHandler.FilterStart =
                        oldHandler.FilterStart == null
                            ? null
                            : instructionMap[
                                oldHandler.FilterStart];

                    ghostMethod.Body.ExceptionHandlers.Add(
                        newHandler);
                }

                using (MemoryStream stream =
                       new MemoryStream())
                {
                    assembly.Write(stream);

                    byte[] result =
                        stream.ToArray();

                    Logger.APILogger.Log(
                        "[ILHOOK] Ghost DLL created: " +
                        result.Length +
                        " bytes.");

                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[ILHOOK] Failed to create ghost DLL: " +
                    ex);

                return null;
            }
        }

        private static GhostInfo LoadGhostMethod(
            byte[] ghostDll,
            string ghostTypeName,
            string replacementDelegateTypeName)
        {
            try
            {
                Assembly ghostAssembly =
                    Assembly.Load(ghostDll);

                Type ghostType =
                    ghostAssembly.GetType(
                        ghostTypeName,
                        false);

                if (ghostType == null)
                {
                    Logger.APILogger.LogError(
                        "[ILHOOK] Ghost type not found: " +
                        ghostTypeName);

                    return null;
                }

                Type delegateType =
                    ghostAssembly.GetType(
                        replacementDelegateTypeName,
                        false);

                if (delegateType == null)
                {
                    Logger.APILogger.LogError(
                        "[ILHOOK] Ghost delegate type not found: " +
                        replacementDelegateTypeName);

                    return null;
                }

                MethodInfo method =
                    ghostType.GetMethod(
                        "Invoke",
                        BindingFlags.Public |
                        BindingFlags.Static);

                if (method == null)
                {
                    Logger.APILogger.LogError(
                        "[ILHOOK] Ghost Invoke method not found.");

                    return null;
                }

                return new GhostInfo
                {
                    GhostAssembly = ghostAssembly,
                    GhostMethod = method,
                    ReplacementDelegateType = delegateType
                };
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[ILHOOK] Failed to load ghost method: " +
                    ex);

                return null;
            }
        }

        private static string DescribeMethodSignature(
            MethodBase method)
        {
            if (method == null)
                return "<null>";

            string parameters =
                string.Join(
                    ", ",
                    method.GetParameters()
                        .Select(p =>
                            p.ParameterType.FullName)
                        .ToArray());

            string returnType =
                method is MethodInfo
                    ? ((MethodInfo)method)
                        .ReturnType.FullName
                    : "void";

            return returnType +
                   " " +
                   method.DeclaringType?.FullName +
                   "." +
                   method.Name +
                   "(" +
                   parameters +
                   ")";
        }

        private static bool IsArgumentInstruction(
            OpCode opcode)
        {
            switch (opcode.Code)
            {
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                case Code.Ldarga:
                case Code.Ldarga_S:
                case Code.Starg:
                case Code.Starg_S:
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsThisArgumentInstruction(
            OpCode opcode)
        {
            switch (opcode.Code)
            {
                case Code.Ldarg_0:
                case Code.Ldarga:
                case Code.Ldarga_S:
                    return true;

                default:
                    return false;
            }
        }

        private static int? GetArgumentIndex(
            Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldarg_0:
                    return 0;

                case Code.Ldarg_1:
                    return 1;

                case Code.Ldarg_2:
                    return 2;

                case Code.Ldarg_3:
                    return 3;
            }

            ParameterDefinition parameter =
                instruction.Operand as ParameterDefinition;

            if (parameter != null)
                return parameter.Index;

            if (instruction.Operand is int)
                return (int)instruction.Operand;

            return null;
        }

        private static Instruction CreateMappedArgumentInstruction(
            Instruction source,
            ParameterDefinition parameter)
        {
            switch (source.OpCode.Code)
            {
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    return Instruction.Create(
                        OpCodes.Ldarg,
                        parameter);

                case Code.Ldarga:
                case Code.Ldarga_S:
                    return Instruction.Create(
                        OpCodes.Ldarga,
                        parameter);

                case Code.Starg:
                case Code.Starg_S:
                    return Instruction.Create(
                        OpCodes.Starg,
                        parameter);

                default:
                    throw new InvalidOperationException(
                        "Unsupported argument opcode: " +
                        source.OpCode.Code);
            }
        }

        private static object ImportOperand(
            object operand,
            ModuleDefinition module,
            Dictionary<ParameterDefinition, ParameterDefinition>
                parameterMap,
            Dictionary<VariableDefinition, VariableDefinition>
                localMap,
            Dictionary<Instruction, Instruction>
                instructionMap)
        {
            if (operand == null)
                return null;

            ParameterDefinition parameter =
                operand as ParameterDefinition;

            if (parameter != null)
            {
                ParameterDefinition mapped;

                if (parameterMap.TryGetValue(
                    parameter,
                    out mapped))
                {
                    return mapped;
                }

                return parameter;
            }

            VariableDefinition variable =
                operand as VariableDefinition;

            if (variable != null)
            {
                VariableDefinition mapped;

                if (localMap.TryGetValue(
                    variable,
                    out mapped))
                {
                    return mapped;
                }

                return variable;
            }

            Instruction instruction =
                operand as Instruction;

            if (instruction != null)
            {
                return instructionMap[instruction];
            }

            Instruction[] instructions =
                operand as Instruction[];

            if (instructions != null)
            {
                return instructions
                    .Select(i => instructionMap[i])
                    .ToArray();
            }

            TypeReference typeReference =
                operand as TypeReference;

            if (typeReference != null)
            {
                return module.ImportReference(
                    typeReference);
            }

            MethodReference methodReference =
                operand as MethodReference;

            if (methodReference != null)
            {
                return module.ImportReference(
                    methodReference);
            }

            FieldReference fieldReference =
                operand as FieldReference;

            if (fieldReference != null)
            {
                return module.ImportReference(
                    fieldReference);
            }

            CallSite callSite =
                operand as CallSite;

            if (callSite != null)
            {
                return callSite;
            }

            return operand;
        }
    }
}