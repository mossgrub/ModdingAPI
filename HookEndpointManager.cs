using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Modding;

namespace MonoMod.RuntimeDetour.HookGen
{
    public static partial class HookEndpointManager
    {
        private static readonly ConcurrentDictionary<MethodBase, HookRegistration> Registrations = new ConcurrentDictionary<MethodBase, HookRegistration>();
        private static bool _warnedAboutIl;

        private static readonly ModuleBuilder DynamicModule = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("Modding.HookGen.Dynamic"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Modding.HookGen.DynamicModule");

        public static void Add<T>(MethodBase method, Delegate handler) where T : class
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            string fullName = method.DeclaringType?.FullName + "." + method.Name;
            if (typeof(T).Name.StartsWith("IL_"))
            {
                if (!_warnedAboutIl)
                {
                    _warnedAboutIl = true;
                    Logger.APILogger.LogWarn("IL.* hooks are experimental on IL2CPP. They require HybridCLR + Dobby backend.");
                }
                
                if (ILHookBackend.IsAvailable)
                {
                    Logger.APILogger.Log($"Attempting IL hook via HybridCLR backend: {typeof(T).FullName} on {fullName}");
                    
                    if (ILHookBackend.TryApplyILHook(method, handler, out string error))
                    {
                        Logger.APILogger.Log($"IL hook successfully applied via HybridCLR: {fullName}");
                        var ilRegistration = Registrations.GetOrAdd(method, m => new HookRegistration(m));
                        ilRegistration.AddHandler(handler);
                        ilRegistration.DetourApplied = true;
                        return;
                    }
                    else
                    {
                        Logger.APILogger.LogError($"IL hook via HybridCLR failed: {fullName}. Error: {error}");
                    }
                }
                
                Logger.APILogger.LogError($"IL hook NOT SUPPORTED: {typeof(T).FullName} on {fullName}");
                Logger.APILogger.LogError($"Reason: IL2CPP compiles C# to native code (ARM/x64). There is NO IL at runtime to manipulate.");
                Logger.APILogger.LogError($"Alternative: Use On.* hooks instead. On.HookEndpointManager.Add<On.{method.DeclaringType?.Name}.{method.Name}>(...) ");
                return;
            }

            Logger.APILogger.LogDebug($"Registering On hook: {typeof(T).FullName} on {fullName}");

            var registration = Registrations.GetOrAdd(method, m => new HookRegistration(m));
            registration.AddHandler(handler);

            if (registration.DetourApplied) return;

            try
            {
                registration.DetourApplied = true;
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError($"Failed to apply On hook for {fullName}: {ex.Message}");
                registration.RemoveHandler(handler);
                if (registration.IsEmpty)
                {
                    Registrations.TryRemove(method, out _);
                }
            }
        }

        public static void Remove<T>(MethodBase method, Delegate handler) where T : class
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (!Registrations.TryGetValue(method, out HookRegistration registration))
            {
                return;
            }

            registration.RemoveHandler(handler);

            if (!registration.IsEmpty) return;

            try
            {
                if (typeof(T).Name.StartsWith("IL_") && ILHookBackend.IsAvailable)
                {
                    if (ILHookBackend.TryRemoveILHook(method, out string error))
                    {
                        Logger.APILogger.Log($"Removed IL hook: {method.DeclaringType?.FullName}.{method.Name}");
                    }
                    else
                    {
                        Logger.APILogger.LogWarn($"Failed to remove IL hook: {method.DeclaringType?.FullName}.{method.Name}. Error: {error}");
                    }
                }
                else
                {
                    DetourBridge.RemoveDetour(method as MethodInfo);
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn($"Failed to remove hook for {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
            }
            finally
            {
                Registrations.TryRemove(method, out _);
            }
        }

        private static Type GenerateReplacementType(HookRegistration registration, MethodInfo slotInvoke)
        {
#if ENABLE_IL2CPP
            Type returnType = slotInvoke.ReturnType;
            ParameterInfo[] slotParams = slotInvoke.GetParameters();
            Type[] slotParamTypes = slotParams.Select(p => p.ParameterType).ToArray();
            Type originalDelegateType = slotParams[0].ParameterType;
            Type[] callParameters = new Type[slotParams.Length - 1];
            for (int i = 0; i < callParameters.Length; i++)
            {
                callParameters[i] = slotParams[i + 1].ParameterType;
            }

            TypeBuilder typeBuilder = DynamicModule.DefineType(
                "Modding.HookGen." + Guid.NewGuid().ToString("N"),
                TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(object));

            {
                MethodBuilder mb = typeBuilder.DefineMethod(
                    "Invoke",
                    MethodAttributes.Public | MethodAttributes.Virtual,
                    returnType,
                    slotParamTypes);

                ILGenerator il = mb.GetILGenerator();

                LocalBuilder handlersLocal = il.DeclareLocal(typeof(Delegate[]));
                LocalBuilder argsLocal = il.DeclareLocal(typeof(object[]));
                LocalBuilder iLocal = il.DeclareLocal(typeof(int));
                LocalBuilder resultLocal = il.DeclareLocal(returnType);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(HookRegistration).GetMethod("get_Handlers"));
                il.Emit(OpCodes.Callvirt, typeof(List<Delegate>).GetMethod("ToArray"));
                il.Emit(OpCodes.Stloc, handlersLocal);

                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, iLocal);

                Label loopLabel = il.DefineLabel();
                Label checkLabel = il.DefineLabel();

                il.Emit(OpCodes.Br_S, checkLabel);
                il.MarkLabel(loopLabel);

                il.Emit(OpCodes.Ldloc, handlersLocal);
                il.Emit(OpCodes.Ldloc, iLocal);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Ldc_I4, slotParams.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stelem_Ref);
                for (int i = 0; i < callParameters.Length; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i + 1);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (callParameters[i].IsValueType)
                    {
                        il.Emit(OpCodes.Box, callParameters[i]);
                    }
                    il.Emit(OpCodes.Stelem_Ref);
                }
                il.Emit(OpCodes.Callvirt, typeof(Delegate).GetMethod("DynamicInvoke", new[] { typeof(object[]) }));

