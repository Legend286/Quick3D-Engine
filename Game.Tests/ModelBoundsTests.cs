// SPDX-License-Identifier: MIT

using System.Numerics;
using Engine.Assets;
using Xunit;

namespace Engine.Game.Tests;

public sealed class ModelBoundsTests
{
    [Fact]
    public void BoundingSphere_ContainsAllPartSpheres()
    {
        var model = new Model
        {
            Parts =
            [
                new ModelPart
                {
                    BoundsMin = new Vector3(-2.0f, -1.0f, -1.0f),
                    BoundsMax = new Vector3(0.0f, 1.0f, 1.0f)
                },
                new ModelPart
                {
                    BoundsMin = new Vector3(0.0f, -2.0f, -1.0f),
                    BoundsMax = new Vector3(4.0f, 2.0f, 1.0f)
                }
            ]
        };

        (Vector3 center, float radius) =
            ModelLoader.GetBoundingSphere(model);

        Vector3 firstCenter = new(-1.0f, 0.0f, 0.0f);
        float firstRadius = MathF.Sqrt(3.0f);
        Vector3 secondCenter = new(2.0f, 0.0f, 0.0f);
        float secondRadius = 3.0f;
        Assert.True(
            Vector3.Distance(center, firstCenter) +
                firstRadius <= radius + 0.0001f);
        Assert.True(
            Vector3.Distance(center, secondCenter) +
                secondRadius <= radius + 0.0001f);
    }

    [Fact]
    public void BoundingSphere_CanTargetOneStablePartIndex()
    {
        var model = new Model
        {
            Parts =
            [
                new ModelPart
                {
                    BoundsMin = new Vector3(-1.0f),
                    BoundsMax = new Vector3(1.0f)
                },
                new ModelPart
                {
                    BoundsMin = new Vector3(2.0f, 0.0f, 0.0f),
                    BoundsMax = new Vector3(4.0f, 2.0f, 2.0f)
                }
            ]
        };

        (Vector3 center, float radius) =
            ModelLoader.GetBoundingSphere(model, 1);

        Assert.Equal(new Vector3(3.0f, 1.0f, 1.0f), center);
        Assert.Equal(MathF.Sqrt(3.0f), radius, 5);
    }

    [Fact]
    public void BoundingSphere_PrefersGeometrySphereOverAabb()
    {
        var model = new Model
        {
            Parts =
            [
                new ModelPart
                {
                    BoundsMin = new Vector3(-10.0f),
                    BoundsMax = new Vector3(10.0f),
                    BoundsSphereCenter =
                        new Vector3(2.0f, 3.0f, 4.0f),
                    BoundsSphereRadius = 1.5f
                }
            ]
        };

        (Vector3 center, float radius) =
            ModelLoader.GetBoundingSphere(model);

        Assert.Equal(new Vector3(2.0f, 3.0f, 4.0f), center);
        Assert.Equal(1.5f, radius);
    }

    [Fact]
    public void BoundingSphere_IncludesPartLocalOffset()
    {
        var model = new Model
        {
            Parts =
            [
                new ModelPart
                {
                    BoundsSphereCenter = Vector3.Zero,
                    BoundsSphereRadius = 2.0f,
                    LocalOffset =
                        new Vector3(8.0f, 3.0f, -4.0f)
                }
            ]
        };

        (Vector3 center, float radius) =
            ModelLoader.GetBoundingSphere(model);

        Assert.Equal(
            new Vector3(8.0f, 3.0f, -4.0f),
            center);
        Assert.Equal(2.0f, radius);
    }

    [Fact]
    public void SelectPart_PreservesSourceIdentity()
    {
        var model = new Model
        {
            SourcePath = "/Content/models/example.mdl",
            Parts =
            [
                new ModelPart(),
                new ModelPart
                {
                    LocalOffset =
                        new Vector3(5.0f, 2.0f, -1.0f)
                },
                new ModelPart()
            ]
        };

        Model selected = ModelLoader.SelectPart(model, 1);

        Assert.Equal(model.SourcePath, selected.SourcePath);
        Assert.Equal(1, selected.SourcePartIndex);
        Assert.Single(selected.Parts);
        Assert.Equal(
            Vector3.Zero,
            selected.Parts[0].LocalOffset);
    }
}
