// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace Engine.CBindings;

public static partial class NativeInput
{
    public const string Library = "EngineC";

    public enum EngineKey : uint
    {
        Unknown = 0,
        Space = 1,
        Apostrophe = 2,
        Comma = 3,
        Minus = 4,
        Period = 5,
        Slash = 6,
        Num0 = 7,
        Num1 = 8,
        Num2 = 9,
        Num3 = 10,
        Num4 = 11,
        Num5 = 12,
        Num6 = 13,
        Num7 = 14,
        Num8 = 15,
        Num9 = 16,
        Semicolon = 17,
        Equal = 18,
        A = 19,
        B = 20,
        C = 21,
        D = 22,
        E = 23,
        F = 24,
        G = 25,
        H = 26,
        I = 27,
        J = 28,
        K = 29,
        L = 30,
        M = 31,
        N = 32,
        O = 33,
        P = 34,
        Q = 35,
        R = 36,
        S = 37,
        T = 38,
        U = 39,
        V = 40,
        W = 41,
        X = 42,
        Y = 43,
        Z = 44,
        LeftBracket = 45,
        Backslash = 46,
        RightBracket = 47,
        GraveAccent = 48,
        Escape = 49,
        Enter = 50,
        Tab = 51,
        Backspace = 52,
        Insert = 53,
        Delete = 54,
        Right = 55,
        Left = 56,
        Down = 57,
        Up = 58,
        PageUp = 59,
        PageDown = 60,
        Home = 61,
        End = 62,
        CapsLock = 63,
        ScrollLock = 64,
        NumLock = 65,
        PrintScreen = 66,
        Pause = 67,
        F1 = 68,
        F2 = 69,
        F3 = 70,
        F4 = 71,
        F5 = 72,
        F6 = 73,
        F7 = 74,
        F8 = 75,
        F9 = 76,
        F10 = 77,
        F11 = 78,
        F12 = 79,
        Kp0 = 80,
        Kp1 = 81,
        Kp2 = 82,
        Kp3 = 83,
        Kp4 = 84,
        Kp5 = 85,
        Kp6 = 86,
        Kp7 = 87,
        Kp8 = 88,
        Kp9 = 89,
        KpDecimal = 90,
        KpDivide = 91,
        KpMultiply = 92,
        KpSubtract = 93,
        KpAdd = 94,
        KpEnter = 95,
        KpEqual = 96,
        LeftShift = 97,
        LeftCtrl = 98,
        LeftAlt = 99,
        LeftSuper = 100,
        RightShift = 101,
        RightCtrl = 102,
        RightAlt = 103,
        RightSuper = 104,
        Menu = 105,
        Count = 106
    }

    public enum EngineMouseButton : uint
    {
        Left = 0,
        Right = 1,
        Middle = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineInputEvent
    {
        public uint Type; // 0=KeyDown, 1=KeyUp, 2=MouseMove, 3=MouseDown, 4=MouseUp, 5=Scroll, 6=Char
        public EngineKey Key;
        public EngineMouseButton MouseButton;
        public float MouseX;
        public float MouseY;
        public float ScrollX;
        public float ScrollY;
        public uint CharCode;
    }

    [LibraryImport(Library, EntryPoint = "engine_input_init")]
    public static partial void Init();

    [LibraryImport(Library, EntryPoint = "engine_input_shutdown")]
    public static partial void Shutdown();

    [LibraryImport(Library, EntryPoint = "engine_input_poll")]
    public static partial void Poll();

    [LibraryImport(Library, EntryPoint = "engine_input_is_key_down")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool IsKeyDown(EngineKey key);

    [LibraryImport(Library, EntryPoint = "engine_input_is_mouse_button_down")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool IsMouseButtonDown(EngineMouseButton button);

    [LibraryImport(Library, EntryPoint = "engine_input_get_mouse_pos")]
    public static partial void GetMousePos(out float x, out float y);

    [LibraryImport(Library, EntryPoint = "engine_input_pop_event")]
    public static partial int PopEvent(out EngineInputEvent outEvent);
}
