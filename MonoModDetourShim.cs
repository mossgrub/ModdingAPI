using System;
using System.Reflection;
using Modding;

namespace MonoMod.RuntimeDetour
{
    public class Detour : IDisposable
    {
        private readonly MethodBase _original;
        private readonly Delegate _replacement;
        private bool _disposed;

        public Detour(MethodBase original, Delegate replacement)
        {
            _original = original ?? throw new ArgumentNullException(nameof(original));
            _replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));

#if ENABLE_IL2CPP
            if (DetourBridge.IsAvailable)
            {
                if (original is MethodInfo originalMethod)
                {
                    MethodInfo replacementMethod = replacement.Method;
                    DetourBridge.CreateDetour(originalMethod, replacementMethod);
                }
            }
            else
            {
                Modding.Logger.APILogger.LogWarn($"Dobby not available, detour for {original} will not work on IL2CPP.");
            }
#else
            try
            {
                var realDetourType = Type.GetType("MonoMod.RuntimeDetour.Detour, MonoMod.RuntimeDetour");
                if (realDetourType != null)
                {
                    var ctor = realDetourType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
                    if (ctor != null)
                    {
                        ctor.Invoke(new object[] { original, replacement });
                    }
                }
            }
            catch (Exception ex)
            {
                Modding.Logger.APILogger.LogError($"Failed to create real detour: {ex.Message}");
            }
#endif
        }

        public Detour(MethodBase original, MethodInfo replacement)
            : this(original, CreateDelegate(original, replacement))
        {
        }

        private static Delegate CreateDelegate(MethodBase original, MethodInfo replacement)
        {
            if (original is MethodInfo originalMethod)
            {
                return Delegate.CreateDelegate(
                    GetDelegateType(originalMethod),
                    replacement
                );
            }
            throw new ArgumentException("Only methods are supported for detouring.", nameof(original));
        }

        private static Type GetDelegateType(MethodInfo method)
        {
            System.Reflection.ParameterInfo[] parameters = method.GetParameters();
            Type returnType = method.ReturnType;

            if (returnType == typeof(void) && parameters.Length == 0)
            {
                return typeof(Action);
            }

            throw new NotSupportedException($"Delegate type generation not yet supported for {method.Name}.");
        }

        public void DetourTo(Delegate replacement)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Detour));
            Modding.Logger.APILogger.Log("DetourTo not fully implemented.");
        }

        public void Undo()
        {
            if (_disposed) return;

#if ENABLE_IL2CPP
            if (_original is MethodInfo originalMethod)
            {
                DetourBridge.RemoveDetour(originalMethod);
            }
#endif
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
        private readonly Detour _detour;
        private bool _disposed;

        public Hook(MethodBase method, Delegate replacement)
        {
            _detour = new Detour(method, replacement);
        }

        public Delegate Original => null;

        public void Dispose()
        {
            if (_disposed) return;
            _detour?.Dispose();
            _disposed = true;
        }
    }
}
