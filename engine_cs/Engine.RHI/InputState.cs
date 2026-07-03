using System.Collections.Generic;

namespace Engine.RHI;

public struct InputState
{
    public List<Engine.CBindings.NativeInput.EngineInputEvent>? Events;

    public float DeltaTime;
    
    // Display
    public float LogicalWidth;
    public float LogicalHeight;
    public float RenderScale;
    
    // Mouse Absolute
    public float MouseX;
    public float MouseY;
    
    // Mouse Relative
    public float MouseDeltaX;
    public float MouseDeltaY;
    
    // Mouse Wheel
    public float MouseWheelX;
    public float MouseWheelY;
    
    // Mouse Buttons
    public bool MouseDownLeft;
    public bool MouseDownRight;
    public bool MouseDownMiddle;
    
    // Key States (Minimal for now)
    public bool KeyW;
    public bool KeyA;
    public bool KeyS;
    public bool KeyD;
    
    // Modifiers
    public bool Shift;
    public bool Ctrl;
    public bool Alt;
}
