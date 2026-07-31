// SPDX-License-Identifier: MIT

using Xunit;

namespace Engine.Game.Tests;

public sealed class DDGIShaderContractTests
{
    private static string ResolveShader(string fileName)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "shaders", fileName);
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException(
            $"DDGI shader fixture '{fileName}' was not found.");
    }

    [Fact]
    public void Placement_AcceptsEmptyCellsAndWritesIndirectDrawArgs()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_probe_placement.slang"));

        Assert.DoesNotContain("NodeIntersectsScene", source);
        Assert.DoesNotContain("AabbIntersectsAabb", source);
        Assert.Contains("push.UseSceneTlas == 0u", source);
        Assert.Contains("push.probePositions[slotIndex]", source);
        Assert.Contains("push.gridToProbeIndex[coarseLinearIdx]", source);
        Assert.Contains("push.probeDrawArgs[1] = 1u", source);
        Assert.Contains("InterlockedAdd(push.probeDrawArgs[0], 24u)", source);
    }

    [Fact]
    public void Update_UsesSkyFallbackWhenTlasIsUnavailable()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_probe_update.slang"));

        Assert.Contains("push.UseSceneTlas != 0u", source);
        Assert.Contains("float3 skySky", source);
        Assert.Contains("irradiance[uint2(baseCol + 0u, 0)]", source);
    }

    [Fact]
    public void DebugDraw_UsesSingleSixteenByteIndirectCommand()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_debug.slang"));
        Assert.Contains("uint probeIdx = vid / 24u", source);
        string passSource = File.ReadAllText(ResolvePassSource());
        Assert.Contains("sink.DrawIndirect(_atlas.ProbeDrawArgs, 0, 1, 16)", passSource);
        Assert.Contains("RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Vertex",
            File.ReadAllText(ResolveAtlasSource()));
    }

    private static string ResolveAtlasSource()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "DDGIAtlasResources.cs");
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException("DDGIAtlasResources.cs was not found.");
    }

    private static string ResolvePassSource()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "DDGIDebugPass.cs");
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException("DDGIDebugPass.cs was not found.");
    }
}
