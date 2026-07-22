using System;
using System.Runtime.InteropServices;
using System.Numerics;

namespace TestSize
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SkyParams
    {
        public Vector3 SunDirection;
        public float SunAngularRadius;
        public float SunIntensity;
        public float Turbidity;
        public float GroundAlbedo;
        public Vector3 pad_sky;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScenePushData
    {
        public ulong Camera;
        public ulong Parts;
        public ulong Instances;
        public ulong Materials;
        public ulong Lights;
        public uint LightCount;
        public uint FrameCount;
        public uint hasGeometry;
        public Vector2 Resolution;
        public SkyParams Sky;
        public uint DebugFlags;
    }

    class Program
    {
        static void Main()
        {
            Console.WriteLine($"Camera: {Marshal.OffsetOf<ScenePushData>("Camera")}");
            Console.WriteLine($"Parts: {Marshal.OffsetOf<ScenePushData>("Parts")}");
            Console.WriteLine($"Instances: {Marshal.OffsetOf<ScenePushData>("Instances")}");
            Console.WriteLine($"Materials: {Marshal.OffsetOf<ScenePushData>("Materials")}");
            Console.WriteLine($"Lights: {Marshal.OffsetOf<ScenePushData>("Lights")}");
            Console.WriteLine($"LightCount: {Marshal.OffsetOf<ScenePushData>("LightCount")}");
            Console.WriteLine($"FrameCount: {Marshal.OffsetOf<ScenePushData>("FrameCount")}");
            Console.WriteLine($"hasGeometry: {Marshal.OffsetOf<ScenePushData>("hasGeometry")}");
            Console.WriteLine($"Resolution: {Marshal.OffsetOf<ScenePushData>("Resolution")}");
            Console.WriteLine($"Sky: {Marshal.OffsetOf<ScenePushData>("Sky")}");
            Console.WriteLine($"DebugFlags: {Marshal.OffsetOf<ScenePushData>("DebugFlags")}");
            Console.WriteLine($"Total Size: {Marshal.SizeOf<ScenePushData>()}");
        }
    }
}
