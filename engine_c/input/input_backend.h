#ifndef ENGINE_INPUT_BACKEND_H
#define ENGINE_INPUT_BACKEND_H

#include "input.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    void (*init)(void);
    void (*shutdown)(void);
    void (*poll)(void);
    bool (*is_key_down)(EngineKey key);
    bool (*is_mouse_button_down)(EngineMouseButton button);
    void (*get_mouse_pos)(float* x, float* y);
    int32_t (*pop_event)(EngineInputEvent* out_event);
} EngineInputBackendVTable;

extern EngineInputBackendVTable g_input_vtable;

#ifdef __APPLE__
void input_mac_register(void);
#elif defined(_WIN32)
void input_win32_register(void);
#else
void input_linux_register(void);
#endif

#ifdef __cplusplus
}
#endif
#endif
