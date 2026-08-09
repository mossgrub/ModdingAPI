using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Modding
{
    internal static class AssemblyLoader
    {
        private static bool _useHybridCLR;

        public static void Initialize()
        {
            _useHybridCLR = HybridCLRInitializer.IsIL2CPP() && HybridCLRInitializer.IsInitialized;
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

                foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loadedAssembly.GetName().Name == requestedName.Name)
                    {
                        return loadedAssembly;
                    }
                }

                string modsPath = GetModsPath();
                if (!string.IsNullOrEmpty(modsPath))
                {
                    string potentialPath = Path.Combine(modsPath, requestedName.Name + ".dll");
                    if (File.Exists(potentialPath))
                    {
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
