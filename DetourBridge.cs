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

        [ThreadStatic]
        private static Dictionary<MethodInfo, IntPtr>
    _currentNativeSelf;

        private static IntPtr PushNativeSelf(
        MethodInfo method,
        IntPtr self,
        out bool hadPrevious,
        out IntPtr previous)
        {
            hadPrevious = false;
            previous = IntPtr.Zero;

            if (method == null ||
                self == IntPtr.Zero)
            {
                return self;
            }

            if (_currentNativeSelf == null)
            {
                _currentNativeSelf =
                    new Dictionary<MethodInfo, IntPtr>();
            }

            if (_currentNativeSelf.TryGetValue(
                method,
                out previous))
            {
                hadPrevious = true;
            }

            _currentNativeSelf[method] =
                self;

            return self;
        }

        private static void PopNativeSelf(
            MethodInfo method,
            bool hadPrevious,
            IntPtr previous)
        {
            if (_currentNativeSelf == null ||
                method == null)
            {
                return;
            }

            if (hadPrevious)
            {
                _currentNativeSelf[method] =
                    previous;
            }
            else
            {
                _currentNativeSelf.Remove(method);
            }
        }

        internal static IntPtr GetCurrentNativeSelf(
            MethodInfo method)
        {
            if (method == null ||
                _currentNativeSelf == null)
            {
                return IntPtr.Zero;
            }

            IntPtr value;

            return _currentNativeSelf.TryGetValue(
                method,
                out value)
                ? value
                : IntPtr.Zero;
        }

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
            public int[] RefIndexes;

            public MethodInfo Target;
            public IntPtr NativeMethod;
            public IntPtr Trampoline;
            public bool InstanceCall;
            public Type OrigParamType;
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
                    // Logger.APILogger.LogDebug("GameManager.Awake lookup error: " + ex.Message);
                }
                // Logger.APILogger.Log(
                //     "GameManager.Awake GetFunctionPointer=0x" + gmFp.ToInt64().ToString("X") +
                //     " resolved=0x" + gmAddr.ToInt64().ToString("X"));

                // Logger.APILogger.Log($"Native addresses ta=0x{ta.ToInt64():X} ra=0x{ra.ToInt64():X}");

                if (ta == ra)
                {
                    // Logger.APILogger.LogWarn("Target and replacement share the same native address (0x" +
                    //     ta.ToInt64().ToString("X") + "); Dobby disabled.");
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
            if (method == null)
                return IntPtr.Zero;

            try
            {
                IntPtr ptr =
                    Il2CppResolver.TryGetMethodPointer(method);

                if (ptr != IntPtr.Zero)
                {
                    Logger.APILogger.LogDebug(
                        "IL2CPP resolver returned native address 0x" +
                        ptr.ToInt64().ToString("X") +
                        " for " +
                        method.DeclaringType?.Name +
                        "." +
                        method.Name);

                    return ptr;
                }

                Logger.APILogger.LogDebug(
                    "IL2CPP resolver could not resolve native address for " +
                    method.DeclaringType?.Name +
                    "." +
                    method.Name);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogDebug(
                    "IL2CPP method resolver failed for " +
                    method.Name +
                    ": " +
                    ex.Message);
            }

            try
            {
                IntPtr fn =
                    method.MethodHandle.GetFunctionPointer();

                if (fn != IntPtr.Zero)
                {
                    Logger.APILogger.LogDebug(
                        "Using MethodHandle fallback for " +
                        method.DeclaringType?.Name +
                        "." +
                        method.Name +
                        ": 0x" +
                        fn.ToInt64().ToString("X"));

                    return fn;
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogDebug(
                    "GetFunctionPointer threw for " +
                    method.Name +
                    ": " +
                    ex.Message);
            }

            Logger.APILogger.LogDebug(
                "Could not obtain native address for " +
                method.DeclaringType?.Name +
                "." +
                method.Name);

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

        private static Type GetManagedBridgeArgumentType(
    MethodInfo method,
    int bridgeIndex)
        {
            if (method == null)
            {
                return null;
            }

            if (!method.IsStatic &&
                bridgeIndex == 0)
            {
                return method.DeclaringType;
            }

            int parameterIndex =
                bridgeIndex -
                (method.IsStatic ? 0 : 1);

            ParameterInfo[] parameters =
                method.GetParameters();

            if (parameterIndex < 0 ||
                parameterIndex >= parameters.Length)
            {
                return null;
            }

            return parameters[parameterIndex]
                .ParameterType;
        }

        internal static Type GetManagedDelegateTypeForMethod(
    MethodInfo method,
    out string error)
        {
            error = null;

            if (method == null)
            {
                error = "null method";
                return null;
            }

            int arity =
                method.GetParameters().Length +
                (method.IsStatic ? 0 : 1);

            if (arity > MaxArgs)
            {
                error =
                    "unsupported arity " +
                    arity +
                    " (max " +
                    MaxArgs +
                    ")";

                return null;
            }

            Type[] typeArgs =
                new Type[arity];

            int index = 0;

            if (!method.IsStatic)
            {
                typeArgs[index++] =
                    method.DeclaringType;
            }

            foreach (ParameterInfo parameter
                     in method.GetParameters())
            {
                if (parameter.ParameterType.IsByRef)
                {
                    error =
                        "byref parameter not supported";

                    return null;
                }

                typeArgs[index++] =
                    parameter.ParameterType;
            }

            try
            {
                if (method.ReturnType == typeof(void))
                {
                    return VoidDelegateTypes[arity]
                        .MakeGenericType(typeArgs);
                }

                Type[] returnArgs =
                    new Type[arity + 1];

                Array.Copy(
                    typeArgs,
                    returnArgs,
                    arity);

                returnArgs[arity] =
                    method.ReturnType;

                return ReturningDelegateTypes[arity]
                    .MakeGenericType(returnArgs);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        internal static Type GetManagedDelegateTypeForMethod(
            MethodInfo method)
        {
            return GetManagedDelegateTypeForMethod(
                method,
                out _);
        }

        private static bool IsRefTypeForBridge(Type t)
        {
            if (t == null) return false;
            if (t.IsPrimitive || t.IsEnum) return false;
            if (t == typeof(string) || t == typeof(IntPtr) || t == typeof(UIntPtr)) return false;
            if (t.IsValueType) return false;
            return true;
        }

        private static Type BuildPtrDelegateTypeForMethod(MethodInfo method, out int[] refIndexes, out string error)
        {
            error = null;
            refIndexes = null;
            if (method == null) { error = "null method"; return null; }
            var ps = method.GetParameters();
            int arity = ps.Length + (method.IsStatic ? 0 : 1);
            if (arity > MaxArgs) { error = "unsupported arity " + arity; return null; }

            var typeArgs = new Type[arity];
            var refs = new List<int>();
            int idx = 0;
            if (!method.IsStatic)
            {
                Type t = method.DeclaringType;
                if (IsRefTypeForBridge(t)) { typeArgs[idx] = typeof(IntPtr); refs.Add(idx); }
                else typeArgs[idx] = t;
                idx++;
            }
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t.IsByRef) { error = "byref parameter not supported"; return null; }
                if (Nullable.GetUnderlyingType(t) == typeof(float)) t = typeof(DieCause);
                if (IsRefTypeForBridge(t)) { typeArgs[idx] = typeof(IntPtr); refs.Add(idx); }
                else typeArgs[idx] = t;
                idx++;
            }
            if (refs.Count > 0) refIndexes = refs.ToArray();

            try
            {
                if (method.ReturnType == typeof(void))
                    return VoidDelegateTypes[arity].MakeGenericType(typeArgs);
                var r = new Type[arity + 1];
                Array.Copy(typeArgs, 0, r, 0, arity);
                r[arity] = IsRefTypeForBridge(method.ReturnType) ? typeof(IntPtr) : method.ReturnType;
                return ReturningDelegateTypes[arity].MakeGenericType(r);
            }
            catch (Exception ex) { error = ex.Message; return null; }
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

            IntPtr rawSelf = IntPtr.Zero;
            bool hadPreviousSelf = false;
            IntPtr previousSelf = IntPtr.Zero;

            if (st.InstanceCall && args != null && args.Length > 0 && args[0] is IntPtr)
            {
                rawSelf = (IntPtr)args[0];

                PushNativeSelf(
                    st.Target,
                    rawSelf,
                    out hadPreviousSelf,
                    out previousSelf);

                Logger.APILogger.LogDebug(
                    "Native self context: " +
                    st.Target.Name +
                    " -> 0x" +
                    rawSelf.ToInt64().ToString("X"));
            }

            try
            {
                int argLen = args != null ? args.Length : 0;
                if (argLen > 0 && st.RefIndexes != null)
                {
                    for (int i = 0; i < st.RefIndexes.Length; i++)
                    {
                        int ri = st.RefIndexes[i];
                        if (ri >= 0 && ri < argLen && args[ri] is IntPtr p)
                        {
                            try
                            {
                                Type expectedType =
                                GetManagedBridgeArgumentType(
                                st.Target,
                                ri);

                                object managedObject =
                                    p == IntPtr.Zero
                                        ? null
                                        : NativeBridge.FromObjectPtr(
                                            p,
                                            expectedType);

                                args[ri] =
                                    managedObject;

                                if (ri == 0 &&
                                    st.InstanceCall)
                                {
                                    Logger.APILogger.LogDebug(
                                        "Self conversion: " +
                                        st.Target.DeclaringType?.FullName +
                                        " -> " +
                                        (managedObject == null
                                            ? "<NULL>"
                                            : managedObject.GetType().FullName));
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.APILogger.LogError($"Failed to convert pointer in argument {ri}: {ex.Message}");
                                args[ri] = null;
                            }
                        }
                    }
                }

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
            finally
            {
                PopNativeSelf(
                    st.Target,
                    hadPreviousSelf,
                    previousSelf);
            }
        }


        private static void TryRebuildOrig(BridgeState st)
        {
            if (st.Orig != null || st.Trampoline == IntPtr.Zero ||
                st.NativeMethod == IntPtr.Zero || st.Target == null)
                return;

            try
            {
                var adapter = new OrigAdapter
                {
                    Target = st.Target,
                    NativeMethod = st.NativeMethod,
                    Trampoline = st.Trampoline,
                    InstanceCall = st.InstanceCall
                };
                Type pt = st.OrigParamType;
                if (pt == null && st.Replacement != null)
                {
                    var parms = st.Replacement.Method?.GetParameters();
                    if (parms != null && parms.Length > 0)
                        pt = parms[0].ParameterType;
                }
                st.Orig = CreateManagedOrigDelegate(pt, st.Target, adapter);
                Logger.APILogger.LogDebug("Rebuilt lost Orig for " + st.Target.Name);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Orig rebuild failed for " +
                    (st.Target?.Name ?? "?") + ": " + ex);
            }
        }

        internal static R InvokeBridgeR<R, TSlot>(object[] args)
        {
            LogBridgeFirstInvoke(typeof(TSlot));

            if (!BridgeStates.TryGetValue(typeof(TSlot), out BridgeState st))
                return default;

            TryRebuildOrig(st);

            if (st.Orig == null)
            {
                Logger.APILogger.LogError(
                    "DetourBridge: " + typeof(TSlot).Name +
                    " has no Orig and cannot recover; returning null. " +
                    "(Target=" + (st.Target?.Name ?? "?") +
                    " Trampoline=0x" + st.Trampoline.ToInt64().ToString("X") +
                    " Handlers=" + (st.Handlers?.Count ?? 0) + ")");
                return default;
            }

            IntPtr rawSelf = IntPtr.Zero;
            bool hadPreviousSelf = false;
            IntPtr previousSelf = IntPtr.Zero;

            if (st.InstanceCall && args != null && args.Length > 0 && args[0] is IntPtr)
            {
                rawSelf = (IntPtr)args[0];

                PushNativeSelf(
                    st.Target,
                    rawSelf,
                    out hadPreviousSelf,
                    out previousSelf);

                Logger.APILogger.LogDebug(
                    "Native self context: " +
                    st.Target.Name +
                    " -> 0x" +
                    rawSelf.ToInt64().ToString("X"));
            }

            try
            {
                int argLen = args != null ? args.Length: 0;

                if (argLen > 0 &&
                    st.RefIndexes != null)
                {
                    for (int i = 0;
                         i < st.RefIndexes.Length;
                         i++)
                    {
                        int ri =
                            st.RefIndexes[i];

                        if (ri < 0 ||
                            ri >= argLen)
                        {
                            continue;
                        }

                        if (!(args[ri] is IntPtr))
                        {
                            continue;
                        }

                        IntPtr p =
                            (IntPtr)args[ri];

                        try
                        {
                            Type expectedType =
                                GetManagedBridgeArgumentType(
                                    st.Target,
                                    ri);

                            object managedObject =
                                p == IntPtr.Zero
                                    ? null
                                    : NativeBridge.FromObjectPtr(
                                        p,
                                        expectedType);

                            args[ri] =
                                managedObject;

                            if (ri == 0 &&
                                st.InstanceCall)
                            {
                                Logger.APILogger.LogDebug(
                                    "Self conversion: " +
                                    st.Target.DeclaringType?.FullName +
                                    " -> " +
                                    (managedObject == null
                                        ? "<NULL>"
                                        : managedObject.GetType().FullName));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.APILogger.LogError(
                                "Failed to convert pointer in argument " +
                                ri +
                                ": " +
                                ex.Message);

                            args[ri] = null;
                        }
                    }
                }
                object[] full = new object[argLen + 1];
                full[0] = st.Orig;
                if (argLen > 0)
                {
                    Array.Copy(args, 0, full, 1, argLen);
                }

                R result = default;
                bool invoked = false;

                lock (st.Handlers)
                {
                    if (st.Handlers.Count > 0)
                    {
                        for (int i = 0; i < st.Handlers.Count; i++)
                        {
                            try
                            {
                                object r = st.Handlers[i].DynamicInvoke(full);
                                if (r is R rr) { result = rr; invoked = true; }
                            }
                            catch (Exception ex)
                            {
                                Logger.APILogger.LogError(
                                    "DetourBridge hook invocation error [" +
                                    typeof(TSlot).Name + " #" + i + "]: " + ex);
                            }
                        }
                    }
                    else if (st.Replacement != null)
                    {
                        try
                        {
                            object r = st.Replacement.DynamicInvoke(full);
                            if (r is R rr) { result = rr; invoked = true; }
                        }
                        catch (Exception ex)
                        {
                            Logger.APILogger.LogError(
                                "DetourBridge hook invocation error [" +
                                typeof(TSlot).Name + " replacement]: " + ex);
                        }
                    }
                }

                return invoked ? result : default;
            }
            finally
            {
                PopNativeSelf(
                    st.Target,
                    hadPreviousSelf,
                    previousSelf);
            }
        }


        internal static IntPtr InvokeBridgePtr<TSlot>(object[] args)
        {
            object result = InvokeBridgeR<object, TSlot>(args);
            return result == null ? IntPtr.Zero : NativeBridge.ObjectToPtr(result);
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

            if (self != null && CompatHooks.TryGetAssemblyPath(self, out string mapped) && !string.IsNullOrEmpty(mapped))
            {
                return mapped;
            }

            try { return self != null ? self.Location : string.Empty; }
            catch { return string.Empty; }
        }

        public static bool TryInstallConcreteDetour(
    MethodInfo target,
    Type slotType,
    Type delegateType,
    Type origType,
    MethodInfo concreteBridge,
    Delegate replacement,
    int[] refIndexes,
    out Delegate orig,
    out string error)
        {
            orig = null;
            error = null;

            if (target == null)
            {
                error = "target is null.";
                return false;
            }

            if (slotType == null)
            {
                error = "slotType is null.";
                return false;
            }

            if (delegateType == null)
            {
                error = "delegateType is null.";
                return false;
            }

            if (concreteBridge == null)
            {
                error = "concreteBridge is null.";
                return false;
            }

            if (!_dobbyAvailable)
            {
                error = "Dobby not available.";
                return false;
            }

            // If this target already has a concrete detour installed,
            // reuse the existing trampoline and replace/add the handler.

            if (Installed.TryGetValue(
                target,
                out InstalledHook existingHook) &&
                existingHook.Slot != slotType &&
                BridgeStates.TryGetValue(
                    existingHook.Slot,
                    out BridgeState existingState) &&
                existingState.Orig != null &&
                existingState.Trampoline != IntPtr.Zero)
            {
                lock (existingState.Handlers)
                {
                    existingState.Handlers.Clear();

                    if (replacement != null)
                    {
                        existingState.Handlers.Add(replacement);
                    }
                }

                orig = existingState.Orig;
                return true;
            }

            // The slot was already reserved by TryGetFreeBridge().
            // Do not install anything until both the native method and
            // the IL2CPP MethodInfo have been validated.

            IntPtr targetAddr = IntPtr.Zero;

            try
            {
                targetAddr =
                    Il2CppResolver.TryGetMethodPointer(target);

                Logger.APILogger.Log(
                    "IL2CPP AOT target resolution: " +
                    target.DeclaringType?.FullName +
                    "." +
                    target.Name +
                    " -> 0x" +
                    targetAddr.ToInt64().ToString("X"));
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "Failed to resolve IL2CPP AOT address for " +
                    target.Name +
                    ": " +
                    ex);

                targetAddr = IntPtr.Zero;
            }

            if (targetAddr == IntPtr.Zero)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "IL2CPP resolver returned no native address for " +
                    target.DeclaringType?.FullName +
                    "." +
                    target.Name +
                    ". Dobby hook was not installed.";

                Logger.APILogger.LogWarn(error);

                return false;
            }

            // Resolve MethodInfo before DobbyHookNative.
            // This is intentional: a valid native address without a valid
            // Il2Cpp MethodInfo is not sufficient for this bridge architecture.

            IntPtr nativeMethod = IntPtr.Zero;

            try
            {
                nativeMethod =
                    Il2CppResolver.TryGetMethodInfoPointer(
                        target);

                Logger.APILogger.Log(
                    "IL2CPP MethodInfo resolution: " +
                    target.DeclaringType?.FullName +
                    "." +
                    target.Name +
                    " -> 0x" +
                    nativeMethod.ToInt64().ToString("X"));
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "Failed to resolve IL2CPP MethodInfo for " +
                    target.Name +
                    ": " +
                    ex);

                nativeMethod = IntPtr.Zero;
            }

            if (nativeMethod == IntPtr.Zero)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "No IL2CPP MethodInfo available for " +
                    target.DeclaringType?.FullName +
                    "." +
                    target.Name +
                    ". Dobby hook was not installed.";

                Logger.APILogger.LogWarn(error);

                return false;
            }

            Delegate bridgeDel;
            IntPtr bridgeAddr;

            try
            {
                bridgeDel =
                    Delegate.CreateDelegate(
                        delegateType,
                        null,
                        concreteBridge);

                bridgeAddr =
                    Marshal.GetFunctionPointerForDelegate(
                        bridgeDel);
            }
            catch (Exception ex)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "Could not create bridge delegate/pointer: " +
                    ex.Message;

                return false;
            }

            if (bridgeAddr == IntPtr.Zero)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "Bridge delegate returned a null native pointer.";

                return false;
            }

            if (targetAddr == bridgeAddr)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "Target and bridge share the same native address.";

                return false;
            }

            BridgeState state =
                new BridgeState
                {
                    Handlers =
                        new List<Delegate>
                        {
                    replacement
                        },

                    RefIndexes = refIndexes,

                    Target = target,

                    NativeMethod = nativeMethod,

                    InstanceCall = !target.IsStatic,

                    OrigParamType =
                        replacement?.Method
                            ?.GetParameters()
                            .Length > 0
                            ? replacement.Method
                                .GetParameters()[0]
                                .ParameterType
                            : delegateType
                };

            state.Bridge = bridgeDel;

            BridgeStates[slotType] = state;

            IntPtr trampPtr = IntPtr.Zero;

            try
            {
                int result =
                    DobbyHookNative(
                        targetAddr,
                        bridgeAddr,
                        out trampPtr);

                Logger.APILogger.Log(
                    "DobbyHook result=" +
                    result +
                    " target=0x" +
                    targetAddr.ToInt64().ToString("X") +
                    " bridge=0x" +
                    bridgeAddr.ToInt64().ToString("X") +
                    " trampoline=0x" +
                    trampPtr.ToInt64().ToString("X"));
            }
            catch (Exception ex)
            {
                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "DobbyHook failed: " +
                    ex.Message;

                Logger.APILogger.LogError(error);

                return false;
            }

            if (trampPtr == IntPtr.Zero)
            {
                TryUnhook(targetAddr);

                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "DobbyHook returned a null trampoline.";

                Logger.APILogger.LogError(error);

                return false;
            }

            state.Trampoline =
                trampPtr;

            // Build the managed orig delegate from the native trampoline.

            Type origParamType =
                replacement?.Method?.GetParameters().Length > 0
                    ? replacement.Method
                        .GetParameters()[0]
                        .ParameterType
                    : delegateType;

            var adapter =
                new OrigAdapter
                {
                    Target = target,
                    NativeMethod = nativeMethod,
                    Trampoline = trampPtr,
                    InstanceCall = !target.IsStatic
                };

            orig =
                CreateManagedOrigDelegate(
                    origParamType,
                    target,
                    adapter);

            if (orig == null)
            {
                TryUnhook(targetAddr);

                BridgeStates.TryRemove(
                    slotType,
                    out _);

                error =
                    "Could not create managed orig delegate for " +
                    (origParamType?.Name ?? "?") +
                    ".";

                Logger.APILogger.LogError(error);

                return false;
            }

            state.Orig = orig;
            state.Bridge = bridgeDel;
            state.Target = target;
            state.NativeMethod = nativeMethod;
            state.Trampoline = trampPtr;
            state.InstanceCall = !target.IsStatic;
            state.OrigParamType = origParamType;

            Logger.APILogger.Log(
                "Concrete detour installed for " +
                target.Name +
                " (bridge 0x" +
                bridgeAddr.ToInt64().ToString("X") +
                ", target 0x" +
                targetAddr.ToInt64().ToString("X") +
                ", MethodInfo 0x" +
                nativeMethod.ToInt64().ToString("X") +
                ").");

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
                typeof(OrigGetLocation), LocationBridgeMethod, replacement, null, out _, out error);
        }

        #region concrete bridges (MonoPInvokeCallback) for game hooks routed via On.*

        private sealed class StartSlashSlot { }
        private sealed class OnDisableSlot { }
        private sealed class TakeDamageSlot { }
        private sealed class HitSlot { }
        private sealed class DieSlot { }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<IntPtr>))]
        private static void BridgeStartSlash(IntPtr a0)
        {
            InvokeBridge<StartSlashSlot>(new object[] { a0 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<IntPtr>))]
        private static void BridgeOnDisable(IntPtr a0)
        {
            InvokeBridge<OnDisableSlot>(new object[] { a0 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<IntPtr, IntPtr, GlobalEnums.CollisionSide, int, int>))]
        private static void BridgeTakeDamage(IntPtr a0, IntPtr a1, GlobalEnums.CollisionSide a2, int a3, int a4)
        {
            InvokeBridge<TakeDamageSlot>(new object[] { a0, a1, a2, a3, a4 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<IntPtr, IntPtr>))]
        private static void BridgeHit(IntPtr a0, IntPtr a1)
        {
            InvokeBridge<HitSlot>(new object[] { a0, a1 });
        }

        [AOT.MonoPInvokeCallback(typeof(DetourAction<IntPtr, DieCause, AttackTypes, bool>))]
        private static void BridgeDie(IntPtr a0, DieCause a1, AttackTypes a2, bool a3)
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
            public bool IsGenerated;
        }

        private static readonly ConcurrentDictionary<Type, List<ConcreteBridgeInfo>> ConcreteBridges =
            new ConcurrentDictionary<Type, List<ConcreteBridgeInfo>>();

        static DetourBridge()
        {
            RegisterConcreteBridge(typeof(StartSlashSlot), nameof(BridgeStartSlash), typeof(DetourAction<IntPtr>), typeof(OrigStartSlash));
            RegisterConcreteBridge(typeof(OnDisableSlot), nameof(BridgeOnDisable), typeof(DetourAction<IntPtr>), typeof(OrigOnDisable));
            RegisterConcreteBridge(typeof(TakeDamageSlot), nameof(BridgeTakeDamage), typeof(DetourAction<IntPtr, IntPtr, GlobalEnums.CollisionSide, int, int>), typeof(OrigTakeDamage));
            RegisterConcreteBridge(typeof(HitSlot), nameof(BridgeHit), typeof(DetourAction<IntPtr, IntPtr>), typeof(OrigHit));
            RegisterConcreteBridge(typeof(DieSlot), nameof(BridgeDie), typeof(DetourAction<IntPtr, DieCause, AttackTypes, bool>), typeof(OrigDie));
            try { GeneratedBridges.RegisterAll(); }
            catch (Exception ex) { Logger.APILogger.LogError("GeneratedBridges.RegisterAll failed: " + ex); }
        }

        private static void RegisterConcreteBridge(
            Type slotType,
            string bridgeMethodName,
            Type delegateType,
            Type origType)
        {
            MethodInfo bridge = typeof(DetourBridge).GetMethod(
                bridgeMethodName,
                BindingFlags.NonPublic | BindingFlags.Static);

            RegisterBridge(
                delegateType,
                slotType,
                bridge,
                origType,
                null,
                false);
        }

        public static void RegisterGeneratedBridge(
            Type delegateType,
            Type slotType,
            MethodInfo bridge,
            Type origType,
            string[] rawParamTypes = null)
        {
            RegisterBridge(
                delegateType,
                slotType,
                bridge,
                origType,
                rawParamTypes,
                true);
        }

        private static void RegisterBridge(
            Type delegateType,
            Type slotType,
            MethodInfo bridge,
            Type origType,
            string[] rawParamTypes,
            bool isGenerated)
        {
            List<ConcreteBridgeInfo> list =
                ConcreteBridges.GetOrAdd(
                    delegateType,
                    _ => new List<ConcreteBridgeInfo>());

            lock (list)
            {
                list.Add(new ConcreteBridgeInfo
                {
                    Slot = slotType,
                    Bridge = bridge,
                    DelegateType = delegateType,
                    OrigType = origType,
                    RawParamTypes = rawParamTypes,
                    IsGenerated = isGenerated
                });
            }
        }

        private static bool TryGetFreeBridge(
            Type delegateType,
            MethodInfo targetMethod,
            out ConcreteBridgeInfo chosen)
        {
            chosen = null;

            if (!ConcreteBridges.TryGetValue(
                delegateType,
                out List<ConcreteBridgeInfo> list))
            {
                return false;
            }

            lock (list)
            {
                foreach (ConcreteBridgeInfo cbi in list)
                {
                    if (!cbi.IsGenerated)
                        continue;

                    if (!IsSlotFree(cbi.Slot))
                        continue;

                    if (BridgeStates.TryAdd(
                        cbi.Slot,
                        new BridgeState()))
                    {
                        chosen = cbi;
                        return true;
                    }
                }

                foreach (ConcreteBridgeInfo cbi in list)
                {
                    if (cbi.IsGenerated)
                        continue;

                    if (!DedicatedBridgeMatchesTarget(cbi, targetMethod))
                        continue;

                    if (!IsSlotFree(cbi.Slot))
                        continue;

                    if (BridgeStates.TryAdd(
                        cbi.Slot,
                        new BridgeState()))
                    {
                        chosen = cbi;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool DedicatedBridgeMatchesTarget(
            ConcreteBridgeInfo bridge,
            MethodInfo target)
        {
            if (bridge == null || target == null)
                return false;

            string slot = bridge.Slot != null
                ? bridge.Slot.Name
                : string.Empty;

            Type declaring = target.DeclaringType;

            switch (slot)
            {
                case nameof(StartSlashSlot):
                    return declaring == typeof(NailSlash)
                           && target.Name == "StartSlash";

                case nameof(OnDisableSlot):
                    return declaring == typeof(GameManager)
                           && target.Name == "OnDisable";

                case nameof(TakeDamageSlot):
                    return declaring == typeof(HeroController)
                           && target.Name == "TakeDamage";

                case nameof(HitSlot):
                    return declaring == typeof(HealthManager)
                           && target.Name == "Hit";

                case nameof(DieSlot):
                    return declaring == typeof(HealthManager)
                           && target.Name == "Die";

                default:
                    return false;
            }
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
            Logger.APILogger.Log(
                "TryCreateOrigDetour: " +
                targetMethod.DeclaringType?.FullName +
                "." +
                targetMethod.Name);

            Logger.APILogger.Log(
                "Replacement method: " +
                replacement.Method.DeclaringType?.FullName +
                "." +
                replacement.Method.Name);

            Logger.APILogger.Log(
                "Replacement first parameter: " +
                replacement.Method.GetParameters()[0]
                    .ParameterType.FullName);
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

            Type ptrDelegateType = BuildPtrDelegateTypeForMethod(targetMethod, out int[] refIndexes, out _);

            Logger.APILogger.Log(
                "Required AOT bridge signature: " +
                (ptrDelegateType != null
                ? ptrDelegateType.FullName
                : "<null>"));

            if (ptrDelegateType != null && TryGetFreeBridge(ptrDelegateType, targetMethod, out ConcreteBridgeInfo ptrCbi))
            {
                Logger.APILogger.Log(
                    "AOT bridge found: " +
                    ptrCbi.Bridge?.Name +
                    " | Slot=" +
                    ptrCbi.Slot?.Name +
                    " | Delegate=" +
                    ptrCbi.DelegateType?.FullName +
                    " | Orig=" +
                    ptrCbi.OrigType?.FullName);

                Delegate origDelegate;
                if (!TryInstallConcreteDetour(targetMethod, ptrCbi.Slot, ptrDelegateType, ptrCbi.OrigType, ptrCbi.Bridge,
                    replacement, refIndexes, out origDelegate, out error))
                {
                    return false;
                }
                Installed[targetMethod] = new InstalledHook { Target = targetMethod, Slot = ptrCbi.Slot, Orig = origDelegate };
                trampolineDelegate = origDelegate;
                return true;
            }

            Logger.APILogger.LogWarn("No AOT bridge for " + targetMethod.DeclaringType?.Name + "." + targetMethod.Name);

            string unsupported = DescribeUnsupportedSignature(targetMethod);
            error = unsupported != null
                ? "no AOT bridge registered for this signature (unsupported type: " + unsupported + ")"
                : "no AOT bridge registered for signature of " + targetMethod.DeclaringType?.Name + "." + targetMethod.Name;
            return false;
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
