// SPDX-License-Identifier: MIT
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

public sealed class EcsEntityIdTests
{
    [Fact]
    public void GetIndex_StripsFlecsGenerationBitsForEditorDisplay()
    {
        const ulong packedEntityId = 4294967768ul;

        Assert.Equal(472u, EcsEntityId.GetIndex(packedEntityId));
    }

    [Fact]
    public void GetIndex_PreservesLowIndexWithoutChangingPackedValue()
    {
        const ulong packedEntityId = 0x000000070000002Aul;

        Assert.Equal(42u, EcsEntityId.GetIndex(packedEntityId));
        Assert.Equal(0x000000070000002Aul, packedEntityId);
    }
}
