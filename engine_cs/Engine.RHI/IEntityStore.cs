// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IEntityStore
{
    void Clear();
    ulong CreateEntity();
    void Set<T>(ulong entity, in T component) where T : unmanaged;
    bool TryGet<T>(ulong entity, out T component) where T : unmanaged;
}
