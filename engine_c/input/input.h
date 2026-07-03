#ifndef ENGINE_INPUT_H
#define ENGINE_INPUT_H

#include <stdint.h>
#include <stdbool.h>

#ifndef ENGINE_API
#  if defined(_WIN32)
#    define ENGINE_API __declspec(dllimport)
#  else
#    define ENGINE_API __attribute__((visibility("default")))
#  endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    ENGINE_KEY_UNKNOWN = 0,
    ENGINE_KEY_SPACE = 1,
    ENGINE_KEY_APOSTROPHE = 2,
    ENGINE_KEY_COMMA = 3,
    ENGINE_KEY_MINUS = 4,
    ENGINE_KEY_PERIOD = 5,
    ENGINE_KEY_SLASH = 6,
    ENGINE_KEY_0 = 7,
    ENGINE_KEY_1 = 8,
    ENGINE_KEY_2 = 9,
    ENGINE_KEY_3 = 10,
    ENGINE_KEY_4 = 11,
    ENGINE_KEY_5 = 12,
    ENGINE_KEY_6 = 13,
    ENGINE_KEY_7 = 14,
    ENGINE_KEY_8 = 15,
    ENGINE_KEY_9 = 16,
    ENGINE_KEY_SEMICOLON = 17,
    ENGINE_KEY_EQUAL = 18,
    ENGINE_KEY_A = 19,
    ENGINE_KEY_B = 20,
    ENGINE_KEY_C = 21,
    ENGINE_KEY_D = 22,
    ENGINE_KEY_E = 23,
    ENGINE_KEY_F = 24,
    ENGINE_KEY_G = 25,
    ENGINE_KEY_H = 26,
    ENGINE_KEY_I = 27,
    ENGINE_KEY_J = 28,
    ENGINE_KEY_K = 29,
    ENGINE_KEY_L = 30,
    ENGINE_KEY_M = 31,
    ENGINE_KEY_N = 32,
    ENGINE_KEY_O = 33,
    ENGINE_KEY_P = 34,
    ENGINE_KEY_Q = 35,
    ENGINE_KEY_R = 36,
    ENGINE_KEY_S = 37,
    ENGINE_KEY_T = 38,
    ENGINE_KEY_U = 39,
    ENGINE_KEY_V = 40,
    ENGINE_KEY_W = 41,
    ENGINE_KEY_X = 42,
    ENGINE_KEY_Y = 43,
    ENGINE_KEY_Z = 44,
    ENGINE_KEY_LEFT_BRACKET = 45,
    ENGINE_KEY_BACKSLASH = 46,
    ENGINE_KEY_RIGHT_BRACKET = 47,
    ENGINE_KEY_GRAVE_ACCENT = 48,
    ENGINE_KEY_ESCAPE = 49,
    ENGINE_KEY_ENTER = 50,
    ENGINE_KEY_TAB = 51,
    ENGINE_KEY_BACKSPACE = 52,
    ENGINE_KEY_INSERT = 53,
    ENGINE_KEY_DELETE = 54,
    ENGINE_KEY_RIGHT = 55,
    ENGINE_KEY_LEFT = 56,
    ENGINE_KEY_DOWN = 57,
    ENGINE_KEY_UP = 58,
    ENGINE_KEY_PAGE_UP = 59,
    ENGINE_KEY_PAGE_DOWN = 60,
    ENGINE_KEY_HOME = 61,
    ENGINE_KEY_END = 62,
    ENGINE_KEY_CAPS_LOCK = 63,
    ENGINE_KEY_SCROLL_LOCK = 64,
    ENGINE_KEY_NUM_LOCK = 65,
    ENGINE_KEY_PRINT_SCREEN = 66,
    ENGINE_KEY_PAUSE = 67,
    ENGINE_KEY_F1 = 68,
    ENGINE_KEY_F2 = 69,
    ENGINE_KEY_F3 = 70,
    ENGINE_KEY_F4 = 71,
    ENGINE_KEY_F5 = 72,
    ENGINE_KEY_F6 = 73,
    ENGINE_KEY_F7 = 74,
    ENGINE_KEY_F8 = 75,
    ENGINE_KEY_F9 = 76,
    ENGINE_KEY_F10 = 77,
    ENGINE_KEY_F11 = 78,
    ENGINE_KEY_F12 = 79,
    ENGINE_KEY_KP_0 = 80,
    ENGINE_KEY_KP_1 = 81,
    ENGINE_KEY_KP_2 = 82,
    ENGINE_KEY_KP_3 = 83,
    ENGINE_KEY_KP_4 = 84,
    ENGINE_KEY_KP_5 = 85,
    ENGINE_KEY_KP_6 = 86,
    ENGINE_KEY_KP_7 = 87,
    ENGINE_KEY_KP_8 = 88,
    ENGINE_KEY_KP_9 = 89,
    ENGINE_KEY_KP_DECIMAL = 90,
    ENGINE_KEY_KP_DIVIDE = 91,
    ENGINE_KEY_KP_MULTIPLY = 92,
    ENGINE_KEY_KP_SUBTRACT = 93,
    ENGINE_KEY_KP_ADD = 94,
    ENGINE_KEY_KP_ENTER = 95,
    ENGINE_KEY_KP_EQUAL = 96,
    ENGINE_KEY_LEFT_SHIFT = 97,
    ENGINE_KEY_LEFT_CTRL = 98,
    ENGINE_KEY_LEFT_ALT = 99,
    ENGINE_KEY_LEFT_SUPER = 100,
    ENGINE_KEY_RIGHT_SHIFT = 101,
    ENGINE_KEY_RIGHT_CTRL = 102,
    ENGINE_KEY_RIGHT_ALT = 103,
    ENGINE_KEY_RIGHT_SUPER = 104,
    ENGINE_KEY_MENU = 105,
    ENGINE_KEY_COUNT = 106
} EngineKey;

typedef enum {
    ENGINE_MOUSE_BUTTON_LEFT = 0,
    ENGINE_MOUSE_BUTTON_RIGHT = 1,
    ENGINE_MOUSE_BUTTON_MIDDLE = 2
} EngineMouseButton;

typedef struct {
    uint32_t type; // 0=KeyDown, 1=KeyUp, 2=MouseMove, 3=MouseDown, 4=MouseUp, 5=Scroll, 6=Char
    EngineKey key;
    EngineMouseButton mouse_button;
    float mouse_x;
    float mouse_y;
    float scroll_x;
    float scroll_y;
    uint32_t char_code;
} EngineInputEvent;

ENGINE_API void engine_input_init(void);
ENGINE_API void engine_input_shutdown(void);
ENGINE_API void engine_input_poll(void);

ENGINE_API bool engine_input_is_key_down(EngineKey key);
ENGINE_API bool engine_input_is_mouse_button_down(EngineMouseButton button);
ENGINE_API void engine_input_get_mouse_pos(float* x, float* y);

// Returns 1 if an event was popped, 0 if queue is empty
ENGINE_API int32_t engine_input_pop_event(EngineInputEvent* out_event);

#ifdef __cplusplus
}
#endif
#endif
