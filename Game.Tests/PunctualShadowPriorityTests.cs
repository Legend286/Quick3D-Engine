// SPDX-License-Identifier: MIT

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
            3.1f);

        Assert.True(nearby > distantDue);
        Assert.True(distantOverdue > nearby);
    }
}
