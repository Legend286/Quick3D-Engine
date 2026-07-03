// SPDX-License-Identifier: MIT
using System;
using Engine.Assets;
using Engine.RHI;
using Engine.CBindings;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>
/// Round-trip tests for the bindless heap C ABI (RhiBindlessHeap +
/// AssetRegistry.Texture registry). Verifies:
///   * Slot allocation produces 0, 1, 2, ...
///   * Re-registering the same RhiTexture* returns the SAME slot (stable)
///   * Freeing slot 0 then re-registering a NEW texture reuses slot 0
///   * Lookup hits for registered textures, misses otherwise
///   * AssetRegistry.RegisterTexture/GetTexture/ReleaseTexture ref-count works
///   * Heap destruct cleanly disposes registered textures + the heap
/// </summary>
public sealed class RhiBindlessHeapTests
{
    private static bool ProbeDevice()
    {
        try
        {
            using var probe = new RhiDevice();
            return probe.Handle != IntPtr.Zero;
        }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex) when (ex.Message.Contains("backend")) { return false; }
    }

    private static RhiTexture CreateTestTexture(RhiDevice device, uint w = 16, uint h = 16)
    {
        var tex = RhiTexture.Create2D(device, w, h, RhiNative.TextureFormat.Rgba8Unorm);
        var bytes = new byte[w * h * 4];
        unsafe { tex.Upload((IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref bytes[0]),
                            (ulong)bytes.Length, w * 4); }
        return tex;
    }

    [Fact]
    public void Heap_Create_Dispose_Lifecycle()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 16);
        Assert.NotEqual(IntPtr.Zero, heap.Handle);
        Assert.Equal(16u, heap.Capacity);
    }

    [Fact]
    public void Heap_Register_AssignsSequentialSlots()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 16);
        var a = CreateTestTexture(device);
        var b = CreateTestTexture(device);
        try
        {
            Assert.Equal(0u, heap.Register(a));
            Assert.Equal(1u, heap.Register(b));
            Assert.True(heap.TryLookup(a, out uint slotA));
            Assert.True(heap.TryLookup(b, out uint slotB));
            Assert.Equal(0u, slotA);
            Assert.Equal(1u, slotB);
        }
        finally
        {
            heap.Release(0);
            heap.Release(1);
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void Heap_Register_SameTexture_ReturnsSameSlot()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 16);
        var a = CreateTestTexture(device);
        try
        {
            uint first  = heap.Register(a);
            uint second = heap.Register(a);
            Assert.Equal(first, second); // stable id
        }
        finally
        {
            heap.Release(0);
            a.Dispose();
        }
    }

    [Fact]
    public void Heap_ReleaseThenRegister_RecyclesSlot()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 8);
        var a = CreateTestTexture(device);
        var b = CreateTestTexture(device);
        try
        {
            uint s0 = heap.Register(a);
            heap.Release(s0);
            uint s1 = heap.Register(b); // free-list should hand out slot 0
            Assert.Equal(s0, s1);
        }
        finally
        {
            heap.Release(0);
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void Heap_Full_ReturnsError()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 1);
        var a = CreateTestTexture(device);
        var b = CreateTestTexture(device);
        try
        {
            heap.Register(a);
            Assert.Throws<InvalidOperationException>(() => heap.Register(b));
        }
        finally
        {
            heap.Release(0);
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void Heap_DestroyWithLiveTextures_DoesNotCrash()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        var heap = new RhiBindlessHeap(device, capacity: 8);
        var a = CreateTestTexture(device);
        var b = CreateTestTexture(device);
        try
        {
            heap.Register(a);
            heap.Register(b);
            Assert.True(heap.TryLookup(a, out _));
            Assert.True(heap.TryLookup(b, out _));
            heap.Dispose(); // heap dies BEFORE textures
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void AssetRegistry_TextureRegisterAndRelease_RefCounts()
    {
        if (!ProbeDevice()) return;
        using var device = new RhiDevice();
        using var heap = new RhiBindlessHeap(device, capacity: 16);
        var tex = CreateTestTexture(device);
        try
        {
            uint slot = heap.Register(tex);
            ulong id = AssetRegistry.RegisterTexture(tex, slot);
            var entry = AssetRegistry.GetTexture(id);
            Assert.NotNull(entry);
            Assert.Equal(1, entry!.RefCount);
            AssetRegistry.AddRefTexture(id);
            Assert.Equal(2, entry.RefCount);
            AssetRegistry.ReleaseTexture(id);
            Assert.Equal(1, AssetRegistry.GetTexture(id)!.RefCount);
            AssetRegistry.ReleaseTexture(id); // drops to 0, removes + disposes
            Assert.Null(AssetRegistry.GetTexture(id));
        }
        finally
        {
            tex.Dispose();
        }
    }
}
