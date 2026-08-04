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
        Assert.Equal(720, Marshal.SizeOf<ScenePushData>());
        Assert.Equal(72, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.PartCount)).ToInt32());
        Assert.Equal(160, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowViewProj)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowParams)).ToInt32());
        Assert.Equal(240, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowViewProj1)).ToInt32());
        Assert.Equal(432, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowSplits)).ToInt32());
        Assert.Equal(448, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DirectionalShadowTextureIndices)).ToInt32());
        Assert.Equal(464, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.PunctualShadowFaces)).ToInt32());
        Assert.Equal(472, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.PunctualShadowFaceCount)).ToInt32());
        Assert.Equal(480, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIAtlasParams)).ToInt32());
        Assert.Equal(496, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIOriginAndCountZ)).ToInt32());
        Assert.Equal(512, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIExtentAndFlags)).ToInt32());
        Assert.Equal(528, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGISpecularParams)).ToInt32());
        Assert.Equal(544, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIProbePositions)).ToInt32());
        Assert.Equal(552, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIGridToProbeIndex)).ToInt32());
        Assert.Equal(560, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIProbeWorldKeys)).ToInt32());
        Assert.Equal(568, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIWorldProbeHash)).ToInt32());
        Assert.Equal(576, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIVolumeState)).ToInt32());
        Assert.Equal(584, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIProbeStates)).ToInt32());
        Assert.Equal(592, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.DDGIProbeSpecularStates)).ToInt32());
        Assert.Equal(600, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmLightViewProj)).ToInt32());
        Assert.Equal(664, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmTextureIndex)).ToInt32());
        Assert.Equal(668, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPageTableSize)).ToInt32());
        Assert.Equal(672, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPad0)).ToInt32());
        Assert.Equal(676, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPad1)).ToInt32());
        Assert.Equal(680, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPageTable)).ToInt32());
        Assert.Equal(688, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPageRequests)).ToInt32());
        Assert.Equal(696, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmAllocateQueue)).ToInt32());
        Assert.Equal(704, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmPageRequestCount)).ToInt32());
        Assert.Equal(708, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmAllocateQueueCapacity)).ToInt32());
        Assert.Equal(712, Marshal.OffsetOf<ScenePushData>(nameof(ScenePushData.VsmTailPad)).ToInt32());
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
        Assert.Equal(208, Marshal.SizeOf<MaterialData>());
        Assert.Equal(
            188,
            Marshal.OffsetOf<MaterialData>(
                nameof(MaterialData.OcclusionTexIndex))
                .ToInt32());
        Assert.Equal(
            192,
            Marshal.OffsetOf<MaterialData>(
                nameof(MaterialData.FeatureFlags))
                .ToInt32());
        Assert.Equal(
            204,
            Marshal.OffsetOf<MaterialData>(
                nameof(MaterialData.FeaturePad2))
                .ToInt32());
        Assert.Equal(64, Marshal.SizeOf<LightData>());
        Assert.Equal(160, Marshal.SizeOf<PunctualShadowFaceData>());
        Assert.Equal(
            112,
            Marshal.OffsetOf<PunctualShadowFaceData>(
                nameof(PunctualShadowFaceData.CommittedLightPosition))
                .ToInt32());
        Assert.Equal(
            128,
            Marshal.OffsetOf<PunctualShadowFaceData>(
                nameof(PunctualShadowFaceData.CommittedLightDirection))
                .ToInt32());
        Assert.Equal(
            144,
            Marshal.OffsetOf<PunctualShadowFaceData>(
                nameof(PunctualShadowFaceData.CommittedLightShapeParams))
                .ToInt32());
    }
}
