using System;
using System.Reflection;
using Modding;

namespace MonoMod.RuntimeDetour
{
    public class Detour : IDisposable
    {
        private readonly MethodInfo _original;
        private Delegate _replacement;
        private Delegate _originalDelegate;
        private bool _disposed;

        private Detour(MethodInfo original, Delegate replacement)
        {
            _original = original;
            _replacement = replacement;
        }

        public Detour(MethodBase original, Delegate replacement)
            : this(original as MethodInfo, replacement)
        {
            if (_original == null) throw new ArgumentException("Only methods are supported.", nameof(original));
            if (_replacement == null) throw new ArgumentNullException(nameof(replacement));
            Apply();
        }

        public Detour(MethodBase original, MethodInfo replacement)
            : this(original as MethodInfo, CreateBoundDelegate(original, replacement))
        {
            if (_original == null) throw new ArgumentException("Only methods are supported.", nameof(original));
            if (_replacement == null) throw new ArgumentNullException(nameof(replacement));
            Apply();
        }

        internal static Delegate CreateBoundDelegate(MethodBase original, MethodInfo replacement)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (original is MethodInfo mi)
            {
                Type dt = DetourBridge.GetDelegateTypeForMethod(mi);
                if (dt != null) return Delegate.CreateDelegate(dt, replacement);
                throw new NotSupportedException("Unsupported target signature: " + mi.Name);
            }
            throw new ArgumentException("Only methods are supported.", nameof(original));
        }

        public MethodBase Method => _original;
        public Delegate Original => _originalDelegate;
        public bool IsApplied => _originalDelegate != null;
        public bool IsDisposed => _disposed;

        private void Apply()
        {
            RemoveCurrent();

#if ENABLE_IL2CPP
            if (DetourBridge.IsAvailable)
            {
                if (IsOrigPattern(_replacement))
                {
                    if (DetourBridge.TryCreateOrigDetour(_original, _replacement, out Delegate tramp, out string err))
                    {
                        _originalDelegate = tramp;
                    }
                    else
                    {
                        Logger.APILogger.LogWarn("Orig detour for " + _original.Name + " failed: " + err);
                    }
                }
                else
                {
                    _originalDelegate = DetourBridge.CreateDetour(_original, _replacement.Method);
                    if (_originalDelegate == null)
                        Logger.APILogger.LogWarn("Direct detour for " + _original.Name + " failed.");
                }
            }
            else
            {
                Logger.APILogger.LogWarn("Dobby not available. Detour for " + _original.Name + " will not be applied on IL2CPP.");
            }
#else
            ApplyMonoDetour();
#endif
        }

        private void RemoveCurrent()
        {
            if (_originalDelegate != null)
            {
                DetourBridge.RemoveDetour(_original);
                _originalDelegate = null;
            }
        }

        private static bool IsOrigPattern(Delegate d)
        {
            var ps = d?.Method?.GetParameters();
            return ps != null && ps.Length > 0 && typeof(Delegate).IsAssignableFrom(ps[0].ParameterType);
        }

#if !ENABLE_IL2CPP
        private void ApplyMonoDetour()
        {
            try
            {
                Type realDetourType = Type.GetType("MonoMod.RuntimeDetour.Detour, MonoMod.RuntimeDetour");
                if (realDetourType != null)
                {
                    Activator.CreateInstance(realDetourType, new object[] { _original, _replacement });
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError("Failed to create real MonoMod detour: " + ex.Message);
            }
        }
#endif

        public void DetourTo(Delegate replacement)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Detour));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            _replacement = replacement;
            Apply();
        }

        public void ApplyDetour()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Detour));
            if (_originalDelegate == null) Apply();
        }

        public void Undo()
        {
            if (_disposed) return;
            RemoveCurrent();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Undo();
            _disposed = true;
        }
    }

    public class Hook : IDisposable
    {
        private readonly MethodBase _method;
        private Delegate _replacement;
        private Detour _detour;
        private bool _disposed;

        public Hook(MethodBase method, Delegate replacement)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
            _detour = new Detour(method, replacement);
        }

        public Hook(MethodBase method, MethodInfo replacement)
            : this(method, replacement == null ? null : Detour.CreateBoundDelegate(method, replacement))
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
        }

        public MethodBase Method => _method;
        public Delegate Original => _detour?.Original;
        public bool IsApplied => _detour != null && _detour.IsApplied;
        public bool IsDisposed => _disposed;

        public void Apply()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Hook));
            if (_detour == null) _detour = new Detour(_method, _replacement);
            else _detour.ApplyDetour();
        }

        public void Undo()
        {
            if (_disposed) return;
            _detour?.Undo();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _detour?.Dispose();
            _disposed = true;
        }
    }
}