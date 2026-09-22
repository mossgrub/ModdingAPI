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
                error = "IL hook backend not available.";
                return false;
            }

            MethodInfo methodInfo = method as MethodInfo;

            if (methodInfo == null)
            {
                error = "Only MethodInfo targets are supported.";
                return false;
            }

            if (handler == null)
            {
                error = "IL hook handler is null.";
                return false;
            }

            Logger.APILogger.Log(
                "IL hook begin!");

            Logger.APILogger.Log(
                "IL target: " +
                methodInfo.DeclaringType?.FullName +
                "." +
                methodInfo.Name);

            Logger.APILogger.Log(
                "IL handler type: " +
                handler.GetType().FullName);

            Logger.APILogger.Log(
                "IL handler method: " +
                handler.Method.DeclaringType?.FullName +
                "." +
                handler.Method.Name);

            MonoMod.Cil.ILContext.Manipulator manipulator =
                handler as MonoMod.Cil.ILContext.Manipulator;

            if (manipulator == null)
            {
                error =
                    "Handler is not a MonoMod.Cil.ILContext.Manipulator.";

                Logger.APILogger.LogError(error);

                return false;
            }

            lock (HookLock)
            {
                if (ActiveHooks.ContainsKey(method))
                {
                    error =
                        "An IL hook is already active for this method.";

                    Logger.APILogger.LogWarn(
                        "IL hook already active: " +
                        methodInfo.DeclaringType?.FullName +
                        "." +
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
                        "Generic IL hook targets are not supported by this initial backend.";

                    Logger.APILogger.LogWarn(error);

                    return false;
                }

                if (methodInfo.DeclaringType != null &&
                    methodInfo.DeclaringType.ContainsGenericParameters)
                {
                    error =
                        "Methods declared on generic types are not supported by this initial backend.";

                    Logger.APILogger.LogWarn(error);

                    return false;
                }

                Type hookDelegateType =
                    FindHookDelegateType(methodInfo);

                if (hookDelegateType == null)
                {
                    error =
                        "Could not find HookGen hook delegate for " +
                        methodInfo.DeclaringType?.FullName +
                        "." +
                        methodInfo.Name;

                    Logger.APILogger.LogWarn(error);

                    return false;
                }

                Logger.APILogger.Log(
                    "Found HookGen delegate: " +
                    hookDelegateType.FullName);

                MethodInfo hookInvoke =
                    hookDelegateType.GetMethod("Invoke");

                if (hookInvoke == null)
                {
                    error =
                        "HookGen delegate has no Invoke method.";

                    Logger.APILogger.LogError(error);

                    return false;
                }

                Logger.APILogger.Log(
                    "HookGen signature: " +
                    DescribeMethodSignature(hookInvoke));

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
                    "Extracted reference IL method: " +
                    cecilMethod.FullName);

                if (!ModifyILWithMonoMod(
                    cecilMethod,
                    manipulator))
                {
                    error = "IL manipulation failed.";
                    return false;
                }

                Logger.APILogger.Log(
                    "IL manipulator finished successfully.");

                byte[] ghostDll =
                    CreateGhostDll(
                        methodInfo,
                        cecilMethod,
                        hookDelegateType);

                if (ghostDll == null)
                {
                    error =
                        "Failed to create IL ghost assembly.";

                    return false;
                }

                MethodInfo ghostMethod =
                    LoadGhostMethod(
                        ghostDll,
                        methodInfo);

                if (ghostMethod == null)
                {
                    error =
                        "Failed to load IL ghost method.";

                    return false;
                }

                Logger.APILogger.Log(
                    "Ghost managed method loaded: " +
                    ghostMethod.DeclaringType?.FullName +
                    "." +
                    ghostMethod.Name);

                Logger.APILogger.Log(
                    "Ghost managed signature: " +
                    DescribeMethodSignature(ghostMethod));

                Delegate replacement;

                try
                {
                    replacement =
                        Delegate.CreateDelegate(
                            hookDelegateType,
                            ghostMethod);
                }
                catch (Exception ex)
                {
                    error =
                        "Could not create HookGen replacement delegate: " +
                        ex.Message;

                    Logger.APILogger.LogError(error);

                    return false;
                }

                Logger.APILogger.Log(
                    "Replacement delegate created successfully.");

                Logger.APILogger.Log(
                    "Routing IL hook through DetourBridge AOT bridge.");

                if (!DetourBridge.TryCreateOrigDetour(
                    methodInfo,
                    replacement,
                    out Delegate trampoline,
                    out string detourError))
                {
                    error =
                        "DetourBridge failed: " +
                        detourError;

                    Logger.APILogger.LogError(error);

                    return false;
                }

                if (trampoline == null)
                {
                    error =
                        "DetourBridge returned a null trampoline.";

                    Logger.APILogger.LogError(error);

                    return false;
                }

                lock (HookLock)
                {
                    ActiveHooks[method] = new ILHookState
                    {
                        GhostAssembly =
                            ghostMethod.Module.Assembly.GetType()
                                .Assembly,

                        GhostMethod = ghostMethod,

                        Replacement = replacement
                    };
                }

                Logger.APILogger.Log(
                    "IL hook installed through AOT bridge.");

                Logger.APILogger.Log(
                    "IL target remains native; managed ghost is only reached through DetourBridge.");

                Logger.APILogger.Log(
                    "IL hook end!");

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "IL hook failed: " +
                    ex;

                Logger.APILogger.LogError(error);

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

        private static Type FindHookDelegateType(
            MethodInfo method)
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
            try
            {
                if (!ReferenceAssemblyManager.EnsureReferenceAssembly(
                    out string path,
                    out string error))
                {
                    Logger.APILogger.LogError(
                        "[ILREF] " + error);

                    return null;
                }

                if (!File.Exists(path))
                {
                    Logger.APILogger.LogError(
                        "[ILREF] Reference file does not exist: " +
                        path);

                    return null;
                }

                Logger.APILogger.Log(
                    "[ILREF] Reading Cecil reference assembly: " +
                    path);

                DefaultAssemblyResolver resolver =
                    new DefaultAssemblyResolver();

                string directory =
                    Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory))
                {
                    resolver.AddSearchDirectory(directory);
                }

                ReaderParameters readerParameters =
                    new ReaderParameters
                    {
                        AssemblyResolver = resolver,
                        ReadSymbols = false
                    };

                using (FileStream stream =
                       File.OpenRead(path))
                {
                    AssemblyDefinition assembly =
                        AssemblyDefinition.ReadAssembly(
                            stream,
                            readerParameters);

                    Logger.APILogger.Log(
                        "[ILREF] Cecil loaded reference: " +
                        assembly.Name.FullName);

                    return assembly;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[ILREF] Failed to load Cecil reference: " +
                    ex);

                return null;
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
                        "Runtime method is null.");

                    return null;
                }

                if (referenceAssembly == null)
                {
                    Logger.APILogger.LogWarn(
                        "Cecil reference assembly is null.");

                    return null;
                }

                string typeName =
                    runtimeMethod.DeclaringType.FullName
                        .Replace('+', '/');

                TypeDefinition type =
                    referenceAssembly.MainModule.GetType(typeName);

                if (type == null)
                {
                    Logger.APILogger.LogWarn(
                        "Reference type not found: " +
                        typeName);

                    return null;
                }

                MethodDefinition method =
                    null;

                foreach (MethodDefinition candidate
                         in type.Methods)
                {
                    if (candidate.Name != runtimeMethod.Name)
                        continue;

                    ParameterInfo[] runtimeParameters =
                        runtimeMethod.GetParameters();

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

                    if (sameSignature)
                    {
                        method = candidate;
                        break;
                    }
                }

                if (method == null)
                {
                    Logger.APILogger.LogWarn(
                        "Reference method not found: " +
                        runtimeMethod.Name);

                    return null;
                }

                return method;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "Failed to extract IL with Cecil: " +
                    ex.Message);

                return null;
            }
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

        private static byte[] CreateGhostDll(
            MethodInfo originalMethod,
            MethodDefinition modifiedCecilMethod,
            Type hookDelegateType)
        {
            try
            {
                MethodInfo hookInvoke =
                    hookDelegateType.GetMethod("Invoke");

                if (hookInvoke == null)
                {
                    Logger.APILogger.LogError(
                        "Hook delegate Invoke method missing.");

                    return null;
                }

                ParameterInfo[] hookParameters =
                    hookInvoke.GetParameters();

                int expectedParameters =
                    originalMethod.GetParameters().Length +
                    (originalMethod.IsStatic ? 0 : 1) +
                    1;

                if (hookParameters.Length !=
                    expectedParameters)
                {
                    Logger.APILogger.LogError(
                        "Hook delegate parameter count mismatch. " +
                        "Expected " +
                        expectedParameters +
                        ", got " +
                        hookParameters.Length);

                    return null;
                }

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
                        originalMethod.DeclaringType.Namespace)
                        ? "ILHook"
                        : originalMethod.DeclaringType.Namespace;

                TypeDefinition ghostType =
                    new TypeDefinition(
                        ns,
                        originalMethod.DeclaringType.Name +
                        "_ILHook_" +
                        Guid.NewGuid().ToString("N"),
                        Mono.Cecil.TypeAttributes.Public |
                        Mono.Cecil.TypeAttributes.Class,
                        objectType);

                module.Types.Add(ghostType);

                TypeReference returnType =
                    module.ImportReference(
                        hookInvoke.ReturnType);

                MethodDefinition ghostMethod =
                    new MethodDefinition(
                        "Invoke",
                        Mono.Cecil.MethodAttributes.Public |
                        Mono.Cecil.MethodAttributes.Static |
                        Mono.Cecil.MethodAttributes.HideBySig,
                        returnType);

                ghostType.Methods.Add(ghostMethod);

                Dictionary<ParameterDefinition, ParameterDefinition>
                    parameterMap =
                    new Dictionary<ParameterDefinition, ParameterDefinition>();

                for (int i = 0;
                     i < hookParameters.Length;
                     i++)
                {
                    ParameterInfo param =
                        hookParameters[i];

                    if (param.ParameterType.IsByRef)
                    {
                        Logger.APILogger.LogWarn(
                            "IL ghost does not currently support byref hook parameters.");

                        return null;
                    }

                    TypeReference parameterType =
                        module.ImportReference(
                            param.ParameterType);

                    ParameterDefinition ghostParameter =
                        new ParameterDefinition(
                            param.Name,
                            (Mono.Cecil.ParameterAttributes)
                                param.Attributes,
                            parameterType);

                    ghostMethod.Parameters.Add(
                        ghostParameter);
                }

                ParameterDefinition[] originalParameters =
                    modifiedCecilMethod.Parameters.ToArray();

                for (int i = 0;
                     i < originalParameters.Length;
                     i++)
                {
                    int newIndex = i + 1;

                    ParameterDefinition newParameter =
                        ghostMethod.Parameters[
                            newIndex +
                            (originalMethod.IsStatic ? 0 : 1)];

                    parameterMap[
                        originalParameters[i]] =
                        newParameter;
                }

                if (modifiedCecilMethod.HasGenericParameters)
                {
                    Logger.APILogger.LogWarn(
                        "Generic Cecil methods are not supported.");

                    return null;
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

                Dictionary<VariableDefinition, VariableDefinition>
                    localMap =
                    new Dictionary<VariableDefinition, VariableDefinition>();

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

                Dictionary<Instruction, Instruction>
                    instructionMap =
                    new Dictionary<Instruction, Instruction>();

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
                            // Ghost parameter 0 = orig delegate
                            // Original arg 0 = ghost arg 1
                            ghostArg = originalArg + 1;
                        }
                        else
                        {
                            // Ghost parameter 0 = orig delegate
                            // Ghost parameter 1 = original this
                            // Original arg 1 = ghost arg 2
                            if (originalArg == 0 &&
                                IsThisArgumentInstruction(
                                    originalInstruction.OpCode))
                            {
                                ghostArg = 1;
                            }
                            else
                            {
                                ghostArg = originalArg + 2;
                            }
                        }

                        if (ghostArg < 0 ||
                            ghostArg >= ghostMethod.Parameters.Count)
                        {
                            throw new InvalidOperationException(
                                "Invalid mapped argument index " +
                                ghostArg);
                        }

                        clone =
                            CreateMappedArgumentInstruction(
                                originalInstruction,
                                ghostMethod.Parameters[ghostArg]);
                    }
                    else
                    {
                        clone =
                            Instruction.Create(
                                originalInstruction.OpCode);

                        clone.Operand =
                            null;
                    }

                    instructionMap[
                        originalInstruction] =
                        clone;

                    processor.Append(clone);
                }

                foreach (Instruction originalInstruction
                         in modifiedCecilMethod.Body.Instructions)
                {
                    Instruction clone =
                        instructionMap[
                            originalInstruction];

                    if (IsArgumentInstruction(
                            originalInstruction.OpCode))
                    {
                        continue;
                    }

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

                string ghostMethodName =
                    "Invoke";

                using (MemoryStream stream =
                       new MemoryStream())
                {
                    assembly.Write(stream);

                    byte[] result =
                        stream.ToArray();

                    Logger.APILogger.Log(
                        "Created IL ghost DLL: " +
                        result.Length +
                        " bytes.");

                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "Failed to create IL ghost DLL: " +
                    ex);

                return null;
            }
        }

        private static MethodInfo LoadGhostMethod(
            byte[] ghostDll,
            MethodInfo originalMethod)
        {
            try
            {
                Assembly ghostAssembly =
                    Assembly.Load(ghostDll);

                Logger.APILogger.Log(
                    "Ghost assembly loaded: " +
                    ghostAssembly.FullName);

                Type ghostType =
                    ghostAssembly
                        .GetTypes()
                        .FirstOrDefault(
                            t => t.Name.StartsWith(
                                originalMethod.DeclaringType.Name +
                                "_ILHook_"));

                if (ghostType == null)
                {
                    Logger.APILogger.LogError(
                        "Could not locate IL ghost type.");

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
                        "Could not locate IL ghost Invoke method.");

                    return null;
                }

                return method;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "Failed to load IL ghost method: " +
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