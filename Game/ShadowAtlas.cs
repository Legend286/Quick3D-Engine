// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using Engine.RHI;

namespace Engine.Game;

internal readonly record struct ShadowAtlasAllocation(
    int PageIndex,
    RhiTexture Texture,
    uint X,
    uint Y,
    uint Size,
    int SlotIndex);

internal sealed class ShadowAtlas : IDisposable
{
    public const uint PageSize = 4096;
    public const ulong BytesPerPage = (ulong)PageSize * PageSize * 4;
    public const ulong DefaultBudgetBytes = 1024ul * 1024 * 1024;
    public const ulong HardBudgetBytes = 1536ul * 1024 * 1024;

    private sealed class TierPage
    {
        public required int PageIndex;
        public required int Subdivision;
        public required BitArray Occupied;
    }

    private readonly RhiDevice _device;
    private readonly ulong _budgetBytes;
    private readonly List<RhiTexture> _pages = new();
    private readonly Dictionary<int, List<TierPage>> _tiers = new();

    public ShadowAtlas(
        RhiDevice device,
        ulong budgetBytes = DefaultBudgetBytes)
    {
        _device = device;
        _budgetBytes = Math.Clamp(
            budgetBytes,
            4 * BytesPerPage,
            HardBudgetBytes);
    }

    public ulong BudgetBytes => _budgetBytes;
    public ulong AllocatedBytes => (ulong)_pages.Count * BytesPerPage;
    public IReadOnlyList<RhiTexture> Pages => _pages;

    public ShadowAtlasAllocation AllocateDedicatedPage()
    {
        int pageIndex = AllocatePage();
        return new ShadowAtlasAllocation(
            pageIndex,
            _pages[pageIndex],
            0,
            0,
            PageSize,
            0);
    }

    public bool TryAllocateTile(
        int subdivision,
        out ShadowAtlasAllocation allocation)
    {
        allocation = default;
        if (subdivision < 2 ||
            subdivision > 32 ||
            (subdivision & (subdivision - 1)) != 0)
        {
            return false;
        }

        if (!_tiers.TryGetValue(subdivision, out List<TierPage>? pages))
        {
            pages = new List<TierPage>();
            _tiers.Add(subdivision, pages);
        }

        TierPage? tierPage = null;
        int slotIndex = -1;
        foreach (TierPage page in pages)
        {
            slotIndex = FindFreeSlot(page.Occupied);
            if (slotIndex >= 0)
            {
                tierPage = page;
                break;
            }
        }

        if (tierPage == null)
        {
            if (AllocatedBytes + BytesPerPage > _budgetBytes)
                return false;

            int pageIndex = AllocatePage();
            tierPage = new TierPage
            {
                PageIndex = pageIndex,
                Subdivision = subdivision,
                Occupied = new BitArray(subdivision * subdivision),
            };
            pages.Add(tierPage);
            slotIndex = 0;
        }

        tierPage.Occupied[slotIndex] = true;
        uint tileSize = PageSize / (uint)subdivision;
        uint tileX = (uint)(slotIndex % subdivision) * tileSize;
        uint tileY = (uint)(slotIndex / subdivision) * tileSize;
        allocation = new ShadowAtlasAllocation(
            tierPage.PageIndex,
            _pages[tierPage.PageIndex],
            tileX,
            tileY,
            tileSize,
            slotIndex);
        return true;
    }

    public bool TryAllocateTileSet(
        int tileCount,
        out ShadowAtlasAllocation[] allocations)
    {
        return TryAllocateTileSet(
            tileCount,
            FindMinimumSubdivision(tileCount),
            out allocations);
    }

    public bool TryAllocateTileSet(
        int tileCount,
        int preferredSubdivision,
        out ShadowAtlasAllocation[] allocations)
    {
        allocations = Array.Empty<ShadowAtlasAllocation>();
        int minimumSubdivision = FindMinimumSubdivision(tileCount);
        if (minimumSubdivision == 0 ||
            preferredSubdivision < minimumSubdivision ||
            preferredSubdivision > 32 ||
            (preferredSubdivision & (preferredSubdivision - 1)) != 0)
        {
            return false;
        }

        for (int subdivision = preferredSubdivision;
             subdivision <= 32;
             subdivision *= 2)
        {
            if (TryAllocateTileSetAtSubdivision(
                    subdivision,
                    tileCount,
                    out allocations))
            {
                return true;
            }
        }
        return false;
    }

