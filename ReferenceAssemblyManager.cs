using System;
using System.IO;
using UnityEngine;

namespace Modding
{
    public static class ReferenceAssemblyManager
    {
        private const string ResourcePath =
            "Modding/ReferenceAssemblies/Assembly-CSharp";

        private static readonly string ReferenceDirectory =
            Path.Combine(
                Application.persistentDataPath,
                "ModdingAPI",
                "ReferenceAssemblies");

        private static readonly string ReferencePath =
            Path.Combine(
                ReferenceDirectory,
                "Assembly-CSharp.dll");

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

            try
            {
                Directory.CreateDirectory(
                    ReferenceDirectory);

                if (File.Exists(ReferencePath))
                {
                    FileInfo info =
                        new FileInfo(ReferencePath);

                    if (info.Length > 1024)
                    {
                        Logger.APILogger.Log(
                            "[ILREF] Reference assembly already exists: " +
                            ReferencePath +
                            " (" +
                            info.Length +
                            " bytes)");

                        return true;
                    }
                }

                TextAsset asset =
                    Resources.Load<TextAsset>(ResourcePath);

                if (asset == null)
                {
                    error =
                        "Embedded Assembly-CSharp reference resource not found: " +
                        ResourcePath;

                    Logger.APILogger.LogError(
                        "[ILREF] " + error);

                    return false;
                }

                if (asset.bytes == null ||
                    asset.bytes.Length < 1024)
                {
                    error =
                        "Embedded Assembly-CSharp reference is empty or invalid.";

                    Logger.APILogger.LogError(
                        "[ILREF] " + error);

                    return false;
                }

                File.WriteAllBytes(
                    ReferencePath,
                    asset.bytes);

                Logger.APILogger.Log(
                    "[ILREF] Extracted Assembly-CSharp reference: " +
                    ReferencePath +
                    " (" +
                    asset.bytes.Length +
                    " bytes)");

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Failed to prepare Assembly-CSharp reference: " +
                    ex;

                Logger.APILogger.LogError(
                    "[ILREF] " + error);

                return false;
            }
        }
    }
}