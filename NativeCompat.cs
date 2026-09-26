using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Modding
{
    internal static class NativeCompat
    {
        private static bool _installed;

        private static readonly ConcurrentDictionary<MethodBase, List<Delegate>> HandlersByMethod =
            new ConcurrentDictionary<MethodBase, List<Delegate>>();

        private static readonly ConcurrentDictionary<MethodBase, bool> AppliedByMethod =
            new ConcurrentDictionary<MethodBase, bool>();

        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            if (!IsIl2Cpp)
            {
                return;
            }

            if (ModHooks.GlobalSettings.NativeLogging)
            {
                NativeBridge.EnsureReady();
            }

            InstallHookEndpointRedirect();

            if (ModHooks.GlobalSettings.ComponentHook)
            {
                NativeBridge.EnsureAddComponentHook();
                NativeBridge.EnsureGameObjectCtorHook();
            }

            LogILHookProvider();
        }

        private static bool IsIl2Cpp
        {
            get
            {
#if ENABLE_IL2CPP
                return true;
#else
                return false;
#endif
            }
        }

        private static void LogILHookProvider()
        {
            try
            {
                Logger.APILogger.Log(
                    "MonoMod.RuntimeDetour provider diagnostic");

                Assembly[] assemblies =
                    AppDomain.CurrentDomain.GetAssemblies();

                bool foundRuntimeDetourAssembly = false;
                bool foundILHookType = false;

                foreach (Assembly assembly in assemblies)
                {
                    string name = assembly.GetName().Name;

                    if (string.Equals(
                        name,
                        "MonoMod.RuntimeDetour",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        foundRuntimeDetourAssembly = true;

                        Logger.APILogger.Log(
                            "MonoMod.RuntimeDetour assembly loaded: " +
                            assembly.FullName);

                        Type ilHookType =
                            assembly.GetType(
                                "MonoMod.RuntimeDetour.ILHook",
                                false);

                        if (ilHookType != null)
                        {
                            foundILHookType = true;

                            Logger.APILogger.Log(
                                "MonoMod.RuntimeDetour.ILHook provider: " +
                                ilHookType.Assembly.FullName);
                        }
                    }
                }

                if (!foundRuntimeDetourAssembly)
                {
                    Logger.APILogger.Log(
                        "MonoMod.RuntimeDetour assembly is not loaded.");
                }

                if (!foundILHookType)
                {
                    Logger.APILogger.Log(
                        "MonoMod.RuntimeDetour.ILHook type was not found.");
                }

                Logger.APILogger.Log(
                    "End MonoMod.RuntimeDetour provider diagnostic");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "ILHook provider diagnostic failed: " + ex);
            }
        }

        private static void InstallHookEndpointRedirect()
        {
            try
            {
                Type hemm = Type.GetType(
                    "MonoMod.RuntimeDetour.HookGen.HookEndpointManager, MonoMod.RuntimeDetour",
                    throwOnError: false);

                if (hemm == null)
                {
                    Logger.APILogger.Log("Real MonoMod runtime not found.");
                    return;
                }

                Subscribe(hemm, "OnAdd", new Func<MethodBase, Delegate, bool>(OnAdd));
                Subscribe(hemm, "OnRemove", new Func<MethodBase, Delegate, bool>(OnRemove));
                Subscribe(hemm, "OnModify", new Func<MethodBase, Delegate, bool>(OnModify));
                Subscribe(hemm, "OnUnmodify", new Func<MethodBase, Delegate, bool>(OnUnmodify));

                Logger.APILogger.Log("On.* hooks redirect installed.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("HookEndpointManager redirect install failed: " + ex);
            }
        }

        private static void Subscribe(Type type, string eventName, Delegate handler)
        {
            try
            {
                EventInfo evt = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
                if (evt == null)
                {
                    Logger.APILogger.LogDebug("Event " + eventName + " not found on HookEndpointManager.");
                    return;
                }

                evt.AddEventHandler(null, handler);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn("Failed to subscribe " + eventName + ": " + ex.Message);
            }
        }

        private static bool OnAdd(MethodBase method, Delegate hook)
        {
            try
            {
                if (method == null || hook == null)
                {
                    return false;
                }

                var mi = method as MethodInfo;
                if (mi == null)
                {
                    Logger.APILogger.LogWarn("On hook target " + method.Name + " is not a MethodInfo.");
                    return false;
                }

                if (!DetourBridge.IsAvailable)
                {
                    Logger.APILogger.LogWarn("On hook " + mi.Name + " not applied.");
                    return false;
                }

                List<Delegate> list = HandlersByMethod.GetOrAdd(mi, m => new List<Delegate>());
                lock (list)
                {
                    list.Add(hook);
                }

                if (!AppliedByMethod.TryAdd(mi, true))
                {
                    Logger.APILogger.LogDebug(
                        "Compat: " + mi.Name + " already hooked.");
                    DetourBridge.AddHandlerFor(mi, hook);
                    return false;
                }

                Delegate tramp;
                string error;
                bool ok;
                if (IsOrigPattern(hook))
                {
                    ok = DetourBridge.TryCreateOrigDetour(mi, hook, out tramp, out error);
                }
                else
                {
                    Logger.APILogger.LogWarn(
                        "Hook for " + mi.Name + " is not an orig-pattern delegate.");
                    tramp = DetourBridge.CreateDetour(mi, hook.Method);
                    error = tramp == null ? "CreateDetour returned null" : null;
                    ok = tramp != null;
                }

                if (ok)
                {
                    Logger.APILogger.Log("On hook applied: " + mi.DeclaringType?.Name + "." + mi.Name);
                }
                else
                {
                    AppliedByMethod.TryRemove(mi, out _);
                    Logger.APILogger.LogWarn("Failed to apply On hook " + mi.Name + ": " + error);
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("OnAdd error for " + method?.Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool OnRemove(MethodBase method, Delegate hook)
        {
            try
            {
                if (method is MethodInfo mi && DetourBridge.IsAvailable)
                {
                    bool shouldUnhook = false;
                    if (HandlersByMethod.TryGetValue(mi, out List<Delegate> list))
                    {
                        lock (list)
                        {
                            list.Remove(hook);
                            shouldUnhook = list.Count == 0;
                        }

                        if (shouldUnhook)
                        {
                            HandlersByMethod.TryRemove(mi, out _);
                        }
                    }

                    if (shouldUnhook)
                    {
                        AppliedByMethod.TryRemove(mi, out _);
                        DetourBridge.RemoveDetour(mi);
                        Logger.APILogger.LogDebug("On hook removed: " + mi.Name);
                    }
                    else
                    {
                        Logger.APILogger.LogDebug("Removed a handler of " + mi.Name + " (More remain).");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("OnRemove error: " + ex.Message);
                return false;
            }
        }

        private static bool OnModify(MethodBase method, Delegate callback)
        {
            Logger.APILogger.LogWarn("IL.modify on " + method?.Name + " ignored.");
            return false;
        }

        private static bool OnUnmodify(MethodBase method, Delegate callback)
        {
            return false;
        }

        private static bool IsOrigPattern(Delegate d)
        {
            ParameterInfo[] ps = d?.Method?.GetParameters();
            return ps != null && ps.Length > 0 && typeof(Delegate).IsAssignableFrom(ps[0].ParameterType);
        }
    }
}
