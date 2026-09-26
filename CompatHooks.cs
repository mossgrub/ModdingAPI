using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Modding
{
    internal static class CompatHooks
    {
        [DllImport("modding_native", EntryPoint = "mod2_init")]
        private static extern int Init();

        [DllImport("modding_native", EntryPoint = "mod2_install_location_hook")]
        private static extern int InstallLocationHook(IntPtr getLocationAddress);

        [DllImport("modding_native", EntryPoint = "mod2_set_location_resolver")]
        private static extern void SetLocationResolverNative(IntPtr resolverMethodInfo);

        [DllImport("modding_native", EntryPoint = "mod2_set_location_resolver_object")]
        private static extern void SetLocationResolverObjectNative(IntPtr resolverMethodInfo);

        [DllImport("modding_native", EntryPoint = "mod2_install_resource_hooks")]
        private static extern int InstallResourceHooksNative(
            IntPtr targetStreamPtr, IntPtr targetNamesPtr, IntPtr helperStreamMethod, IntPtr helperNamesMethod);

        [DllImport("modding_native", EntryPoint = "mod2_register_assembly_path")]
        private static extern void RegisterAssemblyPathNative(
            [MarshalAs(UnmanagedType.IUnknown)] object assemblyObject,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPStr)] string path,
            IntPtr assemblyNative);

        private static readonly bool _isReady = InitializeNative();
        private static bool _applied;
        private static bool _locationHookInstalled;
        private static bool _resourceHooksInstalled;

        internal static readonly ConcurrentDictionary<Assembly, string> AssemblyLocations = new ConcurrentDictionary<Assembly, string>();
        internal static readonly ConcurrentDictionary<string, string> NameLocations = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool Ready => _isReady;

        private static bool InitializeNative()
        {
            try
            {
                return Init() != 0;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"CompatHooks init failed: {ex.Message}");
                return false;
            }
        }

        public static void Apply()
        {
            if (_applied) return;
            _applied = true;

#if ENABLE_IL2CPP
            if (!_isReady) return;
            PatchAssemblyLocation();
            PatchManifestResources();
#endif
        }

        private static void PatchAssemblyLocation()
        {
            if (_locationHookInstalled) return;

            try
            {
                MethodInfo gl = typeof(Assembly).GetMethod("get_Location", BindingFlags.Public | BindingFlags.Instance);
                IntPtr addr = gl != null ? Il2CppResolver.TryGetMethodPointer(gl) : IntPtr.Zero;
                if (addr == IntPtr.Zero) return;

                if (InstallLocationHook(addr) != 0)
                {
                    _locationHookInstalled = true;
                    Logger.APILogger.Log("Assembly.Location native hook installed.");

                    BindResolver(nameof(ResolveLocationFallback), 1, "System.String", SetLocationResolverNative);
                    BindResolver(nameof(ResolveLocationFallbackObject), 1, "System.Reflection.Assembly", SetLocationResolverObjectNative);
                }
                else
                {
                    Logger.APILogger.Log("Assembly.Location native hook failed to install.");
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"Native Location hook install failed: {ex.Message}");
            }
        }

        private static void BindResolver(string methodName, int paramCount, string paramType, Action<IntPtr> nativeSetter)
        {
            MethodInfo m = typeof(CompatHooks).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) return;

            IntPtr info = Il2CppResolver.TryGetMethodInfoPointer(m, paramCount, paramType);
            if (info != IntPtr.Zero) nativeSetter(info);
        }

        private static void PatchManifestResources()
        {
            if (_resourceHooksInstalled) return;

            try
            {
                MethodInfo streamM = typeof(Assembly).GetMethod("GetManifestResourceStream", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                MethodInfo namesM = typeof(Assembly).GetMethod("GetManifestResourceNames", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (streamM == null || namesM == null) return;

                IntPtr streamPtr = Il2CppResolver.TryGetMethodPointer(streamM, 1, "System.String");
                IntPtr namesPtr = Il2CppResolver.TryGetMethodPointer(namesM, 0, (string)null);
                if (streamPtr == IntPtr.Zero || namesPtr == IntPtr.Zero) return;

                MethodInfo hStream = typeof(EmbeddedResourceExtractor).GetMethod(nameof(EmbeddedResourceExtractor.GetManifestResourceStream), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo hNames = typeof(EmbeddedResourceExtractor).GetMethod(nameof(EmbeddedResourceExtractor.GetManifestResourceNames), BindingFlags.NonPublic | BindingFlags.Static);
                if (hStream == null || hNames == null) return;

                IntPtr hStreamInfo = Il2CppResolver.TryGetMethodInfoPointer(hStream, 2, "System.Reflection.Assembly");
                IntPtr hNamesInfo = Il2CppResolver.TryGetMethodInfoPointer(hNames, 1, "System.Reflection.Assembly");
                if (hStreamInfo == IntPtr.Zero || hNamesInfo == IntPtr.Zero) return;

                _resourceHooksInstalled = InstallResourceHooksNative(streamPtr, namesPtr, hStreamInfo, hNamesInfo) != 0;
                Logger.APILogger.Log(_resourceHooksInstalled
                    ? "Assembly resource compat hooks installed."
                    : "Assembly resource compat hooks failed to install.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"Native resource hooks install failed: {ex.Message}");
            }
        }

        public static void Register(Assembly asm, string path)
        {
            if (asm == null || string.IsNullOrEmpty(path)) return;

            AssemblyLocations[asm] = path;
            string name = asm.GetName()?.Name ?? Path.GetFileNameWithoutExtension(path);
            NameLocations[name] = path;

            if (_isReady)
            {
                RegisterAssemblyPathNative(asm, name, path, IntPtr.Zero);
            }
        }

        public static bool TryGetAssemblyPath(Assembly asm, out string path)
        {
            if (asm == null)
            {
                path = null;
                return false;
            }

            if (AssemblyLocations.TryGetValue(asm, out path) && !string.IsNullOrEmpty(path))
                return true;

            try
            {
                string nm = asm.GetName()?.Name;
                if (!string.IsNullOrEmpty(nm) && NameLocations.TryGetValue(nm, out path) && !string.IsNullOrEmpty(path))
                {
                    AssemblyLocations[asm] = path;
                    return true;
                }
            }
            catch { }

            string fallback = asm.Location;
            if (!string.IsNullOrEmpty(fallback))
            {
                path = fallback;
                AssemblyLocations[asm] = fallback;
                return true;
            }

            path = null;
            return false;
        }

        internal static string ResolveLocationFallback(string assemblyName)
            => !string.IsNullOrEmpty(assemblyName) && NameLocations.TryGetValue(assemblyName, out string path) ? path : null;

        internal static string ResolveLocationFallbackObject(Assembly asm)
            => TryGetAssemblyPath(asm, out string path) ? path : null;
    }
}