    internal static int FindMinimumSubdivision(int tileCount)
    {
        if (tileCount <= 0 || tileCount > 32 * 32)
            return 0;
        for (int subdivision = 2;
             subdivision <= 32;
             subdivision *= 2)
        {
            if (subdivision * subdivision >= tileCount)
                return subdivision;
        }
        return 0;
    }

    public void Release(ShadowAtlasAllocation allocation)
    {
        foreach (List<TierPage> pages in _tiers.Values)
        {
            foreach (TierPage page in pages)
            {
                if (page.PageIndex != allocation.PageIndex)
                    continue;
                if ((uint)allocation.SlotIndex < (uint)page.Occupied.Length)
                    page.Occupied[allocation.SlotIndex] = false;
                return;
            }
        }
    }

    private int AllocatePage()
    {
        if (AllocatedBytes + BytesPerPage > _budgetBytes)
            throw new InvalidOperationException(
                "Shadow atlas GPU memory budget exhausted.");
        RhiTexture page = RhiTexture.CreateDepth(
            _device,
            PageSize,
            PageSize,
            shaderReadable: true);
        page.SetDebugName(
            $"Shadow Atlas Page {_pages.Count}",
            "Shadow Atlas");
        _pages.Add(page);
        return _pages.Count - 1;
    }

    private bool TryAllocateTileSetAtSubdivision(
        int subdivision,
        int tileCount,
        out ShadowAtlasAllocation[] allocations)
    {
        allocations = Array.Empty<ShadowAtlasAllocation>();
        if (!_tiers.TryGetValue(subdivision, out List<TierPage>? pages))
        {
            pages = new List<TierPage>();
            _tiers.Add(subdivision, pages);
        }

        TierPage? tierPage = null;
        int[] freeSlots = new int[tileCount];
        foreach (TierPage page in pages)
        {
            if (!TryFindFreeSlots(page.Occupied, freeSlots))
                continue;
            tierPage = page;
            break;
        }

        if (tierPage == null)
        {
            if (pages.Count > 0)
                return false;
            if (subdivision * subdivision < tileCount ||
                AllocatedBytes + BytesPerPage > _budgetBytes)
            {
                return false;
            }

            int pageIndex = AllocatePage();
            tierPage = new TierPage
            {
                PageIndex = pageIndex,
                Subdivision = subdivision,
                Occupied = new BitArray(subdivision * subdivision),
            };
            pages.Add(tierPage);
            for (int slotIndex = 0;
                 slotIndex < tileCount;
                 ++slotIndex)
            {
                freeSlots[slotIndex] = slotIndex;
            }
        }

        allocations = new ShadowAtlasAllocation[tileCount];
        uint tileSize = PageSize / (uint)subdivision;
        for (int allocationIndex = 0;
             allocationIndex < tileCount;
             ++allocationIndex)
        {
            int slotIndex = freeSlots[allocationIndex];
            tierPage.Occupied[slotIndex] = true;
            allocations[allocationIndex] = new ShadowAtlasAllocation(
                tierPage.PageIndex,
                _pages[tierPage.PageIndex],
                (uint)(slotIndex % subdivision) * tileSize,
                (uint)(slotIndex / subdivision) * tileSize,
                tileSize,
                slotIndex);
        }
        return true;
    }

    private static int FindFreeSlot(BitArray occupied)
    {
        for (int i = 0; i < occupied.Length; ++i)
        {
            if (!occupied[i])
                return i;
        }
        return -1;
    }

    private static bool TryFindFreeSlots(
        BitArray occupied,
        Span<int> freeSlots)
    {
        int foundCount = 0;
        for (int slotIndex = 0;
             slotIndex < occupied.Length &&
             foundCount < freeSlots.Length;
             ++slotIndex)
        {
            if (!occupied[slotIndex])
                freeSlots[foundCount++] = slotIndex;
        }
        return foundCount == freeSlots.Length;
    }

    public void Dispose()
    {
        foreach (RhiTexture page in _pages)
            page.Dispose();
        _pages.Clear();
        _tiers.Clear();
    }
}
