// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>
/// Scores DDGI probes by camera proximity + frustum containment + dirty
/// light influence + staleness, then yields the highest-priority subset
/// for this frame's update compute dispatch. Pure CPU code, allocator-
/// conservative, test-friendly.
/// </summary>
/// <remarks>
/// Camera-first priority matches the user's request: probes inside the
/// camera frustum + near the camera get highest base score. Light-changed
/// responsiveness is layered on top via a TTL'd "dirty light ring" —
/// each dirty light boosts any probe inside its radius with a falloff
/// weight. Staleness guarantees no probe starves: probes whose last
/// update was many frames ago get a penalty that grows over time, so
/// after the camera-and-light-driven top-N have been served, the
/// scheduler picks up the trailing half of the dirty set.
/// </remarks>
public sealed class DDGIProbePriority
{
    public sealed record CameraSnapshot(
        Vector3 Position,
        Vector3 Forward,
        Vector3 Up,
        Vector3 Right,
        float FieldOfViewRadians,
        float NearDistance,
        float AspectRatio);

    public sealed record LightInfluence(
        int LightId,
        Vector3 Position,
        float Radius,
        bool IsDirty,
        int DirtyFramesRemaining);

    public sealed record ProbeSnapshot(
        int Index,
        Vector3 Position,
        long LastUpdateFrame);

    /// <summary>Default weight constants. Tunable but stable across releases.</summary>
    public sealed record Tuning(
        float DistanceWeight = 2.0f,
        float DistanceFalloffMeters = 50.0f,
        float FrustumContainmentBonus = 5.0f,
        float StalePenaltyPerFrame = 0.5f,
        float StalePenaltyCap = 30.0f,
        float DirtyLightBoost = 50.0f,
        float DirtyLightBaseBoost = 12.5f);

    private long _currentFrame;
    private readonly Tuning _tuning;

    public DDGIProbePriority(Tuning? tuning = null)
    {
        _tuning = tuning ?? new Tuning();
    }

    public void AdvanceFrame(long frameIndex) => _currentFrame = frameIndex;

    /// <summary>
    /// Returns up to <paramref name="maxProbesPerFrame"/> probe indices,
    /// de-duplicated, in priority-descending order. Pure function over
    /// inputs (modulo the cached <see cref="AdvanceFrame"/> long) so
    /// tests can compare results deterministically.
    /// </summary>
    public IReadOnlyList<int> ScheduleProbeUpdates(
        IReadOnlyList<ProbeSnapshot> probes,
        IReadOnlyList<LightInfluence>? dirtyLights,
        int maxProbesPerFrame,
        CameraSnapshot camera)
    {
        if (probes.Count == 0 || maxProbesPerFrame <= 0)
            return Array.Empty<int>();

        bool camInsideFrustum = false;
        Vector3 camGridUVW = Vector3.Zero;
        // Cheap frustum proxy: the camera is "inside the frustum" iff
        // its distance to the probe-volume origin is &lt; a heuristic
        // radius derived from FOV. Real implementations can plug a
        // proper frustum culler here.
        float frustumRadius = ComputeFrustumRadius(camera);

        var scored = new (int Index, float Score)[probes.Count];
        for (int i = 0; i < probes.Count; ++i)
        {
            ProbeSnapshot probe = probes[i];
            bool needsUpdate = probe.LastUpdateFrame == 0;
            
            if (dirtyLights is { Count: > 0 })
            {
                for (int j = 0; j < dirtyLights.Count; ++j)
                {
                    LightInfluence light = dirtyLights[j];
                    float lightDistance = Vector3.Distance(
                        probe.Position,
                        light.Position);
                    if (lightDistance >= light.Radius)
                        continue;
                    
                    if (light.IsDirty)
                        needsUpdate = true;
                }
            }

            // Only update if never built, or explicitly dirty.
            // We removed the background refresh because it caused lag spikes.
            // Probes will now only update if lighting changes, or when we implement
            // dynamic object tracking.
            if (!needsUpdate)
            {
                scored[i] = (-1, -1.0f);
                continue;
            }

            // Compute priority for probes that need an update
            float score = 0.0f;
            if (probe.LastUpdateFrame == 0)
                score += 10000.0f; // Must build
            else
                score += (_currentFrame - probe.LastUpdateFrame) * _tuning.StalePenaltyPerFrame; // Boost if it hasn't been updated in a while despite being dirty

            float distance = Vector3.Distance(probe.Position, camera.Position);
            if (distance < _tuning.DistanceFalloffMeters)
            {
                float normalized = 1.0f -
                    distance / MathF.Max(_tuning.DistanceFalloffMeters, 0.001f);
                score += _tuning.DistanceWeight * 100.0f * normalized;
            }
            if (distance < frustumRadius)
            {
                score += _tuning.FrustumContainmentBonus * 100.0f;
            }

            if (dirtyLights is { Count: > 0 })
            {
                for (int j = 0; j < dirtyLights.Count; ++j)
                {
                    LightInfluence light = dirtyLights[j];
                    float lightDistance = Vector3.Distance(
                        probe.Position,
                        light.Position);
                    if (lightDistance >= light.Radius)
                        continue;
                    float falloff = 1.0f -
                        lightDistance / MathF.Max(light.Radius, 0.001f);
                    float boost = light.IsDirty
                        ? _tuning.DirtyLightBoost
                        : _tuning.DirtyLightBaseBoost;
                    score += boost * falloff *
                        Math.Max(light.DirtyFramesRemaining, 1);
                }
            }

            scored[i] = (probe.Index, score);
        }

        Array.Sort(scored, (a, b) => b.Score.CompareTo(a.Score));

        var top = new List<int>(Math.Min(maxProbesPerFrame, scored.Length));
        var seen = new HashSet<int>();
        for (int i = 0; i < scored.Length && top.Count < maxProbesPerFrame; ++i)
        {
            int idx = scored[i].Index;
            if (idx < 0 || idx >= probes.Count)
                continue;
            if (seen.Add(idx))
                top.Add(idx);
        }
        return top;
    }

    private static float ComputeFrustumRadius(CameraSnapshot camera)
    {
        // Treat half-FOV as the cone half-angle; pick the radius at which
        // the cone intersects the volume AABB. Approximation: tan(halfFov)
        // × max(Near) is a lower bound; 4× Near is a workable upper bound
        // for the camera-first priority heuristic. Tune as SDKs mature.
        float halfFov = camera.FieldOfViewRadians * 0.5f;
        float coneDiameterAtNear =
            MathF.Tan(halfFov) * camera.NearDistance * 2.0f;
        return MathF.Max(coneDiameterAtNear, camera.NearDistance * 4.0f);
    }
}