                if (returnType != typeof(void))
                {
                    il.Emit(OpCodes.Unbox_Any, returnType);
                    il.Emit(OpCodes.Stloc, resultLocal);
                }

                il.Emit(OpCodes.Ldloc, iLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, iLocal);

                il.MarkLabel(checkLabel);
                il.Emit(OpCodes.Ldloc, iLocal);
                il.Emit(OpCodes.Ldloc, handlersLocal);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Blt_S, loopLabel);

                if (returnType != typeof(void))
                {
                    il.Emit(OpCodes.Ldloc, resultLocal);
                }
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(HookRegistration).GetMethod("InvokeOrig", new[] { originalDelegateType, returnType.MakeByRefType() }));
                if (returnType != typeof(void))
                {
                    il.Emit(OpCodes.Ldloc, resultLocal);
                }
                il.Emit(OpCodes.Ret);
            }

            Type replacementType = typeBuilder.CreateType();
            return replacementType;
#else
            return null;
#endif
        }

        private static MethodInfo CreateStaticWrapper(Type instanceType, MethodInfo instanceMethod)
        {
#if ENABLE_IL2CPP
            ParameterInfo[] parameters = instanceMethod.GetParameters();
            Type[] paramTypes = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                paramTypes[i] = parameters[i].ParameterType;
            }

            DynamicMethod dm = new DynamicMethod(
                "HookStaticWrapper_" + instanceType.Name,
                instanceMethod.ReturnType,
                paramTypes,
                instanceType,
                true);

            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 1; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i);
            }
            il.Emit(OpCodes.Newobj, instanceType.GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Callvirt, instanceMethod);
            il.Emit(OpCodes.Ret);

            return dm;
#else
            return null;
#endif
        }
    }

    internal sealed class HookRegistration
    {
        private readonly object _lock = new object();
        private System.Collections.Generic.List<Delegate> _handlers = new System.Collections.Generic.List<Delegate>();
        public bool DetourApplied { get; set; }
        public Delegate Orig { get; private set; }

        public HookRegistration(MethodBase method)
        {
            Method = method;
        }

        public MethodBase Method { get; }
        public System.Collections.Generic.List<Delegate> Handlers => _handlers;

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _handlers == null || _handlers.Count == 0;
                }
            }
        }

        public Type GetSlotType()
        {
            lock (_lock)
            {
                foreach (Delegate d in _handlers)
                {
                    return d.GetType();
                }
            }
            return null;
        }

        public void AddHandler(Delegate handler)
        {
            lock (_lock)
            {
                _handlers ??= new System.Collections.Generic.List<Delegate>();
                _handlers.Add(handler);
            }
        }

        public void RemoveHandler(Delegate handler)
        {
            lock (_lock)
            {
                if (_handlers != null && _handlers.Count > 0)
                {
                    _handlers.Remove(handler);
                }
            }
        }

        public void InvokeOrig(Delegate originalDelegate, ref object result)
        {
            if (originalDelegate == null) return;

            try
            {
                result = originalDelegate.DynamicInvoke(null);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }
    }
}