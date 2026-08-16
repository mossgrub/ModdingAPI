using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Modding
{
    internal static class Il2CppResolver
    {
        private const string LibIl2Cpp = "il2cpp";
        private const string LibGameAssembly = "GameAssembly";

        private static readonly int MethodPointerOffset = IntPtr.Size;

        public static IntPtr TryGetMethodPointer(MethodInfo method)
        {
            if (method?.DeclaringType == null)
            {
                return IntPtr.Zero;
            }

            string ns = method.DeclaringType.Namespace ?? string.Empty;
            string typeName = method.DeclaringType.Name;
            string methodName = method.Name;

            if (method.DeclaringType.IsNested)
            {
                return IntPtr.Zero;
            }

            IntPtr ptr = IntPtr.Zero;
            foreach (string lib in new[] { LibIl2Cpp, LibGameAssembly })
            {
                try
                {
                    ptr = Resolve(lib, ns, typeName, methodName);
                }
                catch (EntryPointNotFoundException)
                {
                    continue;
                }
                catch (DllNotFoundException)
                {
                    continue;
                }

                if (ptr != IntPtr.Zero)
                {
                    return ptr;
                }
            }

            Logger.APILogger.LogDebug("No native address found.");
            return ptr;
        }

        private static IntPtr Resolve(string lib, string ns, string typeName, string methodName)
        {
            IntPtr domain = il2cpp_domain_get(lib);
            if (domain == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            UIntPtr count;
            IntPtr assemblies = il2cpp_domain_get_assemblies(lib, domain, out count);
            if (assemblies == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            long total = (long)count.ToUInt64();
            for (long i = 0; i < total; i++)
            {
                IntPtr asm = Marshal.ReadIntPtr(assemblies, (int)(i * IntPtr.Size));
                if (asm == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr image = il2cpp_assembly_get_image(lib, asm);
                if (image == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr klass = il2cpp_class_from_name(lib, image, ns, typeName);
                if (klass == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr iter = IntPtr.Zero;
                IntPtr methodInfo;
                while ((methodInfo = il2cpp_class_get_methods(lib, klass, ref iter)) != IntPtr.Zero)
                {
                    IntPtr namePtr = il2cpp_method_get_name(lib, methodInfo);
                    if (namePtr == IntPtr.Zero)
                    {
                        continue;
                    }

                    string name = Marshal.PtrToStringAnsi(namePtr);
                    if (string.IsNullOrEmpty(name) || !string.Equals(name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    IntPtr methodPointer = Marshal.ReadIntPtr(methodInfo, MethodPointerOffset);
                    if (methodPointer != IntPtr.Zero && (ulong)methodPointer.ToInt64() > 0x1000UL)
                    {
                        return methodPointer;
                    }
                }
            }

            return IntPtr.Zero;
        }

        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_domain_get_GA();
        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_domain_get_assemblies_GA(IntPtr domain, out UIntPtr size);
        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_assembly_get_image_GA(IntPtr assembly);
        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_from_name_GA(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string namepsace, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_get_methods_GA(IntPtr klass, ref IntPtr iter);
        [DllImport(LibGameAssembly, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_method_get_name_GA(IntPtr method);

        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_domain_get_LI();
        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_domain_get_assemblies_LI(IntPtr domain, out UIntPtr size);
        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_assembly_get_image_LI(IntPtr assembly);
        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_from_name_LI(IntPtr image, [MarshalAs(UnmanagedType.LPStr)] string namepsace, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_get_methods_LI(IntPtr klass, ref IntPtr iter);
        [DllImport(LibIl2Cpp, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_method_get_name_LI(IntPtr method);

        private static IntPtr il2cpp_domain_get(string lib) =>
            lib == LibGameAssembly ? il2cpp_domain_get_GA() : il2cpp_domain_get_LI();
        private static IntPtr il2cpp_domain_get_assemblies(string lib, IntPtr domain, out UIntPtr size) =>
            lib == LibGameAssembly ? il2cpp_domain_get_assemblies_GA(domain, out size) : il2cpp_domain_get_assemblies_LI(domain, out size);
        private static IntPtr il2cpp_assembly_get_image(string lib, IntPtr asm) =>
            lib == LibGameAssembly ? il2cpp_assembly_get_image_GA(asm) : il2cpp_assembly_get_image_LI(asm);
        private static IntPtr il2cpp_class_from_name(string lib, IntPtr image, string ns, string name) =>
            lib == LibGameAssembly ? il2cpp_class_from_name_GA(image, ns, name) : il2cpp_class_from_name_LI(image, ns, name);
        private static IntPtr il2cpp_class_get_methods(string lib, IntPtr klass, ref IntPtr iter) =>
            lib == LibGameAssembly ? il2cpp_class_get_methods_GA(klass, ref iter) : il2cpp_class_get_methods_LI(klass, ref iter);
        private static IntPtr il2cpp_method_get_name(string lib, IntPtr method) =>
            lib == LibGameAssembly ? il2cpp_method_get_name_GA(method) : il2cpp_method_get_name_LI(method);
    }
}