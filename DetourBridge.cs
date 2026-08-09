using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Modding.Utils;

namespace Modding
{
    internal static class DetourBridge
    {
        private static bool _initialized;
        private static bool _dobbyAvailable;
        private static DobbyHookDelegate _dobbyInstall = null;
        private static DobbyUnhookDelegate _dobbyUninstall = null;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DobbyHookDelegate(IntPtr target, IntPtr replacement, out IntPtr outTrampoline);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DobbyUnhookDelegate(IntPtr target);

        public static bool IsAvailable => _dobbyAvailable;

        public static bool Initialize()
        {
            if (_initialized) return _dobbyAvailable;
            _initialized = true;

#if !ENABLE_IL2CPP
            _dobbyAvailable = false;
            return false;
#else
            return LoadDobby();
#endif
        }

#if ENABLE_IL2CPP
        [DllImport("dobby")]
        private static extern IntPtr DobbyHook(IntPtr target, IntPtr replacement, out IntPtr outTrampoline);

        [DllImport("dobby")]
        private static extern bool DobbyUnhook(IntPtr target);

        private static bool LoadDobby()
        {
            try
            {
                try
                {
                    IntPtr testTrampoline;
                    DobbyHook(IntPtr.Zero, IntPtr.Zero, out testTrampoline);
                }
                catch (DllNotFoundException)
                {
                    Logger.APILogger.LogWarn("Dobby library not found.");
                    _dobbyAvailable = false;
                    return false;
                }

                _dobbyInstall = Marshal.GetDelegateForFunctionPointer<DobbyHookDelegate>(
                    Marshal.GetFunctionPointerForDelegate(new DobbyHookDelegate(DobbyHook))
                );
                _dobbyUninstall = Marshal.GetDelegateForFunctionPointer<DobbyUnhookDelegate>(
                    Marshal.GetFunctionPointerForDelegate(new DobbyUnhookDelegate(DobbyUnhook))
                );

                Logger.APILogger.Log("Dobby loaded.");
                _dobbyAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Dobby init failed: {ex.Message}");
                _dobbyAvailable = false;
                return false;
            }
        }
#endif

        public static unsafe Delegate CreateDetour(MethodInfo targetMethod, MethodInfo replacementMethod)
        {
            if (!_dobbyAvailable)
            {
                Logger.APILogger.LogWarn("Cannot create detour.");
                return null;
            }

            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
            if (replacementMethod == null) throw new ArgumentNullException(nameof(replacementMethod));

            try
            {
                IntPtr targetAddress = GetNativeMethodAddress(targetMethod);
                if (targetAddress == IntPtr.Zero)
                {
                    Logger.APILogger.LogError($"Could not get native address for {targetMethod.DeclaringType.FullName}.{targetMethod.Name}");
                    return null;
                }

                IntPtr replacementAddress = GetNativeMethodAddress(replacementMethod);
                if (replacementAddress == IntPtr.Zero)
                {
                    Logger.APILogger.LogError($"Could not get native address for replacement {replacementMethod.DeclaringType.FullName}.{replacementMethod.Name}");
                    return null;
                }

                IntPtr trampoline = _dobbyInstall.Invoke(targetAddress, replacementAddress, out trampoline);

                if (trampoline == IntPtr.Zero)
                {
                    Logger.APILogger.LogError($"DobbyHook failed for {targetMethod.DeclaringType.FullName}.{targetMethod.Name}");
                    return null;
                }

                return CreateTrampolineDelegate(targetMethod, trampoline);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to create detour: {ex.Message}");
                return null;
            }
        }

        public static bool RemoveDetour(MethodInfo targetMethod)
        {
            if (!_dobbyAvailable) return false;

            try
            {
                IntPtr targetAddress = GetNativeMethodAddress(targetMethod);
                if (targetAddress == IntPtr.Zero) return false;

                return _dobbyUninstall.Invoke(targetAddress);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to remove detour: {ex.Message}");
                return false;
            }
        }

        private static IntPtr GetNativeMethodAddress(MethodInfo method)
        {
            try
            {
                RuntimeMethodHandle handle = method.MethodHandle;
                if (handle != null)
                {
                    IntPtr ptr = handle.GetFunctionPointer();
                    if (ptr != IntPtr.Zero)
                    {
                        return ptr;
                    }
                }

                Logger.APILogger.LogWarn($"Could not get native address for {method.Name}");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to get native address for {method.Name}: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private static Delegate CreateTrampolineDelegate(MethodInfo targetMethod, IntPtr trampoline)
        {
            try
            {
                Type delegateType = GetDelegateTypeForMethod(targetMethod);
                if (delegateType == null) return null;

                return Marshal.GetDelegateForFunctionPointer(trampoline, delegateType);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to create trampoline delegate: {ex.Message}");
                return null;
            }
        }

        private static Type GetDelegateTypeForMethod(MethodInfo method)
        {
            if (!method.ReturnType.IsPublic && method.GetParameters().Length == 0)
            {
                return typeof(Action);
            }

            Logger.APILogger.LogWarn($"Complex signature not yet supported for {method.Name}");
            return null;
        }
    }
}
