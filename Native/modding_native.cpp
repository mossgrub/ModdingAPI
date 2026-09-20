#include "modding_native.h"

#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <stdint.h>
#include <errno.h>
#include <unistd.h>
#include <sys/mman.h>

#define LOG_TAG "ModdingNative"

static char g_logPath[1024] = {0};
static pthread_mutex_t g_logLock = PTHREAD_MUTEX_INITIALIZER;

static void FileAppend(const char *line)
{
    if (!g_logPath[0])
        return;
    pthread_mutex_lock(&g_logLock);
    FILE *f = fopen(g_logPath, "a");
    if (f)
    {
        fprintf(f, "%s\n", line);
        fclose(f);
    }
    pthread_mutex_unlock(&g_logLock);
}

static void LogPrint(int level, const char *fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    __android_log_print(level, LOG_TAG, "%s", buf);
    FileAppend(buf);
}

#define LOGI(...) LogPrint(ANDROID_LOG_INFO, __VA_ARGS__)
#define LOGE(...) LogPrint(ANDROID_LOG_ERROR, __VA_ARGS__)

// Defined below; declared here because the AddComponent installer uses it.
static bool Mod2FlagNextToLog(const char *name);

extern "C" void mod2_set_log_file(const char *path)
{
    pthread_mutex_lock(&g_logLock);
    if (!path || !*path)
    {
        g_logPath[0] = 0;
    }
    else
    {
        strncpy(g_logPath, path, sizeof(g_logPath) - 1);
        g_logPath[sizeof(g_logPath) - 1] = 0;
    }
    pthread_mutex_unlock(&g_logLock);
}

namespace
{

    typedef void *(*Il2CppRuntimeInvoke)(void *method, void *obj, void **params, void **exc);
    typedef void *(*Il2CppStringNew)(const char *str);
    typedef void *(*Il2CppObjectUnbox)(void *obj);

    Il2CppRuntimeInvoke g_invoke = nullptr;
    Il2CppStringNew g_strNew = nullptr;
    Il2CppObjectUnbox g_unbox = nullptr;
    bool g_ready = false;

    typedef void *(*ObjGetClassFn)(void *obj);
    typedef void *(*ClassGetFieldsFn)(void *klass, void **iter);
    typedef const char *(*FieldGetNameFn)(void *field);
    typedef void (*FieldGetValueFn)(void *field, void *obj, void *value);
    typedef void *(*AsmGetImageFn)(void *assembly);
    typedef const char *(*ImageGetNameFn)(void *image);
    typedef void *(*DomainGetFn)(void);
    typedef void **(*DomainAssembliesFn)(void *domain, size_t *size);

    ObjGetClassFn g_objGetClass = nullptr;
    ClassGetFieldsFn g_classGetFields = nullptr;
    FieldGetNameFn g_fieldGetName = nullptr;
    FieldGetValueFn g_fieldGetValue = nullptr;
    AsmGetImageFn g_asmGetImage = nullptr;
    ImageGetNameFn g_imgGetName = nullptr;
    DomainGetFn g_domainGet = nullptr;
    DomainAssembliesFn g_domainAsm = nullptr;

    typedef void *(*Il2CppTypeFromReflectionFn)(void *reflectionType);
    typedef void *(*Il2CppClassFromTypeFn)(void *type);
    typedef const char *(*Il2CppClassGetNameFn)(void *klass);
    typedef uintptr_t (*Il2CppArrayLengthFn)(void *array);

    typedef void *(*Il2CppResolveIcallFn)(const char *name);

    Il2CppResolveIcallFn g_resolveIcall = nullptr;

    Il2CppTypeFromReflectionFn g_typeFromReflection = nullptr;
    Il2CppClassFromTypeFn g_classFromType = nullptr;
    Il2CppClassGetNameFn g_classGetName = nullptr;
    Il2CppArrayLengthFn g_arrayLength = nullptr;

    // marshalling symbols (needed to rebuild the real native calling convention)
    typedef uint32_t (*Il2CppMethodGetParamCountFn)(void *method);
    typedef const void *(*Il2CppMethodGetParamFn)(void *method, uint32_t index);
    typedef int (*Il2CppTypeGetTypeFn)(const void *type);
    typedef int32_t (*Il2CppClassValueSizeFn)(void *klass, uint32_t *align);
    typedef const void *(*Il2CppFieldGetTypeFn)(void *field);
    typedef const char *(*Il2CppMethodGetNameFn)(void *method);
    typedef void *(*Il2CppMethodGetClassFn)(void *method);
    typedef uint32_t (*Il2CppMethodGetFlagsFn)(void *method, uint32_t *iflags);
    typedef const void *(*Il2CppMethodGetReturnTypeFn)(void *method);

    Il2CppMethodGetParamCountFn g_methodParamCount = nullptr;
    Il2CppMethodGetParamFn g_methodParam = nullptr;
    Il2CppTypeGetTypeFn g_typeGetType = nullptr;
    Il2CppClassValueSizeFn g_classValueSize = nullptr;
    Il2CppFieldGetTypeFn g_fieldGetType = nullptr;
    Il2CppMethodGetNameFn g_methodGetName = nullptr;
    Il2CppMethodGetClassFn g_methodGetClass = nullptr;
    Il2CppMethodGetFlagsFn g_methodGetFlags = nullptr;
    Il2CppMethodGetReturnTypeFn g_methodGetReturnType = nullptr;

    // il2cpp type tags (Il2CppTypeEnum), used to decide how each argument travels.
    static const int kTypeEnd = 0x00;
    static const int kTypeVoid = 0x01;
    static const int kTypeBoolean = 0x02;
    static const int kTypeChar = 0x03;
    static const int kTypeI1 = 0x04;
    static const int kTypeU1 = 0x05;
    static const int kTypeI2 = 0x06;
    static const int kTypeU2 = 0x07;
    static const int kTypeI4 = 0x08;
    static const int kTypeU4 = 0x09;
    static const int kTypeI8 = 0x0a;
    static const int kTypeU8 = 0x0b;
    static const int kTypeR4 = 0x0c;
    static const int kTypeR8 = 0x0d;
    static const int kTypeString = 0x0e;
    static const int kTypePtr = 0x0f;
    static const int kTypeByRef = 0x10;
    static const int kTypeValueType = 0x11;
    static const int kTypeClass = 0x12;
    static const int kTypeVar = 0x13;
    static const int kTypeArray = 0x14;
    static const int kTypeGenericInst = 0x15;
    static const int kTypeTypedByRef = 0x16;
    static const int kTypeI = 0x18;
    static const int kTypeU = 0x19;
    static const int kTypeFnPtr = 0x1b;
    static const int kTypeObject = 0x1c;
    static const int kTypeSzArray = 0x1d;

    typedef int (*DobbyHookFn)(void *target, void *replacement, void **outTrampoline);
    typedef int (*DobbyDestroyFn)(void *target);

    DobbyHookFn g_dobbyHook = nullptr;
    DobbyDestroyFn g_dobbyUnhook = nullptr;

    pthread_mutex_t g_pathLock = PTHREAD_MUTEX_INITIALIZER;
    enum
    {
        MAX_PATHS = 256,
        MAX_PATHLEN = 1024
    };
    void *gAsms[MAX_PATHS];
    void *gAsmNative[MAX_PATHS];
    char gNames[MAX_PATHS][MAX_PATHLEN];
    char gPaths[MAX_PATHS][MAX_PATHLEN];
    int gPathCount = 0;

    const char *FindPathByPtr(void *asmObj)
    {
        if (!asmObj)
            return nullptr;
        pthread_mutex_lock(&g_pathLock);
        const char *result = nullptr;
        for (int i = 0; i < gPathCount; ++i)
            if (gAsms[i] == asmObj)
            {
                result = gPaths[i];
                break;
            }
        pthread_mutex_unlock(&g_pathLock);
        return result;
    }

    const char *FindPathByNative(void *asmNative)
    {
        if (!asmNative)
            return nullptr;
        pthread_mutex_lock(&g_pathLock);
        const char *result = nullptr;
        for (int i = 0; i < gPathCount; ++i)
            if (gAsmNative[i] == asmNative)
            {
                result = gPaths[i];
                break;
            }
        pthread_mutex_unlock(&g_pathLock);
        return result;
    }

    static bool NameMatches(const char *needle, const char *hay)
    {
        if (!needle || !hay)
            return false;
        char a[512], b[512];
        strncpy(a, needle, sizeof(a) - 1);
        a[sizeof(a) - 1] = 0;
        strncpy(b, hay, sizeof(b) - 1);
        b[sizeof(b) - 1] = 0;
        char *ca = strchr(a, ',');
        if (ca)
            *ca = 0;
        char *cb = strchr(b, ',');
        if (cb)
            *cb = 0;
        size_t la = strlen(a), lb = strlen(b);
        if (la >= 5 && a[la - 4] == '.' && (a[la - 3] == 'd' || a[la - 3] == 'D') && (a[la - 2] == 'l' || a[la - 2] == 'L') && (a[la - 1] == 'l' || a[la - 1] == 'L'))
            a[la - 4] = 0;
        if (lb >= 5 && b[lb - 4] == '.' && (b[lb - 3] == 'd' || b[lb - 3] == 'D') && (b[lb - 2] == 'l' || b[lb - 2] == 'L') && (b[lb - 1] == 'l' || b[lb - 1] == 'L'))
            b[lb - 4] = 0;
        return la > 0 && strcasecmp(a, b) == 0;
    }

    const char *FindPathByName(const char *name)
    {
        if (!name || !*name)
            return nullptr;
        pthread_mutex_lock(&g_pathLock);
        const char *result = nullptr;
        for (int i = 0; i < gPathCount; ++i)
            if (NameMatches(name, gNames[i]))
            {
                result = gPaths[i];
                break;
            }
        pthread_mutex_unlock(&g_pathLock);
        return result;
    }

