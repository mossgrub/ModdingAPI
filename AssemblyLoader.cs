using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Modding
{
    internal static class AssemblyLoader
    {
        private static bool _useHybridCLR;
        private static bool _resolveSetup;
        private static readonly ConcurrentDictionary<string, string> _assemblyPathCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _loadingAssemblies = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            _useHybridCLR = HybridCLRInitializer.IsIL2CPP() && HybridCLRInitializer.IsInitialized;
            CompatHooks.Apply();
            SetupAssemblyResolve();
            BuildAssemblyCache();
        }

        public static Assembly LoadAssembly(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Logger.APILogger.LogWarn($"Assembly file not found: {path}");
                return null;
            }

            string fileName = Path.GetFileNameWithoutExtension(path);

            if (_loadedAssemblies.TryGetValue(fileName, out Assembly cachedAssembly))
                return cachedAssembly;

            if (!_loadingAssemblies.TryAdd(fileName, true))
            {
                Logger.APILogger.LogError($"Circular dependency detected for: {fileName}");
                return null;
            }

            try
            {
                Assembly asm = _useHybridCLR ? LoadAssemblyHybridCLR(path) : LoadAssemblyMono(path);
                if (asm != null)
                {
                    _loadedAssemblies[fileName] = asm;
                    _loadedAssemblies[asm.GetName().Name] = asm;
                    CompatHooks.Register(asm, path);
                }
                return asm;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to load assembly {path}: {ex.Message}");
                return null;
            }
            finally
            {
                _loadingAssemblies.TryRemove(fileName, out _);
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
                return Assembly.Load(File.ReadAllBytes(path));
            }
        }

        private static Assembly LoadAssemblyHybridCLR(string path)
        {
            try
            {
                byte[] originalBytes = File.ReadAllBytes(path);
                if (originalBytes.Length == 0)
                {
                    Logger.APILogger.LogError($"HybridCLR assembly is empty: {path}");
                    return null;
                }

                byte[] loadBytes = RedirectMonoModRuntimeDetourReference(originalBytes, path, out bool patched);
                Logger.APILogger.Log($"Hybrid CLR loading {(patched ? "patched" : "original")} assembly via Assembly.Load(bytes): {path}");

                return Assembly.Load(loadBytes);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"HybridCLR failed to load {path}: {ex}");
                return null;
            }
        }

        private static byte[] RedirectMonoModRuntimeDetourReference(byte[] assemblyBytes, string assemblyPath, out bool patched)
        {
            patched = false;
            try
            {
                using var input = new MemoryStream(assemblyBytes);
                var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(input, new Mono.Cecil.ReaderParameters { InMemory = true, ReadSymbols = false });

                var targetReference = assembly.MainModule.AssemblyReferences
                    .FirstOrDefault(r => string.Equals(r.Name, "MonoMod.RuntimeDetour", StringComparison.OrdinalIgnoreCase));

                if (targetReference == null)
                {
                    Logger.APILogger.Log($"MonoMod.RuntimeDetour reference not found: {assemblyPath}");
                    return assemblyBytes;
                }

                AssemblyName apiName = typeof(AssemblyLoader).Assembly.GetName();
                Logger.APILogger.Log($"Redirecting MonoMod.RuntimeDetour in {assemblyPath} -> {apiName.Name}, Version={apiName.Version}");

                targetReference.Name = apiName.Name;
                targetReference.Version = apiName.Version;
                targetReference.PublicKeyToken = apiName.GetPublicKeyToken();

                using var output = new MemoryStream();
                assembly.Write(output);

                patched = true;
                return output.ToArray();
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to rewrite MonoMod.RuntimeDetour reference: {ex}");
                return assemblyBytes;
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
                Assembly asm = Assembly.Load(assemblyBytes);
                if (asm != null)
                    _loadedAssemblies[asm.GetName().Name] = asm;
                return asm;
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

        private static void BuildAssemblyCache()
        {
            _assemblyPathCache.Clear();

            foreach (string searchPath in GetAssemblySearchPaths())
            {
                if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath)) continue;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(searchPath, "*.dll", SearchOption.AllDirectories))
                    {
                        _assemblyPathCache.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                    }
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogError($"Error indexing directory {searchPath}: {ex.Message}");
                }
            }
        }

        private static Assembly ResolveModAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string assemblyName = new AssemblyName(args.Name).Name;

                if (_loadedAssemblies.TryGetValue(assemblyName, out Assembly loaded))
                    return loaded;

#if ENABLE_IL2CPP
                if (assemblyName.StartsWith("MMHOOK_"))
                {
                    return _assemblyPathCache.TryGetValue(assemblyName, out string hookPath)
                        ? LoadAssembly(hookPath)
                        : null;
                }

                if (IsMonoModAssembly(assemblyName))
                    return typeof(AssemblyLoader).Assembly;
#endif

                foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loadedAssembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        _loadedAssemblies[assemblyName] = loadedAssembly;
                        return loadedAssembly;
                    }
                }

                return _assemblyPathCache.TryGetValue(assemblyName, out string potentialPath)
                    ? LoadAssembly(potentialPath)
                    : null;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Assembly resolve error for {args.Name}: {ex.Message}");
                return null;
            }
        }

        internal static bool IsMonoModAssembly(string assemblyName) => assemblyName switch
        {
            "MonoMod.RuntimeDetour" or "MonoMod.Common" or "MonoMod.Core" or
            "MonoMod.IL" or "MonoMod.Patcher" or "MonoMod.Utils" or
            "MonoMod.Backports" or "MonoMod.Iced" or "Mono.Cecil" or
            "Mono.Cecil.Mdb" or "Mono.Cecil.Pdb" or "MonoMod.Mono.Cecil" or
            "MonoMod.Mono.Cecil.Mdb" or "MonoMod.Mono.Cecil.Pdb" => true,
            _ => false
        };

        private static string[] GetAssemblySearchPaths()
        {
            var paths = new List<string>();

            string modsPath = GetModsPath();
            if (!string.IsNullOrEmpty(modsPath)) paths.Add(modsPath);

#if UNITY_ANDROID
            string streamingPath = Application.streamingAssetsPath;
            string androidManagedPath = Path.Combine(streamingPath, "bin", "Data", "Managed");
            if (Directory.Exists(androidManagedPath)) paths.Add(androidManagedPath);

            string hybridCLRPath = Path.Combine(streamingPath, "HybridCLRData", "il2cpp_data", "Managed");
            if (Directory.Exists(hybridCLRPath)) paths.Add(hybridCLRPath);
#elif UNITY_EDITOR
            paths.Add(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed");
#else
            string managedPath = Path.Combine(Application.dataPath, "Managed");
            if (Directory.Exists(managedPath)) paths.Add(managedPath);
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
            return Path.Combine(Application.dataPath, SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX ? "Resources/Data/Managed/Mods" : "Managed/Mods");
#endif
        }
    }
}
