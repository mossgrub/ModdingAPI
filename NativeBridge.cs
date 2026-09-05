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

        private static bool _initTried;
        private static bool _ready;
        private static bool _locationHookInstalled;
        private static bool _addComponentHookInstalled;

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

        private static IntPtr ToObjectPtr(object o)
        {
            if (o == null) return IntPtr.Zero;
            GCHandle h = GCHandle.Alloc(o, GCHandleType.Normal);
            try { return GCHandle.ToIntPtr(h); }
            finally { if (h.IsAllocated) h.Free(); }
        }

        private static object FromObjectPtr(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            var box = new object[1];
            GCHandle h = GCHandle.Alloc(box, GCHandleType.Pinned);
            try
            {
                IntPtr slot = h.AddrOfPinnedObject();
                if (IntPtr.Size == 8) Marshal.WriteInt64(slot, 0, p.ToInt64());
                else Marshal.WriteInt32(slot, 0, p.ToInt32());
                return box[0];
            }
            finally { h.Free(); }
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

                            if (Nullable.GetUnderlyingType(pt) == typeof(float))
                            {
                                object val = args[a + i];
                                IntPtr buf = Marshal.AllocHGlobal(8);
                                try
                                {
                                    Marshal.WriteByte(buf, 0, (byte)(val != null ? 1 : 0));
                                    if (val != null)
                                    {
                                        byte[] f = BitConverter.GetBytes((float)val);
                                        Marshal.Copy(f, 0, new IntPtr(buf.ToInt64() + 4), f.Length);
                                    }
                                }
                                catch
                                {
                                    Marshal.FreeHGlobal(buf);
                                    throw;
                                }
                                slots[i] = buf;
                                owned[ownedN++] = buf;
                            }
                            else if (pt.IsValueType)
                            {
                                IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(pt));
                                Marshal.StructureToPtr(args[a + i], buf, false);
                                slots[i] = buf;
                                owned[ownedN++] = buf;
                            }
                            else slots[i] = ToObjectPtr(args[a + i]);
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