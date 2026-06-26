// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IEntityStore
{
    ulong CreateEntity();
    void Set<T>(ulong entity, in T component) where T : struct;
    bool TryGet<T>(ulong entity, out T component) where T : struct;
}
