// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using Engine.Renderer;
using Xunit;

namespace Engine.Game.Tests;

public sealed class PunctualShadowPriorityTests
{
    [Theory]
    [InlineData(0.40f, 0.0f, 1)]
    [InlineData(0.40f, 200.0f, 3)]
    [InlineData(0.10f, 40.0f, 5)]
    [InlineData(0.04f, 100.0f, 8)]
    [InlineData(0.01f, 200.0f, 10)]
    public void UpdateCadence_CombinesScreenSizeAndDistance(
        float projectedScreenRadius,
        float distanceToLight,
        int expectedInterval)
    {
        Assert.Equal(
            expectedInterval,
            PunctualShadowPass.GetUpdateIntervalFrames(
                projectedScreenRadius,
                distanceToLight));
    }

    [Theory]
    [InlineData(6, 0.9f, 4)]
    [InlineData(6, 0.6f, 8)]
    [InlineData(6, 0.3f, 16)]
    [InlineData(6, 0.1f, 32)]
    [InlineData(1, 0.9f, 2)]
    [InlineData(1, 0.6f, 4)]
    [InlineData(1, 0.3f, 8)]
    [InlineData(1, 0.1f, 16)]
    public void ResolutionTier_TracksVisualPriority(
        int faceCount,
        float visualPriority,
        int expectedSubdivision)
    {
        Assert.Equal(
            expectedSubdivision,
            PunctualShadowPass.GetPreferredSubdivision(
                faceCount,
                visualPriority));
    }

    [Fact]
    public void SchedulingScore_FavorsVisiblePriorityUntilWorkIsOverdue()
    {
        float nearby = PunctualShadowPass.GetSchedulingScore(
            1.0f,
            1.0f);
        float distantDue = PunctualShadowPass.GetSchedulingScore(
            0.2f,
            1.1f);
        float distantOverdue = PunctualShadowPass.GetSchedulingScore(
            0.2f,
            10.1f);

        Assert.True(nearby > distantDue);
        Assert.True(distantOverdue > nearby);
    }

    [Fact]
    public void ShadowInvalidation_RespectsMeasuredAdmission()
    {
        string directional = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "DirectionalShadowPass.cs");
        string punctual = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "PunctualShadowPass.cs");

        Assert.DoesNotContain("forced: true", directional);
        Assert.Contains(
            "forced: batchFaceCount > frameFaceLimit",
            punctual);
        Assert.Contains("_nextCascadeToUpdate", directional);
        Assert.Contains(
            "GetUnitAllowance(\n                GpuWorkDomain.Shadows)",
            directional);
        Assert.Contains("dirtyCascadeCount - scheduledCascadeCount", directional);
        Assert.Contains(
            "selectedFaceCount + faceCount > frameFaceLimit",
            punctual);
        Assert.Contains(
            "GpuWorkDomain.PunctualShadows,\n                batchFaceCount);",
            punctual);
        Assert.Contains("PendingResolutionReadyFrame", punctual);
        Assert.Contains("_frameNumber + 1", punctual);
        Assert.Contains("if (!updateStatic && !updateDynamic)", punctual);
        Assert.Contains("CommittedLightDirection", punctual);
        Assert.Contains("CommittedLightShapeParams", punctual);
        string pbr = ReadRepositoryFile(
            "Content",
            "shaders",
            "pbr.slang");
        string clusters = ReadRepositoryFile(
            "Content",
            "shaders",
            "cluster_lights.slang");
        Assert.Contains("GetCommittedPunctualLight", pbr);
        Assert.Contains("LightData shadingLight = light", pbr);
        Assert.Contains(
            "PunctualLightIntersectsCluster(\n                            committedLight",
            clusters);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        for (int depth = 0; depth < 10; ++depth)
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException(
            $"Repository file '{Path.Combine(parts)}' was not found.");
    }
}
