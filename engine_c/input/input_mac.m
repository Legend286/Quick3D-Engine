#import <Cocoa/Cocoa.h>
#include "input.h"
#include "input_backend.h"
#include <pthread.h>

#define EVENT_QUEUE_SIZE 1024

static EngineInputEvent g_event_queue[EVENT_QUEUE_SIZE];
static uint32_t g_queue_head = 0;
static uint32_t g_queue_tail = 0;
static pthread_mutex_t g_queue_mutex = PTHREAD_MUTEX_INITIALIZER;

static bool g_keys[256] = {false};
static bool g_mouse_btns[3] = {false};
static float g_mouse_x = 0.0f;
static float g_mouse_y = 0.0f;

static id g_local_monitor = nil;

static void push_event(EngineInputEvent ev) {
    pthread_mutex_lock(&g_queue_mutex);
    uint32_t next_head = (g_queue_head + 1) % EVENT_QUEUE_SIZE;
    if (next_head != g_queue_tail) {
        g_event_queue[g_queue_head] = ev;
        g_queue_head = next_head;
    }
    pthread_mutex_unlock(&g_queue_mutex);
}

static EngineKey map_mac_key(unsigned short keycode) {
    switch (keycode) {
        case 0: return ENGINE_KEY_A;
        case 1: return ENGINE_KEY_S;
        case 2: return ENGINE_KEY_D;
        case 3: return ENGINE_KEY_F;
        case 4: return ENGINE_KEY_H;
        case 5: return ENGINE_KEY_G;
        case 6: return ENGINE_KEY_Z;
        case 7: return ENGINE_KEY_X;
        case 8: return ENGINE_KEY_C;
        case 9: return ENGINE_KEY_V;
        case 11: return ENGINE_KEY_B;
        case 12: return ENGINE_KEY_Q;
        case 13: return ENGINE_KEY_W;
        case 14: return ENGINE_KEY_E;
        case 15: return ENGINE_KEY_R;
        case 16: return ENGINE_KEY_Y;
        case 17: return ENGINE_KEY_T;
        case 18: return ENGINE_KEY_1;
        case 19: return ENGINE_KEY_2;
        case 20: return ENGINE_KEY_3;
        case 21: return ENGINE_KEY_4;
        case 22: return ENGINE_KEY_6;
        case 23: return ENGINE_KEY_5;
        case 24: return ENGINE_KEY_EQUAL;
        case 25: return ENGINE_KEY_9;
        case 26: return ENGINE_KEY_7;
        case 27: return ENGINE_KEY_MINUS;
        case 28: return ENGINE_KEY_8;
        case 29: return ENGINE_KEY_0;
        case 30: return ENGINE_KEY_RIGHT_BRACKET;
        case 31: return ENGINE_KEY_O;
        case 32: return ENGINE_KEY_U;
        case 33: return ENGINE_KEY_LEFT_BRACKET;
        case 34: return ENGINE_KEY_I;
        case 35: return ENGINE_KEY_P;
        case 36: return ENGINE_KEY_ENTER;
        case 37: return ENGINE_KEY_L;
        case 38: return ENGINE_KEY_J;
        case 39: return ENGINE_KEY_APOSTROPHE;
        case 40: return ENGINE_KEY_K;
        case 41: return ENGINE_KEY_SEMICOLON;
        case 42: return ENGINE_KEY_BACKSLASH;
        case 43: return ENGINE_KEY_COMMA;
        case 44: return ENGINE_KEY_SLASH;
        case 45: return ENGINE_KEY_N;
        case 46: return ENGINE_KEY_M;
        case 47: return ENGINE_KEY_PERIOD;
        case 48: return ENGINE_KEY_TAB;
        case 49: return ENGINE_KEY_SPACE;
        case 50: return ENGINE_KEY_GRAVE_ACCENT;
        case 51: return ENGINE_KEY_BACKSPACE;
        case 53: return ENGINE_KEY_ESCAPE;
        case 54: return ENGINE_KEY_RIGHT_SUPER;
        case 55: return ENGINE_KEY_LEFT_SUPER;
        case 56: return ENGINE_KEY_LEFT_SHIFT;
        case 57: return ENGINE_KEY_CAPS_LOCK;
        case 58: return ENGINE_KEY_LEFT_ALT;
        case 59: return ENGINE_KEY_LEFT_CTRL;
        case 60: return ENGINE_KEY_RIGHT_SHIFT;
        case 61: return ENGINE_KEY_RIGHT_ALT;
        case 62: return ENGINE_KEY_RIGHT_CTRL;
        case 65: return ENGINE_KEY_KP_DECIMAL;
        case 67: return ENGINE_KEY_KP_MULTIPLY;
        case 69: return ENGINE_KEY_KP_ADD;
        case 71: return ENGINE_KEY_NUM_LOCK; // Clear on mac keyboard
        case 75: return ENGINE_KEY_KP_DIVIDE;
        case 76: return ENGINE_KEY_KP_ENTER;
        case 78: return ENGINE_KEY_KP_SUBTRACT;
        case 81: return ENGINE_KEY_KP_EQUAL;
        case 82: return ENGINE_KEY_KP_0;
        case 83: return ENGINE_KEY_KP_1;
        case 84: return ENGINE_KEY_KP_2;
        case 85: return ENGINE_KEY_KP_3;
        case 86: return ENGINE_KEY_KP_4;
        case 87: return ENGINE_KEY_KP_5;
        case 88: return ENGINE_KEY_KP_6;
        case 89: return ENGINE_KEY_KP_7;
        case 91: return ENGINE_KEY_KP_8;
        case 92: return ENGINE_KEY_KP_9;
        case 96: return ENGINE_KEY_F5;
        case 97: return ENGINE_KEY_F6;
        case 98: return ENGINE_KEY_F7;
        case 99: return ENGINE_KEY_F3;
        case 100: return ENGINE_KEY_F8;
        case 101: return ENGINE_KEY_F9;
        case 103: return ENGINE_KEY_F11;
        case 105: return ENGINE_KEY_PRINT_SCREEN;
        case 109: return ENGINE_KEY_F10;
        case 111: return ENGINE_KEY_F12;
        case 113: return ENGINE_KEY_PAUSE;
        case 114: return ENGINE_KEY_INSERT; // Help
        case 115: return ENGINE_KEY_HOME;
        case 116: return ENGINE_KEY_PAGE_UP;
        case 117: return ENGINE_KEY_DELETE; // Forward delete
        case 118: return ENGINE_KEY_F4;
        case 119: return ENGINE_KEY_END;
        case 120: return ENGINE_KEY_F2;
        case 121: return ENGINE_KEY_PAGE_DOWN;
        case 122: return ENGINE_KEY_F1;
        case 123: return ENGINE_KEY_LEFT;
        case 124: return ENGINE_KEY_RIGHT;
        case 125: return ENGINE_KEY_DOWN;
        case 126: return ENGINE_KEY_UP;
        default: return ENGINE_KEY_UNKNOWN;
    }
}

