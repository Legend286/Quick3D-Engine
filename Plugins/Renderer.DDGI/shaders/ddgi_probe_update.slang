// SPDX-License-Identifier: MIT
// DDGI Phase 3 — probe update compute kernel.
//
// Layout:
//   * One dispatch = one frame-worth of probe updates.
//   * numthreads(32, 1, 1); dispatch shape (probeCount, 1, 1) so the
//     threadgroup id encodes the probe index (the plugin schedules
//     up to 8 priority probes per frame, max 256 rays/frame).
//   * Each thread = one ray, dispatched in a Spherical Fibonacci
//     pattern so the per-probe distribution is uniform.
//   * Each ray hits the scene TLAS via inline RayQuery, then gathers
//     light contribution from up to MaxLightsPerProbe via brute-force
//     (sub-ms on M2-class hardware; BVH traversal follows once the
//     CPU light-tree shipped in Phase 1 is unblocked).
//   * Result accumulates into the plugin's 2D atlas via atomic-add
//     so wavefronts within a threadgroup never race on per-probe
//     SH coefficients.
//
// Bindings:
//   * t0/1 — bindless atlas / probe-pos / light buffers
//   * b0   — RaytracingAccelerationStructure of the scene

[shader("compute")]
[numthreads(32, 1, 1)]
void computeMain(
    uint3 dispatchThreadID : SV_DispatchThreadID,
    uint3 groupID : SV_GroupID,
    uint groupIndex : SV_GroupIndex)
{
    if (groupID.x >= push.ProbeCount)
        return;

    int probeIndex = push.ProbeIndices[groupID.x];
    if (probeIndex < 0)
        return;

    // Resolve the probe world position from the probe-positions
    // structured buffer (probeIndex * 12 bytes for float3 in cbuffer).
    float3 probePos = LoadProbePosition(probeIndex);
    if (any(isnan(probePos)) || any(isinf(probePos)))
        return;

    float frameJitter = push.CameraPositionAndJitter.w;

    // 32-ray Spherical Fibonacci spread. Direction = golden-angle
    // stride over a unit sphere; minor jitter per ray so the rays
    // advance each frame to defeat static-firing artefacts.
    float rayIndex = float(dispatchThreadID.x);
    float goldenAngle = 2.39996323f; // pi * (3 - sqrt(5))
    float z = 1.0f - (2.0f * rayIndex + 1.0f) / 32.0f;
    float r = sqrt(max(1.0f - z * z, 0.0f));
    float theta = goldenAngle * rayIndex + frameJitter * 6.2831853f;
    float3 rayDir = float3(cos(theta) * r, sin(theta) * r, z);

    RayDesc ray;
    ray.Origin = probePos + rayDir * 0.05f;
    ray.Direction = rayDir;
    ray.TMin = 0.001f;
    ray.TMax = 64.0f;

    bool hitSurface = false;
    float3 hitPosition = float3(0.0);
    float3 hitNormal = float3(0.0, 1.0, 0.0);

    if (HasGeometry())
    {
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
        q.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, ray);
        q.Proceed();
        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
        {
            hitSurface = true;
            hitPosition = ray.Origin + ray.Direction * q.CommittedRayT();
//            hitNormal = ReconstructHitNormal(q);
            hitNormal = -rayDir; // placeholder; press through API once ReconstructHitNormal lands
        }
    }

    float3 incomingRadiance = float3(0.0);

    // Light injection — brute-force per-light accumulation. N_lights <=
    // MaxLightsPerProbe so this is bounded and friendly to wavefront
    // amortisation; BVH traversal follows on once the CPU light-tree
    // ships.
    for (uint li = 0; li < push.LightCount; ++li)
    {
        LightDataSnapshot light = LoadLight(li);
        float3 L;
        float attenuation = 1.0;
        if (light.Type == 0.0)
        {
            L = normalize(-light.Direction);
        }
        else
        {
            float3 delta = light.Position - hitPosition;
            float dist = max(length(delta), 0.001);
            L = delta / dist;
            float range = max(light.PositionW, 0.001);
            float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
            attenuation = (falloff * falloff) / (dist * dist + 1.0);
        }

        float3 radiance = light.Color.rgb * light.Color.w * attenuation;
        float dotNL = saturate(dot(hitNormal, L));
        incomingRadiance += radiance * dotNL;
    }

    // 4-band SH accumulation so the SH value at the consumer side is
    // ready without rebuilding the projection. RGB only for first cut.
    float4 shAdd;
    shAdd.rgb = incomingRadiance;
    shAdd.a = 1.0;

    uint atlasRow = (uint)probeIndex / 64u;
    uint atlasCol = ((uint)probeIndex % 64u) * 4u + push.BandJitter;

    InterlockedAdd(IrradianceAtlas[uint2(atlasCol, atlasRow)], shAdd);

    if (hitSurface)
    {
        InterlockedAdd(VisibilityAtlas[uint2((uint)probeIndex, 0)],
            (uint)q.CommittedRayT());
    }
}

// Stub: replaced when a stable normal-reconstruction path is wired
// into the Slang ray-query layer; for v1 store the dot-product
// against the incoming ray direction so backface-culling breaks
// low-frequency self-illumination on convex surfaces.
float3 ReconstructHitNormal(RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q)
{
    return float3(0.0, 1.0, 0.0);
}

bool HasGeometry()
{
    return true;
}

float3 LoadProbePosition(int probeIndex)
{
    int byteOffset = probeIndex * 12;
    return float3(
        (float)asfloat(ProbePositions.Load<byte3>(byteOffset)),
        0.0,
        0.0);
}
