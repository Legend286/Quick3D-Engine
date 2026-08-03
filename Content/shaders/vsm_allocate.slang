// SPDX-License-Identifier: MIT

[[vk::binding(0, 0)]] StructuredBuffer<uint> PageRequests : register(t0);

// AllocateQueue[0] is an atomic counter for the number of active requests.
// AllocateQueue[1..N] contains the flat page indices that are requested.
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> AllocateQueue : register(u1);

struct VsmAllocateConstants {
    uint totalVirtualPages;
};

[[vk::binding(2, 0)]] ConstantBuffer<VsmAllocateConstants> vsmConstants : register(b2);

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint pageIndex = dispatchThreadId.x;
    
    if (pageIndex >= vsmConstants.totalVirtualPages)
        return;

    // If this page was requested by the visibility buffer depth analysis
    if (PageRequests[pageIndex] > 0)
    {
        // Atomically increment the allocation queue count
        uint writeIndex;
        InterlockedAdd(AllocateQueue[0], 1, writeIndex);
        
        // Store the requested page index in the queue (offset by 1 due to counter)
        AllocateQueue[writeIndex + 1] = pageIndex;
    }
}