static void mac_init(void) {
    g_local_monitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskAny handler:^NSEvent * _Nullable(NSEvent * _Nonnull event) {
        EngineInputEvent ev = {0};

        if (event.type == NSEventTypeKeyDown) {
            EngineKey ek = map_mac_key(event.keyCode);
            if (ek != ENGINE_KEY_UNKNOWN) {
                g_keys[ek] = true;
                ev.type = 0; // KeyDown
                ev.key = ek;
                push_event(ev);
            }
            NSString* chars = [event characters];
            if (chars && [chars length] > 0) {
                unichar c = [chars characterAtIndex:0];
                EngineInputEvent cev = {0};
                cev.type = 6; // Char
                cev.char_code = c;
                push_event(cev);
            }
        } 
        else if (event.type == NSEventTypeKeyUp) {
            EngineKey ek = map_mac_key(event.keyCode);
            if (ek != ENGINE_KEY_UNKNOWN) {
                g_keys[ek] = false;
                ev.type = 1; // KeyUp
                ev.key = ek;
                push_event(ev);
            }
        }
        else if (event.type == NSEventTypeMouseMoved || event.type == NSEventTypeLeftMouseDragged || event.type == NSEventTypeRightMouseDragged) {
            NSPoint loc = [NSEvent mouseLocation];
            NSWindow* win = [event window];
            if (win) {
                NSPoint winLoc = [event locationInWindow];
                NSRect contentRect = [win.contentView frame];
                g_mouse_x = winLoc.x;
                g_mouse_y = contentRect.size.height - winLoc.y;
            } else {
                g_mouse_x = loc.x;
                g_mouse_y = loc.y;
            }
            ev.type = 2; // MouseMove
            ev.mouse_x = g_mouse_x;
            ev.mouse_y = g_mouse_y;
            push_event(ev);
        }
        else if (event.type == NSEventTypeLeftMouseDown) {
            g_mouse_btns[0] = true;
            ev.type = 3;
            ev.mouse_button = ENGINE_MOUSE_BUTTON_LEFT;
            push_event(ev);
        }
        else if (event.type == NSEventTypeLeftMouseUp) {
            g_mouse_btns[0] = false;
            ev.type = 4;
            ev.mouse_button = ENGINE_MOUSE_BUTTON_LEFT;
            push_event(ev);
        }
        else if (event.type == NSEventTypeRightMouseDown) {
            g_mouse_btns[1] = true;
            ev.type = 3;
            ev.mouse_button = ENGINE_MOUSE_BUTTON_RIGHT;
            push_event(ev);
        }
        else if (event.type == NSEventTypeRightMouseUp) {
            g_mouse_btns[1] = false;
            ev.type = 4;
            ev.mouse_button = ENGINE_MOUSE_BUTTON_RIGHT;
            push_event(ev);
        }
        else if (event.type == NSEventTypeScrollWheel) {
            ev.type = 5; // Scroll
            ev.scroll_x = [event deltaX];
            ev.scroll_y = [event deltaY];
            push_event(ev);
        }

        // Must return event so Avalonia continues to process it.
        return event;
    }];
}

