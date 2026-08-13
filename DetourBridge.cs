using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Modding.Utils;

namespace Modding
{
    public static class DetourBridge
    {
        private static bool _initialized;
        private static bool _dobbyAvailable;
        private const int MaxArgs = 6;

        [DllImport("dobby")]
        public static extern void DobbyHookNative(IntPtr target, IntPtr replacement, out IntPtr outTrampoline);

        [DllImport("dobby")]
        private static extern int DobbyUnhookNative(IntPtr target);

        private static readonly ConcurrentDictionary<MethodInfo, InstalledHook> Installed = new ConcurrentDictionary<MethodInfo, InstalledHook>();

        private sealed class InstalledHook
        {
            public MethodInfo Target;
            public Type Slot;
            public Delegate Orig;
        }

        private sealed class BridgeState
        {
            public Delegate Replacement;
            public Delegate Orig;
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
                if (ta != IntPtr.Zero && ra != IntPtr.Zero)
                {
                    IntPtr tp;
                    DobbyHookNative(ta, ra, out tp);
                    DobbyUnhookNative(ta);
                }

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
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch
                {
                    // fall through to GetFunctionPointer
                }

                return method.MethodHandle.GetFunctionPointer();
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn("Could not get native address for " + method.Name + ": " + ex.Message);
                return IntPtr.Zero;
            }
        }
        public delegate void DetourAction();
        public delegate void DetourAction<A0>(A0 a0);
        public delegate void DetourAction<A0, A1>(A0 a0, A1 a1);
        public delegate void DetourAction<A0, A1, A2>(A0 a0, A1 a1, A2 a2);
        public delegate void DetourAction<A0, A1, A2, A3>(A0 a0, A1 a1, A2 a2, A3 a3);
        public delegate void DetourAction<A0, A1, A2, A3, A4>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4);
        public delegate void DetourAction<A0, A1, A2, A3, A4, A5>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5);

        public delegate R DetourFunc<R>();
        public delegate R DetourFunc<A0, R>(A0 a0);
        public delegate R DetourFunc<A0, A1, R>(A0 a0, A1 a1);
        public delegate R DetourFunc<A0, A1, A2, R>(A0 a0, A1 a1, A2 a2);
        public delegate R DetourFunc<A0, A1, A2, A3, R>(A0 a0, A1 a1, A2 a2, A3 a3);
        public delegate R DetourFunc<A0, A1, A2, A3, A4, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4);
        public delegate R DetourFunc<A0, A1, A2, A3, A4, A5, R>(A0 a0, A1 a1, A2 a2, A3 a3, A4 a4, A5 a5);

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
            }
            if (method.ReturnType.IsByRef) { error = "byref return not supported"; return null; }

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

        private static void InvokeBridge<TSlot>(object[] args)
        {
            if (!BridgeStates.TryGetValue(typeof(TSlot), out BridgeState st) || st.Orig == null)
                return;

            object[] full = new object[args.Length + 1];
            full[0] = st.Orig;
            Array.Copy(args, 0, full, 1, args.Length);
            try
            {
                st.Replacement.DynamicInvoke(full);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("DetourBridge hook invocation error: " + ex);
            }
        }

        private static R InvokeBridgeR<R, TSlot>(object[] args)
        {
            if (!BridgeStates.TryGetValue(typeof(TSlot), out BridgeState st) || st.Orig == null)
                return default;

            object[] full = new object[args.Length + 1];
            full[0] = st.Orig;
            Array.Copy(args, 0, full, 1, args.Length);
            try
            {
                return (R)st.Replacement.DynamicInvoke(full);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("DetourBridge hook invocation error: " + ex);
                return default;
            }
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

            IntPtr bridgeAddr = GetNativeMethodAddress(bridge);
            if (bridgeAddr == IntPtr.Zero) { ReleaseSlot(slot); error = "cannot get bridge address."; return false; }

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
            if (trampPtr == IntPtr.Zero) { ReleaseSlot(slot); error = "DobbyHook returned null trampoline."; return false; }

            Delegate orig;
            try
            {
                orig = Marshal.GetDelegateForFunctionPointer(trampPtr, origParamType);
            }
            catch (Exception ex)
            {
                ReleaseSlot(slot);
                error = "failed to create orig delegate: " + ex.Message;
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
                Logger.APILogger.LogWarn("Cannot create detour - Dobby not available.");
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

            if (info.Slot != null) ReleaseSlot(info.Slot);
            return true;
        }
    }
}