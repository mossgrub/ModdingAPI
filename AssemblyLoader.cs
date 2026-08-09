using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Modding
{
    internal static class AssemblyLoader
    {
        private static bool _useHybridCLR;
        private static bool _resolveSetup;

        public static void Initialize()
        {
            _useHybridCLR = HybridCLRInitializer.IsIL2CPP() && HybridCLRInitializer.IsInitialized;
            SetupAssemblyResolve();
        }

        public static Assembly LoadAssembly(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Logger.APILogger.LogWarn($"Assembly file not found: {path}");
                return null;
            }

            try
            {
                return _useHybridCLR ? LoadAssemblyHybridCLR(path) : LoadAssemblyMono(path);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to load assembly {path}: {ex.Message}");
                return null;
            }
        }

        private static Assembly LoadAssemblyMono(string path)
        {
            try
            {
                return Assembly.LoadFrom(path);
            }
            catch (FileLoadException)
            {
                byte[] assemblyBytes = File.ReadAllBytes(path);
                return Assembly.Load(assemblyBytes);
            }
        }

        private static Assembly LoadAssemblyHybridCLR(string path)
        {
            try
            {
                byte[] assemblyBytes = File.ReadAllBytes(path);
                return Assembly.Load(assemblyBytes);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"HybridCLR failed to load {path}: {ex.Message}");
                return null;
            }
        }

        public static Assembly LoadAssembly(byte[] assemblyBytes)
        {
            if (assemblyBytes == null || assemblyBytes.Length == 0)
            {
                Logger.APILogger.LogWarn("Attempted to load null or empty assembly bytes");
                return null;
            }

            try
            {
                return Assembly.Load(assemblyBytes);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to load assembly from bytes: {ex.Message}");
                return null;
            }
        }

        public static void SetupAssemblyResolve()
        {
            if (_resolveSetup) return;
            _resolveSetup = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveModAssembly;
        }

        public static void TeardownAssemblyResolve()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveModAssembly;
        }

        private static Assembly ResolveModAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requestedName = new AssemblyName(args.Name);
                string assemblyName = requestedName.Name;

#if ENABLE_IL2CPP
                if (assemblyName.StartsWith("MMHOOK_"))
                {
                    Logger.APILogger.LogDebug($"Resolving hook assembly {assemblyName} on IL2CPP");
                    string[] searchPaths = GetAssemblySearchPaths();
                    foreach (string searchPath in searchPaths)
                    {
                        if (string.IsNullOrEmpty(searchPath)) continue;
                        string potentialPath = Path.Combine(searchPath, assemblyName + ".dll");
                        if (File.Exists(potentialPath))
                        {
                            Logger.APILogger.LogDebug($"Resolved hook assembly {assemblyName} from {potentialPath}");
                            return LoadAssembly(potentialPath);
                        }
                    }
                    Logger.APILogger.LogWarn($"Hook assembly {assemblyName} not found in search paths");
                    return null;
                }

                if (IsMonoModAssembly(assemblyName))
                {
                    Logger.APILogger.LogDebug($"Resolving {assemblyName} to the IL2CPP shim host (Assembly-CSharp).");
                    return typeof(AssemblyLoader).Assembly;
                }
#endif

                foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loadedAssembly.GetName().Name == assemblyName)
                    {
                        return loadedAssembly;
                    }
                }

                string[] fallbackSearchPaths = GetAssemblySearchPaths();
                
                foreach (string searchPath in fallbackSearchPaths)
                {
                    if (string.IsNullOrEmpty(searchPath)) continue;
                    
                    string potentialPath = Path.Combine(searchPath, assemblyName + ".dll");
                    if (File.Exists(potentialPath))
                    {
                        Logger.APILogger.LogDebug($"Resolved assembly {assemblyName} from {potentialPath}");
                        return LoadAssembly(potentialPath);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Assembly resolve error for {args.Name}: {ex.Message}");
                return null;
            }
        }

        private static bool IsMonoModAssembly(string assemblyName)
        {
            return assemblyName == "MonoMod.RuntimeDetour" ||
                   assemblyName == "MonoMod.Common" ||
                   assemblyName == "MonoMod.Core" ||
                   assemblyName == "MonoMod.IL" ||
                   assemblyName == "MonoMod.Patcher" ||
                   assemblyName == "MonoMod.Utils" ||
                   assemblyName == "MonoMod.Backports" ||
                   assemblyName == "MonoMod.Iced" ||
                   assemblyName == "Mono.Cecil" ||
                   assemblyName == "Mono.Cecil.Mdb" ||
                   assemblyName == "Mono.Cecil.Pdb" ||
                   assemblyName == "MonoMod.Mono.Cecil" ||
                   assemblyName == "MonoMod.Mono.Cecil.Mdb" ||
                   assemblyName == "MonoMod.Mono.Cecil.Pdb";
        }

        private static string[] GetAssemblySearchPaths()
        {
            var paths = new List<string>();
            
            string modsPath = GetModsPath();
            if (!string.IsNullOrEmpty(modsPath))
            {
                paths.Add(modsPath);
            }
            
#if UNITY_ANDROID
            string streamingPath = Application.streamingAssetsPath;
            
            string androidManagedPath = Path.Combine(streamingPath, "bin", "Data", "Managed");
            if (Directory.Exists(androidManagedPath))
            {
                paths.Add(androidManagedPath);
            }
            
            string hybridCLRPath = Path.Combine(streamingPath, "HybridCLRData", "il2cpp_data", "Managed");
            if (Directory.Exists(hybridCLRPath))
            {
                paths.Add(hybridCLRPath);
            }
#elif UNITY_EDITOR
            paths.Add(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed");
#else
            string managedPath = Path.Combine(Application.dataPath, "Managed");
            if (Directory.Exists(managedPath))
            {
                paths.Add(managedPath);
            }
#endif
            
            return paths.ToArray();
        }

        private static string GetModsPath()
        {
#if UNITY_EDITOR
            return @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Mods";
#elif UNITY_ANDROID
            return Path.Combine(Application.persistentDataPath, "Mods");
#else
            return SystemInfo.operatingSystemFamily switch
            {
                OperatingSystemFamily.Windows => Path.Combine(Application.dataPath, "Managed", "Mods"),
                OperatingSystemFamily.MacOSX => Path.Combine(Application.dataPath, "Resources", "Data", "Managed", "Mods"),
                OperatingSystemFamily.Linux => Path.Combine(Application.dataPath, "Managed", "Mods"),
                _ => null
            };
#endif
        }
    }
}