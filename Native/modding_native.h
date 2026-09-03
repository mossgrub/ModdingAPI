#ifndef MODDING_NATIVE_H_
#define MODDING_NATIVE_H_

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

int mod2_init(void);

void mod2_set_log_file(const char *path);

void *mod2_invoke_orig(void *methodInfoPtr,
                       void *trampoline,
                       void *self,
                       void **args,
                       void **exception);

int mod2_install_location_hook(void *getLocationMethodPtr);

int mod2_location_hook_active(void);

void mod2_unbox(void *boxedObject, void *outBuffer, int size);

void mod2_register_assembly_path(void *assemblyObjectPtr, const char *assemblyName,
                                 const char *absolutePath, void *assemblyNativePtr);

int mod2_install_addcomponent_hook(void *addComponentMethodPtr, void *getComponentMethodInfo);

#ifdef __cplusplus
}
#endif

#endif