    const char *FindPathByDomainName(const char *nameHint)
    {
        if (!g_domainGet || !g_domainAsm || !g_asmGetImage || !g_imgGetName)
            return nullptr;

        void *domain = g_domainGet();
        if (!domain)
            return nullptr;
        size_t size = 0;
        void **asms = g_domainAsm(domain, &size);
        if (!asms || size == 0)
            return nullptr;

        pthread_mutex_lock(&g_pathLock);
        const char *result = nullptr;
        for (size_t a = 0; a < size; ++a)
        {
            void *asmObj = asms[a];
            if (!asmObj)
                continue;
            void *img = g_asmGetImage(asmObj);
            if (!img)
                continue;
            const char *imgName = g_imgGetName(img);
            if (!imgName || !*imgName)
                continue;

            if (!nameHint || NameMatches(imgName, nameHint))
            {
                for (int i = 0; i < gPathCount; ++i)
                {
                    if (NameMatches(imgName, gNames[i]))
                    {
                        result = gPaths[i];
                        goto done;
                    }
                }
            }
        }
    done:
        pthread_mutex_unlock(&g_pathLock);
        return result;
    }

}

static void *g_getLocationTarget = nullptr;
static void *g_origGetLocation = nullptr;
static bool g_locationInstalled = false;

static bool TryGetImageNameFromPtr(void *asmPtr, char *outBuf, size_t outLen)
{
    if (!asmPtr || !g_asmGetImage || !g_imgGetName)
        return false;
    void *img = g_asmGetImage(asmPtr);
    if (!img)
        return false;
    const char *n = g_imgGetName(img);
    if (!n || !*n)
        return false;
    strncpy(outBuf, n, outLen - 1);
    outBuf[outLen - 1] = 0;
    return true;
}

static bool ResolveAssemblyName(const void *self, char *outBuf, size_t outLen)
{
    if (!self)
        return false;

    const char *directNativePath = FindPathByNative((void *)self);
    if (directNativePath)
    {
        strncpy(outBuf, directNativePath, outLen - 1);
        outBuf[outLen - 1] = 0;
        return true;
    }

    if (!g_objGetClass || !g_classGetFields || !g_fieldGetValue)
        return false;
    void *klass = g_objGetClass((void *)self);
    if (!klass)
        return false;

    static const char *kKnown[] = {"_assembly", "m_Assembly", "m_assembly", "_mono_assembly",
                                   "Assembly", "assembly", "_assemblyPtr", "mono_assembly"};

    void *iter = nullptr, *field = nullptr;
    while ((field = g_classGetFields(klass, &iter)) != nullptr)
    {
        const char *fn = g_fieldGetName ? g_fieldGetName(field) : nullptr;
        if (!fn)
            continue;

        bool known = false;
        for (int k = 0; k < (int)(sizeof(kKnown) / sizeof(kKnown[0])); ++k)
        {
            if (strcasecmp(fn, kKnown[k]) == 0)
            {
                known = true;
                break;
            }
        }
        if (!known)
            continue;

        void *asmPtr = nullptr;
        g_fieldGetValue(field, (void *)self, &asmPtr);
        if (!asmPtr)
            continue;

        const char *p = FindPathByNative(asmPtr);
        if (p)
        {
            strncpy(outBuf, p, outLen - 1);
            outBuf[outLen - 1] = 0;
            return true;
        }

        if (TryGetImageNameFromPtr(asmPtr, outBuf, outLen))
            return true;
    }

    uintptr_t *ptrArray = (uintptr_t *)self;
    for (int offset = 1; offset < 12; ++offset)
    {
        void *candidatePtr = (void *)ptrArray[offset];
        if (!candidatePtr)
            continue;

        const char *p = FindPathByNative(candidatePtr);
        if (p)
        {
            strncpy(outBuf, p, outLen - 1);
            outBuf[outLen - 1] = 0;
            return true;
        }

        if (TryGetImageNameFromPtr(candidatePtr, outBuf, outLen))
            return true;
    }

    return false;
}

static const char *ResolvePathFromAssemblyObject(void *self)
{
    if (!self)
        return nullptr;

    const char *p = FindPathByPtr(self);
    if (p && *p)
        return p;

    uintptr_t *ptrArray = (uintptr_t *)self;
    for (int offset = 1; offset < 12; ++offset)
    {
        void *candidatePtr = (void *)ptrArray[offset];
        if (!candidatePtr)
            continue;

        p = FindPathByNative(candidatePtr);
        if (p && *p)
            return p;

        if (g_asmGetImage && g_imgGetName)
        {
            void *img = g_asmGetImage(candidatePtr);
            if (img)
            {
                const char *imgName = g_imgGetName(img);
                if (imgName && *imgName)
                {
                    p = FindPathByName(imgName);
                    if (p && *p)
                        return p;
                    p = FindPathByDomainName(imgName);
                    if (p && *p)
                        return p;
                }
            }
        }
    }

    char nm[512] = {0};
    if (ResolveAssemblyName(self, nm, sizeof(nm)))
    {
        p = FindPathByName(nm);
        if (p && *p)
            return p;
        p = FindPathByDomainName(nm);
        if (p && *p)
            return p;
    }

    p = FindPathByDomainName(nullptr);
    if (p && *p)
        return p;

    return nullptr;
}

// Optional MethodInfo for System.Type.get_FullName. Only used to print real
// type names in the native diagnostics instead of the literal RuntimeType.
static void *g_typeFullNameMethodInfo = nullptr;

extern "C" void mod2_set_type_name_resolver(void *methodInfo)
{
    g_typeFullNameMethodInfo = methodInfo;
}

static void *g_locationResolverMethodInfo = nullptr;

extern "C" void mod2_set_location_resolver(void *resolverMethodInfo)
{
    g_locationResolverMethodInfo = resolverMethodInfo;
}

static void *LocationResolverCall(const char *name)
{
    if (!g_locationResolverMethodInfo || !g_invoke || !g_strNew || !name || !*name)
        return nullptr;
    void *nameStr = g_strNew(name);
    if (!nameStr)
        return nullptr;
    void *args[1] = {nameStr};
    void *exc = nullptr;
    void *res = g_invoke(g_locationResolverMethodInfo, nullptr, args, &exc);
    return (res && !exc) ? res : nullptr;
}

static void *g_locationResolverObjectMethodInfo = nullptr;

extern "C" void mod2_set_location_resolver_object(void *resolverMethodInfo)
{
    g_locationResolverObjectMethodInfo = resolverMethodInfo;
}

static void *LocationResolverCallObject(void *asmObj)
{
    if (!g_locationResolverObjectMethodInfo || !g_invoke || !asmObj)
        return nullptr;
    void *args[1] = {asmObj};
    void *exc = nullptr;
    void *res = g_invoke(g_locationResolverObjectMethodInfo, nullptr, args, &exc);
    return (res && !exc) ? res : nullptr;
}

