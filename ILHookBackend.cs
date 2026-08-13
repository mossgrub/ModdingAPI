using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Modding.Utils;

namespace Modding
{
    public static class ILHookBackend
    {
        private static bool _initialized;
        private static bool _available;

        public static bool IsAvailable
        {
            get
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _available = HybridCLRInitializer.IsIL2CPP() && HybridCLRInitializer.IsInitialized && DetourBridge.IsAvailable;
                    Logger.APILogger.Log(_available ? "IL hook backend available." : "IL hook backend not available.");
                }
                return _available;
            }
        }

        public static bool TryApplyILHook(MethodBase method, Delegate handler, out string error)
        {
            error = null;
            if (!IsAvailable) { error = "IL hook backend not available."; return false; }

            try
            {
                MethodInfo methodInfo = method as MethodInfo;
                if (methodInfo == null) { error = "Only methods are supported."; return false; }

                Logger.APILogger.Log("Applying IL hook to " + methodInfo.DeclaringType?.Name + "." + methodInfo.Name);

                System.Reflection.Assembly refAsm = LoadReferenceAssembly();
                if (refAsm == null) { error = "Reference assembly not found."; return false; }

                MethodDefinition cecilMethod = ExtractMethodWithMonoCecil(methodInfo, refAsm);
                if (cecilMethod == null) { error = "Could not extract method with Mono.Cecil."; return false; }

                Logger.APILogger.Log("Extracted method definition for " + cecilMethod.Name);

                if (!ModifyILWithMonoMod(cecilMethod, handler))
                {
                    error = "IL modification failed.";
                    return false;
                }

                byte[] ghostDll = CreateGhostDll(methodInfo, cecilMethod);
                if (ghostDll == null) { error = "Failed to create ghost DLL."; return false; }

                MethodInfo modifiedMethod = LoadGhostMethod(ghostDll, methodInfo);
                if (modifiedMethod == null) { error = "Failed to load modified method."; return false; }

                IntPtr modifiedAddress = modifiedMethod.MethodHandle.GetFunctionPointer();
                if (modifiedAddress == IntPtr.Zero) { error = "Failed to get native address."; return false; }

                IntPtr originalAddress = DetourBridge.GetNativeMethodAddress(methodInfo);
                if (originalAddress == IntPtr.Zero) { error = "Could not get original address."; return false; }

                IntPtr trampoline;
                DetourBridge.DobbyHookNative(originalAddress, modifiedAddress, out trampoline);

                Logger.APILogger.Log("IL hook applied successfully!");
                return true;
            }
            catch (Exception ex)
            {
                error = "IL hook failed: " + ex.Message;
                Logger.APILogger.LogError(error);
                return false;
            }
        }

        public static bool TryRemoveILHook(MethodBase method, out string error)
        {
            error = null;
            DetourBridge.RemoveDetour(method as MethodInfo);
            return true;
        }

        private static System.Reflection.Assembly LoadReferenceAssembly()
        {
            try
            {
                string[] searchPaths = new string[]
                {
                    Path.Combine(Application.streamingAssetsPath, "HybridCLRData", "il2cpp_data", "Managed"),
                    Path.Combine(Application.dataPath, "Managed"),
                    Application.streamingAssetsPath,
                    Path.Combine(Application.persistentDataPath, "Mods")
                };

                string[] assemblyNames = new string[] { "Assembly-CSharp", "Assembly-CSharp-firstpass" };

                foreach (string searchPath in searchPaths)
                {
                    if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath))
                        continue;

                    foreach (string assemblyName in assemblyNames)
                    {
                        string assemblyPath = Path.Combine(searchPath, assemblyName + ".dll");
                        if (File.Exists(assemblyPath))
                        {
                            Logger.APILogger.Log("Loading reference assembly: " + assemblyPath);
                            return System.Reflection.Assembly.LoadFrom(assemblyPath);
                        }
                    }
                }

                Logger.APILogger.LogWarn("Reference assembly not found.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to load reference assembly: " + ex.Message);
                return null;
            }
        }

        private static MethodDefinition ExtractMethodWithMonoCecil(MethodInfo runtimeMethod, System.Reflection.Assembly referenceAssembly)
        {
            try
            {
                string refAsmPath = referenceAssembly.Location;
                if (string.IsNullOrEmpty(refAsmPath) || !File.Exists(refAsmPath))
                {
                    Logger.APILogger.LogWarn("Reference assembly location not found.");
                    return null;
                }

                AssemblyDefinition refAsmDef = AssemblyDefinition.ReadAssembly(refAsmPath);
                TypeDefinition refType = refAsmDef.MainModule.GetType(runtimeMethod.DeclaringType.FullName);
                if (refType == null)
                {
                    Logger.APILogger.LogWarn("Type " + runtimeMethod.DeclaringType.FullName + " not found in reference assembly");
                    return null;
                }

                MethodDefinition refMethod = refType.Methods.FirstOrDefault(m => m.Name == runtimeMethod.Name && m.Parameters.Count == runtimeMethod.GetParameters().Length);
                if (refMethod == null)
                {
                    Logger.APILogger.LogWarn("Method " + runtimeMethod.Name + " not found in reference assembly");
                    return null;
                }

                return refMethod;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to extract method via Cecil: " + ex.Message);
                return null;
            }
        }

        private static bool ModifyILWithMonoMod(MethodDefinition cecilMethod, Delegate handler)
        {
            try
            {
                var ilContext = new ILContext(cecilMethod);
                Action<ILCursor> ilAction = handler as Action<ILCursor>;
                if (ilAction == null)
                {
                    Logger.APILogger.LogError("Handler must be Action<ILCursor>.");
                    return false;
                }

                ilAction(new ILCursor(ilContext));
                Logger.APILogger.Log("IL modification complete for " + cecilMethod.Name);
                return true;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to apply IL modifications: " + ex.Message);
                return false;
            }
        }

        private static byte[] CreateGhostDll(MethodInfo originalMethod, MethodDefinition modifiedCecilMethod)
        {
            try
            {
                AssemblyNameDefinition assemblyName = new AssemblyNameDefinition("ILHookGhost_" + Guid.NewGuid().ToString("N"), new Version(1, 0, 0, 0));
                AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(assemblyName, "ILHookGhostModule", ModuleKind.Dll);
                ModuleDefinition module = assembly.MainModule;

                TypeReference objectTypeRef = module.ImportReference(typeof(object));

                string ns = string.IsNullOrEmpty(originalMethod.DeclaringType.Namespace) ? "ILHook" : originalMethod.DeclaringType.Namespace;
                TypeDefinition type = new TypeDefinition(
                    ns,
                    originalMethod.DeclaringType.Name + "_ILHook",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                    objectTypeRef
                );
                module.Types.Add(type);

                TypeReference returnTypeRef = module.ImportReference(originalMethod.ReturnType);
                MethodDefinition ghostMethod = new MethodDefinition(
                    originalMethod.Name,
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                    returnTypeRef
                );
                type.Methods.Add(ghostMethod);

                foreach (ParameterInfo param in originalMethod.GetParameters())
                {
                    TypeReference paramTypeRef = module.ImportReference(param.ParameterType);
                    ParameterDefinition paramDef = new ParameterDefinition(param.Name, (Mono.Cecil.ParameterAttributes)param.Attributes, paramTypeRef);
                    ghostMethod.Parameters.Add(paramDef);
                }

                ghostMethod.Body = new Mono.Cecil.Cil.MethodBody(ghostMethod);

                foreach (var local in modifiedCecilMethod.Body.Variables)
                {
                    ghostMethod.Body.Variables.Add(new VariableDefinition(module.ImportReference(local.VariableType)));
                }

                ILProcessor ilProcessor = ghostMethod.Body.GetILProcessor();
                foreach (Instruction instr in modifiedCecilMethod.Body.Instructions)
                {
                    ilProcessor.Append(instr);
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    assembly.Write(ms);
                    byte[] dllBytes = ms.ToArray();
                    Logger.APILogger.Log("Created ghost DLL: " + dllBytes.Length + " bytes");
                    return dllBytes;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to create ghost DLL: " + ex.Message);
                return null;
            }
        }

        private static MethodInfo LoadGhostMethod(byte[] ghostDll, MethodInfo originalMethod)
        {
            try
            {
                System.Reflection.Assembly ghostAsm = System.Reflection.Assembly.Load(ghostDll);
                Logger.APILogger.Log("Ghost DLL loaded: " + ghostAsm.FullName);

                string ns = string.IsNullOrEmpty(originalMethod.DeclaringType.Namespace) ? "ILHook" : originalMethod.DeclaringType.Namespace;
                string fullTypeName = ns + "." + originalMethod.DeclaringType.Name + "_ILHook";
                Type ghostType = ghostAsm.GetType(fullTypeName);

                if (ghostType == null)
                {
                    Logger.APILogger.LogError("Type " + fullTypeName + " not found in ghost DLL");
                    return null;
                }

                MethodInfo modifiedMethod = ghostType.GetMethod(originalMethod.Name);
                if (modifiedMethod == null)
                {
                    Logger.APILogger.LogError("Method " + originalMethod.Name + " not found in ghost DLL");
                    return null;
                }

                Logger.APILogger.Log("Loaded modified method: " + modifiedMethod.Name);
                return modifiedMethod;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to load ghost method: " + ex.Message);
                return null;
            }
        }
    }
}