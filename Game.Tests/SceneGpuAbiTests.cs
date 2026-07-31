// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Engine.Renderer;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>
/// Verifies managed scene records shared with Slang shaders.
/// </summary>
public sealed class SceneGpuAbiTests
{
    [Fact]
    public void ScenePushData_MatchesShaderLayout()
    {
        Assert.Equal(480, Marshal.SizeOf<ScenePushData>());
        Assert.Equal(160, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowViewProj)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowParams)).ToInt32());
        Assert.Equal(240, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowViewProj1)).ToInt32());
        Assert.Equal(432, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowSplits)).ToInt32());
        Assert.Equal(448, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowTextureIndices)).ToInt32());
        Assert.Equal(464, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.PunctualShadowFaces)).ToInt32());
        Assert.Equal(472, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.PunctualShadowFaceCount)).ToInt32());
    }

    [Fact]
    public void SharedSceneRecords_MatchShaderLayout()
    {
        Assert.Equal(80, Marshal.SizeOf<PartData>());
        Assert.Equal(
            32,
            Marshal.OffsetOf<PartData>(
                nameof(PartData.LocalOffset))
                .ToInt32());
        Assert.Equal(128, Marshal.SizeOf<InstanceData>());
        Assert.Equal(64, Marshal.SizeOf<LightData>());
        Assert.Equal(128, Marshal.SizeOf<PunctualShadowFaceData>());
        Assert.Equal(
            112,
            Marshal.OffsetOf<PunctualShadowFaceData>(
                nameof(PunctualShadowFaceData.CommittedLightPosition))
                .ToInt32());
    }
}