static int g_locLogs = 0;
#define LOC_LOG(fmt, ...)                                        \
    do                                                           \
    {                                                            \
        if (g_locLogs < 40)                                      \
        {                                                        \
            ++g_locLogs;                                         \
            LOGI("mod2 loc#%d: " fmt, g_locLogs, ##__VA_ARGS__); \
        }                                                        \
    } while (0)

static void *ResolveManagedLocationFallback(void *self)
{
    if (!g_locationResolverMethodInfo || !g_invoke || !g_strNew || !self)
        return nullptr;

    char nm[512] = {0};
    if (ResolveAssemblyName(self, nm, sizeof(nm)))
    {
        void *str = LocationResolverCall(nm);
        if (str)
        {
            LOC_LOG("Resolved Assembly Location (managed fallback): %s", nm);
            return str;
        }
    }

    uintptr_t *ptrArray = (uintptr_t *)self;
    for (int offset = 1; offset < 12; ++offset)
    {
        void *candidatePtr = (void *)ptrArray[offset];
        if (!candidatePtr)
            continue;
        if (g_asmGetImage && g_imgGetName)
        {
            void *img = g_asmGetImage(candidatePtr);
            if (img)
            {
                const char *imgName = g_imgGetName(img);
                if (imgName && *imgName)
                {
                    void *str2 = LocationResolverCall(imgName);
                    if (str2)
                    {
                        LOC_LOG("Resolved Assembly Location (managed fallback): %s", imgName);
                        return str2;
                    }
                }
            }
        }
    }

    return nullptr;
}

static void *LocationHookImpl(void *self, void *methodInfo)
{
    if (g_strNew && self)
    {
        const char *p = ResolvePathFromAssemblyObject(self);
        if (p && *p)
        {
            LOC_LOG("Resolved Assembly Location: %s", p);
            return g_strNew(p);
        }
        void *managed = ResolveManagedLocationFallback(self);
        if (managed)
            return managed;
        void *managedObj = LocationResolverCallObject(self);
        if (managedObj)
        {
            LOGI("mod2 locobj: resolved via managed object fallback");
            return managedObj;
        }
        static int failLogs = 0;
        if (failLogs < 20)
        {
            ++failLogs;
            LOGI("mod2 locfail#%d: resolve failed for self=%p", failLogs, self);
        }
    }

    void *origRes = g_origGetLocation ? ((void *(*)(void *, void *))g_origGetLocation)(self, methodInfo) : nullptr;
    return origRes;
}

extern "C"
{

    int mod2_init(void)
    {
        if (g_ready)
            return 1;

        void *h = dlopen("libil2cpp.so", RTLD_NOW | RTLD_GLOBAL);
        if (!h)
        {
            LOGE("mod2_init: dlopen libil2cpp.so failed: %s", dlerror());
            return 0;
        }

        g_invoke = (Il2CppRuntimeInvoke)dlsym(h, "il2cpp_runtime_invoke");
        g_strNew = (Il2CppStringNew)dlsym(h, "il2cpp_string_new");
        g_unbox = (Il2CppObjectUnbox)dlsym(h, "il2cpp_object_unbox");
        if (!g_invoke || !g_strNew)
        {
            LOGE("mod2_init: missing il2cpp symbols invoke=%p strNew=%p", (void *)g_invoke, (void *)g_strNew);
            return 0;
        }
        LOGI("mod2_init: il2cpp resolved (invoke=%p strNew=%p unbox=%p)", (void *)g_invoke, (void *)g_strNew, (void *)g_unbox);

        g_objGetClass = (ObjGetClassFn)dlsym(h, "il2cpp_object_get_class");
        g_classGetFields = (ClassGetFieldsFn)dlsym(h, "il2cpp_class_get_fields");
        g_fieldGetName = (FieldGetNameFn)dlsym(h, "il2cpp_field_get_name");
        g_fieldGetValue = (FieldGetValueFn)dlsym(h, "il2cpp_field_get_value");
        g_asmGetImage = (AsmGetImageFn)dlsym(h, "il2cpp_assembly_get_image");
        g_imgGetName = (ImageGetNameFn)dlsym(h, "il2cpp_image_get_name");
        g_domainGet = (DomainGetFn)dlsym(h, "il2cpp_domain_get");
        g_domainAsm = (DomainAssembliesFn)dlsym(h, "il2cpp_domain_get_assemblies");
        LOGI("mod2_init: reflection syms obj=%p fields=%p name=%p value=%p img=%p imgName=%p",
             (void *)g_objGetClass, (void *)g_classGetFields, (void *)g_fieldGetName,
             (void *)g_fieldGetValue, (void *)g_asmGetImage, (void *)g_imgGetName);

        void *dh = dlopen("libdobby.so", RTLD_NOW | RTLD_GLOBAL);
        if (!dh)
        {
            LOGE("mod2_init: dlopen libdobby.so failed: %s", dlerror());
            return 0;
        }
        g_dobbyHook = (DobbyHookFn)dlsym(dh, "DobbyHook");
        g_dobbyUnhook = (DobbyDestroyFn)dlsym(dh, "DobbyDestroy");
        if (!g_dobbyHook || !g_dobbyUnhook)
        {
            LOGE("mod2_init: missing Dobby symbols hook=%p destroy=%p", (void *)g_dobbyHook, (void *)g_dobbyUnhook);
            return 0;
        }
        LOGI("mod2_init: dobby resolved (hook=%p)", (void *)g_dobbyHook);

        g_typeFromReflection = (Il2CppTypeFromReflectionFn)dlsym(
            h,
            "il2cpp_type_from_reflection");

        g_classFromType = (Il2CppClassFromTypeFn)dlsym(
            h,
            "il2cpp_class_from_type");

        g_classGetName = (Il2CppClassGetNameFn)dlsym(
            h,
            "il2cpp_class_get_name");

        g_arrayLength = (Il2CppArrayLengthFn)dlsym(
            h,
            "il2cpp_array_length");

        g_resolveIcall = (Il2CppResolveIcallFn)dlsym(
            h,
            "il2cpp_resolve_icall");

        LOGI(
            "mod2_init: type_from_reflection=%p class_from_type=%p "
            "class_get_name=%p resolve_icall=%p",
            (void *)g_typeFromReflection,
            (void *)g_classFromType,
            (void *)g_classGetName,
            (void *)g_resolveIcall);

        g_methodParamCount = (Il2CppMethodGetParamCountFn)dlsym(h, "il2cpp_method_get_param_count");
        g_methodParam = (Il2CppMethodGetParamFn)dlsym(h, "il2cpp_method_get_param");
        g_typeGetType = (Il2CppTypeGetTypeFn)dlsym(h, "il2cpp_type_get_type");
        g_classValueSize = (Il2CppClassValueSizeFn)dlsym(h, "il2cpp_class_value_size");
        g_fieldGetType = (Il2CppFieldGetTypeFn)dlsym(h, "il2cpp_field_get_type");
        g_methodGetName = (Il2CppMethodGetNameFn)dlsym(h, "il2cpp_method_get_name");
        g_methodGetClass = (Il2CppMethodGetClassFn)dlsym(h, "il2cpp_method_get_class");
        g_methodGetFlags = (Il2CppMethodGetFlagsFn)dlsym(h, "il2cpp_method_get_flags");
        g_methodGetReturnType = (Il2CppMethodGetReturnTypeFn)dlsym(h, "il2cpp_method_get_return_type");
        LOGI("mod2_init: marshal syms paramCount=%p param=%p typeType=%p valueSize=%p fieldType=%p flags=%p",
             (void *)g_methodParamCount, (void *)g_methodParam, (void *)g_typeGetType,
             (void *)g_classValueSize, (void *)g_fieldGetType, (void *)g_methodGetFlags);

        g_ready = true;
        return 1;
    }

    // HybridCLR stub shadowing

#if defined(__aarch64__)

    typedef struct
    {
        void *target;
        void *trampoline;
        uint64_t rawX0;
        int active;
    } ShadowSlot;

    static ShadowSlot g_shadows[64];
    static int g_shadowCount = 0;
    static int g_shadowLogs = 0;

    static void ShadowHelper(uint64_t idx, uint64_t raw)
    {
        if (idx < (uint64_t)g_shadowCount)
            g_shadows[idx].rawX0 = raw;
    }

    static uint32_t ShadowLdrLit(int rt, int imm19)
    {
        return 0x58000000u | ((uint32_t)imm19 << 5) | (uint32_t)(rt & 31);
    }
    static uint32_t ShadowStpX(int rt, int rt2, int rn, int off)
    {
        uint32_t imm7 = (uint32_t)(off / 8) & 0x7Fu;
        return 0xA9000000u | (imm7 << 15) | ((uint32_t)(rt2 & 31) << 10) |
               ((uint32_t)(rn & 31) << 5) | (uint32_t)(rt & 31);
    }
    static uint32_t ShadowLdpX(int rt, int rt2, int rn, int off)
    {
        uint32_t imm7 = (uint32_t)(off / 8) & 0x7Fu;
        return 0xA9400000u | (imm7 << 15) | ((uint32_t)(rt2 & 31) << 10) |
               ((uint32_t)(rn & 31) << 5) | (uint32_t)(rt & 31);
    }
    static uint32_t ShadowStpQ(int rt, int rt2, int rn, int off)
    {
        uint32_t imm7 = (uint32_t)(off / 16) & 0x7Fu;
        return 0xAD000000u | (imm7 << 15) | ((uint32_t)(rt2 & 31) << 10) |
               ((uint32_t)(rn & 31) << 5) | (uint32_t)(rt & 31);
    }
    static uint32_t ShadowLdpQ(int rt, int rt2, int rn, int off)
    {
        uint32_t imm7 = (uint32_t)(off / 16) & 0x7Fu;
        return 0xAD400000u | (imm7 << 15) | ((uint32_t)(rt2 & 31) << 10) |
               ((uint32_t)(rn & 31) << 5) | (uint32_t)(rt & 31);
    }

    // 35 instructions (140 bytes) + two literals at +144/+152.
    // The whole frame lives on the stack, so the stub stays re-entrant and keeps
    // every argument register (x0..x15, q0..q7, x30) intact for the tail call.
    static void ShadowEmitStub(uint8_t *code, uint32_t idx, void *helper, void *bridge)
    {
        uint32_t ins[35];
        int i = 0;
        ins[i++] = 0xD10443FFu;             // sub  sp, sp, #272
        ins[i++] = 0xF90083FEu;             // str  x30, [sp, #256]
        ins[i++] = ShadowStpX(0, 1, 31, 0); // stp  x0, x1, [sp]
        ins[i++] = ShadowStpX(2, 3, 31, 16);
        ins[i++] = ShadowStpX(4, 5, 31, 32);
        ins[i++] = ShadowStpX(6, 7, 31, 48);
        ins[i++] = ShadowStpX(8, 9, 31, 64); // stp  x8, x9, [sp, #64]
        ins[i++] = ShadowStpX(10, 11, 31, 80);
        ins[i++] = ShadowStpX(12, 13, 31, 96);
        ins[i++] = ShadowStpX(14, 15, 31, 112);
        ins[i++] = ShadowStpQ(0, 1, 31, 128); // stp  q0, q1, [sp, #128]
        ins[i++] = ShadowStpQ(2, 3, 31, 160);
        ins[i++] = ShadowStpQ(4, 5, 31, 192);
        ins[i++] = ShadowStpQ(6, 7, 31, 224);
        ins[i++] = 0xD2800000u | ((uint32_t)(idx & 0xFFFFu) << 5) | 17u; // movz x17, #idx
        ins[i++] = 0xAA0003E1u;                                          // mov  x1, x0   (raw self)
        ins[i++] = 0xAA1103E0u;                                          // mov  x0, x17  (slot)
        ins[i++] = ShadowLdrLit(16, 19);                                 // ldr  x16, [pc, #76] (helper)
        ins[i++] = 0xD63F0200u;                                          // blr  x16
        ins[i++] = ShadowLdpX(0, 1, 31, 0);                              // ldp  x0, x1, [sp]
        ins[i++] = ShadowLdpX(2, 3, 31, 16);
        ins[i++] = ShadowLdpX(4, 5, 31, 32);
        ins[i++] = ShadowLdpX(6, 7, 31, 48);
        ins[i++] = ShadowLdpX(8, 9, 31, 64);
        ins[i++] = ShadowLdpX(10, 11, 31, 80);
        ins[i++] = ShadowLdpX(12, 13, 31, 96);
        ins[i++] = ShadowLdpX(14, 15, 31, 112);
        ins[i++] = ShadowLdpQ(0, 1, 31, 128);
        ins[i++] = ShadowLdpQ(2, 3, 31, 160);
        ins[i++] = ShadowLdpQ(4, 5, 31, 192);
        ins[i++] = ShadowLdpQ(6, 7, 31, 224);
        ins[i++] = 0xF94083FEu;         // ldr  x30, [sp, #256]
        ins[i++] = 0x910443FFu;         // add  sp, sp, #272
        ins[i++] = ShadowLdrLit(17, 5); // ldr  x17, [pc, #20] (bridge)
        ins[i++] = 0xD61F0220u;         // br   x17
        memcpy(code, ins, sizeof(ins));

        uint64_t *lit = (uint64_t *)(code + 144);
        lit[0] = (uint64_t)(uintptr_t)helper;
        lit[1] = (uint64_t)(uintptr_t)bridge;
    }

    //   - patchSize 4  (b)     : +-128MB
    //   - patchSize 12 (adrp)  : +-4GB
    static void *ShadowAllocNear(uint64_t target, int patchSize)
    {
        if (target < 0x10000ULL)
            return nullptr;
        const size_t pageSz = 0x1000;
        const uint64_t base = target & ~0xFFFULL;
        const int64_t maxRange = (patchSize == 4) ? (int64_t)0x07F00000 : (int64_t)0xF0000000;

        const int64_t steps[2] = {0x10000, 0x100000};
        for (int pass = 0; pass < 2; ++pass)
        {
            const int64_t step = steps[pass];
            const int64_t passLimit = (pass == 0) ? (int64_t)0x01000000 : maxRange;
            for (int64_t off = 0; off <= passLimit; off += step)
            {
                for (int dir = 0; dir < 2; ++dir)
                {
                    if (off == 0 && dir == 1)
                        continue;
                    const int64_t sign = (dir == 0) ? 1 : -1;
                    const int64_t cand = (int64_t)base + sign * off;
                    if (cand < (int64_t)0x10000)
                        continue;
                    void *p = mmap((void *)(uintptr_t)cand, pageSz,
                                   PROT_READ | PROT_WRITE | PROT_EXEC,
                                   MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0);
                    if (p == (void *)(uintptr_t)cand)
                        return p;
                    if (p != MAP_FAILED)
                        munmap(p, pageSz);
                }
            }
        }
        if (g_shadowLogs < 10)
        {
            g_shadowLogs++;
            LOGE("shadow: no free page near target=%p (patchSize=%d)", (void *)target, patchSize);
        }
        return nullptr;
    }

    static int ShadowCurrentProt(uintptr_t addr)
    {
        FILE *f = fopen("/proc/self/maps", "r");
        if (!f)
            return -1;
        char line[512];
        int result = -1;
        while (fgets(line, sizeof(line), f))
        {
            unsigned long long start = 0, end = 0;
            char perms[8] = {0};
            if (sscanf(line, "%llx-%llx %7s", &start, &end, perms) != 3)
                continue;
            if (addr < (uintptr_t)start || addr >= (uintptr_t)end)
                continue;
            int prot = 0;
            if (perms[0] == 'r')
                prot |= PROT_READ;
            if (perms[1] == 'w')
                prot |= PROT_WRITE;
            if (perms[2] == 'x')
                prot |= PROT_EXEC;
            result = prot;
            break;
        }
        fclose(f);
        return result;
    }

    // Rewrites the stub at `target` so it branches to `stub`.

    static int ShadowPatchTarget(uint64_t target, void *stub, int patchSize)
    {
        if (patchSize != 4 && patchSize != 12)
            return 0;

        const uint64_t start = target;
        const uint64_t end = target + (uint64_t)patchSize;
        const uint64_t pageStart = start & ~0xFFFULL;
        const uint64_t pageEnd = (end + 0xFFFULL) & ~0xFFFULL;
        size_t len = (size_t)(pageEnd - pageStart);
        if (len == 0)
            len = 0x1000;

        const size_t second = (len > 0x1000) ? (len - 0x1000) : 0;
        const int prevA = ShadowCurrentProt(pageStart);
        const int prevB = second ? ShadowCurrentProt(pageStart + 0x1000) : prevA;

        if (prevA < 0)
        {
            if (g_shadowLogs < 10)
            {
                g_shadowLogs++;
                LOGE("shadow: %p is not inside any mapping; refusing to patch", (void *)pageStart);
            }
            return 0;
        }

        if (mprotect((void *)pageStart, 0x1000, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
        {
            if (g_shadowLogs < 10)
            {
                g_shadowLogs++;
                LOGE("shadow: mprotect RWX failed at %p errno=%d", (void *)pageStart, errno);
            }
            return 0;
        }
        if (second)
        {
            if (mprotect((void *)(pageStart + 0x1000), second,
                         PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
            {
                mprotect((void *)pageStart, 0x1000, prevA > 0 ? prevA : (PROT_READ | PROT_EXEC));
                if (g_shadowLogs < 10)
                {
                    g_shadowLogs++;
                    LOGE("shadow: mprotect RWX failed on second page %p errno=%d",
                         (void *)(pageStart + 0x1000), errno);
                }
                return 0;
            }
        }

        int rc = 0;
        if (patchSize == 4)
        {
            const int64_t delta = (int64_t)((uintptr_t)stub - (uintptr_t)target);
            if (delta >= -(int64_t)0x08000000 && delta <= (int64_t)0x07FFFFFC && ((delta & 3) == 0))
            {
                uint32_t b = 0x14000000u | (uint32_t)((delta >> 2) & 0x03FFFFFF);
                memcpy((void *)target, &b, 4);
                rc = 1;
            }
        }
        else
        {
            const int64_t pageOff = (int64_t)((uintptr_t)stub & ~0xFFFULL) - (int64_t)(target & ~0xFFFULL);
            const int64_t pg = pageOff >> 12;
            if (pg >= -(int64_t)(1 << 20) && pg < (int64_t)(1 << 20))
            {
                uint32_t immLo = (uint32_t)(pg & 3);
                uint32_t immHi = (uint32_t)((pg >> 2) & 0x7FFFF);
                uint32_t w0 = 0x90000000u | 0x11u | (immLo << 29) | (immHi << 5);
                uint32_t w1 = 0x91000231u | (((uint32_t)((uintptr_t)stub & 0xFFFu)) << 10);
                uint32_t w2 = 0xD61F0220u;
                memcpy((void *)target, &w0, 4);
                memcpy((void *)(target + 4), &w1, 4);
                memcpy((void *)(target + 8), &w2, 4);
                rc = 1;
            }
        }

        if (rc)
            __builtin___clear_cache((char *)start, (char *)end);

        mprotect((void *)pageStart, 0x1000, prevA > 0 ? prevA : (PROT_READ | PROT_EXEC));
        if (second)
            mprotect((void *)(pageStart + 0x1000), second,
                     prevB > 0 ? prevB : (PROT_READ | PROT_EXEC));
        return rc;
    }

    //   4 bytes  : b <off>
    //   12 bytes : adrp xN, page ; add xN, xN, #off ; br xN
    //   16 bytes : ldr xN, [pc, #off] ; br xN ; <literal>
    static int ShadowDecodeAt(uintptr_t target, void **bridgeOut, int *patchSizeOut)
    {
        *bridgeOut = nullptr;
        *patchSizeOut = 0;
        if (!target)
            return 0;

        uint32_t w0 = 0, w1 = 0, w2 = 0;
        memcpy(&w0, (const void *)target, 4);
        memcpy(&w1, (const void *)(target + 4), 4);
        memcpy(&w2, (const void *)(target + 8), 4);

        // b <offset>
        if ((w0 & 0xFC000000u) == 0x14000000u)
        {
            int64_t simm = (int64_t)(w0 & 0x03FFFFFFu);
            if (simm & 0x02000000)
                simm -= 0x04000000;
            *bridgeOut = (void *)(uintptr_t)((int64_t)target + (simm << 2));
            *patchSizeOut = 4;
            return 1;
        }

        // bits 30:29 belong to immlo, so the mask must not constrain them.
        if ((w0 & 0x9F000000u) == 0x90000000u &&
            (w1 & 0xFF000000u) == 0x91000000u &&
            ((w1 >> 5) & 31u) == (w0 & 31u) && (w1 & 31u) == (w0 & 31u) &&
            (w2 & 0xFFFFFC1Fu) == 0xD61F0000u && (((w2 >> 5) & 31u) == (w0 & 31u)))
        {
            int64_t immHi = (int64_t)((w0 >> 5) & 0x7FFFFu);
            if (immHi & 0x40000)
                immHi -= 0x80000;
            int64_t immLo = (int64_t)((w0 >> 29) & 3u);
            int64_t pageOff = ((immHi << 2) | immLo) << 12;
            uintptr_t base = (target & ~0xFFFULL) + (uintptr_t)pageOff;
            uint64_t imm12 = (w1 >> 10) & 0xFFFu;
            int shift = (int)((w1 >> 22) & 1u);
            *bridgeOut = (void *)(base + (uintptr_t)(imm12 << (shift ? 12 : 0)));
            *patchSizeOut = 12;
            return 1;
        }

        // ldr xN, [pc, #off] ; br xN ; <literal>
        if ((w0 & 0xFF000000u) == 0x58000000u && (w1 & 0xFFFFFC1Fu) == 0xD61F0000u &&
            (((w1 >> 5) & 31u) == (w0 & 31u)))
        {
            int64_t simm = (int64_t)((w0 >> 5) & 0x7FFFFu);
            if (simm & 0x40000)
                simm -= 0x80000;
            uintptr_t litAddr = (uintptr_t)((int64_t)target + (simm << 2));
            if (litAddr >= target && litAddr < target + 128)
            {
                uintptr_t bridge = 0;
                memcpy(&bridge, (const void *)litAddr, sizeof(bridge));
                if (bridge)
                {
                    *bridgeOut = (void *)bridge;
                    *patchSizeOut = 16;
                    return 1;
                }
            }
        }

        return 0;
    }

    static int ShadowDecodeTarget(void *methodInfoPtr, void **targetOut, void **bridgeOut,
                                  int *patchSizeOut)
    {
        *targetOut = nullptr;
        *bridgeOut = nullptr;
        *patchSizeOut = 0;
        if (!methodInfoPtr)
            return 0;
        uintptr_t target = 0;
        memcpy(&target, (const void *)methodInfoPtr, 8);
        if (!target)
            return 0;
        *targetOut = (void *)target;
        return ShadowDecodeAt(target, bridgeOut, patchSizeOut);
    }

    static void *ShadowRecoverSelf(void *methodInfoPtr, void *trampoline)
    {
        void *target = nullptr;
        void *bridge = nullptr;
        int patchSize = 0;
        if (!ShadowDecodeTarget(methodInfoPtr, &target, &bridge, &patchSize))
        {
            if (g_shadowLogs < 10)
            {
                g_shadowLogs++;
                uintptr_t tgt = 0;
                memcpy(&tgt, (const void *)methodInfoPtr, 8);
                LOGE("shadow: decode failed methodInfo=%p target=%p", methodInfoPtr, (void *)tgt);
                if (tgt)
                {
                    uint32_t tw[4] = {0};
                    memcpy(tw, (const void *)tgt, sizeof(tw));
                    LOGE("shadow:  target words %08x %08x %08x %08x", tw[0], tw[1], tw[2], tw[3]);
                }
            }
            return nullptr;
        }

        int slot = -1;
        for (int i = 0; i < g_shadowCount; i++)
        {
            if (g_shadows[i].active &&
                (g_shadows[i].trampoline == trampoline || g_shadows[i].target == target))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            if (g_shadowCount >= 64)
                return nullptr;
            slot = g_shadowCount++;
            memset(&g_shadows[slot], 0, sizeof(g_shadows[slot]));
            g_shadows[slot].target = target;
            g_shadows[slot].trampoline = trampoline;
        }

        if (!g_shadows[slot].active)
        {
            if (patchSize != 4 && patchSize != 12)
            {
                if (g_shadowLogs < 10)
                {
                    g_shadowLogs++;
                    LOGE("shadow: patchSize=%d unsupported; self recovery disabled for %p",
                         patchSize, target);
                }
                return nullptr;
            }
            if (!bridge)
                return nullptr;

            uint8_t *stub = (uint8_t *)ShadowAllocNear((uint64_t)(uintptr_t)target, patchSize);
            if (!stub)
                return nullptr;

            ShadowEmitStub(stub, (uint32_t)slot, (void *)&ShadowHelper, bridge);
            __builtin___clear_cache((char *)stub, (char *)stub + 128);

            if (!ShadowPatchTarget((uint64_t)(uintptr_t)target, stub, patchSize))
            {
                munmap(stub, 0x1000);
                return nullptr;
            }
            g_shadows[slot].active = 1;
            LOGI("shadow: installed slot %d target=%p bridge=%p patchSize=%d stub=%p",
                 slot, target, bridge, patchSize, stub);
        }

        if (g_shadows[slot].rawX0)
            return (void *)(uintptr_t)g_shadows[slot].rawX0;
        return nullptr;
    }

#endif // __aarch64__

    // HybridCLR routes AOT calls through 12-byte stubs (adrp/add/br), so the
    // Dobby trampoline must be entered with the real native calling convention
    // (x0..x7 + q0..q7, plus a trailing MethodInfo*) instead of a raw void.

    static int DetectHomogeneousFloatStruct(void *klass, int *count)
    {
        if (!klass || !g_classGetFields || !g_fieldGetType || !g_typeGetType)
            return 0;
        void *iter = nullptr;
        void *field = nullptr;
        int floats = 0, doubles = 0, others = 0;
        while ((field = g_classGetFields(klass, &iter)) != nullptr)
        {
            const void *ft = g_fieldGetType(field);
            int t = ft ? g_typeGetType(ft) : -1;
            if (t == kTypeR4)
                floats++;
            else if (t == kTypeR8)
                doubles++;
            else
                others++;
            if (floats + doubles + others > 4)
                return 0;
        }
        int total = floats + doubles + others;
        if (total == 0 || others != 0)
            return 0;
        if (floats == total)
        {
            *count = floats;
            return 1;
        }
        if (doubles == total)
        {
            *count = doubles;
            return 2;
        }
        return 0;
    }

#if defined(__aarch64__)
    __attribute__((naked)) static void *mod2_call_tramp(void *fn, uint64_t *xr, double *vr)
    {
        __asm__ volatile(
            "stp x29, x30, [sp, #-16]!\n\t"
            "mov x15, x0\n\t"
            "mov x14, x1\n\t"
            "mov x13, x2\n\t"
            "ldp x0, x1, [x14, #0]\n\t"
            "ldp x2, x3, [x14, #16]\n\t"
            "ldp x4, x5, [x14, #32]\n\t"
            "ldp x6, x7, [x14, #48]\n\t"
            "ldp q0, q1, [x13, #0]\n\t"
            "ldp q2, q3, [x13, #32]\n\t"
            "ldp q4, q5, [x13, #64]\n\t"
            "ldp q6, q7, [x13, #96]\n\t"
            "blr x15\n\t"
            "ldp x29, x30, [sp], #16\n\t"
            "ret\n\t");
    }
#endif

    // Returns the original method result. On success *ok is set to true, on failure
    // (unsupported signature) the caller must not use the return value.
    static void *MarshalAndCallOrig(void *trampoline, void *methodInfoPtr, void *self,
                                    void **args, bool *ok)
    {
        *ok = false;
        if (!trampoline || !methodInfoPtr)
            return nullptr;
        if (!g_methodParamCount || !g_methodParam || !g_typeGetType)
            return nullptr;

        uint32_t argc = g_methodParamCount(methodInfoPtr);
        if (argc > 6)
        {
            static int overLogs = 0;
            if (overLogs++ < 8)
                LOGE("marshal: argc=%u (>6) unsupported for method %p", argc, methodInfoPtr);
            return nullptr;
        }

        // A large struct return travels through a hidden "sret" pointer in x0, which
        // shifts every argument. Refuse instead of corrupting memory.
        if (g_methodGetReturnType && g_typeGetType && g_classFromType && g_classValueSize)
        {
            const void *rt = g_methodGetReturnType(methodInfoPtr);
            int rtTag = rt ? g_typeGetType(rt) : kTypeVoid;
            if (rtTag == kTypeValueType || rtTag == kTypeGenericInst)
            {
                void *rk = g_classFromType((void *)rt);
                uint32_t rsize = 0;
                if (rk)
                {
                    uint32_t align = 0;
                    rsize = (uint32_t)g_classValueSize(rk, &align);
                }
                if (rsize > 16)
                {
                    static int sretLogs = 0;
                    if (sretLogs++ < 8)
                        LOGE("marshal: %u-byte struct return (sret) unsupported for method %p",
                             rsize, methodInfoPtr);
                    return nullptr;
                }
            }
        }

        uint64_t xr[8] __attribute__((aligned(16))) = {0, 0, 0, 0, 0, 0, 0, 0};
        double vr[8] __attribute__((aligned(16))) = {0};

        bool isStatic = (self == nullptr);
        if (g_methodGetFlags)
        {
            uint32_t ifl = 0;
            isStatic = (g_methodGetFlags(methodInfoPtr, &ifl) & 0x10u) != 0;
        }

        int xi = 0, vi = 0;
        bool bad = false;

        if (!isStatic)
        {
            if (xi >= 8)
                bad = true;
            else
                xr[xi++] = (uint64_t)(uintptr_t)self;
        }

        for (uint32_t i = 0; i < argc && !bad; i++)
        {
            const void *pt = g_methodParam(methodInfoPtr, i);
            int t = pt ? g_typeGetType(pt) : kTypeI4;
            void *slot = args ? args[i] : nullptr;

            if (t == kTypeR4)
            {
                if (vi >= 8)
                {
                    bad = true;
                    break;
                }
                float f = 0.0f;
                if (slot)
                    memcpy(&f, slot, 4);
                memcpy(&vr[vi], &f, 4);
                vi++;
            }
            else if (t == kTypeR8)
            {
                if (vi >= 8)
                {
                    bad = true;
                    break;
                }
                double d = 0.0;
                if (slot)
                    memcpy(&d, slot, 8);
                memcpy(&vr[vi], &d, 8);
                vi++;
            }
            else if (t == kTypeValueType || t == kTypeGenericInst)
            {
                void *klass = g_classFromType ? g_classFromType((void *)pt) : nullptr;
                uint32_t size = 0;
                if (klass && g_classValueSize)
                {
                    uint32_t align = 0;
                    size = (uint32_t)g_classValueSize(klass, &align);
                }
                int hcount = 0;
                int hfa = klass ? DetectHomogeneousFloatStruct(klass, &hcount) : 0;
                if (hfa == 1)
                {
                    for (int k = 0; k < hcount && vi < 8; k++)
                    {
                        float f = 0.0f;
                        if (slot)
                            memcpy(&f, (const char *)slot + (size_t)k * 4, 4);
                        memcpy(&vr[vi], &f, 4);
                        vi++;
                    }
                }
                else if (hfa == 2)
                {
                    for (int k = 0; k < hcount && vi < 8; k++)
                    {
                        double d = 0.0;
                        if (slot)
                            memcpy(&d, (const char *)slot + (size_t)k * 8, 8);
                        memcpy(&vr[vi], &d, 8);
                        vi++;
                    }
                }
                else if (size > 16)
                {
                    if (xi >= 8)
                    {
                        bad = true;
                        break;
                    }
                    xr[xi++] = (uint64_t)(uintptr_t)slot;
                }
                else
                {
                    uint64_t v = 0;
                    if (slot && size > 0)
                        memcpy(&v, slot, size > 8 ? 8 : size);
                    if (xi >= 8)
                    {
                        bad = true;
                        break;
                    }
                    xr[xi++] = v;
                    if (size > 8)
                    {
                        uint64_t v2 = 0;
                        if (slot)
                            memcpy(&v2, (const char *)slot + 8, size - 8);
                        if (xi >= 8)
                        {
                            bad = true;
                            break;
                        }
                        xr[xi++] = v2;
                    }
                }
            }
            else if (t == kTypeClass || t == kTypeString || t == kTypeArray || t == kTypeObject ||
                     t == kTypeSzArray || t == kTypePtr || t == kTypeFnPtr ||
                     t == kTypeVar || t == kTypeByRef)
            {
                if (xi >= 8)
                {
                    bad = true;
                    break;
                }
                xr[xi++] = (uint64_t)(uintptr_t)slot;
            }
            else
            {
                uint64_t v = 0;
                if (slot)
                {
                    if (t == kTypeI1 || t == kTypeU1 || t == kTypeBoolean || t == kTypeI2 ||
                        t == kTypeU2 || t == kTypeI4 || t == kTypeU4 || t == kTypeChar)
                    {
                        int32_t tmp32 = 0;
                        memcpy(&tmp32, slot, 4);
                        v = (uint64_t)(uint32_t)tmp32;
                    }
                    else
                    {
                        memcpy(&v, slot, 8);
                    }
                }
                if (xi >= 8)
                {
                    bad = true;
                    break;
                }
                xr[xi++] = v;
            }
        }
        if (!bad)
        {
            if (xi < 8)
                xr[xi++] = (uint64_t)(uintptr_t)methodInfoPtr;
            else
                bad = true;
        }
        if (bad)
            return nullptr;

#if defined(__aarch64__)
        *ok = true;
        return mod2_call_tramp(trampoline, xr, vr);
#else
        return nullptr;
#endif
    }

    void *mod2_invoke_orig(void *methodInfoPtr, void *trampoline, void *self,
                           void **args, void **exception)
    {
        if (!g_ready || !methodInfoPtr)
            return nullptr;

        if (trampoline)
        {
            bool instanceMethod = false;
            if (g_methodGetFlags)
            {
                uint32_t f = 0;
                g_methodGetFlags(methodInfoPtr, &f);
                instanceMethod = ((f & 0x10u) == 0u);
            }

            if (instanceMethod && self == nullptr)
            {
#if defined(__aarch64__)
                void *recovered = ShadowRecoverSelf(methodInfoPtr, trampoline);
                if (recovered)
                {
                    static int recLogs = 0;
                    if (recLogs++ < 20)
                    {
                        const char *mn = g_methodGetName ? g_methodGetName(methodInfoPtr) : "?";
                        LOGI("mod2_invoke_orig: recovered self=%p for %s", recovered, mn ? mn : "?");
                    }
                    self = recovered;
                }
                else
#endif
                {
                    static int nullLogs = 0;
                    if (nullLogs++ < 20)
                    {
                        const char *mn = g_methodGetName ? g_methodGetName(methodInfoPtr) : "?";
                        LOGE("mod2_invoke_orig: instance method %s (%p) without self; "
                             "original call skipped instead of crashing",
                             mn ? mn : "?", methodInfoPtr);
                    }
                    return nullptr;
                }
            }

            bool ok = false;
            void *res = MarshalAndCallOrig(trampoline, methodInfoPtr, self, args, &ok);
            if (ok)
                return res;

            static int warnLogs = 0;
            if (warnLogs++ < 20)
            {
                LOGE("mod2_invoke_orig: could not rebuild ABI for method=%p self=%p; "
                     "original call skipped",
                     methodInfoPtr, self);
            }
            return nullptr;
        }

        void *excLocal = exception ? *exception : nullptr;
        void *ret = g_invoke(methodInfoPtr, self, args, &excLocal);
        if (exception)
            *exception = excLocal;
        if (excLocal)
            LOGE("mod2_invoke_orig: pending exception in the original call");
        return ret;
    }

    void mod2_unbox(void *boxedObject, void *outBuffer, int size)
    {
        if (!boxedObject || !outBuffer || size <= 0)
            return;
        if (!g_unbox)
        {
            LOGE("mod2_unbox: il2cpp_object_unbox not resolved");
            return;
        }

        void *p = g_unbox(boxedObject);
        if (!p)
        {
            LOGE("mod2_unbox: unbox(%p) returned null", boxedObject);
            return;
        }

        memcpy(outBuffer, p, (size_t)size);
    }

    static void NormalizePathSlashes(char *path)
    {
        if (!path)
            return;
        for (char *p = path; *p; ++p)
        {
            if (*p == '\\')
                *p = '/';
        }
    }

    void mod2_register_assembly_path(void *assemblyObjectPtr, const char *assemblyName,
                                     const char *absolutePath, void *assemblyNativePtr)
    {
        if (!g_ready || !absolutePath)
            return;
        if (!assemblyName && !assemblyObjectPtr && !assemblyNativePtr)
            return;

        char cleanPath[MAX_PATHLEN];
        strncpy(cleanPath, absolutePath, MAX_PATHLEN - 1);
        cleanPath[MAX_PATHLEN - 1] = '\0';
        NormalizePathSlashes(cleanPath);

        pthread_mutex_lock(&g_pathLock);
        for (int i = 0; i < gPathCount; ++i)
        {
            bool sameName = assemblyName && NameMatches(assemblyName, gNames[i]);
            bool sameObj = assemblyObjectPtr && gAsms[i] == assemblyObjectPtr;
            bool sameNative = assemblyNativePtr && gAsmNative[i] == assemblyNativePtr;
            if (sameName || sameObj || sameNative)
            {
                strncpy(gPaths[i], cleanPath, MAX_PATHLEN - 1);
                gPaths[i][MAX_PATHLEN - 1] = '\0';
                if (assemblyName)
                {
                    strncpy(gNames[i], assemblyName, MAX_PATHLEN - 1);
                    gNames[i][MAX_PATHLEN - 1] = '\0';
                }
                if (assemblyObjectPtr)
                    gAsms[i] = assemblyObjectPtr;
                if (assemblyNativePtr)
                    gAsmNative[i] = assemblyNativePtr;
                pthread_mutex_unlock(&g_pathLock);
                return;
            }
        }
        if (gPathCount < MAX_PATHS)
        {
            int i = gPathCount;
            gAsms[i] = assemblyObjectPtr;
            gAsmNative[i] = assemblyNativePtr;
            strncpy(gNames[i], assemblyName ? assemblyName : "", MAX_PATHLEN - 1);
            gNames[i][MAX_PATHLEN - 1] = '\0';
            strncpy(gPaths[i], cleanPath, MAX_PATHLEN - 1);
            gPaths[i][MAX_PATHLEN - 1] = '\0';
            gPathCount++;
        }
        pthread_mutex_unlock(&g_pathLock);
    }

    int mod2_location_hook_active(void) { return g_locationInstalled ? 1 : 0; }

    int mod2_install_location_hook(void *getLocationMethodPtr)
    {
        if (g_locationInstalled)
            return 1;
        if (!getLocationMethodPtr || !g_ready)
            return 0;

        g_getLocationTarget = getLocationMethodPtr;
        g_origGetLocation = getLocationMethodPtr;

        int rc = g_dobbyHook(g_getLocationTarget, (void *)LocationHookImpl, (void **)&g_origGetLocation);
        g_locationInstalled = (rc == 0);
        if (!g_locationInstalled)
            LOGE("Assembly.get_Location DobbyHook failed rc=%d", rc);
        return g_locationInstalled ? 1 : 0;
    }

    static void *g_addComponentTarget = nullptr;
    static void *g_addComponentOrig = nullptr;

    static void *g_getComponentMethodInfo = nullptr;
    static void *g_getComponentFuncPtr = nullptr;
    static void *g_getComponentIcall = nullptr;

    static bool g_addComponentInstalled = false;
    static int g_addCompLogs = 0;

    static void AddCompLog(const char *fmt, ...)
    {
        if (g_addCompLogs >= 2000)
            return;
        ++g_addCompLogs;

        char buf[512];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(buf, sizeof(buf), fmt, ap);
        va_end(ap);
        LOGI("mod2 ac#%d: %s", g_addCompLogs, buf);
    }

    static const char *ObjClassNameForLog(void *obj)
    {
        if (!obj)
            return "null";
        if (g_objGetClass && g_classGetName)
        {
            void *k = g_objGetClass(obj);
            if (k)
            {
                const char *n = g_classGetName(k);
                if (n && *n)
                    return n;
            }
        }
        return "?";
    }

    // Il2CppString layout: klass, monitor, int32 length, utf16 chars[].
    static bool CopyIl2CppStringAscii(void *strObj, char *out, size_t outLen)
    {
        if (!strObj || !out || outLen < 2)
            return false;
        int32_t n = *((const int32_t *)((const char *)strObj + 16));
        if (n <= 0 || n > 512)
            return false;
        const uint16_t *chars = (const uint16_t *)((const char *)strObj + 20);
        size_t j = 0;
        for (int32_t i = 0; i < n && j + 1 < outLen; ++i)
        {
            uint16_t ch = chars[i];
            out[j++] = (ch < 0x80) ? (char)ch : 63;
        }
        out[j] = 0;
        return j > 0;
    }

    static void *ResolveClassFromSystemType(void *typeObj)
    {
        if (!typeObj)
            return nullptr;

        typedef void *(*ClassFromSystemTypeFn)(void *reflectionType);

        static ClassFromSystemTypeFn fn = nullptr;
        static bool searched = false;

        if (!searched)
        {
            searched = true;

            void *h = dlopen("libil2cpp.so", RTLD_NOW | RTLD_GLOBAL);

            if (h)
            {
                fn = (ClassFromSystemTypeFn)dlsym(
                    h,
                    "il2cpp_class_from_system_type");
            }
        }

        if (!fn)
            return nullptr;

        return fn(typeObj);
    }

    static const char *TypeNameForLog(void *typeObj)
    {
        if (!typeObj)
            return "null";

        // First try the real Il2CppClass behind System.Type.

        void *klass = ResolveClassFromSystemType(typeObj);

        if (klass && g_classGetName)
        {
            const char *name = g_classGetName(klass);

            if (name && *name)
                return name;
        }

        // Fallback to the managed System.Type.FullName resolver.

        const char *oc = ObjClassNameForLog(typeObj);

        if (!oc)
            return "?";

        if (g_typeFullNameMethodInfo && g_invoke)
        {
            static char nameBufs[8][256];
            static int nameSlot = 0;

            void *exc = nullptr;

            void *strObj = g_invoke(
                g_typeFullNameMethodInfo,
                typeObj,
                nullptr,
                &exc);

            nameSlot = (nameSlot + 1) & 7;

            if (!exc &&
                CopyIl2CppStringAscii(
                    strObj,
                    nameBufs[nameSlot],
                    sizeof(nameBufs[0])))
            {
                return nameBufs[nameSlot];
            }
        }

        return oc;
    }

    static void *ResolveInternalAddComponentWithType()
    {
        if (!g_resolveIcall)
        {
            LOGE(
                "ResolveInternalAddComponentWithType: "
                "il2cpp_resolve_icall is unavailable.");
            return nullptr;
        }

        static const char *kName =
            "UnityEngine.GameObject::Internal_AddComponentWithType(System.Type)";

        void *target = g_resolveIcall(kName);

        LOGI(
            "Resolved ICall %s -> %p",
            kName,
            target);

        return target;
    }

    static void *ResolveGameObjectGetComponentIcall()
    {
        if (!g_resolveIcall)
        {
            LOGE(
                "ResolveGameObjectGetComponentIcall: "
                "il2cpp_resolve_icall is unavailable.");
            return nullptr;
        }

        static const char *kName =
            "UnityEngine.GameObject::GetComponent(System.Type)";

        void *target = g_resolveIcall(kName);

        LOGI(
            "Resolved ICall %s -> %p",
            kName,
            target);

        return target;
    }

    struct Mod2CtorComponentEntry
    {
        void *self;
        void *klass;
        void *component;
    };

    struct Mod2CtorFrame
    {
        void *self;
        int cacheBegin;
    };

    static thread_local Mod2CtorComponentEntry g_ctorComponentCache[128];
    static thread_local int g_ctorComponentCacheCount = 0;

    static thread_local Mod2CtorFrame g_ctorFrames[8];
    static thread_local int g_ctorFrameDepth = 0;
    static void *GetComponentTypeClass(void *type)
    {
        if (!type)
            return nullptr;

        void *klass = ResolveClassFromSystemType(type);

        if (klass)
            return klass;

        return nullptr;
    }

    static void *FindCtorCachedComponent(void *self, void *klass)
    {
        if (!self || !klass || g_ctorFrameDepth <= 0)
            return nullptr;

        Mod2CtorFrame &frame = g_ctorFrames[g_ctorFrameDepth - 1];

        if (frame.self != self)
            return nullptr;

        for (int i = g_ctorComponentCacheCount - 1; i >= frame.cacheBegin; --i)
        {
            Mod2CtorComponentEntry &entry = g_ctorComponentCache[i];

            if (entry.self == self &&
                entry.klass == klass &&
                entry.component)
            {
                return entry.component;
            }
        }

        return nullptr;
    }

    static void RememberCtorComponent(void *self, void *klass, void *component)
    {
        if (!self || !klass || !component || g_ctorFrameDepth <= 0)
            return;

        Mod2CtorFrame &frame = g_ctorFrames[g_ctorFrameDepth - 1];

        if (frame.self != self)
            return;

        for (int i = frame.cacheBegin; i < g_ctorComponentCacheCount; ++i)
        {
            Mod2CtorComponentEntry &entry = g_ctorComponentCache[i];

            if (entry.self == self && entry.klass == klass)
            {
                entry.component = component;
                return;
            }
        }

        if (g_ctorComponentCacheCount >=
            (int)(sizeof(g_ctorComponentCache) / sizeof(g_ctorComponentCache[0])))
        {
            return;
        }

        Mod2CtorComponentEntry &entry =
            g_ctorComponentCache[g_ctorComponentCacheCount++];

        entry.self = self;
        entry.klass = klass;
        entry.component = component;
    }

    static void *GetComponentExisting(void *self, void *type)
    {
        if (!self || !type)
            return nullptr;

        // Preferred path:
        // direct native Unity Internal Call.
        if (g_getComponentIcall)
        {
            typedef void *(*GetComponentIcallFn)(
                void *,
                void *);

            void *result =
                ((GetComponentIcallFn)g_getComponentIcall)(
                    self,
                    type);

            if (result)
                return result;
        }

        // Fallback:
        // managed IL2CPP wrapper with MethodInfo.
        if (g_getComponentFuncPtr &&
            g_getComponentMethodInfo)
        {
            typedef void *(*GetComponentFn)(
                void *,
                void *,
                void *);

            void *result =
                ((GetComponentFn)g_getComponentFuncPtr)(
                    self,
                    type,
                    g_getComponentMethodInfo);

            if (result)
                return result;
        }

        // Last fallback:
        // runtime_invoke through MethodInfo.
        if (g_invoke &&
            g_getComponentMethodInfo)
        {
            void *args[1] = {type};
            void *exception = nullptr;

            void *result =
                g_invoke(
                    g_getComponentMethodInfo,
                    self,
                    args,
                    &exception);

            if (exception)
            {
                static int exceptionLogs = 0;

                if (exceptionLogs++ < 10)
                {
                    LOGE(
                        "GetComponent runtime invoke exception "
                        "self=%p type=%p methodInfo=%p exc=%p",
                        self,
                        type,
                        g_getComponentMethodInfo,
                        exception);
                }

                return nullptr;
            }

            if (result)
                return result;
        }

        return nullptr;
    }

    static void *AddComponentHook(
        void *self,
        void *type)
    {
        if (!self || !type)
            return nullptr;

        const char *typeName =
            TypeNameForLog(type);

        void *typeClass =
            GetComponentTypeClass(type);

        AddCompLog(
            "Internal_AddComponentWithType(%s) self=%p type=%p class=%p",
            typeName,
            self,
            type,
            typeClass);

        // First check the constructor-local cache.
        if (g_ctorFrameDepth > 0 && typeClass)
        {
            void *cached =
                FindCtorCachedComponent(
                    self,
                    typeClass);

            if (cached)
            {
                AddCompLog(
                    "CACHE HIT %s -> %p",
                    typeName,
                    cached);

                return cached;
            }
        }

        // Ask Unity whether the component already exists.
        void *existing =
            GetComponentExisting(
                self,
                type);

        if (existing)
        {
            AddCompLog(
                "EXISTING %s -> %p",
                typeName,
                existing);

            if (typeClass)
            {
                RememberCtorComponent(
                    self,
                    typeClass,
                    existing);
            }

            return existing;
        }

        // No existing component:
        // perform the real native operation.
        if (!g_addComponentOrig)
            return nullptr;

        typedef void *(*AddComponentFn)(
            void *,
            void *);

        void *result =
            ((AddComponentFn)g_addComponentOrig)(
                self,
                type);

        AddCompLog(
            "CREATED %s -> %p",
            typeName,
            result);

        if (result && typeClass)
        {
            RememberCtorComponent(
                self,
                typeClass,
                result);

            return result;
        }

        // Last recovery attempt.
        void *recovered =
            GetComponentExisting(
                self,
                type);

        if (recovered)
        {
            AddCompLog(
                "RECOVERED %s -> %p",
                typeName,
                recovered);

            if (typeClass)
            {
                RememberCtorComponent(
                    self,
                    typeClass,
                    recovered);
            }

            return recovered;
        }

        AddCompLog(
            "FAILED %s",
            typeName);

        return nullptr;
    }

    int mod2_install_addcomponent_hook(
        void *addComponentMethodPtr,
        void *getComponentMethodInfo,
        void *getComponentFuncPtr)
    {
        if (g_addComponentInstalled)
            return 1;

        if (!g_ready || !g_resolveIcall)
        {
            LOGE(
                "Component hook cannot install: "
                "runtime or il2cpp_resolve_icall unavailable.");
            return 0;
        }

        if (Mod2FlagNextToLog(
                "no_addcomponent_hook.flag"))
        {
            LOGI(
                "AddComponent hook skipped "
                "(no_addcomponent_hook.flag present).");

            return 0;
        }

        // Keep the managed GetComponent information as a fallback.
        g_getComponentMethodInfo =
            getComponentMethodInfo;

        g_getComponentFuncPtr =
            getComponentFuncPtr;

        // Resolve the real native Unity Internal Calls.
        g_getComponentIcall =
            ResolveGameObjectGetComponentIcall();

        void *internalTarget =
            ResolveInternalAddComponentWithType();

        if (!internalTarget)
        {
            LOGE(
                "Could not install Component hook: "
                "Internal_AddComponentWithType target not found.");

            return 0;
        }

        if (!g_getComponentIcall)
        {
            LOGE(
                "Could not install Component hook: "
                "GameObject.GetComponent(System.Type) ICall not found.");

            return 0;
        }

        g_addComponentTarget =
            internalTarget;

        int rc =
            g_dobbyHook(
                g_addComponentTarget,
                (void *)AddComponentHook,
                (void **)&g_addComponentOrig);

        g_addComponentInstalled =
            (rc == 0);

        if (!g_addComponentInstalled)
        {
            LOGE(
                "Internal_AddComponentWithType DobbyHook "
                "failed rc=%d target=%p",
                rc,
                g_addComponentTarget);
        }
        else
        {
            LOGI(
                "GameObject.Internal_AddComponentWithType "
                "native compat hook installed "
                "(target=%p orig=%p getComponent=%p publicAdd=%p).",
                g_addComponentTarget,
                g_addComponentOrig,
                g_getComponentIcall,
                addComponentMethodPtr);
        }

        return g_addComponentInstalled ? 1 : 0;
    }

    typedef struct
    {
        void *klass;
        void *monitor;
    } Il2CppObjectHeaderCompat;
    typedef struct
    {
        Il2CppObjectHeaderCompat obj;
        void *bounds;
        uintptr_t max_length;
        void *vector[1];
    } Il2CppArrayHeaderCompat;

    static void *g_gameObjectCtorTarget = nullptr;
    static void *g_gameObjectCtorOrig = nullptr;
    static bool g_gameObjectCtorInstalled = false;
    static int g_ctorLogs = 0;

    static void CtorLog(const char *fmt, ...)
    {
        if (g_ctorLogs >= 256)
            return;
        ++g_ctorLogs;
        char buf[512];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(buf, sizeof(buf), fmt, ap);
        va_end(ap);
        LOGI("mod2 ctor#%d: %s", g_ctorLogs, buf);
    }

    static void GameObjectCtorHook(
        void *self,
        void *name,
        void *componentsArray,
        void *methodInfo)
    {
        if (!self)
            return;

        // Each constructor gets its own frame, so nested GameObject
        // constructors do not contaminate one another.

        bool framePushed = false;

        if (g_ctorFrameDepth <
            (int)(sizeof(g_ctorFrames) / sizeof(g_ctorFrames[0])))
        {
            Mod2CtorFrame &frame =
                g_ctorFrames[g_ctorFrameDepth++];

            frame.self = self;
            frame.cacheBegin = g_ctorComponentCacheCount;

            framePushed = true;
        }
        else
        {
            LOGE(
                "GameObject ctor cache stack overflow for self=%p",
                self);
        }

        Il2CppArrayHeaderCompat *arr =
            (Il2CppArrayHeaderCompat *)componentsArray;

        uintptr_t length =
            (arr && arr->max_length < 64)
                ? arr->max_length
                : 0;

        const char *arrClass =
            arr ? ObjClassNameForLog((void *)arr) : "null";

        CtorLog(
            "enter self=%p comps=%p len=%u class=%s",
            self,
            componentsArray,
            (unsigned)length,
            arrClass);

        for (uintptr_t i = 0; arr && i < length; ++i)
        {
            void *typeObj = arr->vector[i];

            if (typeObj)
            {
                CtorLog(
                    "  comp[%u]=%s type=%p class=%p",
                    (unsigned)i,
                    TypeNameForLog(typeObj),
                    typeObj,
                    GetComponentTypeClass(typeObj));
            }
            else
            {
                CtorLog(
                    "  comp[%u]=null",
                    (unsigned)i);
            }
        }

        // Unity's GameObject(string, Type[]) constructor requests its
        // components through Unity's AddComponent/Internal Call path.
        // The native Internal_AddComponentWithType hook should therefore
        // observe component creation performed by this constructor.

        if (g_gameObjectCtorOrig)
        {
            typedef void (*GameObjectCtorFn)(
                void *,
                void *,
                void *,
                void *);

            ((GameObjectCtorFn)g_gameObjectCtorOrig)(
                self,
                name,
                componentsArray,
                methodInfo);
        }

        // Destroy this constructor's temporary component cache.

        if (framePushed)
        {
            Mod2CtorFrame &frame =
                g_ctorFrames[g_ctorFrameDepth - 1];

            g_ctorComponentCacheCount =
                frame.cacheBegin;

            --g_ctorFrameDepth;
        }

        CtorLog(
            "leave self=%p",
            self);
    }

    static bool Mod2FlagNextToLog(const char *name)
    {
        if (!g_logPath[0] || !name)
            return false;
        char dir[1024];
        strncpy(dir, g_logPath, sizeof(dir) - 1);
        dir[sizeof(dir) - 1] = 0;
        char *slash = strrchr(dir, '/');
        if (!slash)
            return false;
        slash[1] = 0;
        char full[1200];
        snprintf(full, sizeof(full), "%s%s", dir, name);
        return access(full, F_OK) == 0;
    }

    int mod2_install_gameobject_ctor_hook(void *ctorTargetAddr)
    {
        if (g_gameObjectCtorInstalled)
            return 1;
        if (!ctorTargetAddr || !g_ready)
            return 0;
        if (Mod2FlagNextToLog("no_ctor_hook.flag"))
        {
            LOGI("GameObject ctor hook skipped (no_ctor_hook.flag present).");
            return 0;
        }
        g_gameObjectCtorTarget = ctorTargetAddr;

        // Diagnostic + target selection.
        void *hookTarget = ctorTargetAddr;
#if defined(__aarch64__)
        void *impl = nullptr;
        int stubSize = 0;
        if (ShadowDecodeAt((uintptr_t)ctorTargetAddr, &impl, &stubSize))
        {
            LOGI("GameObject ctor entry=%p is a %d-byte stub -> impl=%p",
                 ctorTargetAddr, stubSize, impl);
            if (stubSize == 12 && impl && impl != ctorTargetAddr)
                hookTarget = impl;
        }
        else
        {
            uint32_t w[4] = {0};
            memcpy(w, ctorTargetAddr, sizeof(w));
            LOGI("GameObject ctor entry=%p is not a known stub; words %08x %08x %08x %08x",
                 ctorTargetAddr, w[0], w[1], w[2], w[3]);
        }
#endif

        int rc = g_dobbyHook(hookTarget, (void *)GameObjectCtorHook, (void **)&g_gameObjectCtorOrig);
        g_gameObjectCtorInstalled = (rc == 0);
        if (!g_gameObjectCtorInstalled)
            LOGE("GameObject ctor hook failed rc=%d (target=%p)", rc, hookTarget);
        else
            LOGI("GameObject(string, params Type[]) ctor hook installed (target=%p orig=%p).",
                 hookTarget, g_gameObjectCtorOrig);
        return g_gameObjectCtorInstalled ? 1 : 0;
    }

    // Assembly.GetManifestResourceStream / GetManifestResourceNames
    static void *g_helperStreamMethod = nullptr;
    static void *g_helperNamesMethod = nullptr;
    static void *g_origGetResourceStream = nullptr;
    static void *g_origGetResourceNames = nullptr;
    static bool g_resourceHooksInstalled = false;

    static void *ResourceStreamHook(void *self, void *nameStr, void *methodInfo)
    {
        if (!nameStr)
            return nullptr;
        void *origResult = nullptr;
        if (g_origGetResourceStream)
        {
            typedef void *(*Func)(void *, void *, void *);
            origResult = ((Func)g_origGetResourceStream)(self, nameStr, methodInfo);
        }
        if (origResult)
            return origResult;
        if (g_helperStreamMethod && g_invoke && self)
        {
            void *args[2] = {self, nameStr};
            void *exc = nullptr;
            void *helperRes = g_invoke(g_helperStreamMethod, nullptr, args, &exc);
            if (!exc && helperRes)
                return helperRes;
        }
        return nullptr;
    }

    static void *ResourceNamesHook(void *self, void *methodInfo)
    {
        void *origResult = nullptr;
        if (g_origGetResourceNames)
        {
            typedef void *(*Func)(void *, void *);
            origResult = ((Func)g_origGetResourceNames)(self, methodInfo);
        }
        bool isEmpty = true;
        if (origResult && g_arrayLength)
        {
            if (g_arrayLength(origResult) > 0)
            {
                isEmpty = false;
            }
        }
        else if (origResult && !g_arrayLength)
        {
            isEmpty = false;
        }
        if (!isEmpty)
            return origResult;
        if (g_helperNamesMethod && g_invoke && self)
        {
            void *args[1] = {self};
            void *exc = nullptr;
            void *helperRes = g_invoke(g_helperNamesMethod, nullptr, args, &exc);
            if (!exc && helperRes)
                return helperRes;
        }
        return origResult;
    }

    int mod2_install_resource_hooks(void *targetStreamPtr, void *targetNamesPtr,
                                    void *helperStreamMethod, void *helperNamesMethod)
    {
        if (g_resourceHooksInstalled)
            return 1;
        if (!g_dobbyHook || !targetStreamPtr || !targetNamesPtr || !g_ready)
            return 0;
        g_helperStreamMethod = helperStreamMethod;
        g_helperNamesMethod = helperNamesMethod;
        int rc1 = g_dobbyHook(targetStreamPtr, (void *)ResourceStreamHook, (void **)&g_origGetResourceStream);
        int rc2 = g_dobbyHook(targetNamesPtr, (void *)ResourceNamesHook, (void **)&g_origGetResourceNames);
        g_resourceHooksInstalled = (rc1 == 0 && rc2 == 0);
        if (!g_resourceHooksInstalled)
            LOGE("Assembly resource hooks failed rc1=%d rc2=%d", rc1, rc2);
        else
            LOGI("Assembly resource compat hooks installed.");
        return g_resourceHooksInstalled ? 1 : 0;
    }

    // HeroController.TakeMP

    static void *g_takeMPTarget = nullptr;
    static void *g_takeMPOrig = nullptr;
    static bool g_takeMPInstalled = false;

    static void TakeMP_Hook(void *self, int amount, void *methodInfo)
    {
        if (amount == 1)
        {
            LOGI("mod2: TakeMP(1) intercepted and ignored");
            return;
        }

        if (g_takeMPOrig)
        {
            typedef void (*TakeMPFn)(void *, int, void *);
            ((TakeMPFn)g_takeMPOrig)(self, amount, methodInfo);
        }
    }

    int mod2_install_takemp_hook(void *takeMPMethodPtr)
    {
        if (g_takeMPInstalled)
            return 1;
        if (!takeMPMethodPtr || !g_ready || !g_dobbyHook)
            return 0;

        g_takeMPTarget = takeMPMethodPtr;
        int rc = g_dobbyHook(g_takeMPTarget, (void *)TakeMP_Hook, (void **)&g_takeMPOrig);
        g_takeMPInstalled = (rc == 0);

        if (!g_takeMPInstalled)
        {
            LOGE("HeroController.TakeMP DobbyHook failed rc=%d", rc);
        }
        else
        {
            LOGI("HeroController.TakeMP native hook installed");
        }

        return g_takeMPInstalled ? 1 : 0;
    }
}
