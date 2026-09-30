using System;
using System.Collections.Generic;
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

        [DllImport("modding_native", EntryPoint = "mod2_invoke_orig")]
        private static extern IntPtr InvokeOrigNative(IntPtr methodInfo, IntPtr trampoline, IntPtr obj,
            IntPtr args, IntPtr exc);

        [DllImport("modding_native", EntryPoint = "mod2_unbox")]
        private static extern void UnboxNative(IntPtr boxedObject, IntPtr outBuffer, int size);

        [DllImport("modding_native", EntryPoint = "mod2_install_takemp_hook")]
        private static extern int InstallTakeMPHookNative(IntPtr takeMPTargetAddr);

        [DllImport("modding_native", EntryPoint = "mod2_managed_object_to_native")]
        private static extern IntPtr ManagedObjectToNative(IntPtr managedGcHandle);

        [DllImport("modding_native", EntryPoint = "mod2_managed_handle_to_native")]
        private static extern IntPtr ManagedHandleToNative(uint managedHandle);

        [DllImport("modding_native", EntryPoint = "mod2_set_crash_log_path", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetCrashLogPathNative([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport("modding_native", EntryPoint = "mod2_set_crash_context", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetCrashContextNative([MarshalAs(UnmanagedType.LPStr)] string context);

        [DllImport("modding_native", EntryPoint = "mod2_install_crash_handler", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InstallCrashHandlerNative();

        private static bool _initTried;
        private static bool _ready;
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
                try { SetLogFileNative(Application.persistentDataPath + "/NativeLog.txt"); }
                catch (Exception ex) { Logger.APILogger.LogWarn("Could not set native log file: " + ex.Message); }
            }
        }

        internal static bool InstallCrashHandler(string path)
        {
            try
            {
                SetCrashLogPathNative(path);

                return InstallCrashHandlerNative() != 0;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "Native crash handler installation failed: " +
                    ex.Message);

                return false;
            }
        }

        internal static void SetCrashContext(
            string context)
        {
            try
            {
                SetCrashContextNative(
                    string.IsNullOrEmpty(context)
                        ? "No managed crash context"
                        : context);
            }
            catch
            {
            }
        }

        private static readonly object ManagedObjectCacheLock =
            new object();

        private static readonly Dictionary<IntPtr, WeakReference>
            ManagedObjectCache =
                new Dictionary<IntPtr, WeakReference>();

        private static FieldInfo _unityCachedPtrField;
        private static bool _unityCachedPtrFieldSearched;

        private static FieldInfo GetUnityCachedPtrField()
        {
            if (_unityCachedPtrFieldSearched)
            {
                return _unityCachedPtrField;
            }

            _unityCachedPtrFieldSearched = true;

            try
            {
                _unityCachedPtrField =
                    typeof(UnityEngine.Object).GetField(
                        "m_CachedPtr",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic);
            }
            catch
            {
                _unityCachedPtrField = null;
            }

            return _unityCachedPtrField;
        }

        private static IntPtr GetUnityCachedPtr(
            UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                MethodInfo method =
                    GetUnityGetCachedPtrMethod();

                if (method != null)
                {
                    object value =
                        method.Invoke(
                            obj,
                            null);

                    if (value is IntPtr)
                    {
                        return (IntPtr)value;
                    }
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field =
                    GetUnityCachedPtrField();

                if (field != null)
                {
                    object value =
                        field.GetValue(obj);

                    if (value is IntPtr)
                    {
                        return (IntPtr)value;
                    }
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        private static MethodInfo _unityGetCachedPtrMethod;
        private static bool _unityGetCachedPtrMethodSearched;

        private static MethodInfo GetUnityGetCachedPtrMethod()
        {
            if (_unityGetCachedPtrMethodSearched)
            {
                return _unityGetCachedPtrMethod;
            }

            _unityGetCachedPtrMethodSearched = true;

            try
            {
                _unityGetCachedPtrMethod =
                    typeof(UnityEngine.Object).GetMethod(
                        "GetCachedPtr",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic);
            }
            catch
            {
                _unityGetCachedPtrMethod = null;
            }

            return _unityGetCachedPtrMethod;
        }

        private static object TryGetStaticInstance(
            Type expectedType)
        {
            if (expectedType == null)
            {
                return null;
            }

            try
            {
                FieldInfo field =
                    expectedType.GetField(
                        "instance",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (field != null)
                {
                    object value =
                        field.GetValue(null);

                    if (value != null &&
                        expectedType.IsInstanceOfType(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            try
            {
                PropertyInfo property =
                    expectedType.GetProperty(
                        "instance",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (property != null &&
                    property.CanRead)
                {
                    object value =
                        property.GetValue(
                            null,
                            null);

                    if (value != null &&
                        expectedType.IsInstanceOfType(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field =
                    expectedType.GetField(
                        "Instance",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (field != null)
                {
                    object value =
                        field.GetValue(null);

                    if (value != null &&
                        expectedType.IsInstanceOfType(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            try
            {
                PropertyInfo property =
                    expectedType.GetProperty(
                        "Instance",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (property != null &&
                    property.CanRead)
                {
                    object value =
                        property.GetValue(
                            null,
                            null);

                    if (value != null &&
                        expectedType.IsInstanceOfType(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        internal static void EnsureTakeMPHook()
        {
            if (_takeMPHookInstalled || !_ready) return;
            try
            {
                Type[] candidates = new Type[] { typeof(HeroController), typeof(PlayerData) };
                for (int c = 0; c < candidates.Length && !_takeMPHookInstalled; c++)
                {
                    Type t = candidates[c];
                    MethodInfo takeMP = t.GetMethod("TakeMP",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new Type[] { typeof(int) }, null);
                    if (takeMP == null)
                    {
                        Logger.APILogger.LogDebug(t.Name + ".TakeMP(int) not found.");
                        continue;
                    }

                    IntPtr takeMPPtr = Il2CppResolver.TryGetMethodPointer(takeMP, 1, "System.Int32");
                    if (takeMPPtr == IntPtr.Zero)
                    {
                        Logger.APILogger.LogWarn(t.Name + ".TakeMP method address not found.");
                        continue;
                    }

                    _takeMPHookInstalled = InstallTakeMPHookNative(takeMPPtr) != 0;
                    Logger.APILogger.Log(_takeMPHookInstalled
                        ? t.Name + ".TakeMP native hook installed."
                        : t.Name + ".TakeMP native hook failed to install.");
                }
            }
            catch (Exception ex) { Logger.APILogger.LogWarn("TakeMP hook install failed: " + ex.Message); }
        }

        internal static IntPtr ObjectToPtr(object o)
        {
            if (o == null)
                return IntPtr.Zero;

            UnityEngine.Object unityObject =
                o as UnityEngine.Object;

            if (unityObject != null)
            {
                IntPtr ptr =
                    GetUnityCachedPtr(unityObject);

                if (ptr != IntPtr.Zero)
                    return ptr;
            }

            GCHandle handle =
                GCHandle.Alloc(
                    o,
                    GCHandleType.Normal);

            try
            {
                return ManagedObjectToNative(
                    GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
        }

        private static unsafe object FromObjectPtrUnsafe(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return null;

            object o = null;

            TypedReference tr = __makeref(o);

            *(IntPtr*)&tr = p;

            return o;
        }

        internal static object FromObjectPtr(
            IntPtr ptr,
            Type expectedType)
        {
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            if (expectedType != null)
            {
                lock (ManagedObjectCacheLock)
                {
                    WeakReference cached;

                    if (ManagedObjectCache.TryGetValue(
                            ptr,
                            out cached))
                    {
                        try
                        {
                            object cachedTarget =
                                cached.Target;

                            if (cachedTarget != null &&
                                expectedType.IsInstanceOfType(
                                    cachedTarget))
                            {
                                UnityEngine.Object cachedUnityObject =
                                    cachedTarget as UnityEngine.Object;

                                if (cachedUnityObject != null)
                                {
                                    IntPtr cachedPtr =
                                        GetUnityCachedPtr(
                                            cachedUnityObject);

                                    if (cachedPtr == ptr)
                                    {
                                        return cachedTarget;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (expectedType != null &&
                typeof(UnityEngine.Object).IsAssignableFrom(
                    expectedType))
            {
                if (expectedType == typeof(HeroController))
                {
                    HeroController hero =
                        HeroController.instance;

                    if (hero != null)
                    {
                        lock (ManagedObjectCacheLock)
                        {
                            ManagedObjectCache[ptr] =
                                new WeakReference(hero);
                        }

                        return hero;
                    }
                }

                object singleton =
                    TryGetStaticInstance(
                        expectedType);

                if (singleton != null)
                {
                    UnityEngine.Object unityObject =
                        singleton as UnityEngine.Object;

                    if (unityObject != null)
                    {
                        IntPtr singletonPtr =
                            GetUnityCachedPtr(
                                unityObject);

                        Logger.APILogger.LogDebug(
                            "[OBJPTR] Singleton candidate: " +
                            expectedType.FullName +
                            " ptr=0x" +
                            singletonPtr.ToInt64().ToString("X") +
                            " native=0x" +
                            ptr.ToInt64().ToString("X"));

                        if (singletonPtr == ptr ||
                            singletonPtr == IntPtr.Zero)
                        {
                            lock (ManagedObjectCacheLock)
                            {
                                ManagedObjectCache[ptr] =
                                    new WeakReference(
                                        singleton);
                            }

                            return singleton;
                        }

                        if (expectedType ==
                            typeof(HeroController) &&
                            singletonPtr == ptr)
                        {
                            lock (ManagedObjectCacheLock)
                            {
                                ManagedObjectCache[ptr] =
                                    new WeakReference(
                                        singleton);
                            }

                            return singleton;
                        }
                    }
                }

                try
                {
                    UnityEngine.Object[] objects =
                        UnityEngine.Resources.FindObjectsOfTypeAll(
                            expectedType);

                    if (objects != null)
                    {
                        for (int i = 0;
                             i < objects.Length;
                             i++)
                        {
                            UnityEngine.Object obj =
                                objects[i];

                            if (obj == null)
                            {
                                continue;
                            }

                            IntPtr cachedPtr =
                                GetUnityCachedPtr(
                                    obj);

                            if (cachedPtr != ptr)
                            {
                                continue;
                            }

                            lock (ManagedObjectCacheLock)
                            {
                                ManagedObjectCache[ptr] =
                                    new WeakReference(
                                        obj);
                            }

                            Logger.APILogger.LogDebug(
                                "Resolved " +
                                expectedType.FullName +
                                " from native pointer 0x" +
                                ptr.ToInt64().ToString("X"));

                            return obj;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogWarn(
                        "Unity object lookup failed for " +
                        expectedType.FullName +
                        ": " +
                        ex.Message);
                }
            }

            return FromObjectPtrUnsafe(
                ptr);
        }

        private static unsafe IntPtr ToObjectPtr(
            object o)
        {
            if (o == null)
            {
                return IntPtr.Zero;
            }

            UnityEngine.Object unityObject =
                o as UnityEngine.Object;

            if (unityObject != null)
            {
                return GetUnityCachedPtr(
                    unityObject);
            }

            TypedReference tr =
                __makeref(o);

            return *(IntPtr*)&tr;
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
                                if (val is IntPtr nativePtr)
                                {
                                    slots[i] = nativePtr;
                                }
                                else
                                {
                                    slots[i] = ToObjectPtr(val);
                                }
                            }
                        }
                    }

                    pin = slots != null ? GCHandle.Alloc(slots, GCHandleType.Pinned) : default(GCHandle);
                    IntPtr argsPtr = slots != null ? pin.AddrOfPinnedObject() : IntPtr.Zero;
                    IntPtr objPtr =
                        IntPtr.Zero;

                    if (instanceCall)
                    {
                        objPtr =
                            DetourBridge.GetCurrentNativeSelf(
                                target);

                        if (objPtr != IntPtr.Zero)
                        {
                            Logger.APILogger.LogDebug(
                                "InvokeOrig using native self context for " +
                                target.Name +
                                ": 0x" +
                                objPtr.ToInt64().ToString("X"));
                        }

                        if (objPtr == IntPtr.Zero &&
                            args != null &&
                            args.Length > 0)
                        {
                            objPtr =
                                ToObjectPtr(args[0]);

                            if (objPtr != IntPtr.Zero)
                            {
                                Logger.APILogger.LogDebug(
                                    "InvokeOrig using managed ObjectToPtr fallback for " +
                                    target.Name);
                            }
                        }
                    }
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
                    return FromObjectPtr(result, retType);
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