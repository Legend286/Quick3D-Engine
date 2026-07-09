// nishita_sky.slang
// Physically based Nishita sky model

static const float EarthRadius = 6360000.0;
static const float AtmosphereRadius = 6420000.0;
static const float3 RayleighScatteringBase = float3(5.8e-6, 13.5e-6, 33.1e-6);
static const float RayleighScaleHeight = 8000.0;
static const float MieScatteringBase = 2e-5;
static const float MieScaleHeight = 1200.0;
static const float MiePreferredScatteringDir = 0.758;

bool IntersectSphere(float3 origin, float3 dir, float radius, out float t0, out float t1) {
    float b = dot(origin, dir);
    float c = dot(origin, origin) - radius * radius;
    float d = b * b - c;
    if (d < 0.0) return false;
    float sqrtD = sqrt(d);
    t0 = -b - sqrtD;
    t1 = -b + sqrtD;
    return true;
}

float3 GetSkyRadiance(float3 viewDir, float3 sunDirection, SkyParams sky) {
    float3 origin = float3(0.0, EarthRadius + 1.0, 0.0);
    float t0 = 0.0, t1 = 0.0;
    if (!IntersectSphere(origin, viewDir, AtmosphereRadius, t0, t1)) {
        return float3(0.0);
    }
    float tMax = t1;
    float tEarth0 = 0.0, tEarth1 = 0.0;
    bool hitsEarth = IntersectSphere(origin, viewDir, EarthRadius, tEarth0, tEarth1) && tEarth0 > 0.0;
    if (hitsEarth) {
        tMax = max(0.0, tEarth0);
        // If we're exactly on the ground or the ray hits it immediately, we still need to render the ground!
    }
    
    const int NUM_SAMPLES = 16;
    const int NUM_LIGHT_SAMPLES = 8;
    float segmentLength = tMax / float(NUM_SAMPLES);
    
    float opticalDepthR = 0.0;
    float opticalDepthM = 0.0;
    
    float3 sumR = float3(0.0);
    float3 sumM = float3(0.0);
    
    float mu = dot(viewDir, sunDirection);
    float phaseR = 3.0 / (16.0 * 3.14159) * (1.0 + mu * mu);
    float g = MiePreferredScatteringDir;
    float phaseM = 3.0 / (8.0 * 3.14159) * ((1.0 - g * g) * (1.0 + mu * mu)) / ((2.0 + g * g) * pow(1.0 + g * g - 2.0 * g * mu, 1.5));
    
    float tCurrent = 0.0;
    for (int i = 0; i < NUM_SAMPLES; ++i) {
        float3 samplePos = origin + viewDir * (tCurrent + segmentLength * 0.5);
        float height = length(samplePos) - EarthRadius;
        if (height < 0.0) break;
        
        float hr = exp(-height / RayleighScaleHeight) * segmentLength;
        float hm = exp(-height / MieScaleHeight) * segmentLength;
        
        opticalDepthR += hr;
        opticalDepthM += hm;
        
        float t0Light=0.0, t1Light=0.0;
        IntersectSphere(samplePos, sunDirection, AtmosphereRadius, t0Light, t1Light);
        float segmentLengthLight = t1Light / float(NUM_LIGHT_SAMPLES);
        float tCurrentLight = 0.0;
        float opticalDepthLightR = 0.0;
        float opticalDepthLightM = 0.0;
        
        bool blocked = false;
        if (IntersectSphere(samplePos, sunDirection, EarthRadius, tEarth0, tEarth1) && tEarth0 > 0.0) {
            blocked = true;
        }
        
        if (!blocked) {
            for (int j = 0; j < NUM_LIGHT_SAMPLES; ++j) {
                float3 sampleLightPos = samplePos + sunDirection * (tCurrentLight + segmentLengthLight * 0.5);
                float heightLight = length(sampleLightPos) - EarthRadius;
                if (heightLight < 0.0) break;
                
                opticalDepthLightR += exp(-heightLight / RayleighScaleHeight) * segmentLengthLight;
                opticalDepthLightM += exp(-heightLight / MieScaleHeight) * segmentLengthLight;
                tCurrentLight += segmentLengthLight;
            }
            
            float3 tau = RayleighScatteringBase * (opticalDepthR + opticalDepthLightR) + 
                         MieScatteringBase * 1.1 * (opticalDepthM + opticalDepthLightM);
            float3 attenuation = exp(-tau);
            
            sumR += attenuation * hr;
            sumM += attenuation * hm;
        }
        tCurrent += segmentLength;
    }
    
    float3 scatteredLight = (sumR * RayleighScatteringBase * phaseR + sumM * MieScatteringBase * phaseM) * sky.intensityTurbidityAlbedoPad.x * 20.0;
    float3 attenuation = exp(-(RayleighScatteringBase * opticalDepthR + MieScatteringBase * 1.1 * opticalDepthM));
    float3 skyRadiance = scatteredLight;
    
    // If the ray hit the ground, we add a solid ground color (e.g. using the ground albedo),
    // and apply the atmospheric attenuation to it so it fades into the horizon fog.
    if (hitsEarth) {
        float NdotL = max(0.0, sunDirection.y);
        // Ground receives direct sun (attenuated by atmosphere) and some ambient
        float3 groundAlbedo = float3(sky.intensityTurbidityAlbedoPad.z);
        float3 directSunLight = groundAlbedo * NdotL * sky.intensityTurbidityAlbedoPad.x;
        float3 ambientLight = groundAlbedo * 0.1 * sky.intensityTurbidityAlbedoPad.x;
        float3 groundRadiance = directSunLight + ambientLight;
        
        skyRadiance += groundRadiance * attenuation;
    } else {
        // Draw the sun disc if we didn't hit the earth
        float cosTheta = dot(viewDir, sunDirection);
        float sunAngularRadius = sky.sunDirAndRadius.w;
        if (cosTheta > cos(sunAngularRadius)) {
            float3 sunDisk = float3(sky.intensityTurbidityAlbedoPad.x) * 1000.0; // High intensity for path tracing
            skyRadiance += sunDisk * attenuation;
        }
    }
    
    return max(skyRadiance, 0.0);
}
