using System;
using System.Numerics;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct SkyParams {
    public Vector3 SunDirection;
    public float SunAngularRadius;
    public float SunIntensity;
    public float Turbidity;
    public float GroundAlbedo;
    public float Pad0;
    public Vector3 PadSky;
    public float Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ScenePushData {
    public IntPtr Parts;
    public IntPtr Instances;
    public IntPtr Materials;
    public IntPtr Camera;
    public IntPtr Lights;
    public uint LightCount;
    public uint FrameCount;
    public Vector2 Resolution;
    public uint DebugFlags;
    public uint HasGeometry;
    public SkyParams Sky;
}

class Program {
    static void Main() {
        Console.WriteLine($"SkyParams: {Marshal.SizeOf<SkyParams>()}");
        Console.WriteLine($"ScenePushData: {Marshal.SizeOf<ScenePushData>()}");
    }
}
