using System;
using System.IO;
using UnityEngine;

namespace Modding
{
    public static class ReferenceAssemblyManager
    {
        private const string ResourcePath =
            "modding/Assembly-CSharp";

        private static readonly string ReferenceDirectory =
            Path.Combine(
                Application.persistentDataPath,
                "Modding API");

        private static readonly string ReferencePath =
            Path.Combine(
                ReferenceDirectory,
                "Assembly-CSharp.dll");

        private static bool _prepared;

        private static bool _preparationAttempted;

        public static string GetReferencePath()
        {
            return ReferencePath;
        }

        public static bool EnsureReferenceAssembly(
            out string path,
            out string error)
        {
            path = ReferencePath;
            error = null;

            if (_prepared)
            {
                return File.Exists(ReferencePath);
            }

            if (_preparationAttempted)
            {
                return File.Exists(ReferencePath);
            }

            _preparationAttempted = true;

            try
            {
                if (!Directory.Exists(ReferenceDirectory))
                {
                    Directory.CreateDirectory(
                        ReferenceDirectory);

                    Logger.APILogger.Log(
                        "IL Reef created reference directory: " +
                        ReferenceDirectory);
                }

                TextAsset referenceAsset =
                    null;

                try
                {
                    referenceAsset =
                        Resources.Load<TextAsset>(
                            ResourcePath);
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogWarn(
                        "IL Ref failed to load reference asset: " +
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

                    _prepared = true;

                    return true;
                }

                // Fallback
                if (File.Exists(ReferencePath))
                {
                    FileInfo info =
                        new FileInfo(ReferencePath);

                    if (info.Length > 1024)
                    {
                        Logger.APILogger.LogWarn(
                            "IL Ref resources reference was not found. " +
                            "Using existing reference file: " +
                            ReferencePath);

                        _prepared = true;

                        return true;
                    }
                }

                error =
                    "Assembly-CSharp reference resource/file not found.";

                Logger.APILogger.LogError(
                    "IL Reef " + error);

                return false;
            }
            catch (Exception ex)
            {
                error =
                    "Failed to prepare Assembly-CSharp reference: " +
                    ex;

                Logger.APILogger.LogError(
                    "IL Reef " + error);

                return false;
            }
        }
    }
}