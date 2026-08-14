using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HybridCLR;
using Modding.Utils;
using UnityEngine;
using UnityEngine.Networking;

namespace Modding
{
    internal static class HybridCLRInitializer
    {
        private static bool _initialized;
        private static readonly object LockObj = new object();

        private const string RelativeManagedDir = "HybridCLRData/il2cpp_data/Managed";
        private const string RelativeHotUpdateDir = "HybridCLRData/hot_update_dlls";

        public static void Initialize()
        {
            if (_initialized) return;

            lock (LockObj)
            {
                if (_initialized) return;

                try
                {
                    Logger.APILogger.Log("Initializing HybridCLR runtime.");

                    LoadSupplementaryMetadata();

                    LoadHotUpdateDlls();

                    RuntimeApi.SetInterpreterThreadObjectStackSize(1024);
                    RuntimeApi.SetInterpreterThreadFrameStackSize(512);

                    _initialized = true;
                    Logger.APILogger.Log("HybridCLR runtime initialized successfully.");
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogError($"Failed to initialize HybridCLR: {ex}");
                }
            }
        }

        private static void LoadSupplementaryMetadata()
        {
            try
            {
                Dictionary<string, byte[]> metadataDlls = CollectMetadataDlls();
                if (metadataDlls == null || metadataDlls.Count == 0)
                {
                    Logger.APILogger.LogWarn("HybridCLR metadata not found (no DLLs).");
                    return;
                }

                int loadedCount = 0;
                foreach (KeyValuePair<string, byte[]> kvp in metadataDlls)
                {
                    try
                    {
                        LoadImageErrorCode errorCode = RuntimeApi.LoadMetadataForAOTAssembly(kvp.Value, HomologousImageMode.Consistent);

                        if (errorCode == LoadImageErrorCode.OK)
                        {
                            loadedCount++;
                        }
                        else
                        {
                            Logger.APILogger.LogWarn($"Failed to load metadata for {kvp.Key}: {errorCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogError($"Error loading metadata for {kvp.Key}: {ex.Message}");
                    }
                }

                Logger.APILogger.Log($"Loaded supplementary metadata for {loadedCount}/{metadataDlls.Count} assemblies.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Error in LoadSupplementaryMetadata: {ex}");
            }
        }

        private static void LoadHotUpdateDlls()
        {
            try
            {
                Dictionary<string, byte[]> hotUpdateDlls = CollectHotUpdateDlls();
                if (hotUpdateDlls == null || hotUpdateDlls.Count == 0)
                {
                    Logger.APILogger.LogWarn("No hot-update DLLs found to load.");
                    return;
                }

                foreach (KeyValuePair<string, byte[]> kvp in hotUpdateDlls)
                {
                    if (AssemblyLoader.IsMonoModAssembly(Path.GetFileNameWithoutExtension(kvp.Key)))
                    {
                        Logger.APILogger.Log($"Skipping hot-update load of {kvp.Key} (provided by AOT shim in Assembly-CSharp).");
                        continue;
                    }

                    try
                    {
                        Assembly asm = Assembly.Load(kvp.Value);
                        Logger.APILogger.Log($"Loaded hot-update assembly: {asm.FullName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogError($"Failed to load hot-update assembly {kvp.Key}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Error in LoadHotUpdateDlls: {ex}");
            }
        }

        private static Dictionary<string, byte[]> CollectHotUpdateDlls()
        {
            string[] localPaths = new string[]
            {
                Path.Combine(Application.persistentDataPath, RelativeHotUpdateDir.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(Application.dataPath, RelativeHotUpdateDir.Replace('/', Path.DirectorySeparatorChar)),
            };

            foreach (string path in localPaths)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    continue;

                string[] files = Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly);
                if (files.Length == 0)
                    continue;

                var dlls = new Dictionary<string, byte[]>();
                foreach (string file in files)
                {
                    try
                    {
                        dlls[Path.GetFileName(file)] = File.ReadAllBytes(file);
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn($"Could not read {file}: {ex.Message}");
                    }
                }
                if (dlls.Count > 0)
                {
                    Logger.APILogger.Log($"Read hot-update from local path: {path} ({dlls.Count} dlls)");
                    return dlls;
                }
            }

#if UNITY_ANDROID
            return ReadHotUpdateStreamingAssets();
#else
            return new Dictionary<string, byte[]>();
#endif
        }

#if UNITY_ANDROID
        private static Dictionary<string, byte[]> ReadHotUpdateStreamingAssets()
        {
            var result = new Dictionary<string, byte[]>();
            try
            {
                string manifestPath = Application.streamingAssetsPath + "/" + RelativeHotUpdateDir + "/manifest.txt";
                string manifestText = ReadStreamingAssetText(manifestPath);

                List<string> names = new List<string>();
                if (!string.IsNullOrEmpty(manifestText))
                {
                    foreach (string line in manifestText.Replace("\r", "").Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                            continue;
                        names.Add(trimmed);
                    }
                }

                if (names.Count == 0)
                    names.Add("Assembly-CSharp.dll");

                foreach (string name in names)
                {
                    if (result.ContainsKey(name))
                        continue;

                    string dllName = name.EndsWith(".dll") ? name : name + ".dll";
                    string url = Application.streamingAssetsPath + "/" + RelativeHotUpdateDir + "/" + dllName;
                    byte[] data = ReadStreamingAssetBytes(url);
                    if (data != null && data.Length > 0)
                        result[dllName] = data;
                }

                Logger.APILogger.Log($"Read hot-update from StreamingAssets via UWR: {result.Count} dlls");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Error in ReadHotUpdateStreamingAssets: {ex.Message}");
            }
            return result;
        }
#endif

        private static Dictionary<string, byte[]> CollectMetadataDlls()
        {
            string[] localPaths = GetLocalMetadataPaths();
            foreach (string path in localPaths)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    continue;

                string[] files = Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly);
                if (files.Length == 0)
                    continue;

                var dlls = new Dictionary<string, byte[]>();
                foreach (string file in files)
                {
                    try
                    {
                        dlls[Path.GetFileName(file)] = File.ReadAllBytes(file);
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn($"Could not read {file}: {ex.Message}");
                    }
                }
                if (dlls.Count > 0)
                {
                    Logger.APILogger.Log($"Read metadata from local path: {path} ({dlls.Count} dlls)");
                    return dlls;
                }
            }

#if UNITY_ANDROID
            return ReadStreamingAssetsMetadata();
#else
            return new Dictionary<string, byte[]>();
#endif
        }

        private static string[] GetLocalMetadataPaths()
        {
            return new string[]
            {
                Path.Combine(Application.persistentDataPath, RelativeManagedDir.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(Application.dataPath, RelativeManagedDir.Replace('/', Path.DirectorySeparatorChar)),
            };
        }

#if UNITY_ANDROID
        private static Dictionary<string, byte[]> ReadStreamingAssetsMetadata()
        {
            var result = new Dictionary<string, byte[]>();
            try
            {
                string manifestPath = Application.streamingAssetsPath + "/" + RelativeManagedDir + "/manifest.txt";
                string manifestText = ReadStreamingAssetText(manifestPath);

                List<string> names = new List<string>();
                if (!string.IsNullOrEmpty(manifestText))
                {
                    foreach (string line in manifestText.Replace("\r", "").Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                            continue;
                        names.Add(trimmed);
                    }
                }

                if (names.Count == 0)
                {
                                        names.AddRange(new[]
                    {
                        "mscorlib.dll", "System.dll", "System.Core.dll",
                        "UnityEngine.dll", "UnityEngine.CoreModule.dll"
                    });
                }

                foreach (string name in names)
                {
                    if (result.ContainsKey(name))
                        continue;

                    string dllName = name.EndsWith(".dll") ? name : name + ".dll";
                    string url = Application.streamingAssetsPath + "/" + RelativeManagedDir + "/" + dllName;
                    byte[] data = ReadStreamingAssetBytes(url);
                    if (data != null && data.Length > 0)
                        result[dllName] = data;
                }

                Logger.APILogger.Log($"Read metadata from StreamingAssets via UWR: {result.Count} dlls");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Error in ReadStreamingAssetsMetadata: {ex.Message}");
            }
            return result;
        }

        private static string ReadStreamingAssetText(string url)
        {
            byte[] data = ReadStreamingAssetBytes(url);
            if (data == null)
                return null;
            try
            {
                return System.Text.Encoding.UTF8.GetString(data);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadStreamingAssetBytes(string url)
        {
            try
            {
                using (UnityWebRequest uwr = UnityWebRequest.Get(url))
                {
                    var op = uwr.SendWebRequest();
                    float deadline = Time.realtimeSinceStartup + 15f;
                    while (!op.isDone && Time.realtimeSinceStartup < deadline)
                    {
                        System.Threading.Thread.Sleep(5);
                    }

                    if (!op.isDone)
                    {
                        uwr.Abort();
                        Logger.APILogger.LogWarn($"Timeout reading streaming asset: {Path.GetFileName(url)}");
                        return null;
                    }

                    if (uwr.isNetworkError || uwr.isHttpError)
                    {
                        Logger.APILogger.LogWarn($"Failed to read streaming asset {Path.GetFileName(url)}: {uwr.error}");
                        return null;
                    }

                    return uwr.downloadHandler != null ? uwr.downloadHandler.data : null;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"Exception reading streaming asset {Path.GetFileName(url)}: {ex.Message}");
                return null;
            }
        }
#endif

        public static bool IsInitialized => _initialized;

        public static bool IsIL2CPP()
        {
#if ENABLE_IL2CPP
            return true;
#else
            return false;
#endif
        }
    }
}