static void mac_shutdown(void) {
    if (g_local_monitor) {
        [NSEvent removeMonitor:g_local_monitor];
        g_local_monitor = nil;
    }
}

static void mac_poll(void) {
}

static bool mac_is_key_down(EngineKey key) {
    if (key >= 0 && key < 256) return g_keys[key];
    return false;
}

static bool mac_is_mouse_button_down(EngineMouseButton button) {
    if (button >= 0 && button < 3) return g_mouse_btns[button];
    return false;
}

static void mac_get_mouse_pos(float* x, float* y) {
    if (x) *x = g_mouse_x;
    if (y) *y = g_mouse_y;
}

static int32_t mac_pop_event(EngineInputEvent* out_event) {
    int32_t popped = 0;
    pthread_mutex_lock(&g_queue_mutex);
    if (g_queue_head != g_queue_tail) {
        *out_event = g_event_queue[g_queue_tail];
        g_queue_tail = (g_queue_tail + 1) % EVENT_QUEUE_SIZE;
        popped = 1;
    }
    pthread_mutex_unlock(&g_queue_mutex);
    return popped;
}

void input_mac_register(void) {
    g_input_vtable.init = mac_init;
    g_input_vtable.shutdown = mac_shutdown;
    g_input_vtable.poll = mac_poll;
    g_input_vtable.is_key_down = mac_is_key_down;
    g_input_vtable.is_mouse_button_down = mac_is_mouse_button_down;
    g_input_vtable.get_mouse_pos = mac_get_mouse_pos;
    g_input_vtable.pop_event = mac_pop_event;
}
