// SPDX-License-Identifier: MIT

using System.Numerics;
using Engine.RHI;
using Engine.Renderer;
using Engine.Scene;
using Xunit;

namespace Engine.Game.Tests;

public sealed class ProceduralDemoTests
{
    [Fact]
    public void SubmittedTriangleCount_MatchesGeneratedInstances()
    {
        int triangleCount =
            96 * 64 * 64 * 2 +
            48 * 64 * 32 * 2 +
            64 * 12 +
            100 * 100 * 2;

        Assert.Equal(
            ProceduralDemoSceneBuilder.SubmittedTriangleCount,
            triangleCount);
    }

    [Fact]
    public void EvaluateOrbit_PreservesConfiguredRadius()
    {
        var orbit = new OrbitingLightComponent
        {
            Center = new Vector3(3.0f, 2.0f, -4.0f),
            Radius = 12.0f,
            AngularSpeed = 0.7f,
            Phase = 0.3f,
            OrbitHeight = 5.0f,
            VerticalAmplitude = 0.0f,
        };

        Vector3 position =
            ProceduralDemoSceneBuilder.EvaluateOrbit(orbit, 9.0f);
        Vector2 horizontalOffset = new(
            position.X - orbit.Center.X,
            position.Z - orbit.Center.Z);

        Assert.Equal(orbit.Radius, horizontalOffset.Length(), 4);
        Assert.Equal(
            orbit.Center.Y + orbit.OrbitHeight,
            position.Y,
            4);
    }

    [Fact]
    public void StressDefaults_MixCachedAndDynamicShadowWork()
    {
        var definition = new ProceduralDemoDefinition();

        Assert.True(
            definition.AnimatedPointLightCount <
            definition.PointLightCount);
        Assert.True(
            definition.AnimatedSpotLightCount <
            definition.SpotLightCount);
        Assert.True(definition.AnimateObjects);
        Assert.InRange(definition.MovingObjectCount, 1, 63);
    }

    [Fact]
    public void EvaluateOscillation_StaysWithinConfiguredAmplitude()
    {
        var motion = new OscillatingModelComponent
        {
            Origin = new Vector3(2.0f, 3.0f, -4.0f),
            Axis = Vector3.UnitY,
            Amplitude = 2.5f,
            Frequency = 0.75f,
            Phase = 0.3f,
        };

        Vector3 position = GameRenderer.EvaluateOscillation(
            motion,
            8.0f);

        Assert.Equal(motion.Origin.X, position.X, 5);
        Assert.Equal(motion.Origin.Z, position.Z, 5);
        Assert.InRange(
            MathF.Abs(position.Y - motion.Origin.Y),
            0.0f,
            motion.Amplitude);
    }
}
