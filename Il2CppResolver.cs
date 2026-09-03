using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Modding
{
    internal static class Il2CppResolver
    {
        // Il2CppMethodInfo layout for Unity 2020.3
        // 0 methodPointer = AOT native code of the method
        // 8 invoker_method = per-signature shared stub
        // 16 method = Il2CppMethodDefinition* in metadata
        private static readonly int MethodPointerOffset = 0;

        public static IntPtr TryGetMethodInfoPointer(MethodInfo method)
        {
            return TryGetMethodInfoPointer(method, -1, (string[])null);
        }

        public static IntPtr TryGetMethodInfoPointer(MethodInfo method, int paramCountHint, string paramTypeName)
        {
            string[] paramTypes = string.IsNullOrEmpty(paramTypeName) ? null : new string[] { paramTypeName };
            return TryGetMethodInfoPointer(method, paramCountHint, paramTypes);
        }

        public static IntPtr TryGetMethodPointer(MethodInfo method)
        {
            return TryGetMethodPointer(method, -1, (string[])null);
        }

        public static IntPtr TryGetMethodPointer(MethodInfo method, int paramCountHint, string paramTypeName)
        {
            string[] paramTypes = string.IsNullOrEmpty(paramTypeName) ? null : new string[] { paramTypeName };
            return TryGetMethodPointer(method, paramCountHint, paramTypes);
        }

        public static IntPtr TryGetMethodInfoPointer(MethodInfo method, int paramCountHint, string[] paramTypeNames)
        {
            if (method?.DeclaringType == null) return IntPtr.Zero;

            string ns = method.DeclaringType.Namespace ?? string.Empty;
            string typeName = method.DeclaringType.Name;
            string methodName = method.Name;

            foreach (string lib in LibNames)
            {
                try
                {
                    Api api = GetApi(lib);
                    if (api == null) continue;

                    IntPtr mi = ResolveMethodInfo(api, lib, ns, typeName, methodName, paramCountHint, paramTypeNames);
                    if (mi != IntPtr.Zero) return mi;
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogDebug("il2cpp method-info resolver error on " + lib + ": " + ex.Message);
                }
            }
            return IntPtr.Zero;
        }

        public static IntPtr TryGetMethodPointer(MethodInfo method, int paramCountHint, string[] paramTypeNames)
        {
            if (method?.DeclaringType == null) return IntPtr.Zero;

            string ns = method.DeclaringType.Namespace ?? string.Empty;
            string typeName = method.DeclaringType.Name;
            string methodName = method.Name;

            foreach (string lib in LibNames)
            {
                try
                {
                    Api api = GetApi(lib);
                    if (api == null) continue;

                    IntPtr mi = ResolveMethodInfo(api, lib, ns, typeName, methodName, paramCountHint, paramTypeNames);
                    if (mi != IntPtr.Zero)
                    {
                        IntPtr p = Marshal.ReadIntPtr(mi, MethodPointerOffset);
                        if (p != IntPtr.Zero)
                        {
                            Logger.APILogger.LogDebug(
                                "IL2CPP resolver returned native address 0x" + p.ToInt64().ToString("X") +
                                " for " + typeName + "." + methodName + " (filtered)");
                            return p;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogDebug("il2cpp filtered method resolver error on " + lib + ": " + ex.Message);
                }
            }
            return IntPtr.Zero;
        }

        private static IntPtr ResolveMethodInfo(
            Api a,
            string lib,
            string ns,
            string typeName,
            string methodName,
            int paramCountHint,
            string[] paramTypeNames)
        {
            IntPtr domain = a.DomainGet();
            if (domain == IntPtr.Zero) return IntPtr.Zero;

            IntPtr assemblies = a.DomainAssemblies(domain, out UIntPtr count);
            if (assemblies == IntPtr.Zero) return IntPtr.Zero;

            long total = (long)count.ToUInt64();
            for (long i = 0; i < total; i++)
            {
                IntPtr asm = Marshal.ReadIntPtr(assemblies, (int)(i * IntPtr.Size));
                if (asm == IntPtr.Zero) continue;

                IntPtr image = a.AssemblyImage(asm);
                if (image == IntPtr.Zero) continue;

                IntPtr klass = a.ClassFromName(image, ns, typeName);
                if (klass == IntPtr.Zero)
                {
                    UIntPtr classCount = a.ImageClassCount(image);
                    for (ulong c = 0; c < classCount.ToUInt64(); c++)
                    {
                        IntPtr k = a.ImageGetClass(image, (UIntPtr)c);
                        if (k == IntPtr.Zero) continue;

                        IntPtr kName = a.ClassGetName(k);
                        string cname = kName == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(kName);
                        if (cname == typeName) { klass = k; break; }
                    }
                    if (klass == IntPtr.Zero) continue;
                }

                IntPtr iter = IntPtr.Zero;
                IntPtr methodInfo;
                while ((methodInfo = a.ClassGetMethods(klass, ref iter)) != IntPtr.Zero)
                {
                    IntPtr namePtr = a.MethodGetName(methodInfo);
                    if (namePtr == IntPtr.Zero) continue;

                    string name = Marshal.PtrToStringAnsi(namePtr);
                    if (string.IsNullOrEmpty(name) || !string.Equals(name, methodName, StringComparison.Ordinal)) continue;

                    if (paramCountHint >= 0 && a.MethodGetParamCount != null)
                    {
                        uint cnt = a.MethodGetParamCount(methodInfo);
                        if ((int)cnt != paramCountHint) continue;
                    }

                    if (paramTypeNames != null && paramTypeNames.Length > 0 && a.MethodGetParam != null && a.TypeGetName != null)
                    {
                        bool match = true;
                        for (uint pIdx = 0; pIdx < paramTypeNames.Length; pIdx++)
                        {
                            IntPtr paramPtr = a.MethodGetParam(methodInfo, pIdx);
                            IntPtr paramNamePtr = paramPtr == IntPtr.Zero ? IntPtr.Zero : a.TypeGetName(paramPtr);
                            string paramName = paramNamePtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(paramNamePtr);

                            if (!string.Equals(paramName, paramTypeNames[pIdx], StringComparison.Ordinal))
                            {
                                match = false;
                                break;
                            }
                        }
                        if (!match) continue;
                    }

                    IntPtr mp = Marshal.ReadIntPtr(methodInfo, MethodPointerOffset);
                    if (mp != IntPtr.Zero && (ulong)mp.ToInt64() > 0x1000UL) return methodInfo;
                }
            }
            return IntPtr.Zero;
        }


        private const int RTLD_NOW = 2;

        [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string filename, int flags);
        [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);
        [DllImport("libdl.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        private delegate IntPtr DDomain();
        private delegate IntPtr DDomainAssemblies(IntPtr domain, out UIntPtr size);
        private delegate IntPtr DAssemblyImage(IntPtr assembly);
        private delegate IntPtr DClassFromName(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string ns, [MarshalAs(UnmanagedType.LPStr)] string name);
        private delegate IntPtr DClassGetMethods(IntPtr klass, ref IntPtr iter);
        private delegate IntPtr DMethodGetName(IntPtr method);
        private delegate UIntPtr DImageClassCount(IntPtr image);
        private delegate IntPtr DImageGetClass(IntPtr image, UIntPtr index);
        private delegate IntPtr DClassGetName(IntPtr klass);
        private delegate uint DMethodGetParamCount(IntPtr method);
        private delegate IntPtr DMethodGetParam(IntPtr method, uint index);
        private delegate IntPtr DTypeGetName(IntPtr type);

        private sealed class Api
        {
            public DDomain DomainGet;
            public DDomainAssemblies DomainAssemblies;
            public DAssemblyImage AssemblyImage;
            public DClassFromName ClassFromName;
            public DClassGetMethods ClassGetMethods;
            public DMethodGetName MethodGetName;
            public DImageClassCount ImageClassCount;
            public DImageGetClass ImageGetClass;
            public DClassGetName ClassGetName;
            public DMethodGetParamCount MethodGetParamCount;
            public DMethodGetParam MethodGetParam;
            public DTypeGetName TypeGetName;

            public static Api Open(string libFileName)
            {
                IntPtr h = dlopen(libFileName, RTLD_NOW);
                if (h == IntPtr.Zero)
                {
                    Logger.APILogger.LogDebug("dlopen(" + libFileName + ") failed: " + GetDlError());
                    return null;
                }

                IntPtr dg = dlsym(h, "il2cpp_domain_get");
                IntPtr da = dlsym(h, "il2cpp_domain_get_assemblies");
                IntPtr ai = dlsym(h, "il2cpp_assembly_get_image");
                IntPtr cf = dlsym(h, "il2cpp_class_from_name");
                IntPtr cm = dlsym(h, "il2cpp_class_get_methods");
                IntPtr mn = dlsym(h, "il2cpp_method_get_name");
                IntPtr cc = dlsym(h, "il2cpp_image_get_class_count");
                IntPtr cg = dlsym(h, "il2cpp_image_get_class");
                IntPtr cn = dlsym(h, "il2cpp_class_get_name");
                IntPtr mpc = dlsym(h, "il2cpp_method_get_param_count");
                IntPtr mp0 = dlsym(h, "il2cpp_method_get_param");
                IntPtr tn = dlsym(h, "il2cpp_type_get_name");

                if (dg == IntPtr.Zero || da == IntPtr.Zero || ai == IntPtr.Zero || cf == IntPtr.Zero ||
                    cm == IntPtr.Zero || mn == IntPtr.Zero || cc == IntPtr.Zero || cg == IntPtr.Zero ||
                    cn == IntPtr.Zero)
                {
                    Logger.APILogger.LogDebug("dlsym on " + libFileName + " missing required symbol; resolved: domain=" +
                        dg.ToInt64().ToString("X") + " asm=" + da.ToInt64().ToString("X"));
                    return null;
                }

                return new Api
                {
                    DomainGet = (DDomain)Marshal.GetDelegateForFunctionPointer(dg, typeof(DDomain)),
                    DomainAssemblies = (DDomainAssemblies)Marshal.GetDelegateForFunctionPointer(da, typeof(DDomainAssemblies)),
                    AssemblyImage = (DAssemblyImage)Marshal.GetDelegateForFunctionPointer(ai, typeof(DAssemblyImage)),
                    ClassFromName = (DClassFromName)Marshal.GetDelegateForFunctionPointer(cf, typeof(DClassFromName)),
                    ClassGetMethods = (DClassGetMethods)Marshal.GetDelegateForFunctionPointer(cm, typeof(DClassGetMethods)),
                    MethodGetName = (DMethodGetName)Marshal.GetDelegateForFunctionPointer(mn, typeof(DMethodGetName)),
                    ImageClassCount = (DImageClassCount)Marshal.GetDelegateForFunctionPointer(cc, typeof(DImageClassCount)),
                    ImageGetClass = (DImageGetClass)Marshal.GetDelegateForFunctionPointer(cg, typeof(DImageGetClass)),
                    ClassGetName = (DClassGetName)Marshal.GetDelegateForFunctionPointer(cn, typeof(DClassGetName)),
                    MethodGetParamCount = mpc == IntPtr.Zero ? null : (DMethodGetParamCount)Marshal.GetDelegateForFunctionPointer(mpc, typeof(DMethodGetParamCount)),
                    MethodGetParam = mp0 == IntPtr.Zero ? null : (DMethodGetParam)Marshal.GetDelegateForFunctionPointer(mp0, typeof(DMethodGetParam)),
                    TypeGetName = tn == IntPtr.Zero ? null : (DTypeGetName)Marshal.GetDelegateForFunctionPointer(tn, typeof(DTypeGetName))
                };
            }
        }

        private static string GetDlError()
        {
            try
            {
                IntPtr p = dlerror();
                return p == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(p);
            }
            catch
            {
                return "unknown";
            }
        }

        private static readonly Dictionary<string, Api> LoadedApis = new Dictionary<string, Api>();

        private static Api GetApi(string libFileName)
        {
            lock (LoadedApis)
            {
                if (LoadedApis.TryGetValue(libFileName, out Api a))
                {
                    return a;
                }

                Api created = Api.Open(libFileName);
                LoadedApis[libFileName] = created;
                return created;
            }
        }

        private static readonly string[] LibNames = { "libil2cpp.so", "libGameAssembly.so" };
    }
}