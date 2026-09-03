LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)
LOCAL_MODULE    := modding_native
LOCAL_SRC_FILES := modding_native.cpp
LOCAL_CPPFLAGS  := -std=c++17 -fexceptions -fpic -O2
LOCAL_LDLIBS    := -llog -ldl -pthread
include $(BUILD_SHARED_LIBRARY)
