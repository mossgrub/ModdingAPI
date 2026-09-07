using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Modding
{
    internal static class NativeBridge
    {
        [DllImport("modding_native", EntryPoint = "mod2_init")]
        private static extern int Init();

        [DllImport("modding_native", EntryPoint = "mod2_set_log_file")]
        private static extern void SetLogFileNative(
            [MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport("modding_native", EntryPoint = "mod2_install_location_hook")]
        private static extern int InstallLocationHook(IntPtr getLocationAddress);

        [DllImport("modding_native", EntryPoint = "mod2_location_hook_active")]
        private static extern int LocationHookActive();

        [DllImport("modding_native", EntryPoint = "mod2_register_assembly_path")]
        private static extern void RegisterAssemblyPath(
            [MarshalAs(UnmanagedType.IUnknown)] object assemblyObject,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPStr)] string path,
            IntPtr assemblyNative);

        [DllImport("modding_native", EntryPoint = "mod2_invoke_orig")]
        private static extern IntPtr InvokeOrigNative(IntPtr methodInfo, IntPtr trampoline, IntPtr obj,
            IntPtr args, IntPtr exc);

        [DllImport("modding_native", EntryPoint = "mod2_unbox")]
        private static extern void UnboxNative(IntPtr boxedObject, IntPtr outBuffer, int size);

        [DllImport("modding_native", EntryPoint = "mod2_install_addcomponent_hook")]
        private static extern int InstallAddComponentHook(IntPtr addComponentMethodPtr, IntPtr getComponentMethodInfo, IntPtr getComponentFuncPtr);

        [DllImport("modding_native", EntryPoint = "mod2_set_location_resolver")]
        private static extern void SetLocationResolverNative(IntPtr resolverMethodInfo);

        [DllImport("modding_native", EntryPoint = "mod2_set_location_resolver_object")]
        private static extern void SetLocationResolverObjectNative(IntPtr resolverMethodInfo);

        [DllImport("modding_native", EntryPoint = "mod2_install_resource_hooks")]
        private static extern int InstallResourceHooksNative(
            IntPtr targetStreamPtr, IntPtr targetNamesPtr, IntPtr helperStreamMethod, IntPtr helperNamesMethod);

        [DllImport("modding_native", EntryPoint = "mod2_install_gameobject_ctor_hook")]
        private static extern int InstallGameObjectCtorHookNative(IntPtr ctorTargetAddr);

        [DllImport("modding_native", EntryPoint = "mod2_install_takemp_hook")]
        private static extern int InstallTakeMPHookNative(IntPtr takeMPTargetAddr);

        private static bool _initTried;
        private static bool _ready;
        private static bool _locationHookInstalled;
        private static bool _addComponentHookInstalled;
        private static bool _resourceHooksInstalled;
        private static bool _gameObjectCtorInstalled;
        private static bool _takeMPHookInstalled;

        internal static bool Ready => _ready;

        internal static void EnsureReady()
        {
            if (_initTried) return;
            _initTried = true;
            try { _ready = Init() != 0; }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn("NativeBridge init failed: " + ex.Message);
                _ready = false;
            }
            if (_ready)
            {
                try { SetLogFileNative(Application.persistentDataPath + "/ModdingNative.log"); }
                catch (Exception ex) { Logger.APILogger.LogWarn("Could not set native log file: " + ex.Message); }
            }
        }

        internal static void EnsureLocationHook()
        {
            if (_locationHookInstalled || !_ready) return;
            try
            {
                MethodInfo gl = typeof(Assembly).GetMethod("get_Location", BindingFlags.Public | BindingFlags.Instance);
                if (gl == null) return;
                IntPtr addr = Il2CppResolver.TryGetMethodPointer(gl);
                if (addr == IntPtr.Zero) return;
                _locationHookInstalled = InstallLocationHook(addr) != 0;
                Logger.APILogger.Log(_locationHookInstalled
                    ? "Assembly.Location native hook installed."
                    : "Assembly.Location native hook failed to install.");
                if (_locationHookInstalled)
                {
                    try
                    {
                        MethodInfo rl = typeof(NativeCompat).GetMethod(nameof(NativeCompat.ResolveLocationFallback),
                            BindingFlags.NonPublic | BindingFlags.Static);
                        if (rl != null)
                        {
                            IntPtr rlInfo = Il2CppResolver.TryGetMethodInfoPointer(rl, 1, "System.String");
                            if (rlInfo != IntPtr.Zero) SetLocationResolverNative(rlInfo);
                        }
                        MethodInfo rlo = typeof(NativeCompat).GetMethod(nameof(NativeCompat.ResolveLocationFallbackObject),

                            BindingFlags.NonPublic | BindingFlags.Static);
                        if (rlo != null)
                        {
                            IntPtr rloInfo = Il2CppResolver.TryGetMethodInfoPointer(rlo, 1, "System.Reflection.Assembly");
                            if (rloInfo != IntPtr.Zero) SetLocationResolverObjectNative(rloInfo);
                        }
                    }
                    catch (Exception ex2) { Logger.APILogger.LogWarn("Native location resolver setup failed: " + ex2.Message); }
                }
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("Native Location hook install failed: " + ex.Message); }
        }
        internal static void EnsureAddComponentHook()
        {
            if (_addComponentHookInstalled || !_ready) return;
            try
            {
                MethodInfo addType = typeof(UnityEngine.GameObject).GetMethod("AddComponent",
                    BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Type) }, null);
                MethodInfo getType = typeof(UnityEngine.GameObject).GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Type) }, null);
                if (addType == null || getType == null) return;

                IntPtr addPtr = Il2CppResolver.TryGetMethodPointer(addType, 1, "System.Type");
                IntPtr getInfo = Il2CppResolver.TryGetMethodInfoPointer(getType, 1, "System.Type");
                IntPtr getPtr = Il2CppResolver.TryGetMethodPointer(getType, 1, "System.Type");
                Logger.APILogger.LogDebug("AddComponent hook resolve: addComponentPtr=0x" + addPtr.ToInt64().ToString("X") + " getComponentMethodInfo=0x" + getInfo.ToInt64().ToString("X") + " getComponentFuncPtr=0x" + getPtr.ToInt64().ToString("X"));
                if (addPtr == IntPtr.Zero || getPtr == IntPtr.Zero) return;

                _addComponentHookInstalled = InstallAddComponentHook(addPtr, getInfo, getPtr) != 0;
                Logger.APILogger.Log(_addComponentHookInstalled
                    ? "GameObject.AddComponent native compat hook installed."
                    : "GameObject.AddComponent native compat hook failed to install.");
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("Native AddComponent hook install failed: " + ex.Message); }
        }

        internal static void EnsureResourceHooks()
        {
            if (_resourceHooksInstalled || !_ready) return;
            try
            {
                MethodInfo streamM = typeof(Assembly).GetMethod("GetManifestResourceStream",
                    BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(string) }, null);
                MethodInfo namesM = typeof(Assembly).GetMethod("GetManifestResourceNames",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (streamM == null || namesM == null) return;
                IntPtr streamPtr = Il2CppResolver.TryGetMethodPointer(streamM, 1, "System.String");
                IntPtr namesPtr = Il2CppResolver.TryGetMethodPointer(namesM, 0, (string)null);
                if (streamPtr == IntPtr.Zero || namesPtr == IntPtr.Zero) return;
                MethodInfo hStream = typeof(EmbeddedResourceExtractor).GetMethod(
                    nameof(EmbeddedResourceExtractor.GetManifestResourceStream),
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo hNames = typeof(EmbeddedResourceExtractor).GetMethod(
                    nameof(EmbeddedResourceExtractor.GetManifestResourceNames),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (hStream == null || hNames == null) return;
                IntPtr hStreamInfo = Il2CppResolver.TryGetMethodInfoPointer(hStream, 2, "System.Reflection.Assembly");
                IntPtr hNamesInfo = Il2CppResolver.TryGetMethodInfoPointer(hNames, 1, "System.Reflection.Assembly");
                if (hStreamInfo == IntPtr.Zero || hNamesInfo == IntPtr.Zero) return;
                _resourceHooksInstalled = InstallResourceHooksNative(streamPtr, namesPtr, hStreamInfo, hNamesInfo) != 0;
                Logger.APILogger.Log(_resourceHooksInstalled
                    ? "Assembly resource compat hooks installed."
                    : "Assembly resource compat hooks failed to install.");
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("Native resource hooks install failed: " + ex.Message); }

        }

        internal static void EnsureGameObjectCtorHook()
        {
            if (_gameObjectCtorInstalled || !_ready) return;
            try
            {
                ConstructorInfo ctor = typeof(GameObject).GetConstructor(
                    new Type[] { typeof(string), typeof(Type[]) });
                if (ctor == null) return;

                IntPtr ctorPtr = Il2CppResolver.TryGetConstructorPointer(ctor, 2, null);
                if (ctorPtr == IntPtr.Zero)
                {
                    Logger.APILogger.LogWarn("GameObject(string, params Type[]) ctor address not found.");
                    return;
                }

                _gameObjectCtorInstalled = InstallGameObjectCtorHookNative(ctorPtr) != 0;
                Logger.APILogger.Log(_gameObjectCtorInstalled
                    ? "GameObject(string, params Type[]) ctor hook installed."
                    : "GameObject(string, params Type[]) ctor hook failed to install.");
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("GameObject ctor hook install failed: " + ex.Message); }
        }

        internal static void EnsureTakeMPHook()
        {
            if (_takeMPHookInstalled || !_ready) return;
            try
            {
                MethodInfo takeMP = typeof(PlayerData).GetMethod("TakeMP", BindingFlags.Public | BindingFlags.Instance);
                if (takeMP == null) return;

                IntPtr takeMPPtr = Il2CppResolver.TryGetMethodPointer(takeMP, 1, "System.Int32");
                if (takeMPPtr == IntPtr.Zero)
                {
                    Logger.APILogger.LogWarn("PlayerData.TakeMP method address not found.");
                    return;
                }

                _takeMPHookInstalled = InstallTakeMPHookNative(takeMPPtr) != 0;
                Logger.APILogger.Log(_takeMPHookInstalled
                    ? "PlayerData.TakeMP native hook installed."
                    : "PlayerData.TakeMP native hook failed to install.");
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("PlayerData.TakeMP hook install failed: " + ex.Message); }
        }

        internal static void Register(Assembly asm, string path)
        {
            if (asm == null || string.IsNullOrEmpty(path)) return;
            EnsureReady();
            if (!_ready) return;
            try
            {
                string name = null;
                try { name = asm.GetName()?.Name; } catch { }
                if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);

                Logger.APILogger.LogDebug("NativeBridge.Register: name='" + name + "' path='" + path + "'");

                RegisterAssemblyPath(asm, name, path, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("NativeBridge.Register error: " + ex);
            }
        }

        internal static IntPtr ObjectToPtr(object o) => ToObjectPtr(o);

        private static unsafe IntPtr ToObjectPtr(object o)
        {
            if (o == null) return IntPtr.Zero;
            TypedReference tr = __makeref(o);
            return *(IntPtr*)&tr;
        }

        internal static unsafe object FromObjectPtr(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            object o = null;
            TypedReference tr = __makeref(o);
            *(IntPtr*)&tr = p;
            return o;
        }

        internal static object InvokeOrig(MethodInfo target, IntPtr nativeMethod, IntPtr trampoline,
            bool instanceCall, object[] args)
        {
            if (!_ready || target == null || nativeMethod == IntPtr.Zero || trampoline == IntPtr.Zero)
                return null;

            Type retType = target.ReturnType;
            try
            {
                ParameterInfo[] ps = target.GetParameters();
                int argCount = instanceCall ? args.Length - 1 : args.Length;

                IntPtr[] slots = argCount > 0 ? new IntPtr[argCount] : null;
                IntPtr[] owned = null;
                int ownedN = 0;
                GCHandle pin = default(GCHandle);
                try
                {
                    int a = instanceCall ? 1 : 0;
                    if (argCount > 0)
                    {
                        owned = new IntPtr[argCount];
                        for (int i = 0; i < argCount; i++)
                        {
                            Type pt = ps[i].ParameterType;
                            object val = args[a + i];

                            if (Nullable.GetUnderlyingType(pt) == typeof(float))
                            {
                                IntPtr buf = Marshal.AllocHGlobal(8);
                                Marshal.WriteByte(buf, 0, (byte)(val != null ? 1 : 0));
                                if (val != null)
                                {
                                    byte[] f = BitConverter.GetBytes((float)val);
                                    Marshal.Copy(f, 0, new IntPtr(buf.ToInt64() + 4), f.Length);
                                }
                                slots[i] = buf;
                                owned[ownedN++] = buf;
                            }
                            else if (pt.IsValueType && val != null)
                            {
                                IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(pt));
                                Marshal.StructureToPtr(val, buf, false);
                                slots[i] = buf;
                                owned[ownedN++] = buf;
                            }
                            else
                            {
                                slots[i] = ToObjectPtr(val);
                            }
                        }
                    }

                    pin = slots != null ? GCHandle.Alloc(slots, GCHandleType.Pinned) : default(GCHandle);
                    IntPtr argsPtr = slots != null ? pin.AddrOfPinnedObject() : IntPtr.Zero;
                    IntPtr objPtr = instanceCall ? ToObjectPtr(args[0]) : IntPtr.Zero;
                    IntPtr exc = IntPtr.Zero;

                    IntPtr result = InvokeOrigNative(nativeMethod, trampoline, objPtr, argsPtr, exc);

                    if (retType == typeof(void)) return null;
                    if (retType.IsValueType)
                    {
                        if (result == IntPtr.Zero) return Activator.CreateInstance(retType);
                        int sz = Marshal.SizeOf(retType);
                        IntPtr tmp = Marshal.AllocHGlobal(Math.Max(sz, 1));
                        try
                        {
                            UnboxNative(result, tmp, sz);
                            return Marshal.PtrToStructure(tmp, retType);
                        }
                        finally { Marshal.FreeHGlobal(tmp); }
                    }
                    return FromObjectPtr(result);
                }
                finally
                {
                    if (pin.IsAllocated) pin.Free();
                    for (int i = 0; i < ownedN; i++)
                        if (owned[i] != IntPtr.Zero) Marshal.FreeHGlobal(owned[i]);
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("NativeBridge.InvokeOrig error for " + target.Name + ": " + ex);
                return retType != typeof(void) && retType.IsValueType
                    ? Activator.CreateInstance(retType)
                    : null;
            }
        }
    }
}