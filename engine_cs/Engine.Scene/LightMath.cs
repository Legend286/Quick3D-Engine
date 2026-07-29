// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.Scene;

/// <summary>
/// Converts authored light transforms into runtime light directions and back.
/// </summary>
public static class LightMath
{
    public static readonly Vector3 SpotLocalDirection = new(0f, -1f, 0f);

    /// <summary>
    /// Returns the world-space spot direction for a light transform rotation.
    /// </summary>
    public static Vector3 GetSpotDirection(Quaternion rotation)
    {
        var normalized = SanitizeQuaternion(rotation);
        return NormalizeOrFallback(Vector3.Transform(SpotLocalDirection, normalized), SpotLocalDirection);
    }

    /// <summary>
    /// Builds a rotation that aligns the authored spot axis with a world-space direction.
    /// </summary>
    public static Quaternion GetSpotRotation(Vector3 direction)
    {
        var target = NormalizeOrFallback(direction, SpotLocalDirection);
        float dot = Math.Clamp(Vector3.Dot(SpotLocalDirection, target), -1f, 1f);
        if (dot >= 0.9999f)
            return Quaternion.Identity;
        if (dot <= -0.9999f)
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        Vector3 axis = Vector3.Normalize(Vector3.Cross(SpotLocalDirection, target));
        float angle = MathF.Acos(dot);
        return SanitizeQuaternion(Quaternion.CreateFromAxisAngle(axis, angle));
    }

    /// <summary>
    /// Normalizes a vector or falls back when it is degenerate.
    /// </summary>
    public static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (float.IsNaN(value.X) || float.IsNaN(value.Y) || float.IsNaN(value.Z) ||
            float.IsInfinity(value.X) || float.IsInfinity(value.Y) || float.IsInfinity(value.Z) ||
            value.LengthSquared() < 1e-6f)
        {
            return fallback;
        }

        return Vector3.Normalize(value);
    }

    /// <summary>
    /// Returns a normalized quaternion or identity when the input is invalid.
    /// </summary>
    public static Quaternion SanitizeQuaternion(Quaternion q)
    {
        if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W) ||
            float.IsInfinity(q.X) || float.IsInfinity(q.Y) || float.IsInfinity(q.Z) || float.IsInfinity(q.W) ||
            q.LengthSquared() < 1e-6f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.Normalize(q);
    }
}
