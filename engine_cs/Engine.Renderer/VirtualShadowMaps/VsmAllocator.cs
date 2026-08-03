// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Engine.RHI;
using Engine.CBindings;

namespace Engine.Renderer.VirtualShadowMaps;

/// <summary>
/// Manages physical memory allocation for sparse Virtual Shadow Map pages.
/// Sub-allocates from a single large RhiHeap using fixed-size blocks (typically 64KB).
/// </summary>
public sealed class VsmAllocator : IDisposable
{
    public const ulong PageSize = 64 * 1024; // 64KB per page is typical for Metal tile sizes

    public RhiHeap Heap { get; }
    public uint TotalPages { get; }
    public uint AllocatedPages => TotalPages - (uint)_freePages.Count;

    private readonly Stack<uint> _freePages;
    private bool _isDisposed;

    public VsmAllocator(RhiDevice device, uint pageCount)
    {
        TotalPages = pageCount;
        ulong sizeBytes = pageCount * PageSize;

        Heap = new RhiHeap(device, sizeBytes, RhiNative.HeapUsageSparse | RhiNative.HeapUsageStorage | RhiNative.HeapUsageShaderRead | RhiNative.HeapUsageRenderTarget);

        _freePages = new Stack<uint>((int)pageCount);
        for (int i = (int)pageCount - 1; i >= 0; i--)
        {
            _freePages.Push((uint)i);
        }
    }

    /// <summary>
    /// Allocates a new physical page from the sparse heap.
    /// </summary>
    /// <returns>The physical offset in bytes, or ulong.MaxValue if out of memory.</returns>
    public ulong AllocatePage()
    {
        if (_freePages.Count == 0)
        {
            return ulong.MaxValue;
        }

        uint pageIndex = _freePages.Pop();
        return pageIndex * PageSize;
    }

    /// <summary>
    /// Frees a previously allocated physical page.
    /// </summary>
    public void FreePage(ulong physicalOffset)
    {
        if (physicalOffset % PageSize != 0)
        {
            throw new ArgumentException("Physical offset must be page-aligned", nameof(physicalOffset));
        }

        uint pageIndex = (uint)(physicalOffset / PageSize);
        if (pageIndex >= TotalPages)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalOffset), "Physical offset is out of heap bounds");
        }

        _freePages.Push(pageIndex);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        Heap.Dispose();
    }
}
