using System;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, string> _assemblyPathCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _loadingAssemblies = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            _useHybridCLR = HybridCLRInitializer.IsIL2CPP() && HybridCLRInitializer.IsInitialized;
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
            {
                return cachedAssembly;
            }

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
                    if (_useHybridCLR)
                    {
                        try
                        {
                            EmbeddedResourceExtractor.Extract(path);
                        }
                        catch (Exception ex)
                        {
                            Logger.APILogger.LogWarn($"EmbeddedResourceExtractor error for `{path}`: {ex.Message}");
                        }
                    }
                    _loadedAssemblies[fileName] = asm;
                    _loadedAssemblies[asm.GetName().Name] = asm;
                    NativeCompat.AssemblyLocations[asm] = path;
                    NativeBridge.Register(asm, path);
                    try
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                    }
                    catch (Exception dirEx)
                    {
                        Logger.APILogger.LogWarn($"Could not ensure mod directory for `{path}`: {dirEx.Message}");
                    }
                    try
                    {
                        string loc = null;
                        try { loc = asm.Location; } catch (Exception lx) { loc = "<err:" + lx.GetType().Name + ">"; }
                        Logger.APILogger.Log("[LOCPROBE] " + (asm.GetName().Name ?? "?") +
                            " Location='" + (loc ?? "<null>") + "' expected='" + path + "'");
                    }
                    catch { }
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
                byte[] assemblyBytes = File.ReadAllBytes(path);
                return Assembly.Load(assemblyBytes);
            }
        }

        private static Assembly LoadAssemblyHybridCLR(string path)
        {
            try
            {
                Assembly asm = null;
                try { asm = Assembly.LoadFrom(path); } catch { asm = null; }

                if (asm == null)
                {
                    byte[] assemblyBytes = File.ReadAllBytes(path);
                    asm = Assembly.Load(assemblyBytes);
                }

                if (asm != null)
                {
                    NativeCompat.AssemblyLocations[asm] = path;
                }

                return asm;
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
                Assembly asm = Assembly.Load(assemblyBytes);
                if (asm != null)
                {
                    _loadedAssemblies[asm.GetName().Name] = asm;
                }
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
            string[] searchPaths = GetAssemblySearchPaths();

            foreach (string searchPath in searchPaths)
            {
                if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath)) continue;

                try
                {
                    IndexDirectory(searchPath);

                    foreach (string subDir in Directory.GetDirectories(searchPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        IndexDirectory(subDir);
                    }
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogError($"Error indexing directory {searchPath}: {ex.Message}");
                }
            }
        }

        private static void IndexDirectory(string dirPath)
        {
            string[] files = Directory.GetFiles(dirPath, "*.dll", SearchOption.TopDirectoryOnly);
            foreach (string file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                _assemblyPathCache.TryAdd(fileName, file);
            }
        }

        private static Assembly ResolveModAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requestedName = new AssemblyName(args.Name);
                string assemblyName = requestedName.Name;

                if (_loadedAssemblies.TryGetValue(assemblyName, out Assembly loaded))
                {
                    return loaded;
                }

#if ENABLE_IL2CPP
                if (assemblyName.StartsWith("MMHOOK_"))
                {
                    if (_assemblyPathCache.TryGetValue(assemblyName, out string hookPath))
                    {
                        return LoadAssembly(hookPath);
                    }
                    return null;
                }

                if (IsMonoModAssembly(assemblyName))
                {
                    return typeof(AssemblyLoader).Assembly;
                }
#endif

                foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loadedAssembly.GetName().Name == assemblyName)
                    {
                        _loadedAssemblies[assemblyName] = loadedAssembly;
                        return loadedAssembly;
                    }
                }

                if (_assemblyPathCache.TryGetValue(assemblyName, out string potentialPath))
                {
                    return LoadAssembly(potentialPath);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Assembly resolve error for {args.Name}: {ex.Message}");
                return null;
            }
        }

        internal static bool IsMonoModAssembly(string assemblyName)
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