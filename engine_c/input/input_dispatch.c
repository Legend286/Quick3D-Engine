#include "input.h"
#include "input_backend.h"

EngineInputBackendVTable g_input_vtable = {0};

static void input_register_platform() {
#ifdef __APPLE__
    input_mac_register();
#elif defined(_WIN32)
    input_win32_register();
#else
    input_linux_register();
#endif
}

ENGINE_API void engine_input_init(void) {
    input_register_platform();
    if (g_input_vtable.init) {
        g_input_vtable.init();
    }
}

ENGINE_API void engine_input_shutdown(void) {
    if (g_input_vtable.shutdown) {
        g_input_vtable.shutdown();
    }
}

ENGINE_API void engine_input_poll(void) {
    if (g_input_vtable.poll) {
        g_input_vtable.poll();
    }
}

ENGINE_API bool engine_input_is_key_down(EngineKey key) {
    if (g_input_vtable.is_key_down) {
        return g_input_vtable.is_key_down(key);
    }
    return false;
}

ENGINE_API bool engine_input_is_mouse_button_down(EngineMouseButton button) {
    if (g_input_vtable.is_mouse_button_down) {
        return g_input_vtable.is_mouse_button_down(button);
    }
    return false;
}

ENGINE_API void engine_input_get_mouse_pos(float* x, float* y) {
    if (g_input_vtable.get_mouse_pos) {
        g_input_vtable.get_mouse_pos(x, y);
    } else {
        if (x) *x = 0;
        if (y) *y = 0;
    }
}

ENGINE_API int32_t engine_input_pop_event(EngineInputEvent* out_event) {
    if (g_input_vtable.pop_event) {
        return g_input_vtable.pop_event(out_event);
    }
    return 0;
}
