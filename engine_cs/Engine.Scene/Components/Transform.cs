using System.Numerics;

namespace Engine.Scene.Components;

public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;
    
    public static Transform Default => new Transform
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}
