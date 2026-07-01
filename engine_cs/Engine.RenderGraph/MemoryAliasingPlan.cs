// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Engine.RenderGraph;

public sealed class MemoryAliasingPlan
{
    public ulong TotalHeapSize { get; init; }
    public IReadOnlyDictionary<ResourceHandle, ulong> ResourceOffsets { get; init; } = null!;
}
