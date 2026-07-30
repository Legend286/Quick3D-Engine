// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IEntityStore
{
    event Action? OnWorldCleared;
    System.Collections.Generic.IReadOnlyList<ulong> Entities { get; }
    void Clear();
    ulong CreateEntity();
    ulong RestoreEntity(ulong entity);
    bool DeleteEntity(ulong entity);
    void Set<T>(ulong entity, in T component) where T : unmanaged;
    bool TryGet<T>(ulong entity, out T component) where T : unmanaged;
}
