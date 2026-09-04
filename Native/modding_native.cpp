#include "modding_native.h"

#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <stdint.h>

#define LOG_TAG "ModdingNative"

static char g_logPath[1024] = {0};

static void FileAppend(const char *line) {
    if (!g_logPath[0]) return;
    FILE *f = fopen(g_logPath, "a");
    if (!f) return;
    fprintf(f, "%s\n", line);
    fclose(f);
}

static void LogPrint(int level, const char *fmt, ...) {
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

extern "C" void mod2_set_log_file(const char *path) {
    if (!path || !*path) { g_logPath[0] = 0; return; }
    strncpy(g_logPath, path, sizeof(g_logPath) - 1);
    g_logPath[sizeof(g_logPath) - 1] = 0;
}

namespace {

typedef void *(*Il2CppRuntimeInvoke)(void *method, void *obj, void **params, void **exc);
typedef void *(*Il2CppStringNew)(const char *str);
typedef void *(*Il2CppObjectUnbox)(void *obj);

Il2CppRuntimeInvoke g_invoke = nullptr;
Il2CppStringNew     g_strNew = nullptr;
Il2CppObjectUnbox   g_unbox  = nullptr;
bool                g_ready  = false;

typedef void *(*ObjGetClassFn)(void *obj);
typedef void *(*ClassGetFieldsFn)(void *klass, void **iter);
typedef const char *(*FieldGetNameFn)(void *field);
typedef void (*FieldGetValueFn)(void *field, void *obj, void *value);
typedef void *(*AsmGetImageFn)(void *assembly);
typedef const char *(*ImageGetNameFn)(void *image);
typedef void *(*DomainGetFn)(void);
typedef void **(*DomainAssembliesFn)(void *domain, size_t *size);

ObjGetClassFn      g_objGetClass    = nullptr;
ClassGetFieldsFn   g_classGetFields = nullptr;
FieldGetNameFn     g_fieldGetName   = nullptr;
FieldGetValueFn    g_fieldGetValue  = nullptr;
AsmGetImageFn      g_asmGetImage    = nullptr;
ImageGetNameFn     g_imgGetName     = nullptr;
DomainGetFn        g_domainGet      = nullptr;
DomainAssembliesFn g_domainAsm      = nullptr;

typedef void *(*Il2CppTypeFromReflectionFn)(void *reflectionType);
typedef void *(*Il2CppClassFromTypeFn)(void *type);
typedef const char *(*Il2CppClassGetNameFn)(void *klass);

Il2CppTypeFromReflectionFn g_typeFromReflection = nullptr;
Il2CppClassFromTypeFn       g_classFromType       = nullptr;
Il2CppClassGetNameFn        g_classGetName        = nullptr;

typedef int (*DobbyHookFn)(void *target, void *replacement, void **outTrampoline);
typedef int (*DobbyDestroyFn)(void *target);

DobbyHookFn    g_dobbyHook   = nullptr;
DobbyDestroyFn g_dobbyUnhook = nullptr;

pthread_mutex_t g_pathLock = PTHREAD_MUTEX_INITIALIZER;
enum { MAX_PATHS = 256, MAX_PATHLEN = 1024 };
void     *gAsms[MAX_PATHS];
void     *gAsmNative[MAX_PATHS];
char      gNames[MAX_PATHS][MAX_PATHLEN];
char      gPaths[MAX_PATHS][MAX_PATHLEN];
int       gPathCount = 0;

const char *FindPathByPtr(void *asmObj) {
    if (!asmObj) return nullptr;
    pthread_mutex_lock(&g_pathLock);
    const char *result = nullptr;
    for (int i = 0; i < gPathCount; ++i)
        if (gAsms[i] == asmObj) { result = gPaths[i]; break; }
    pthread_mutex_unlock(&g_pathLock);
    return result;
}

const char *FindPathByNative(void *asmNative) {
    if (!asmNative) return nullptr;
    pthread_mutex_lock(&g_pathLock);
    const char *result = nullptr;
    for (int i = 0; i < gPathCount; ++i)
        if (gAsmNative[i] == asmNative) { result = gPaths[i]; break; }
    pthread_mutex_unlock(&g_pathLock);
    return result;
}

static bool NameMatches(const char *needle, const char *hay) {
    if (!needle || !hay) return false;
    char a[512], b[512];
    strncpy(a, needle, sizeof(a) - 1); a[sizeof(a)-1] = 0;
    strncpy(b, hay,    sizeof(b) - 1); b[sizeof(b)-1] = 0;
    char *ca = strchr(a, ','); if (ca) *ca = 0;
    char *cb = strchr(b, ','); if (cb) *cb = 0;
    size_t la = strlen(a), lb = strlen(b);
    if (la >= 5 && a[la-4]=='.' && (a[la-3]=='d'||a[la-3]=='D') && (a[la-2]=='l'||a[la-2]=='L') && (a[la-1]=='l'||a[la-1]=='L')) a[la-4] = 0;
    if (lb >= 5 && b[lb-4]=='.' && (b[lb-3]=='d'||b[lb-3]=='D') && (b[lb-2]=='l'||b[lb-2]=='L') && (b[lb-1]=='l'||b[lb-1]=='L')) b[lb-4] = 0;
    return la > 0 && strcasecmp(a, b) == 0;
}

const char *FindPathByName(const char *name) {
    if (!name || !*name) return nullptr;
    pthread_mutex_lock(&g_pathLock);
    const char *result = nullptr;
    for (int i = 0; i < gPathCount; ++i)
        if (NameMatches(name, gNames[i])) { result = gPaths[i]; break; }
    pthread_mutex_unlock(&g_pathLock);
    return result;
}

const char *FindPathByDomainName(const char *nameHint) {
    if (!nameHint || !*nameHint) return nullptr;
    if (!g_domainGet || !g_domainAsm || !g_asmGetImage || !g_imgGetName) return nullptr;
    
    void *domain = g_domainGet();
    if (!domain) return nullptr;
    size_t size = 0;
    void **asms = g_domainAsm(domain, &size);
    if (!asms || size == 0) return nullptr;

    pthread_mutex_lock(&g_pathLock);
    const char *result = nullptr;
    for (size_t a = 0; a < size; ++a) {
        void *asmObj = asms[a];
        if (!asmObj) continue;
        void *img = g_asmGetImage(asmObj);
        if (!img) continue;
        const char *imgName = g_imgGetName(img);
        if (!imgName || !*imgName) continue;
        
        if (NameMatches(imgName, nameHint)) {
            for (int i = 0; i < gPathCount; ++i) {
                if (NameMatches(imgName, gNames[i])) { 
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
static void *g_origGetLocation    = nullptr;
static bool  g_locationInstalled = false;

static bool TryGetImageNameFromPtr(void *asmPtr, char *outBuf, size_t outLen) {
    if (!asmPtr || !g_asmGetImage || !g_imgGetName) return false;
    void *img = g_asmGetImage(asmPtr);
    if (!img) return false;
    const char *n = g_imgGetName(img);
    if (!n || !*n) return false;
    strncpy(outBuf, n, outLen - 1);
    outBuf[outLen - 1] = 0;
    return true;
}

static bool ResolveAssemblyName(const void *self, char *outBuf, size_t outLen) {
    if (!self) return false;

    const char *directNativePath = FindPathByNative((void*)self);
    if (directNativePath) {
        strncpy(outBuf, directNativePath, outLen - 1);
        outBuf[outLen - 1] = 0;
        return true;
    }

    if (!g_objGetClass || !g_classGetFields || !g_fieldGetValue) return false;
    void *klass = g_objGetClass((void*)self);
    if (!klass) return false;

    static const char *kKnown[] = { "_assembly", "m_Assembly", "m_assembly", "_mono_assembly",
                                    "Assembly", "assembly", "_assemblyPtr", "mono_assembly" };
    
    void *iter = nullptr, *field = nullptr;
    while ((field = g_classGetFields(klass, &iter)) != nullptr) {
        const char *fn = g_fieldGetName ? g_fieldGetName(field) : nullptr;
        if (!fn) continue;
        
        bool known = false;
        for (int k = 0; k < (int)(sizeof(kKnown)/sizeof(kKnown[0])); ++k) {
            if (strcasecmp(fn, kKnown[k]) == 0) { known = true; break; }
        }
        if (!known) continue;
        
        void *asmPtr = nullptr;
        g_fieldGetValue(field, (void*)self, &asmPtr);
        if (!asmPtr) continue;

        const char *p = FindPathByNative(asmPtr);
        if (p) {
            strncpy(outBuf, p, outLen - 1);
            outBuf[outLen - 1] = 0;
            return true;
        }

        if (TryGetImageNameFromPtr(asmPtr, outBuf, outLen)) return true;
    }

    uintptr_t *ptrArray = (uintptr_t*)self;
    for (int offset = 1; offset < 12; ++offset) {
        void *candidatePtr = (void*)ptrArray[offset];
        if (!candidatePtr) continue;

        const char *p = FindPathByNative(candidatePtr);
        if (p) {
            strncpy(outBuf, p, outLen - 1);
            outBuf[outLen - 1] = 0;
            return true;
        }

        if (TryGetImageNameFromPtr(candidatePtr, outBuf, outLen)) return true;
    }

    return false;
}

static const char *ResolvePathFromAssemblyObject(void *self) {
    if (!self) return nullptr;

    const char *p = FindPathByPtr(self);
    if (p && *p) return p;

    uintptr_t *ptrArray = (uintptr_t*)self;
    for (int offset = 1; offset < 12; ++offset) {
        void *candidatePtr = (void*)ptrArray[offset];
        if (!candidatePtr) continue;

        p = FindPathByNative(candidatePtr);
        if (p && *p) return p;

        if (g_asmGetImage && g_imgGetName) {
            void *img = g_asmGetImage(candidatePtr);
            if (img) {
                const char *imgName = g_imgGetName(img);
                if (imgName && *imgName) {
                    p = FindPathByName(imgName);
                    if (p && *p) return p;
                    p = FindPathByDomainName(imgName);
                    if (p && *p) return p;
                }
            }
        }
    }

    char nm[512] = {0};
    if (ResolveAssemblyName(self, nm, sizeof(nm))) {
        p = FindPathByName(nm);
        if (p && *p) return p;
        p = FindPathByDomainName(nm);
        if (p && *p) return p;
    }

    return nullptr;
}

static int g_locLogs = 0;
#define LOC_LOG(fmt, ...) do { \
    if (g_locLogs < 40) { \
        ++g_locLogs; \
        LOGI("mod2 loc#%d: " fmt, g_locLogs, ##__VA_ARGS__); \
    } \
} while (0)

static void *LocationHookImpl(void *self) {
    if (g_strNew && self) {
        const char *p = ResolvePathFromAssemblyObject(self);
        if (p && *p) {
            LOC_LOG("Resolved Assembly Location: %s", p);
            return g_strNew(p);
        }
    }

    void *origRes = g_origGetLocation ? ((void *(*)(void *))g_origGetLocation)(self) : nullptr;
    return origRes;
}

extern "C" {

int mod2_init(void) {
    if (g_ready) return 1;

    void *h = dlopen("libil2cpp.so", RTLD_NOW | RTLD_GLOBAL);
    if (!h) { LOGE("mod2_init: dlopen libil2cpp.so failed: %s", dlerror()); return 0; }

    g_invoke = (Il2CppRuntimeInvoke)dlsym(h, "il2cpp_runtime_invoke");
    g_strNew = (Il2CppStringNew)dlsym(h, "il2cpp_string_new");
    g_unbox  = (Il2CppObjectUnbox)dlsym(h, "il2cpp_object_unbox");
    if (!g_invoke || !g_strNew) {
        LOGE("mod2_init: missing il2cpp symbols invoke=%p strNew=%p", (void*)g_invoke, (void*)g_strNew);
        return 0;
    }
    LOGI("mod2_init: il2cpp resolved (invoke=%p strNew=%p unbox=%p)", (void*)g_invoke, (void*)g_strNew, (void*)g_unbox);

    g_objGetClass    = (ObjGetClassFn)dlsym(h, "il2cpp_object_get_class");
    g_classGetFields = (ClassGetFieldsFn)dlsym(h, "il2cpp_class_get_fields");
    g_fieldGetName   = (FieldGetNameFn)dlsym(h, "il2cpp_field_get_name");
    g_fieldGetValue  = (FieldGetValueFn)dlsym(h, "il2cpp_field_get_value");
    g_asmGetImage    = (AsmGetImageFn)dlsym(h, "il2cpp_assembly_get_image");
    g_imgGetName     = (ImageGetNameFn)dlsym(h, "il2cpp_image_get_name");
    g_domainGet      = (DomainGetFn)dlsym(h, "il2cpp_domain_get");
    g_domainAsm      = (DomainAssembliesFn)dlsym(h, "il2cpp_domain_get_assemblies");
    LOGI("mod2_init: reflection syms obj=%p fields=%p name=%p value=%p img=%p imgName=%p",
         (void*)g_objGetClass, (void*)g_classGetFields, (void*)g_fieldGetName,
         (void*)g_fieldGetValue, (void*)g_asmGetImage, (void*)g_imgGetName);

    void *dh = dlopen("libdobby.so", RTLD_NOW | RTLD_GLOBAL);
    if (!dh) { LOGE("mod2_init: dlopen libdobby.so failed: %s", dlerror()); return 0; }
    g_dobbyHook   = (DobbyHookFn)dlsym(dh, "DobbyHook");
    g_dobbyUnhook = (DobbyDestroyFn)dlsym(dh, "DobbyDestroy");
    if (!g_dobbyHook || !g_dobbyUnhook) {
        LOGE("mod2_init: missing Dobby symbols hook=%p destroy=%p", (void*)g_dobbyHook, (void*)g_dobbyUnhook);
        return 0;
    }
    LOGI("mod2_init: dobby resolved (hook=%p)", (void*)g_dobbyHook);

    g_typeFromReflection = (Il2CppTypeFromReflectionFn)dlsym(h, "il2cpp_type_from_reflection");
    g_classFromType       = (Il2CppClassFromTypeFn)dlsym(h, "il2cpp_class_from_type");
    g_classGetName        = (Il2CppClassGetNameFn)dlsym(h, "il2cpp_class_get_name");
    LOGI("mod2_init: type_from_reflection=%p class_from_type=%p class_get_name=%p", (void*)g_typeFromReflection, (void*)g_classFromType, (void*)g_classGetName);

    g_ready = true;
    return 1;
}

void *mod2_invoke_orig(void *methodInfoPtr, void *trampoline, void *self,
                       void **args, void **exception) {
    if (!g_ready || !methodInfoPtr) return nullptr;

    if (trampoline) {
        typedef void* (*NativeMethodFn)(void* self, void** args);
        return ((NativeMethodFn)trampoline)(self, args);
    }

    void *excLocal = exception ? *exception : nullptr;
    void *ret = g_invoke(methodInfoPtr, self, args, &excLocal);
    if (exception) *exception = excLocal;
    if (excLocal) LOGE("mod2_invoke_orig: pending exception in the original call");
    return ret;
}

void mod2_unbox(void *boxedObject, void *outBuffer, int size) {
    if (!boxedObject || !outBuffer || size <= 0) return;
    if (!g_unbox) { 
        LOGE("mod2_unbox: il2cpp_object_unbox not resolved"); 
        return; 
    }
    
    void *p = g_unbox(boxedObject);
    if (!p) { 
        LOGE("mod2_unbox: unbox(%p) returned null", boxedObject); 
        return; 
    }
    
    memcpy(outBuffer, p, (size_t)size);
}

static void NormalizePathSlashes(char *path) {
    if (!path) return;
    for (char *p = path; *p; ++p) {
        if (*p == '\\') *p = '/';
    }
}

void mod2_register_assembly_path(void *assemblyObjectPtr, const char *assemblyName,
                                  const char *absolutePath, void *assemblyNativePtr) {
    if (!g_ready || !absolutePath) return;
    if (!assemblyName && !assemblyObjectPtr && !assemblyNativePtr) return;

    char cleanPath[MAX_PATHLEN];
    strncpy(cleanPath, absolutePath, MAX_PATHLEN - 1);
    cleanPath[MAX_PATHLEN - 1] = '\0';
    NormalizePathSlashes(cleanPath);

    pthread_mutex_lock(&g_pathLock);
    for (int i = 0; i < gPathCount; ++i) {
        bool sameName   = assemblyName && NameMatches(assemblyName, gNames[i]);
        bool sameObj    = assemblyObjectPtr && gAsms[i] == assemblyObjectPtr;
        bool sameNative = assemblyNativePtr && gAsmNative[i] == assemblyNativePtr;
        if (sameName || sameObj || sameNative) {
            strncpy(gPaths[i], cleanPath, MAX_PATHLEN - 1);
            gPaths[i][MAX_PATHLEN - 1] = '\0';
            if (assemblyName) { strncpy(gNames[i], assemblyName, MAX_PATHLEN - 1); gNames[i][MAX_PATHLEN-1] = '\0'; }
            if (assemblyObjectPtr) gAsms[i] = assemblyObjectPtr;
            if (assemblyNativePtr) gAsmNative[i] = assemblyNativePtr;
            pthread_mutex_unlock(&g_pathLock);
            return;
        }
    }
    if (gPathCount < MAX_PATHS) {
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

int mod2_install_location_hook(void *getLocationMethodPtr) {
    if (g_locationInstalled) return 1;
    if (!getLocationMethodPtr || !g_ready) return 0;

    g_getLocationTarget = getLocationMethodPtr;
    g_origGetLocation   = getLocationMethodPtr;

    int rc = g_dobbyHook(g_getLocationTarget, (void *)LocationHookImpl, (void **)&g_origGetLocation);
    g_locationInstalled = (rc == 0);
    if (!g_locationInstalled) LOGE("Assembly.get_Location DobbyHook failed rc=%d", rc);
    return g_locationInstalled ? 1 : 0;
}

static void *g_addComponentTarget     = nullptr;
static void *g_addComponentOrig       = nullptr;
static void *g_getComponentMethodInfo = nullptr;
static bool  g_addComponentInstalled  = false;
static int   g_addCompLogs            = 0;

static void AddCompLog(const char *fmt, ...) {
    if (g_addCompLogs >= 240) return;
    ++g_addCompLogs;

    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    LOGI("mod2 ac#%d: %s", g_addCompLogs, buf);
}

static const char *ObjClassNameForLog(void *obj) {
    if (!obj) return "null";
    if (g_objGetClass && g_classGetName) {
        void *k = g_objGetClass(obj);
        if (k) {
            const char *n = g_classGetName(k);
            if (n && *n) return n;
        }
    }
    return "?";
}

static const char *TypeNameForLog(void *typeObj) {
    if (!typeObj) return "null";
    const char *oc = ObjClassNameForLog(typeObj);
    if (!oc) return "?";

    if (strcmp(oc, "System.Type") != 0 && strcmp(oc, "System.RuntimeType") != 0) return oc;

    if (g_typeFromReflection && g_classFromType && g_classGetName) {
        void *t = g_typeFromReflection(typeObj);
        if (t) {
            void *k = g_classFromType(t);
            if (k) {
                const char *n = g_classGetName(k);
                if (n && *n) return n;
            }
        }
    }
    return oc;
}

static void *AddComponentHook(void *self, void *type, void *methodInfo) {
    if (!self || !type) return nullptr;
    AddCompLog("AddComponent(%s) self=%p typeObjClass=%s", TypeNameForLog(type), self, ObjClassNameForLog(type));

    if (g_getComponentMethodInfo && g_invoke) {
        void *args[1] = { type };
        void *exc = nullptr;
        void *existing = g_invoke(g_getComponentMethodInfo, self, args, &exc);
        AddCompLog("GetComponent(%s) -> existing=%p exc=%p", TypeNameForLog(type), existing, exc);

        if (existing && !exc) {
            if (g_addCompLogs < 60) {
                ++g_addCompLogs;
                LOGI("mod2 ac#%d: Existing component reused in the GameObject %p",
                     g_addCompLogs, self);
            }
            return existing;
        }
    }

    if (g_addComponentOrig) {
        AddCompLog("fallback to orig AddComponent(%s)", TypeNameForLog(type));
        typedef void* (*AddComponentFn)(void*, void*, void*);
        return ((AddComponentFn)g_addComponentOrig)(self, type, methodInfo);
    }

    return nullptr;
}

int mod2_install_addcomponent_hook(void *addComponentMethodPtr, void *getComponentMethodInfo) {
    if (g_addComponentInstalled) return 1;
    if (!addComponentMethodPtr || !getComponentMethodInfo || !g_ready) return 0;

    g_getComponentMethodInfo = getComponentMethodInfo;
    int rc = g_dobbyHook(addComponentMethodPtr, (void *)AddComponentHook, (void **)&g_addComponentOrig);
    g_addComponentInstalled = (rc == 0);
    if (!g_addComponentInstalled) LOGE("GameObject.AddComponent DobbyHook failed rc=%d", rc);
    else LOGI("GameObject.AddComponent native compat hook installed.");
    return g_addComponentInstalled ? 1 : 0;
}

}