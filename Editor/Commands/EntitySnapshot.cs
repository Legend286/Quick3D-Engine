// SPDX-License-Identifier: MIT
using System.Linq;
using Engine.RHI;
using Engine.Scene.Components;

namespace Engine.Editor.Commands;

/// <summary>Captures editor-supported components for one scene entity.</summary>
public sealed record EntitySnapshot(
    ulong EntityId,
    Transform? Transform,
    ModelComponent? Model,
    MaterialComponent? Material,
    Camera? Camera,
    DirectionalLightComponent? DirectionalLight,
    PointLightComponent? PointLight,
    SpotLightComponent? SpotLight)
{
    /// <summary>Captures an entity if it exists in the supplied world.</summary>
    public static EntitySnapshot? Capture(
        EcsWorld world,
        ulong entityId)
    {
        if (!world.Entities.Contains(entityId))
            return null;

        return new EntitySnapshot(
            entityId,
            Get<Transform>(world, entityId),
            Get<ModelComponent>(world, entityId),
            Get<MaterialComponent>(world, entityId),
            Get<Camera>(world, entityId),
            Get<DirectionalLightComponent>(world, entityId),
            Get<PointLightComponent>(world, entityId),
            Get<SpotLightComponent>(world, entityId));
    }

    /// <summary>Restores the entity and every captured component.</summary>
    public bool Restore(EcsWorld world)
    {
        if (!world.Entities.Contains(EntityId) &&
            world.RestoreEntity(EntityId) == 0)
        {
            return false;
        }

        Apply(world);
        return true;
    }

    /// <summary>Applies captured component values to an existing entity.</summary>
    public void Apply(EcsWorld world)
    {
        Set(world, EntityId, Transform);
        Set(world, EntityId, Model);
        Set(world, EntityId, Material);
        Set(world, EntityId, Camera);
        Set(world, EntityId, DirectionalLight);
        Set(world, EntityId, PointLight);
        Set(world, EntityId, SpotLight);
    }

    private static T? Get<T>(
        EcsWorld world,
        ulong entityId)
        where T : unmanaged
        => world.TryGet<T>(entityId, out T value)
            ? value
            : null;

    private static void Set<T>(
        EcsWorld world,
        ulong entityId,
        T? value)
        where T : unmanaged
    {
        if (value.HasValue)
            world.Set(entityId, value.Value);
    }
}
