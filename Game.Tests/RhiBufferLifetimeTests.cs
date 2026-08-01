// SPDX-License-Identifier: MIT

using System.Reflection;
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RhiBufferLifetimeTests
{
    [Fact]
    public void DeviceAddress_RejectsReleasedHandleBeforeNativeCall()
    {
        ConstructorInfo constructor = typeof(RhiBuffer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IntPtr), typeof(ulong), typeof(bool), typeof(bool)],
            modifiers: null)!;
        var buffer = (RhiBuffer)constructor.Invoke(
            [IntPtr.Zero, 0ul, false, false]);

        Assert.True(buffer.IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            () => _ = buffer.DeviceAddress);
    }
}
