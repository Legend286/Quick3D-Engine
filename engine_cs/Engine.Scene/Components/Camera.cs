using System.Numerics;

namespace Engine.Scene.Components;

public struct Camera
{
    public float FieldOfView;
    public float NearClip;
    public float FarClip;
    
    public static Camera Default => new Camera
    {
        FieldOfView = 60.0f * (MathF.PI / 180.0f),
        NearClip = 0.1f,
        FarClip = 1000.0f
    };
}
