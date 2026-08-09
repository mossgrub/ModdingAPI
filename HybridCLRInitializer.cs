using System;
using System.IO;
using System.Reflection;
using HybridCLR;
using Modding.Utils;
using UnityEngine;

namespace Modding
{
    internal static class HybridCLRInitializer
    {
        private static bool _initialized;
        private static readonly object LockObj = new object();

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
                string metadataPath = GetMetadataPath();
                if (string.IsNullOrEmpty(metadataPath) || !Directory.Exists(metadataPath))
                {
                    Logger.APILogger.LogWarn("HybridCLR metadata directory not found.");
                    return;
                }

                string[] dllFiles = Directory.GetFiles(metadataPath, "*.dll", SearchOption.TopDirectoryOnly);
                int loadedCount = 0;

                foreach (string dllPath in dllFiles)
                {
                    try
                    {
                        byte[] dllBytes = File.ReadAllBytes(dllPath);
                        LoadImageErrorCode errorCode = RuntimeApi.LoadMetadataForAOTAssembly(dllBytes, HomologousImageMode.Consistent);

                        if (errorCode == LoadImageErrorCode.OK)
                        {
                            loadedCount++;
                        }
                        else
                        {
                            Logger.APILogger.LogWarn($"Failed to load metadata for {Path.GetFileName(dllPath)}: {errorCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogError($"Error loading metadata from {dllPath}: {ex.Message}");
                    }
                }

                Logger.APILogger.Log($"Loaded supplementary metadata for {loadedCount}/{dllFiles.Length} assemblies.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Error in LoadSupplementaryMetadata: {ex}");
            }
        }

        private static string GetMetadataPath()
        {
            string[] possiblePaths = new string[]
            {
                Path.Combine(Application.persistentDataPath, "HybridCLRData", "il2cpp_data", "Managed"),
                Path.Combine(Application.streamingAssetsPath, "HybridCLRData", "il2cpp_data", "Managed"),
                Path.Combine(Application.dataPath, "HybridCLRData", "il2cpp_data", "Managed"),
            };

            foreach (string path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

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
