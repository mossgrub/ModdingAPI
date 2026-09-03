using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Modding
{
    internal static class NativeCompat
    {
        private static bool _installed;

        internal static readonly ConcurrentDictionary<Assembly, string> AssemblyLocations =
            new ConcurrentDictionary<Assembly, string>();

        internal static readonly ConcurrentDictionary<string, string> NameLocations =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<MethodBase, List<Delegate>> HandlersByMethod =
            new ConcurrentDictionary<MethodBase, List<Delegate>>();

        private static readonly ConcurrentDictionary<MethodBase, bool> AppliedByMethod =
            new ConcurrentDictionary<MethodBase, bool>();

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            if (!IsIl2Cpp)
            {
                return;
            }

            NativeBridge.EnsureReady();
            InstallHookEndpointRedirect();
            NativeBridge.EnsureLocationHook();
            NativeBridge.EnsureAddComponentHook();
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

        private static bool _locationPatched;

        private static void InstallAssemblyLocationPatch()
        {
            if (_locationPatched || !DetourBridge.IsAvailable) return;

            try
            {
                MethodInfo getLocation = typeof(Assembly).GetMethod("get_Location", BindingFlags.Public | BindingFlags.Instance);
                if (getLocation == null)
                {
                    Logger.APILogger.LogWarn("Could not find Assembly.get_Location.");
                    return;
                }

                Delegate handler = new Func<Func<Assembly, string>, Assembly, string>(LocationHandler);
                if (DetourBridge.TryInstallLocationHook(handler, out string error))
                {
                    _locationPatched = true;
                    Logger.APILogger.Log("Assembly.Location patched.");
                }
                else
                {
                    Logger.APILogger.LogWarn("Assembly.Location patch failed: " + error);
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Assembly.Location patch error: " + ex.Message);
            }
        }

        private static string LocationHandler(Func<Assembly, string> orig, Assembly self)
        {
            if (self != null && AssemblyLocations.TryGetValue(self, out string mapped))
            {
                return mapped;
            }

            try
            {
                return orig(self);
            }
            catch
            {
                return string.Empty;
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
            {
                return true;
            }

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

        public static void RegisterAssemblyPath(Assembly asm, string path)
        {
            if (asm != null && !string.IsNullOrEmpty(path))
            {
                AssemblyLocations[asm] = path;
                try
                {
                    string name = asm.GetName()?.Name;
                    if (!string.IsNullOrEmpty(name)) NameLocations[name] = path;
                }
                catch { }
            }
        }
    }
}