using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Modding.Utils;

namespace Modding
{
    public static class DetourBridge
    {
        private static bool _initialized;
        private static bool _dobbyAvailable;
        private const int MaxArgs = 6;

        [DllImport("dobby", EntryPoint = "DobbyHook")]
        public static extern int DobbyHookNative(IntPtr target, IntPtr replacement, out IntPtr outTrampoline);

        [DllImport("dobby", EntryPoint = "DobbyDestroy")]
        private static extern int DobbyUnhookNative(IntPtr target);

        // Unhooks without throwing, so a failed detour never leaves a dangling native hook on the target
        // MarshalDirectiveException spam when Assembly.get_Location was left hooked).
        private static void TryUnhook(IntPtr target)
        {
            if (target == IntPtr.Zero) return;
            try { DobbyUnhookNative(target); }
            catch { }
        }

        private static bool IsUnmarshallableType(Type t)
        {
            if (t == null) return false;

            if (t.IsPrimitive || t.IsEnum || t == typeof(IntPtr) || t == typeof(UIntPtr))
                return false;

            if (!t.IsValueType && t != typeof(string))
                return true;

            return typeof(System.Reflection.Assembly).IsAssignableFrom(t);
        }

        private static string DescribeUnmarshallable(Type t)
        {
            if (t == null) return null;
            if (t.IsByRef) return t.Name + " (byref/out)";
            if (t.IsPointer) return t.Name + " (unsafe pointer)";
            if (IsUnmarshallableType(t)) return t.Name + " (System.Reflection.Assembly family)";
            if (t.IsGenericTypeDefinition || t.IsGenericParameter) return t.Name + " (open generic type)";
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return null;
            if (t.IsValueType && !t.IsPrimitive && !t.IsEnum)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string inner = DescribeUnmarshallable(f.FieldType);
                    if (inner != null) return t.Name + "." + f.Name + " -> " + inner;
                }
            }
            return null;
        }

        // Scans the target signature and returns a description or null 
        private static string DescribeUnsupportedSignature(MethodInfo targetMethod)
        {
            if (!targetMethod.IsStatic)
            {
                string d = DescribeUnmarshallable(targetMethod.DeclaringType);
                if (d != null) return d;
            }
            ParameterInfo[] ps = targetMethod.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                string r = DescribeUnmarshallable(ps[i].ParameterType);
                if (r != null) return "param[" + i + "] " + r;
            }
            string ret = DescribeUnmarshallable(targetMethod.ReturnType);
            if (ret != null) return "return " + ret;
            return null;
        }

        private static readonly ConcurrentDictionary<MethodInfo, InstalledHook> Installed = new ConcurrentDictionary<MethodInfo, InstalledHook>();

        private sealed class InstalledHook
        {
            public MethodInfo Target;
            public Type Slot;
            public Delegate Orig;
        }

        private sealed class BridgeState
        {
            public List<Delegate> Handlers = new List<Delegate>();
            public Delegate Orig;

            public Delegate Bridge;

            public Delegate Replacement;
        }

        private static readonly ConcurrentDictionary<Type, BridgeState> BridgeStates = new ConcurrentDictionary<Type, BridgeState>();
        private static int _slotCursor;

        public static bool IsAvailable => _dobbyAvailable;

        public static bool Initialize()
        {
            if (_initialized) return _dobbyAvailable;
            _initialized = true;

#if !ENABLE_IL2CPP
            _dobbyAvailable = false;
            return false;
#else
            try
            {
                MethodInfo targetM = typeof(DetourBridge).GetMethod(nameof(ProbeTarget), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo replM = typeof(DetourBridge).GetMethod(nameof(ProbeReplacement), BindingFlags.NonPublic | BindingFlags.Static);
                IntPtr ta = GetNativeMethodAddress(targetM);
                IntPtr ra = GetNativeMethodAddress(replM);

                IntPtr gmAddr = IntPtr.Zero;
                IntPtr gmFp = IntPtr.Zero;
                try
                {
                    MethodInfo gm = typeof(GameManager).GetMethod(
                        "Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (gm != null)
                    {
                        gmFp = gm.MethodHandle.GetFunctionPointer();
                        gmAddr = GetNativeMethodAddress(gm);
                    }
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogDebug("GameManager.Awake lookup error: " + ex.Message);
                }
                Logger.APILogger.Log(
                    "GameManager.Awake GetFunctionPointer=0x" + gmFp.ToInt64().ToString("X") +
                    " resolved=0x" + gmAddr.ToInt64().ToString("X"));

                Logger.APILogger.Log($"Native addresses ta=0x{ta.ToInt64():X} ra=0x{ra.ToInt64():X}");

                if (ta == ra)
                {
                    Logger.APILogger.LogWarn("Target and replacement share the same native address (0x" +
                        ta.ToInt64().ToString("X") + "); Dobby disabled.");
                    return false;
                }

                _dobbyAvailable = ta != IntPtr.Zero && ra != IntPtr.Zero;
                if (!_dobbyAvailable)
                {
                    return false;
                }

                try
                {
                    IntPtr tp;
                    DobbyHookNative(ta, ra, out tp);
                    DobbyUnhookNative(ta);
                    _dobbyAvailable = true;
                    Logger.APILogger.Log("Dobby available.");
                }
                catch (DllNotFoundException)
                {
                    Logger.APILogger.LogWarn("Dobby library not found.");
                    _dobbyAvailable = false;
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogWarn("Dobby probe hook failed: " + ex.Message);
                    _dobbyAvailable = true;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn("Dobby probe failed: " + ex.Message);
                _dobbyAvailable = true;
            }

            return _dobbyAvailable;
#endif
        }

        private static void ProbeTarget() { }
        private static void ProbeReplacement() { }

        public static IntPtr GetNativeMethodAddress(MethodInfo method)
        {
            if (method == null) return IntPtr.Zero;
            try
            {
                IntPtr fn = method.MethodHandle.GetFunctionPointer();
                if (fn != IntPtr.Zero)
                {
                    return fn;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogDebug("GetFunctionPointer threw for " + method.Name + ": " + ex.Message);
            }

            try
            {
                IntPtr ptr = Il2CppResolver.TryGetMethodPointer(method);
                if (ptr != IntPtr.Zero)
                {
                    Logger.APILogger.LogDebug(
                        "IL2CPP resolver returned native address 0x" + ptr.ToInt64().ToString("X") +
                        " for " + method.DeclaringType?.Name + "." + method.Name);
                    return ptr;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogDebug("IL2CPP method resolver failed for " + method.Name + ": " + ex.Message);
            }

            Logger.APILogger.LogDebug(
                "Could not obtain a native address for " + method.DeclaringType?.Name + "." + method.Name +
                " (GetFunctionPointer is 0).");
            return IntPtr.Zero;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0>(A0 a0);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0, A1>(A0 a0, A1 a1);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0, A1, A2>(A0 a0, A1 a1, A2 a2);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0, A1, A2, A3>(A0 a0, A1 a1, A2 a2, A3 a3);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0, A1, A2, A3, A4>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DetourAction<A0, A1, A2, A3, A4, A5>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<R>();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, R>(A0 a0);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, A1, R>(A0 a0, A1 a1);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, A1, A2, R>(A0 a0, A1 a1, A2 a2);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, A1, A2, A3, R>(A0 a0, A1 a1, A2 a2, A3 a3);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, A1, A2, A3, A4, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate R DetourFunc<A0, A1, A2, A3, A4, A5, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5);


        // Each On.* signature therefore needs one concrete non-generic delegate type 
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OrigStartSlash(NailSlash a0);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OrigOnDisable(GameManager a0);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OrigTakeDamage(HeroController a0, UnityEngine.GameObject a1, GlobalEnums.CollisionSide a2, int a3, int a4);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OrigHit(HealthManager a0, HitInstance a1);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OrigDie(HealthManager a0, System.Nullable<float> a1, AttackTypes a2, bool a3);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate string OrigGetLocation(Assembly self);

        [StructLayout(LayoutKind.Sequential)]
        internal struct DieCause
        {
            public bool hasValue;
            public float value;
        }

        private static System.Nullable<float> UnwrapDieCause(DieCause c)
            => c.hasValue ? new System.Nullable<float>(c.value) : new System.Nullable<float>();

        private static readonly Type[] VoidDelegateTypes =
        {
            typeof(DetourAction), typeof(DetourAction<>), typeof(DetourAction<,>), typeof(DetourAction<,,>),
            typeof(DetourAction<,,,>), typeof(DetourAction<,,,,>), typeof(DetourAction<,,,,,>)
        };

        private static readonly Type[] ReturningDelegateTypes =
        {
            typeof(DetourFunc<>), typeof(DetourFunc<,>), typeof(DetourFunc<,,>), typeof(DetourFunc<,,,>),
            typeof(DetourFunc<,,,,>), typeof(DetourFunc<,,,,,>), typeof(DetourFunc<,,,,,,>)
        };

        private sealed class OrigAdapter
        {
            public MethodInfo Target;
            public IntPtr NativeMethod;
            public IntPtr Trampoline;
            public bool InstanceCall;

            private object InvokeOrig(params object[] all)
                => NativeBridge.InvokeOrig(Target, NativeMethod, Trampoline, InstanceCall, all);

            public void F0() => InvokeOrig(Array.Empty<object>());
            public void F1<T0>(T0 a0) => InvokeOrig(a0);
            public void F2<T0, T1>(T0 a0, T1 a1) => InvokeOrig(a0, a1);
            public void F3<T0, T1, T2>(T0 a0, T1 a1, T2 a2) => InvokeOrig(a0, a1, a2);
            public void F4<T0, T1, T2, T3>(T0 a0, T1 a1, T2 a2, T3 a3) => InvokeOrig(a0, a1, a2, a3);
            public void F5<T0, T1, T2, T3, T4>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4) => InvokeOrig(a0, a1, a2, a3, a4);
            public void F6<T0, T1, T2, T3, T4, T5>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5) => InvokeOrig(a0, a1, a2, a3, a4, a5);

            public R G0<R>() => (R)InvokeOrig(Array.Empty<object>());
            public R G1<T0, R>(T0 a0) => (R)InvokeOrig(a0);
            public R G2<T0, T1, R>(T0 a0, T1 a1) => (R)InvokeOrig(a0, a1);
            public R G3<T0, T1, T2, R>(T0 a0, T1 a1, T2 a2) => (R)InvokeOrig(a0, a1, a2);
            public R G4<T0, T1, T2, T3, R>(T0 a0, T1 a1, T2 a2, T3 a3) => (R)InvokeOrig(a0, a1, a2, a3);
            public R G5<T0, T1, T2, T3, T4, R>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4) => (R)InvokeOrig(a0, a1, a2, a3, a4);
            public R G6<T0, T1, T2, T3, T4, T5, R>(T0 a0, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5) => (R)InvokeOrig(a0, a1, a2, a3, a4, a5);
        }

        private static readonly MethodInfo[] OrigForwardVoid =
        {
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F0), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F1), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F2), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F3), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F4), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F5), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F6), BindingFlags.Public | BindingFlags.Instance)
        };

        private static readonly MethodInfo[] OrigForwardReturn =
        {
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G0), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G1), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G2), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G3), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G4), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G5), BindingFlags.Public | BindingFlags.Instance),
            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G6), BindingFlags.Public | BindingFlags.Instance)
        };

        // Builds a managed orig delegate typed as the mod's generated '.orig_X' expects,
        // bound to an OrigAdapter whose body forwards every call to the native glue.
        private static Delegate CreateManagedOrigDelegate(Type origDelegateType, MethodInfo targetMethod, OrigAdapter adapter)
        {
            if (origDelegateType == null || targetMethod == null || adapter == null) return null;

            var ps = targetMethod.GetParameters();
            int arity = ps.Length + (targetMethod.IsStatic ? 0 : 1);
            if (arity > MaxArgs) return null;

            Type[] types = new Type[arity];
            int idx = 0;
            if (!targetMethod.IsStatic) types[idx++] = targetMethod.DeclaringType;
            for (int i = 0; i < ps.Length; i++) types[idx++] = ps[i].ParameterType;

            try
            {
                if (targetMethod.ReturnType == typeof(void))
                {
                    if (arity == 0)
                        return Delegate.CreateDelegate(origDelegateType, adapter,
                            typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.F0), BindingFlags.Public | BindingFlags.Instance));
                    return Delegate.CreateDelegate(origDelegateType, adapter,
                        OrigForwardVoid[arity].MakeGenericMethod(types));
                }

                if (arity == 0)
                {
                    MethodInfo g0 = typeof(OrigAdapter).GetMethod(nameof(OrigAdapter.G0), BindingFlags.Public | BindingFlags.Instance)
                        .MakeGenericMethod(new Type[] { targetMethod.ReturnType });
                    return Delegate.CreateDelegate(origDelegateType, adapter, g0);
                }

                Type[] r = new Type[arity + 1];
                Array.Copy(types, 0, r, 0, arity);
                r[arity] = targetMethod.ReturnType;
                return Delegate.CreateDelegate(origDelegateType, adapter,
                    OrigForwardReturn[arity].MakeGenericMethod(r));
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn("Could not adapt orig delegate " + origDelegateType.Name + ": " + ex.Message);
                return null;
            }
        }

        public static Type GetDelegateTypeForMethod(MethodInfo method, out string error)
        {
            error = null;
            if (method == null) { error = "null method"; return null; }

            var ps = method.GetParameters();
            int arity = ps.Length + (method.IsStatic ? 0 : 1);
            if (arity > MaxArgs) { error = "unsupported arity " + arity + " (max " + MaxArgs + ")"; return null; }

            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef) { error = "byref parameter not supported"; return null; }
                if (IsUnmarshallableType(ps[i].ParameterType)) { error = "parameter type cannot be marshalled (" + ps[i].ParameterType.Name + ")"; return null; }
            }
            if (method.ReturnType.IsByRef) { error = "byref return not supported"; return null; }
            if (IsUnmarshallableType(method.ReturnType)) { error = "return type cannot be marshalled (" + method.ReturnType.Name + ")"; return null; }
            if (!method.IsStatic && IsUnmarshallableType(method.DeclaringType)) { error = "declaring type cannot be marshalled (" + method.DeclaringType.Name + ")"; return null; }

            var typeArgs = new Type[arity];
            int idx = 0;
            if (!method.IsStatic) typeArgs[idx++] = method.DeclaringType;
            for (int i = 0; i < ps.Length; i++) typeArgs[idx++] = ps[i].ParameterType;

            try
            {
                if (method.ReturnType == typeof(void))
                    return VoidDelegateTypes[arity].MakeGenericType(typeArgs);

                var r = new Type[arity + 1];
                Array.Copy(typeArgs, 0, r, 0, arity);
                r[arity] = method.ReturnType;
                return ReturningDelegateTypes[arity].MakeGenericType(r);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static Type GetDelegateTypeForMethod(MethodInfo method)
        {
            return GetDelegateTypeForMethod(method, out _);
        }

        private static bool HasNullableParameter(MethodInfo m)
        {
            if (m == null) return false;
            foreach (ParameterInfo p in m.GetParameters())
            {
                if (Nullable.GetUnderlyingType(p.ParameterType) != null) return true;
            }
            return false;
        }

        // Builds the delegate type for a method whose Nullable<T> params are flattened to DieCause
        private static Type GetFlattenDelegateTypeForMethod(MethodInfo method, out string error)
        {
            error = null;
            if (method == null) { error = "null method"; return null; }
            var ps = method.GetParameters();
            int arity = ps.Length + (method.IsStatic ? 0 : 1);
            if (arity > MaxArgs) { error = "unsupported arity " + arity; return null; }
            var typeArgs = new Type[arity];
            int idx = 0;
            if (!method.IsStatic) typeArgs[idx++] = method.DeclaringType;
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (Nullable.GetUnderlyingType(t) == typeof(float)) t = typeof(DieCause);
                typeArgs[idx++] = t;
            }
            try
            {
                if (method.ReturnType == typeof(void))
                    return VoidDelegateTypes[arity].MakeGenericType(typeArgs);
                var r = new Type[arity + 1];
                Array.Copy(typeArgs, 0, r, 0, arity);
                r[arity] = method.ReturnType;
                return ReturningDelegateTypes[arity].MakeGenericType(r);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private sealed class Slot0 { }
        private sealed class Slot1 { }
        private sealed class Slot2 { }
        private sealed class Slot3 { }
        private sealed class Slot4 { }
        private sealed class Slot5 { }
        private sealed class Slot6 { }
        private sealed class Slot7 { }
        private sealed class Slot8 { }
        private sealed class Slot9 { }
        private sealed class Slot10 { }
        private sealed class Slot11 { }
        private sealed class Slot12 { }
        private sealed class Slot13 { }
        private sealed class Slot14 { }
        private sealed class Slot15 { }
        private sealed class Slot16 { }
        private sealed class Slot17 { }
        private sealed class Slot18 { }
        private sealed class Slot19 { }
        private sealed class Slot20 { }
        private sealed class Slot21 { }
        private sealed class Slot22 { }
        private sealed class Slot23 { }
        private sealed class Slot24 { }
        private sealed class Slot25 { }
        private sealed class Slot26 { }
        private sealed class Slot27 { }
        private sealed class Slot28 { }
        private sealed class Slot29 { }
        private sealed class Slot30 { }
        private sealed class Slot31 { }

        private static readonly Type[] SlotTypes =
        {
            typeof(Slot0), typeof(Slot1), typeof(Slot2), typeof(Slot3), typeof(Slot4), typeof(Slot5),
            typeof(Slot6), typeof(Slot7), typeof(Slot8), typeof(Slot9), typeof(Slot10), typeof(Slot11),
            typeof(Slot12), typeof(Slot13), typeof(Slot14), typeof(Slot15), typeof(Slot16), typeof(Slot17),
            typeof(Slot18), typeof(Slot19), typeof(Slot20), typeof(Slot21), typeof(Slot22), typeof(Slot23),
            typeof(Slot24), typeof(Slot25), typeof(Slot26), typeof(Slot27), typeof(Slot28), typeof(Slot29),
            typeof(Slot30), typeof(Slot31)
        };

        private static Type AllocateSlot(Delegate replacement)
        {
            Type slot = SlotTypes[_slotCursor++ % SlotTypes.Length];
            BridgeStates[slot] = new BridgeState { Replacement = replacement };
            return slot;
        }

        private static void SetSlotOrig(Type slot, Delegate orig)
        {
            if (BridgeStates.TryGetValue(slot, out BridgeState st)) st.Orig = orig;
        }

        private static void ReleaseSlot(Type slot)
        {
            BridgeStates.TryRemove(slot, out _);
        }

        private static readonly ConcurrentDictionary<Type, bool> _bridgeInvokeLogged = new ConcurrentDictionary<Type, bool>();

        private static void LogBridgeFirstInvoke(Type slot)
        {
            if (_bridgeInvokeLogged.ContainsKey(slot)) return;
            if (_bridgeInvokeLogged.TryAdd(slot, true))
            {
                string target = "?";
                if (BridgeStates.TryGetValue(slot, out BridgeState bst) && bst.Replacement?.Method != null)
                {
                    try { target = bst.Replacement.Method.DeclaringType?.Name + "." + bst.Replacement.Method.Name; } catch { }
                }
                Logger.APILogger.Log("Bridge invoked: slot=" + slot.Name + " target=" + target);
            }
        }

        internal static void InvokeBridge<TSlot>(object[] args)
        {
            LogBridgeFirstInvoke(typeof(TSlot));
            if (!BridgeStates.TryGetValue(typeof(TSlot), out BridgeState st) || st.Orig == null)
                return;

            int argLen = args != null ? args.Length : 0;
            object[] full = new object[argLen + 1];
            full[0] = st.Orig;
            if (argLen > 0)
            {
                Array.Copy(args, 0, full, 1, argLen);
            }

            lock (st.Handlers)
            {
                if (st.Handlers.Count > 0)
                {
                    for (int i = 0; i < st.Handlers.Count; i++)
                    {
                        try
                        {
                            st.Handlers[i].DynamicInvoke(full);
                        }
                        catch (Exception ex)
                        {
                            Logger.APILogger.LogError("DetourBridge hook invocation error: " + ex);
                        }
                    }
                    return;
                }
            }

            if (st.Replacement != null)
            {
                try { st.Replacement.DynamicInvoke(full); }
                catch (Exception ex) { Logger.APILogger.LogError("DetourBridge hook invocation error: " + ex); }
            }
        }

        internal static R InvokeBridgeR<R, TSlot>(object[] args)
        {
            LogBridgeFirstInvoke(typeof(TSlot));
            if (!BridgeStates.TryGetValue(typeof(TSlot), out BridgeState st) || st.Orig == null)
                return default;

            int argLen = args != null ? args.Length : 0;
            object[] full = new object[argLen + 1];
            full[0] = st.Orig;
            if (argLen > 0)
            {
                Array.Copy(args, 0, full, 1, argLen);
            }

            R result = default;
            bool invoked = false;

            try
            {
                lock (st.Handlers)
                {
                    if (st.Handlers.Count > 0)
                    {
                        for (int i = 0; i < st.Handlers.Count; i++)
                        {
                            object r = st.Handlers[i].DynamicInvoke(full);
                            if (r is R rr) { result = rr; invoked = true; }
                        }
                    }
                    else if (st.Replacement != null)
                    {
                        object r = st.Replacement.DynamicInvoke(full);
                        if (r is R rr) { result = rr; invoked = true; }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("DetourBridge hook invocation error: " + ex);
            }

            return invoked ? result : default;
        }

        private static void BridgeV0<TSlot>() => InvokeBridge<TSlot>(Array.Empty<object>());
        private static void BridgeV1<TSlot, A0>(A0 a0) => InvokeBridge<TSlot>(new object[] { a0 });
        private static void BridgeV2<TSlot, A0, A1>(A0 a0, A1 a1) => InvokeBridge<TSlot>(new object[] { a0, a1 });
        private static void BridgeV3<TSlot, A0, A1, A2>(A0 a0, A1 a1, A2 a2) => InvokeBridge<TSlot>(new object[] { a0, a1, a2 });
        private static void BridgeV4<TSlot, A0, A1, A2, A3>(A0 a0, A1 a1, A2 a2, A3 a3) => InvokeBridge<TSlot>(new object[] { a0, a1, a2, a3 });
        private static void BridgeV5<TSlot, A0, A1, A2, A3, A4>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4) => InvokeBridge<TSlot>(new object[] { a0, a1, a2, a3, a4 });
        private static void BridgeV6<TSlot, A0, A1, A2, A3, A4, A5>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5) => InvokeBridge<TSlot>(new object[] { a0, a1, a2, a3, a4, a5 });

        private static R BridgeR0<TSlot, R>() => InvokeBridgeR<R, TSlot>(Array.Empty<object>());
        private static R BridgeR1<TSlot, A0, R>(A0 a0) => InvokeBridgeR<R, TSlot>(new object[] { a0 });
        private static R BridgeR2<TSlot, A0, A1, R>(A0 a0, A1 a1) => InvokeBridgeR<R, TSlot>(new object[] { a0, a1 });
        private static R BridgeR3<TSlot, A0, A1, A2, R>(A0 a0, A1 a1, A2 a2) => InvokeBridgeR<R, TSlot>(new object[] { a0, a1, a2 });
        private static R BridgeR4<TSlot, A0, A1, A2, A3, R>(A0 a0, A1 a1, A2 a2, A3 a3) => InvokeBridgeR<R, TSlot>(new object[] { a0, a1, a2, a3 });
        private static R BridgeR5<TSlot, A0, A1, A2, A3, A4, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4) => InvokeBridgeR<R, TSlot>(new object[] { a0, a1, a2, a3, a4 });
        private static R BridgeR6<TSlot, A0, A1, A2, A3, A4, A5, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5) => InvokeBridgeR<R, TSlot>(new object[] { a0, a1, a2, a3, a4, a5 });

        private static readonly MethodInfo[] VoidBridges =
        {
            typeof(DetourBridge).GetMethod(nameof(BridgeV0), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV1), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV2), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV3), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV4), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV5), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeV6), BindingFlags.NonPublic | BindingFlags.Static)
        };

        private static readonly MethodInfo[] ReturningBridges =
        {
            typeof(DetourBridge).GetMethod(nameof(BridgeR0), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR1), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR2), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR3), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR4), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR5), BindingFlags.NonPublic | BindingFlags.Static),
            typeof(DetourBridge).GetMethod(nameof(BridgeR6), BindingFlags.NonPublic | BindingFlags.Static)
        };

        // Generic bridges built at runtime via MakeGenericMethod never get a wrapper,
        // so we must provide one concrete annotated method per target signature.
        private sealed class LocationSlot { }

        private static readonly MethodInfo LocationBridgeMethod =
            typeof(DetourBridge).GetMethod(nameof(BridgeGetLocation), BindingFlags.NonPublic | BindingFlags.Static);

        [AOT.MonoPInvokeCallback(typeof(Func<Assembly, string>))]
        private static string BridgeGetLocation(Assembly self)
        {
            if (BridgeStates.TryGetValue(typeof(LocationSlot), out BridgeState st) && st.Orig != null)
            {
                string result = null;
                foreach (Delegate h in st.Handlers)
                {
                    try
                    {
                        object r = h.DynamicInvoke(st.Orig, self);
                        if (r is string s) result = s;
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogError("Location hook invocation error: " + ex);
                    }
                }
                if (result != null) return result;
            }

            if (self != null && NativeCompat.TryGetAssemblyPath(self, out string mapped) && !string.IsNullOrEmpty(mapped))
            {
                return mapped;
            }

            try { return self != null ? self.Location : string.Empty; }
            catch { return string.Empty; }
        }

        // Generic installer for a concrete reverse-pinvoke bridge. <paramref name="concreteBridge"/>
        // must be a non-generic static method annotated with [AOT.MonoPInvokeCallback(typeof(delegateType))]
        public static bool TryInstallConcreteDetour(MethodInfo target, Type slotType, Type delegateType, Type origType,
            MethodInfo concreteBridge, Delegate replacement, out Delegate orig, out string error)
        {
            orig = null;
            error = null;

            BridgeStates[slotType] = new BridgeState { Handlers = new List<Delegate> { replacement } };

            IntPtr targetAddr = GetNativeMethodAddress(target);
            if (targetAddr == IntPtr.Zero) { error = "cannot get target address."; return false; }

            Delegate bridgeDel;
            IntPtr bridgeAddr;
            try
            {
                bridgeDel = Delegate.CreateDelegate(delegateType, null, concreteBridge);
                bridgeAddr = Marshal.GetFunctionPointerForDelegate(bridgeDel);
            }
            catch (Exception ex)
            {
                error = "bridge delegate/pointer: " + ex.Message;
                return false;
            }
            if (bridgeAddr == IntPtr.Zero) { error = "bridge delegate has no native function pointer."; return false; }
            if (BridgeStates.TryGetValue(slotType, out BridgeState bs)) bs.Bridge = bridgeDel;

            if (targetAddr == bridgeAddr)
            {
                BridgeStates.TryRemove(slotType, out _);
                error = "target and bridge share the same native address.";
                return false;
            }

            IntPtr trampPtr = IntPtr.Zero;
            try { DobbyHookNative(targetAddr, bridgeAddr, out trampPtr); }
            catch (Exception ex) { error = "DobbyHook: " + ex.Message; return false; }
            if (trampPtr == IntPtr.Zero)
            {
                TryUnhook(targetAddr);
                error = "DobbyHook returned null trampoline.";
                return false;
            }

            Type origParamType = replacement?.Method?.GetParameters().Length > 0
                ? replacement.Method.GetParameters()[0].ParameterType
                : delegateType;

            IntPtr nativeMethod = Il2CppResolver.TryGetMethodInfoPointer(target);
            if (nativeMethod == IntPtr.Zero)
            {
                TryUnhook(targetAddr);
                error = "orig native thunk: no il2cpp MethodInfo available for " + target.Name + ".";
                return false;
            }

            var adapter = new OrigAdapter
            {
                Target = target,
                NativeMethod = nativeMethod,
                Trampoline = trampPtr,
                InstanceCall = !target.IsStatic
            };
            orig = CreateManagedOrigDelegate(origParamType, target, adapter);
            if (orig == null)
            {
                TryUnhook(targetAddr);
                error = "orig delegate: could not adapt to " + (origParamType?.Name ?? "?") +
                        " (managed forwarding unsupported).";
                return false;
            }

            if (BridgeStates.TryGetValue(slotType, out BridgeState s2)) { s2.Orig = orig; s2.Bridge = bridgeDel; }
            Logger.APILogger.LogDebug("Concrete detour installed for " + target.Name +
                " (bridge 0x" + bridgeAddr.ToInt64().ToString("X") + ").");
            return true;
        }

        public static void AddHandlerFor(MethodInfo target, Delegate handler)
        {
            if (target == null || handler == null) return;
            if (Installed.TryGetValue(target, out InstalledHook hk) &&
                BridgeStates.TryGetValue(hk.Slot, out BridgeState st))
            {
                lock (st.Handlers) st.Handlers.Add(handler);
            }
            else
            {
                Logger.APILogger.LogWarn("AddHandlerFor: no installed hook for " + target?.DeclaringType?.Name + "." + target?.Name);
            }
        }

        public static bool TryInstallLocationHook(Delegate replacement, out string error)
        {
            error = null;
            MethodInfo getLocation = typeof(Assembly).GetMethod("get_Location", BindingFlags.Public | BindingFlags.Instance);
            if (getLocation == null) { error = "Assembly.get_Location not found."; return false; }
            return TryInstallConcreteDetour(
                getLocation, typeof(LocationSlot), typeof(Func<Assembly, string>),
                typeof(OrigGetLocation), LocationBridgeMethod, replacement, out _, out error);
        }

        #region concrete bridges (MonoPInvokeCallback) for game hooks routed via On.*

        // Each game hook signature needs one concrete, non-generic static method annotated with
        // [AOT.MonoPInvokeCallback(...)] so HybridCLR can emit a reverse-P/Invoke wrapper.
        private sealed class StartSlashSlot { }
        private sealed class OnDisableSlot { }
        private sealed class TakeDamageSlot { }
        private sealed class HitSlot { }
        private sealed class DieSlot { }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<NailSlash>))]
        private static void BridgeStartSlash(NailSlash a0)
        {
            InvokeBridge<StartSlashSlot>(new object[] { a0 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<GameManager>))]
        private static void BridgeOnDisable(GameManager a0)
        {
            InvokeBridge<OnDisableSlot>(new object[] { a0 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<HeroController, UnityEngine.GameObject, GlobalEnums.CollisionSide, int, int>))]
        private static void BridgeTakeDamage(HeroController a0, UnityEngine.GameObject a1, GlobalEnums.CollisionSide a2, int a3, int a4)
        {
            InvokeBridge<TakeDamageSlot>(new object[] { a0, a1, a2, a3, a4 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<HealthManager, HitInstance>))]
        private static void BridgeHit(HealthManager a0, HitInstance a1)
        {
            InvokeBridge<HitSlot>(new object[] { a0, a1 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<HealthManager, DieCause, AttackTypes, bool>))]
        private static void BridgeDie(HealthManager a0, DieCause a1, AttackTypes a2, bool a3)
        {
            InvokeBridge<DieSlot>(new object[] { a0, UnwrapDieCause(a1), a2, a3 });
        }
        #endregion

        private sealed class ConcreteBridgeInfo
        {
            public Type Slot;
            public MethodInfo Bridge;
            public Type DelegateType;
            public Type OrigType;
            public string[] RawParamTypes;
        }

        private static readonly ConcurrentDictionary<Type, List<ConcreteBridgeInfo>> ConcreteBridges =
            new ConcurrentDictionary<Type, List<ConcreteBridgeInfo>>();

        static DetourBridge()
        {
            RegisterConcreteBridge(typeof(StartSlashSlot), nameof(BridgeStartSlash), typeof(DetourAction<IntPtr>), typeof(OrigStartSlash));
            RegisterConcreteBridge(typeof(OnDisableSlot), nameof(BridgeOnDisable), typeof(DetourAction<IntPtr>), typeof(OrigOnDisable));
            RegisterConcreteBridge(typeof(TakeDamageSlot), nameof(BridgeTakeDamage), typeof(DetourAction<IntPtr, IntPtr, GlobalEnums.CollisionSide, int, int>), typeof(OrigTakeDamage));
            RegisterConcreteBridge(typeof(HitSlot), nameof(BridgeHit), typeof(DetourAction<IntPtr, HitInstance>), typeof(OrigHit));
            RegisterConcreteBridge(typeof(DieSlot), nameof(BridgeDie), typeof(DetourAction<IntPtr, DieCause, AttackTypes, bool>), typeof(OrigDie));
            try { GeneratedBridges.RegisterAll(); }
            catch (Exception ex) { Logger.APILogger.LogError("GeneratedBridges.RegisterAll failed: " + ex); }
        }

        private static void RegisterConcreteBridge(Type slotType, string bridgeMethodName, Type delegateType, Type origType)
        {
            MethodInfo bridge = typeof(DetourBridge).GetMethod(bridgeMethodName,
                BindingFlags.NonPublic | BindingFlags.Static);
            RegisterBridge(delegateType, slotType, bridge, origType, null);
        }

        public static void RegisterGeneratedBridge(Type delegateType, Type slotType, MethodInfo bridge, Type origType, string[] rawParamTypes = null)
        {
            RegisterBridge(delegateType, slotType, bridge, origType, rawParamTypes);
        }

        private static void RegisterBridge(Type delegateType, Type slotType, MethodInfo bridge, Type origType, string[] rawParamTypes)
        {
            List<ConcreteBridgeInfo> list = ConcreteBridges.GetOrAdd(delegateType, _ => new List<ConcreteBridgeInfo>());
            lock (list)
            {
                list.Add(new ConcreteBridgeInfo
                {
                    Slot = slotType,
                    Bridge = bridge,
                    DelegateType = delegateType,
                    OrigType = origType,
                    RawParamTypes = rawParamTypes
                });
            }
        }

        private static bool TryGetFreeBridge(Type delegateType, out ConcreteBridgeInfo chosen)
        {
            chosen = null;
            if (!ConcreteBridges.TryGetValue(delegateType, out List<ConcreteBridgeInfo> list))
            {
                return false;
            }

            lock (list)
            {
                foreach (ConcreteBridgeInfo cbi in list)
                {
                    if (IsSlotFree(cbi.Slot))
                    {
                        if (BridgeStates.TryAdd(cbi.Slot, new BridgeState()))
                        {
                            chosen = cbi;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsSlotFree(Type slot)
        {
            if (BridgeStates.ContainsKey(slot)) return false;
            foreach (KeyValuePair<MethodInfo, InstalledHook> kv in Installed)
            {
                if (kv.Value.Slot == slot) return false;
            }
            return true;
        }

        public static bool TryCreateOrigDetour(MethodInfo targetMethod, Delegate replacement, out Delegate trampolineDelegate, out string error)
        {
            trampolineDelegate = null;
            error = null;

            if (!_dobbyAvailable) { error = "Dobby not available."; return false; }
            if (targetMethod == null) { error = "null target"; return false; }
            if (replacement == null || replacement.Method == null) { error = "null replacement"; return false; }

            var targetParams = targetMethod.GetParameters();
            for (int i = 0; i < targetParams.Length; i++)
            {
                if (targetParams[i].ParameterType.IsByRef)
                {
                    error = "ref/out parameters are not supported for orig detours.";
                    return false;
                }
            }

            var replParams = replacement.Method.GetParameters();
            if (replParams.Length == 0) { error = "replacement has no parameters (expected orig pattern)."; return false; }
            Type origParamType = replParams[0].ParameterType;
            if (!typeof(Delegate).IsAssignableFrom(origParamType))
            {
                error = "first replacement parameter is not a delegate (expected orig pattern).";
                return false;
            }

            int nativeArity = targetParams.Length + (targetMethod.IsStatic ? 0 : 1);
            if (nativeArity > MaxArgs) { error = "too many parameters (" + nativeArity + " > " + MaxArgs + ")."; return false; }
            if (replParams.Length - 1 != nativeArity)
            {
                error = "signature mismatch (replacement arity " + (replParams.Length - 1) + " vs target " + nativeArity + ").";
                return false;
            }

#if ENABLE_IL2CPP

            string unimarsh = DescribeUnsupportedSignature(targetMethod);
            if (unimarsh != null)
            {
                error = "target signature cannot cross a reverse-P/Invoke bridge on IL2CPP: " + unimarsh +
                        ". Rework it around a nullable-free signature.";
                return false;
            }
#endif

            Type concreteDelegateType = GetDelegateTypeForMethod(targetMethod, out string concreteSigErr);
            if (concreteDelegateType != null && !HasNullableParameter(targetMethod) && TryGetFreeBridge(concreteDelegateType, out ConcreteBridgeInfo cbi))
            {
                Delegate origDelegate;
                if (!TryInstallConcreteDetour(targetMethod, cbi.Slot, concreteDelegateType, cbi.OrigType, cbi.Bridge, replacement, out origDelegate, out error))
                {
                    return false;
                }
                Installed[targetMethod] = new InstalledHook { Target = targetMethod, Slot = cbi.Slot, Orig = origDelegate };
                trampolineDelegate = origDelegate;
                return true;
            }

            if (HasNullableParameter(targetMethod) &&
                GetFlattenDelegateTypeForMethod(targetMethod, out _) is Type flatDelegateType &&
                flatDelegateType != null && TryGetFreeBridge(flatDelegateType, out ConcreteBridgeInfo flatCbi))
            {
                Delegate origDelegate2;
                if (!TryInstallConcreteDetour(targetMethod, flatCbi.Slot, flatDelegateType, flatCbi.OrigType, flatCbi.Bridge, replacement, out origDelegate2, out error))
                {
                    return false;
                }
                Installed[targetMethod] = new InstalledHook { Target = targetMethod, Slot = flatCbi.Slot, Orig = origDelegate2 };
                trampolineDelegate = origDelegate2;
                return true;
            }

            // Reject target signatures that would build an unmarshallable bridge.
            for (int i = 0; i < targetParams.Length; i++)
            {
                if (IsUnmarshallableType(targetParams[i].ParameterType))
                {
                    error = "target has unmarshallable parameter type.";
                    return false;
                }
            }
            if (IsUnmarshallableType(targetMethod.ReturnType))
            {
                error = "target has unmarshallable return type.";
                return false;
            }
            if (!targetMethod.IsStatic && IsUnmarshallableType(targetMethod.DeclaringType))
            {
                error = "target declaring type is unmarshallable.";
                return false;
            }

            Logger.APILogger.LogWarn("No concrete AOT bridge for " + targetMethod.DeclaringType?.Name + ".");

#if ENABLE_IL2CPP
            {
                string gate = DescribeUnsupportedSignature(targetMethod);

                bool hasNullable = false;
                foreach (ParameterInfo p in targetParams)
                {
                    if (Nullable.GetUnderlyingType(p.ParameterType) != null) { hasNullable = true; break; }
                }
                if (gate != null || hasNullable)
                {
                    error = "No AOT bridge + generic fallback rejected.";
                    return false;
                }
            }
#endif

            Type slot = AllocateSlot(replacement);

            int extra = targetMethod.ReturnType == typeof(void) ? 1 : 2;
            Type[] typeArgs = new Type[nativeArity + extra];
            typeArgs[0] = slot;
            int idx = 1;
            if (!targetMethod.IsStatic) typeArgs[idx++] = targetMethod.DeclaringType;
            for (int i = 0; i < targetParams.Length; i++) typeArgs[idx++] = targetParams[i].ParameterType;
            if (targetMethod.ReturnType != typeof(void)) typeArgs[idx] = targetMethod.ReturnType;

            MethodInfo bridge;
            try
            {
                bridge = targetMethod.ReturnType == typeof(void)
                    ? VoidBridges[nativeArity].MakeGenericMethod(typeArgs)
                    : ReturningBridges[nativeArity].MakeGenericMethod(typeArgs);
            }
            catch (Exception ex)
            {
                ReleaseSlot(slot);
                error = "failed to build bridge: " + ex.Message;
                return false;
            }

            IntPtr targetAddr = GetNativeMethodAddress(targetMethod);
            if (targetAddr == IntPtr.Zero) { ReleaseSlot(slot); error = "cannot get target address."; return false; }

            string bridgeSigErr;
            Type targetDelegateType = GetDelegateTypeForMethod(targetMethod, out bridgeSigErr);
            if (targetDelegateType == null)
            {
                ReleaseSlot(slot);
                error = "cannot build target delegate type: " + bridgeSigErr;
                return false;
            }

            IntPtr bridgeAddr;
            Delegate bridgeDel;
            try
            {
                bridgeDel = Delegate.CreateDelegate(targetDelegateType, null, bridge);
                bridgeAddr = Marshal.GetFunctionPointerForDelegate(bridgeDel);
            }
            catch (Exception ex)
            {
                ReleaseSlot(slot);
                error = "failed to build bridge delegate/pointer: " + ex.Message;
                return false;
            }
            if (bridgeAddr == IntPtr.Zero)
            {
                ReleaseSlot(slot);
                error = "bridge delegate has no native function pointer.";
                return false;
            }
            if (BridgeStates.TryGetValue(slot, out BridgeState bs)) bs.Bridge = bridgeDel;
            Logger.APILogger.LogDebug("Bridge native thunk for " + targetMethod.Name + " = 0x" +
                bridgeAddr.ToInt64().ToString("X") + " (delegate " + targetDelegateType.Name + ")");

            if (targetAddr == bridgeAddr)
            {
                ReleaseSlot(slot);
                error = "target and bridge share the same native address.";
                return false;
            }

            IntPtr trampPtr;
            try
            {
                DobbyHookNative(targetAddr, bridgeAddr, out trampPtr);
            }
            catch (Exception ex)
            {
                ReleaseSlot(slot);
                error = "DobbyHook failed: " + ex.Message;
                return false;
            }
            if (trampPtr == IntPtr.Zero)
            {
                ReleaseSlot(slot);
                TryUnhook(targetAddr);
                error = "DobbyHook returned null trampoline.";
                return false;
            }

            IntPtr nativeMethod = Il2CppResolver.TryGetMethodInfoPointer(targetMethod);
            if (nativeMethod == IntPtr.Zero)
            {
                ReleaseSlot(slot);
                TryUnhook(targetAddr);
                error = "no il2cpp MethodInfo available for " + targetMethod.Name + ".";
                return false;
            }

            var genericAdapter = new OrigAdapter
            {
                Target = targetMethod,
                NativeMethod = nativeMethod,
                Trampoline = trampPtr,
                InstanceCall = !targetMethod.IsStatic
            };
            Delegate orig = CreateManagedOrigDelegate(origParamType, targetMethod, genericAdapter);
            if (orig == null)
            {
                ReleaseSlot(slot);
                TryUnhook(targetAddr);
                error = "failed to create orig delegate.";
                return false;
            }

            SetSlotOrig(slot, orig);
            Installed[targetMethod] = new InstalledHook { Target = targetMethod, Slot = slot, Orig = orig };
            trampolineDelegate = orig;
            return true;
        }

        public static Delegate CreateDetour(MethodInfo targetMethod, MethodInfo replacementMethod)
        {
            if (!_dobbyAvailable)
            {
                Logger.APILogger.LogWarn("Cannot create detour.");
                return null;
            }
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
            if (replacementMethod == null) throw new ArgumentNullException(nameof(replacementMethod));
            if (!replacementMethod.IsStatic)
            {
                Logger.APILogger.LogWarn("Replacement " + replacementMethod.Name + " must be static for direct detours.");
                return null;
            }

            Type targetDelegateType = GetDelegateTypeForMethod(targetMethod, out string sigErr);
            if (targetDelegateType == null)
            {
                Logger.APILogger.LogWarn("Unsupported target signature for " + targetMethod.Name + ": " + sigErr);
                return null;
            }

            IntPtr targetAddr = GetNativeMethodAddress(targetMethod);
            if (targetAddr == IntPtr.Zero) return null;
            IntPtr replAddr = GetNativeMethodAddress(replacementMethod);
            if (replAddr == IntPtr.Zero) return null;

            if (targetAddr == replAddr)
            {
                Logger.APILogger.LogWarn("Cannot create detour for " + targetMethod.Name +
                    ": target and replacement share the same native address (0x" +
                    targetAddr.ToInt64().ToString("X") + "). Refusing to hook.");
                return null;
            }

            IntPtr trampPtr;
            try
            {
                DobbyHookNative(targetAddr, replAddr, out trampPtr);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("DobbyHook failed for " + targetMethod.Name + ": " + ex.Message);
                return null;
            }
            if (trampPtr == IntPtr.Zero)
            {
                Logger.APILogger.LogError("DobbyHook returned null trampoline for " + targetMethod.Name);
                return null;
            }

            Delegate tramp;
            try
            {
                tramp = Marshal.GetDelegateForFunctionPointer(trampPtr, targetDelegateType);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to create trampoline delegate for " + targetMethod.Name + ": " + ex.Message);
                return null;
            }

            Installed[targetMethod] = new InstalledHook { Target = targetMethod, Orig = tramp };
            return tramp;
        }

        public static bool RemoveDetour(MethodInfo targetMethod)
        {
            if (!_dobbyAvailable) return false;
            if (targetMethod == null) return false;

            if (!Installed.TryRemove(targetMethod, out InstalledHook info)) return false;

            IntPtr addr = GetNativeMethodAddress(targetMethod);
            if (addr != IntPtr.Zero)
            {
                try { DobbyUnhookNative(addr); }
                catch (Exception ex) { Logger.APILogger.LogWarn("DobbyUnhook failed: " + ex.Message); }
            }

            if (info.Slot != null)
            {
                BridgeStates.TryRemove(info.Slot, out _);
                ReleaseSlot(info.Slot);
            }
            return true;
        }
    }
}
