using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace Modding
{
    /// <summary>
    /// This class pre-extracts those embedded resources to disk on
    /// behalf of the mod, mirroring what the PC runtime did lazily.
    /// </summary>
    internal static class EmbeddedResourceExtractor
    {
        private static readonly HashSet<string> Processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new object();

        private static readonly string[] KnownExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".exr", ".hdr",
            ".wav", ".ogg", ".mp3", ".flac", ".aiff",
            ".txt", ".json", ".xml", ".csv", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".md",
            ".bytes", ".asset", ".bundle", ".prefab", ".mat", ".controller", ".anim", ".overridecontroller", ".mixer",
            ".ttf", ".otf", ".shader", ".spriteatlas", ".fontsettings", ".physicsmaterial2d", ".mask",
            ".lz4", ".zip", ".gz"
        };

        public static void Extract(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) return;

            lock (Lock)
            {
                if (!Processed.Add(assemblyPath)) return;
            }

            try
            {
                string modDir = Path.GetDirectoryName(assemblyPath);
                if (string.IsNullOrEmpty(modDir)) return;

                string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

                var readParams = new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    ReadSymbols = false
                };

                using (AssemblyDefinition asmDef = AssemblyDefinition.ReadAssembly(assemblyPath, readParams))
                {
                    if (asmDef?.MainModule?.Resources == null || asmDef.MainModule.Resources.Count == 0)
                    {
                        Logger.APILogger.LogDebug("No resources in `" + assemblyName + "`");
                        return;
                    }

                    int written = 0;
                    int skippedExisting = 0;
                    int skippedUnsafe = 0;

                    foreach (Resource res in asmDef.MainModule.Resources)
                    {
                        if (!(res is EmbeddedResource embedded)) continue;
                        string name = embedded.Name;
                        if (string.IsNullOrEmpty(name)) continue;

                        byte[] data;
                        try { data = embedded.GetResourceData(); }
                        catch (Exception ex)
                        {
                            Logger.APILogger.LogWarn($"Failed reading `{name}`: {ex.Message}");
                            continue;
                        }

                        if (data == null || data.Length == 0) continue;

                        foreach (string relPath in BuildCandidatePaths(name, assemblyName))
                        {
                            if (!IsSafeRelativePath(relPath)) { skippedUnsafe++; continue; }

                            string full = Path.Combine(modDir, relPath);
                            if (File.Exists(full)) { skippedExisting++; continue; }

                            try
                            {
                                string dir = Path.GetDirectoryName(full);
                                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                File.WriteAllBytes(full, data);
                                written++;
                            }
                            catch (Exception ex)
                            {
                                Logger.APILogger.LogWarn($"Failed writing `{full}`: {ex.Message}");
                            }
                        }
                    }

                    if (written + skippedExisting + skippedUnsafe > 0)
                    {
                        Logger.APILogger.Log(
                            $"Resource Extractor: `{assemblyName}`: wrote {written}, existing {skippedExisting}, unsafe {skippedUnsafe} (total resources {asmDef.MainModule.Resources.Count})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"Failed for `{assemblyPath}`: {ex.Message}");
            }
        }

        private static IEnumerable<string> BuildCandidatePaths(string resourceName, string assemblyName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(resourceName)) return result;

            AddUnique(result, ToPathWithDots(resourceName));

            string stripped = StripAssemblyPrefix(resourceName, assemblyName);
            if (stripped != null)
            {
                AddUnique(result, ToPathWithDots(stripped));

                foreach (string container in new[] { "Resources.", "res.", "Embedded." })
                {
                    if (stripped.StartsWith(container, StringComparison.OrdinalIgnoreCase))
                        AddUnique(result, ToPathWithDots(stripped.Substring(container.Length)));
                }
            }

            return result;
        }

        private static string StripAssemblyPrefix(string resourceName, string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return null;
            string prefix = assemblyName + ".";
            if (resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return resourceName.Substring(prefix.Length);
            return null;
        }

        private static void AddUnique(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!list.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }

        private static string ToPathWithDots(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string ext = GetKnownExtension(name);
            string core = ext != null ? name.Substring(0, name.Length - ext.Length) : name;
            string converted = core.Replace('.', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(converted)) return null;

            return ext != null ? converted + ext : converted;
        }

        private static string GetKnownExtension(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (string ext in KnownExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return ext;
            }
            return null;
        }

        private static bool IsSafeRelativePath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            if (Path.IsPathRooted(relPath)) return false;

            string[] segments = relPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            foreach (string segment in segments)
            {
                if (segment == "." || segment == "..") return false;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            }

            return true;
        }
    }
}