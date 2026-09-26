using System;
using System.IO;
using UnityEngine;

namespace Modding
{
    public static class ReferenceAssemblyManager
    {
        private const string AssemblyCSharpResourcePath = "modding/Assembly-CSharp";

        private const string ReferenceResourcesPath = "modding/References";

        private const string ReferencePackVersion = "1";

        private static readonly string ReferenceDirectory =
            Path.Combine(
                Application.persistentDataPath,
                "Modding API");

        private static readonly string ReferencePackDirectory =
            Path.Combine(
                ReferenceDirectory,
                "References");

        private static readonly string ReferencePath =
            Path.Combine(
                ReferenceDirectory,
                "Assembly-CSharp.dll");

        private static readonly string ReferencePackVersionPath =
            Path.Combine(
                ReferencePackDirectory,
                ".version");

        private static bool _prepared;

        private static bool _preparationAttempted;

        private static bool _packPrepared;

        private static bool _packPreparationAttempted;

        public static string GetReferencePath()
        {
            return ReferencePath;
        }

        public static string GetReferenceDirectory()
        {
            return ReferenceDirectory;
        }

        public static string GetReferencePackDirectory()
        {
            return ReferencePackDirectory;
        }

        public static bool EnsureReferenceAssembly(
            out string path,
            out string error)
        {
            path =
                ReferencePath;

            error = null;

            if (_prepared &&
                File.Exists(ReferencePath))
            {
                EnsureReferencePack(
                    out _,
                    out _);

                return true;
            }

            if (_preparationAttempted)
            {
                return File.Exists(
                    ReferencePath);
            }

            _preparationAttempted = true;

            try
            {
                if (!Directory.Exists(
                        ReferenceDirectory))
                {
                    Directory.CreateDirectory(
                        ReferenceDirectory);

                    Logger.APILogger.Log(
                        "IL Ref created reference directory: " +
                        ReferenceDirectory);
                }

                TextAsset referenceAsset =
                    null;

                try
                {
                    referenceAsset =
                        Resources.Load<TextAsset>(
                            AssemblyCSharpResourcePath);
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogWarn(
                        "IL Ref failed to load Assembly-CSharp reference asset: " +
                        ex.Message);
                }

                if (referenceAsset != null &&
                    referenceAsset.bytes != null &&
                    referenceAsset.bytes.Length > 1024)
                {
                    File.WriteAllBytes(
                        ReferencePath,
                        referenceAsset.bytes);

                    Logger.APILogger.Log(
                        "IL Ref extracted Assembly-CSharp reference: " +
                        ReferencePath +
                        " (" +
                        referenceAsset.bytes.Length +
                        " bytes)");
                }
                else if (File.Exists(
                             ReferencePath))
                {
                    FileInfo info =
                        new FileInfo(
                            ReferencePath);

                    if (info.Length <= 1024)
                    {
                        try
                        {
                            File.Delete(
                                ReferencePath);
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        Logger.APILogger.Log(
                            "IL Ref using existing Assembly-CSharp reference: " +
                            ReferencePath);
                    }
                }

                if (!File.Exists(
                        ReferencePath))
                {
                    error =
                        "Assembly-CSharp reference resource/file not found.";

                    Logger.APILogger.LogError(
                        "IL Ref " +
                        error);

                    return false;
                }

                FileInfo referenceInfo =
                    new FileInfo(
                        ReferencePath);

                if (referenceInfo.Length <= 1024)
                {
                    error =
                        "Assembly-CSharp reference file is invalid or empty.";

                    Logger.APILogger.LogError(
                        "IL Ref " +
                        error);

                    return false;
                }

                EnsureReferencePack(
                    out string packError,
                    out bool packReady);

                if (!packReady &&
                    !string.IsNullOrEmpty(packError))
                {
                    Logger.APILogger.LogWarn(
                        "IL Ref reference pack unavailable: " +
                        packError);
                }

                _prepared = true;

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Failed to prepare Assembly-CSharp reference: " +
                    ex;

                Logger.APILogger.LogError(
                    "IL Ref " +
                    error);

                return false;
            }
        }

        public static bool EnsureReferencePack(
            out string error,
            out bool ready)
        {
            error = null;
            ready = false;

            if (_packPrepared &&
                Directory.Exists(
                    ReferencePackDirectory))
            {
                ready = true;
                return true;
            }

            if (_packPreparationAttempted)
            {
                ready =
                    Directory.Exists(
                        ReferencePackDirectory);

                return ready;
            }

            _packPreparationAttempted = true;

            try
            {
                if (!Directory.Exists(
                        ReferencePackDirectory))
                {
                    Directory.CreateDirectory(
                        ReferencePackDirectory);

                    Logger.APILogger.Log(
                        "IL Ref created reference pack directory: " +
                        ReferencePackDirectory);
                }

                string installedVersion =
                    null;

                try
                {
                    if (File.Exists(
                            ReferencePackVersionPath))
                    {
                        installedVersion =
                            File.ReadAllText(
                                ReferencePackVersionPath)
                                .Trim();
                    }
                }
                catch
                {
                    installedVersion = null;
                }

                if (installedVersion !=
                    ReferencePackVersion)
                {
                    TextAsset[] assets =
                        null;

                    try
                    {
                        assets =
                            Resources.LoadAll<TextAsset>(
                                ReferenceResourcesPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn(
                            "IL Ref failed to load reference pack: " +
                            ex.Message);
                    }

                    if (assets == null ||
                        assets.Length == 0)
                    {
                        error =
                            "No reference DLLs were found in Resources/" +
                            ReferenceResourcesPath +
                            ".";

                        Logger.APILogger.LogWarn(
                            "IL Ref " +
                            error);

                        if (DirectoryContainsDlls())
                        {
                            ready = true;
                            _packPrepared = true;
                            return true;
                        }

                        return false;
                    }

                    int copied = 0;

                    for (int i = 0;
                         i < assets.Length;
                         i++)
                    {
                        TextAsset asset =
                            assets[i];

                        if (asset == null)
                        {
                            continue;
                        }

                        byte[] bytes =
                            asset.bytes;

                        if (bytes == null ||
                            bytes.Length <= 0)
                        {
                            continue;
                        }

                        string fileName =
                            asset.name;

                        if (string.IsNullOrEmpty(
                                fileName))
                        {
                            continue;
                        }

                        if (!fileName.EndsWith(
                                ".dll",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            fileName += ".dll";
                        }

                        string outputPath =
                            Path.Combine(
                                ReferencePackDirectory,
                                fileName);

                        File.WriteAllBytes(
                            outputPath,
                            bytes);

                        copied++;
                    }

                    try
                    {
                        if (File.Exists(
                                ReferencePackVersionPath))
                        {
                            File.Delete(
                                ReferencePackVersionPath);
                        }
                    }
                    catch
                    {
                    }

                    File.WriteAllText(
                        ReferencePackVersionPath,
                        ReferencePackVersion);

                    Logger.APILogger.Log(
                        "IL Ref extracted " +
                        copied +
                        " reference DLL(s) to: " +
                        ReferencePackDirectory);
                }

                ready =
                    DirectoryContainsDlls();

                if (!ready)
                {
                    error =
                        "Reference pack directory contains no DLL files.";

                    Logger.APILogger.LogWarn(
                        "IL Ref " +
                        error);

                    return false;
                }

                _packPrepared = true;

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Failed to prepare reference pack: " +
                    ex;

                Logger.APILogger.LogError(
                    "IL Ref " +
                    error);

                ready =
                    DirectoryContainsDlls();

                if (ready)
                {
                    _packPrepared = true;
                    return true;
                }

                return false;
            }
        }

        private static bool DirectoryContainsDlls()
        {
            try
            {
                if (!Directory.Exists(
                        ReferencePackDirectory))
                {
                    return false;
                }

                string[] files =
                    Directory.GetFiles(
                        ReferencePackDirectory,
                        "*.dll",
                        SearchOption.TopDirectoryOnly);

                return files != null &&
                       files.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}