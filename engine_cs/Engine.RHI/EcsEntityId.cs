// SPDX-License-Identifier: MIT
namespace Engine.RHI;

/// <summary>Provides display-safe access to packed Flecs entity identifiers.</summary>
public static class EcsEntityId
{
    /// <summary>
    /// Extracts the entity index stored in the low 32 bits of a packed Flecs ID.
    /// </summary>
    /// <remarks>
    /// The full packed value must remain unchanged for ECS lookups, selection,
    /// deletion, and stale-entity protection.
    /// </remarks>
    public static uint GetIndex(ulong entityId)
        => checked((uint)(entityId & 0xFFFFFFFFul));
}
