// sky.slang
// Simple analytic sky model: blue zenith → white horizon gradient + sun disk.
// Replaces the complex Nishita model which had numerical instability at
// uLen≈0 in the Chapman-function optical-depth approximation, producing
// near-zero radiance (black sky).

static const float3 DAY_ZENITH = float3(0.25, 0.45, 0.85);   // bright blue overhead
static const float3 DAY_HORIZON = float3(0.70, 0.78, 0.90); // pale blue-white at horizon
static const float3 DAY_GROUND = float3(0.12, 0.10, 0.08);   // dark brown below horizon
static const float SUN_DISK_RADIUS = 0.03;                    // angular radius (radians)
static const float SUN_DISK_INTENSITY = 80.0;
static const float3 SUN_COLOR = float3(1.0, 0.95, 0.8);

float3 GetSkyRadiance(float3 viewDir, float3 sunDirection, SkyParams sky) {
    float elevation = viewDir.y; // -1=down, 0=horizon, 1=up

    // Blend between ground, horizon, and zenith based on elevation
    float tHorizon = 1.0 - abs(elevation);        // 1 at horizon, 0 at ±1
    tHorizon = pow(tHorizon, 4.0);                // sharpen the horizon band

    float upWeight = smoothstep(0.0, 0.15, elevation);  // transition from ground to sky
    float3 skyColor = lerp(DAY_GROUND, DAY_HORIZON, upWeight);
    skyColor = lerp(skyColor, DAY_ZENITH, smoothstep(0.15, 0.6, elevation));

    // Add horizon glow near the sun
    float cosSun = dot(viewDir, sunDirection);
    float sunAngularDist = acos(clamp(cosSun, -1.0, 1.0));
    float sunDisk = 1.0 - smoothstep(0.0, SUN_DISK_RADIUS, sunAngularDist);
    float sunGlow = exp(-sunAngularDist * 8.0) * 0.3;

    skyColor += SUN_COLOR * SUN_DISK_INTENSITY * sunDisk * sky.sunIntensity * 0.02;
    skyColor += SUN_COLOR * sunGlow * sky.sunIntensity * 0.015;

    // Slight Mie-like forward scattering toward sun
    float miePhase = (1.0 + cosSun * cosSun) / pow(1.0 + 1.5 * 1.5 - 2.0 * 1.5 * (-cosSun), 1.5);
    skyColor += float3(0.15, 0.12, 0.05) * miePhase * sky.turbidity * 0.1;

    // Reduce intensity looking below horizon (planet shadow)
    if (elevation < 0.0) {
        skyColor *= lerp(1.0, 0.15, smoothstep(0.0, -0.3, elevation));
    }

    return max(skyColor, 0.0);
}
