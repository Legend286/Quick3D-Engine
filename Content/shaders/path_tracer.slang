// SPDX-License-Identifier: MIT

#include "scene_data.slang"
#include "disney_bsdf.slang"
#include "nishita_sky.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct BindlessHeap {
    Texture2D textures[4096];
};
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;
[[vk::binding(2, 0)]] SamplerState tex_sampler;

[[vk::binding(3, 0)]] RWTexture2D<float4> accumulationBuffer;
[[vk::binding(4, 0)]] RWTexture2D<float4> outputBuffer;
[[vk::binding(5, 0)]] RaytracingAccelerationStructure tlas;

// =====================================================================
// PCG RNG
// =====================================================================
uint pcg_hash(uint input) {
    uint state = input * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint seed) {
    seed = pcg_hash(seed);
    return float(seed) / 4294967296.0;
}

// =====================================================================
// Geometry helpers
// =====================================================================
struct RayPayload {
    bool hit;
    float t;
    uint instanceId;
    uint primitiveId;
    float2 barycentrics;
};

// Sample a cosine-weighted direction on a hemisphere (for SSS walk)
float3 SampleUniformSphere(float u1, float u2) {
    float cosTheta = 1.0 - 2.0 * u1;
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float phi = 2.0 * PI * u2;
    return float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
}

// Returns Disney material from hit geometry
DisneyMaterial GetMaterial(PartData part, InstanceData inst, uint primitiveId, float3 barycentrics,
                            out float3 hitNormal, out float3 hitTangent, out float3 hitBitangent,
                            out float3 albedo, out float subsurface, out float3 subsurfaceRadius, out float3 subsurfaceColor) {
    
    uint i0, i1, i2;
    if ((part.flags & 1u) != 0u) {
        i0 = part.indices[primitiveId * 3 + 0];
        i1 = part.indices[primitiveId * 3 + 1];
        i2 = part.indices[primitiveId * 3 + 2];
    } else {
        uint dword0 = part.indices[(primitiveId * 3 + 0) / 2];
        uint dword1 = part.indices[(primitiveId * 3 + 1) / 2];
        uint dword2 = part.indices[(primitiveId * 3 + 2) / 2];
        i0 = ((primitiveId * 3 + 0) % 2 == 0) ? (dword0 & 0xFFFF) : (dword0 >> 16);
        i1 = ((primitiveId * 3 + 1) % 2 == 0) ? (dword1 & 0xFFFF) : (dword1 >> 16);
        i2 = ((primitiveId * 3 + 2) % 2 == 0) ? (dword2 & 0xFFFF) : (dword2 >> 16);
    }
    
    Vertex v0 = part.vertices[i0];
    Vertex v1 = part.vertices[i1];
    Vertex v2 = part.vertices[i2];
    
    float3 n0 = float3(v0.nx, v0.ny, v0.nz);
    float3 n1 = float3(v1.nx, v1.ny, v1.nz);
    float3 n2 = float3(v2.nx, v2.ny, v2.nz);
    float3 localNormal = normalize(n0 * barycentrics.x + n1 * barycentrics.y + n2 * barycentrics.z);
    
    float3 t0 = float3(v0.tx, v0.ty, v0.tz);
    float3 t1 = float3(v1.tx, v1.ty, v1.tz);
    float3 t2 = float3(v2.tx, v2.ty, v2.tz);
    float3 localTangent = normalize(t0 * barycentrics.x + t1 * barycentrics.y + t2 * barycentrics.z);
    float tangentW = v0.tw;
    
    float2 uv0 = float2(v0.u, v0.v);
    float2 uv1 = float2(v1.u, v1.v);
    float2 uv2 = float2(v2.u, v2.v);
    float2 uv = uv0 * barycentrics.x + uv1 * barycentrics.y + uv2 * barycentrics.z;
    
    hitNormal   = normalize(mul((float3x3)inst.modelMatrix, localNormal));
    hitTangent  = normalize(mul((float3x3)inst.modelMatrix, localTangent));
    if (!(dot(hitTangent, hitTangent) > 0.001)) {
        float3 up = abs(hitNormal.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        hitTangent = normalize(cross(up, hitNormal));
    }
    hitBitangent = cross(hitNormal, hitTangent) * tangentW;
    if (!(dot(hitBitangent, hitBitangent) > 0.001)) hitBitangent = cross(hitNormal, hitTangent);
    hitBitangent = normalize(hitBitangent);
    hitTangent   = normalize(cross(hitBitangent, hitNormal));
    
    MaterialData mat = push.materials[part.materialIdx];
    
    float3 baseColor = mat.baseColor.rgb;
    if (mat.albedoTexIndex != 0xFFFFFFFF) {
        float4 texColor = bindless.textures[mat.albedoTexIndex].SampleLevel(tex_sampler, uv, 0);
        baseColor *= texColor.rgb;
    }
    albedo = baseColor;
    
    float metallic  = mat.metallic;
    float roughness = mat.roughness;
    if (mat.rmaTexIndex != 0xFFFFFFFF) {
        float3 rma = bindless.textures[mat.rmaTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
        roughness *= rma.g;
        metallic  *= rma.b;
    }
    
    if (mat.normalTexIndex != 0xFFFFFFFF) {
        float3 normalMap = bindless.textures[mat.normalTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        float3x3 TBN = float3x3(hitTangent, hitBitangent, hitNormal);
        hitNormal = normalize(mul(normalMap, TBN));
        hitBitangent = cross(hitNormal, hitTangent) * tangentW;
        if (!(dot(hitBitangent, hitBitangent) > 0.001)) hitBitangent = cross(hitNormal, hitTangent);
        hitBitangent = normalize(hitBitangent);
        hitTangent   = normalize(cross(hitBitangent, hitNormal));
    }
    
    subsurface       = mat.subsurface;
    subsurfaceRadius = mat.subsurfaceRadius.xyz;
    subsurfaceColor  = mat.subsurfaceColor.xyz;
    
    DisneyMaterial dmat;
    dmat.baseColor    = baseColor;
    dmat.metallic     = metallic;
    dmat.subsurface   = 0.0; // SSS handled separately in path tracer, not in diffuse lobe
    dmat.specular     = 0.5;
    dmat.roughness    = max(roughness, 0.01);
    dmat.specularTint = 0.0;
    dmat.anisotropic  = 0.0;
    dmat.sheen        = 0.0;
    dmat.sheenTint    = 0.5;
    dmat.clearcoat    = 0.0;
    dmat.clearcoatGloss = 1.0;
    return dmat;
}

// =====================================================================
// MIS helpers
// =====================================================================
float MISWeight(float pdfA, float pdfB) {
    float a2 = pdfA * pdfA;
    float b2 = pdfB * pdfB;
    return a2 / (a2 + b2 + 1e-10);
}

float SunLightPdf(float sunAngularRadius) {
    float cosR = cos(sunAngularRadius);
    return 1.0 / max(2.0 * PI * (1.0 - cosR), 1e-10);
}

bool HitsSun(float3 dir, float3 sunDir, float sunAngularRadius) {
    return dot(dir, sunDir) > cos(sunAngularRadius);
}

float3 GetSkyRadianceNoSun(float3 viewDir, float3 sunDirection, SkyParams sky) {
    float3 full = GetSkyRadiance(viewDir, sunDirection, sky);
    if (HitsSun(viewDir, sunDirection, sky.sunDirAndRadius.w)) {
        float3 sunDisk = float3(sky.intensityTurbidityAlbedoPad.x) * 1000.0;
        full = max(full - sunDisk, float3(0.0));
    }
    return full;
}

// =====================================================================
// Random-Walk Subsurface Scattering
// =====================================================================
// Simulates a single SSS walk starting from 'entryPos' going through 'entryDir'.
// Returns the radiance that exits the surface via the walk.
// sigma_t (extinction) = 1 / subsurfaceRadius (per channel).
// scatteringAlbedo = subsurfaceColor.
// We trace per-channel by sampling the channel with maximum luminance and
// computing RGB throughput analytically via Beer-Lambert at each free path.
float3 RandomWalkSSS(float3 entryPos, float3 entryDir, float3 N,
                     float3 sigma_t, float3 scatteringAlbedo,
                     inout uint seed) {
    const int MAX_SSS_BOUNCES = 8;
    
    // Pick a random channel to drive the free-path sampling (luminance-weighted)
    float3 lum = float3(0.2126, 0.7152, 0.0722);
    float sigma_avg = max(dot(sigma_t, lum), 1e-5);
    
    float3 throughput = float3(1.0);
    float3 pos = entryPos - N * 0.002; // offset inside the surface
    float3 dir = entryDir;
    
    for (int i = 0; i < MAX_SSS_BOUNCES; i++) {
        // Sample free path distance using exponential distribution
        float freePath = -log(max(rand(seed), 1e-7)) / sigma_avg;
        
        // Trace through the scene
        RayDesc walkRay;
        walkRay.Origin    = pos;
        walkRay.Direction = dir;
        walkRay.TMin      = 1e-5;
        walkRay.TMax      = freePath;
        
        RayQuery<RAY_FLAG_FORCE_OPAQUE> wq;
        bool hasGeom = (push.hasGeometry != 0u);
        if (hasGeom) {
            wq.TraceRayInline(tlas, RAY_FLAG_FORCE_OPAQUE, 0xFF, walkRay);
            wq.Proceed();
        }
        
        // Beer-Lambert attenuation over the sampled distance
        float travelDist = freePath;
        bool hitSurface  = false;
        float3 exitNorm  = float3(0.0);
        
        if (hasGeom && wq.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            travelDist = wq.CommittedRayT();
            hitSurface = true;
            // Approximate exit normal from the committed hit (inverted, since we're inside)
            uint pIdx = wq.CommittedInstanceID();
            uint primId = wq.CommittedPrimitiveIndex();
            float2 b = wq.CommittedTriangleBarycentrics();
            float3 bary = float3(1.0 - b.x - b.y, b.x, b.y);
            PartData exitPart = push.parts[pIdx];
            InstanceData exitInst = push.instances[exitPart.instanceIdx];
            uint ei0, ei1, ei2;
            if ((exitPart.flags & 1u) != 0u) {
                ei0 = exitPart.indices[primId * 3 + 0];
                ei1 = exitPart.indices[primId * 3 + 1];
                ei2 = exitPart.indices[primId * 3 + 2];
            } else {
                uint ew0 = exitPart.indices[(primId * 3 + 0) / 2];
                uint ew1 = exitPart.indices[(primId * 3 + 1) / 2];
                uint ew2 = exitPart.indices[(primId * 3 + 2) / 2];
                ei0 = ((primId * 3 + 0) % 2 == 0) ? (ew0 & 0xFFFF) : (ew0 >> 16);
                ei1 = ((primId * 3 + 1) % 2 == 0) ? (ew1 & 0xFFFF) : (ew1 >> 16);
                ei2 = ((primId * 3 + 2) % 2 == 0) ? (ew2 & 0xFFFF) : (ew2 >> 16);
            }
            Vertex en0 = exitPart.vertices[ei0];
            Vertex en1 = exitPart.vertices[ei1];
            Vertex en2 = exitPart.vertices[ei2];
            float3 localN = normalize(float3(en0.nx, en0.ny, en0.nz) * bary.x +
                                      float3(en1.nx, en1.ny, en1.nz) * bary.y +
                                      float3(en2.nx, en2.ny, en2.nz) * bary.z);
            exitNorm = normalize(mul((float3x3)exitInst.modelMatrix, localN));
        }
        
        float3 atten = exp(-sigma_t * travelDist);
        throughput *= atten;
        
        pos = pos + dir * travelDist;
        
        if (hitSurface) {
            // Ray exited the object — compute diffuse exit radiance via NEE
            float3 exitRadiance = float3(0.0);
            for (uint li = 0; li < push.lightCount; li++) {
                LightData light = push.lights[li];
                float lightType = light.direction.w;
                float3 L;
                float attenuation = 1.0;
                
                if (lightType == 0.0) {
                    L = normalize(light.direction.xyz);
                } else {
                    float3 delta = light.position.xyz - pos;
                    float dist   = length(delta);
                    L = delta / dist;
                    float range   = light.position.w;
                    float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                    attenuation   = (falloff * falloff) / (dist * dist + 1.0);
                }
                
                RayDesc shadowRay;
                shadowRay.Origin    = pos + exitNorm * 0.002;
                shadowRay.Direction = L;
                shadowRay.TMin      = 0.001;
                shadowRay.TMax      = lightType == 0.0 ? 10000.0 : length(light.position.xyz - pos) - 0.001;
                
                RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> sq;
                if (hasGeom) { sq.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, shadowRay); sq.Proceed(); }
                
                if (!hasGeom || sq.CommittedStatus() != COMMITTED_TRIANGLE_HIT) {
                    float dotNL = clamp(dot(exitNorm, L), 0.0, 1.0);
                    exitRadiance += light.color.rgb * light.color.w * attenuation * dotNL / PI;
                }
            }
            
            // Single-scatter sky contribution at exit point
            exitRadiance += GetSkyRadianceNoSun(exitNorm, push.sky.sunDirAndRadius.xyz, push.sky) * 0.3;
            
            return throughput * scatteringAlbedo * exitRadiance;
        }
        
        // Still inside — scatter isotropically (Henyey-Greenstein g=0 = isotropic)
        throughput *= scatteringAlbedo;
        
        float lum2 = max(throughput.x, max(throughput.y, throughput.z));
        if (!(lum2 > 0.0)) break;
        
        // Russian Roulette inside SSS walk
        if (i > 2) {
            float rrP = min(lum2, 0.95);
            if (rand(seed) > rrP) break;
            throughput /= rrP;
        }
        
        // Sample new isotropic direction
        dir = SampleUniformSphere(rand(seed), rand(seed));
    }
    
    return float3(0.0);
}

// =====================================================================
// Main compute shader
// =====================================================================
[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 dispatchThreadID : SV_DispatchThreadID) {
    if (dispatchThreadID.x >= uint(push.resolution.x) || dispatchThreadID.y >= uint(push.resolution.y)) return;

    // ---- Debug mode ----
    if ((push.debugFlags & 1u) != 0u) {
        float2 inUV = (float2(dispatchThreadID.xy) + 0.5) / push.resolution;
        float2 ndc = inUV * 2.0 - 1.0;
        ndc.y = -ndc.y;
        float3 rayOrigin = push.camera->cameraPosition.xyz;
        float4 target = mul(push.camera->invViewProj, float4(ndc.x, ndc.y, 1.0, 1.0));
        float3 worldPos = target.xyz / target.w;
        float3 rayDir = normalize(worldPos - rayOrigin);
        RayDesc dbgRay;
        dbgRay.Origin = rayOrigin; dbgRay.TMin = 0.001;
        dbgRay.Direction = rayDir; dbgRay.TMax = 10000.0;
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> dbgQ;
        if (push.hasGeometry != 0u) { dbgQ.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, dbgRay); dbgQ.Proceed(); }
        float3 dbgColor;
        if (push.hasGeometry != 0u && dbgQ.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            float t = dbgQ.CommittedRayT();
            float depth = saturate(t / 20.0);
            dbgColor = float3(1.0 - depth);
            uint instId = dbgQ.CommittedInstanceID();
            if ((instId & 1u) != 0u) dbgColor.rb *= 0.8;
            if ((instId & 2u) != 0u) dbgColor.gb *= 0.8;
        } else {
            dbgColor = GetSkyRadiance(rayDir, push.sky.sunDirAndRadius.xyz, push.sky);
        }
        outputBuffer[dispatchThreadID.xy] = float4(dbgColor, 1.0);
        return;
    }

    uint seed = dispatchThreadID.y * uint(push.resolution.x) + dispatchThreadID.x + push.frameCount * 719393;
    
    float2 pixelCenter = float2(dispatchThreadID.xy) + float2(rand(seed), rand(seed));
    float2 inUV = pixelCenter / push.resolution;
    float2 ndc  = inUV * 2.0 - 1.0;
    ndc.y = -ndc.y;
    
    float3 rayOrigin = push.camera->cameraPosition.xyz;
    float4 target  = mul(push.camera->invViewProj, float4(ndc.x, ndc.y, 1.0, 1.0));
    float3 worldPos = target.xyz / target.w;
    float3 rayDir   = normalize(worldPos - rayOrigin);
    
    float3 throughput = float3(1.0);
    float3 radiance   = float3(0.0);
    
    RayDesc ray;
    ray.Origin    = rayOrigin;
    ray.TMin      = 0.001;
    ray.Direction = rayDir;
    ray.TMax      = 10000.0;
    
    const int MAX_BOUNCES = 4;
    int bounce = 0;
    float lastBsdfPdf = 1.0;
    
    float3 sunDir    = push.sky.sunDirAndRadius.xyz;
    float  sunRadius = push.sky.sunDirAndRadius.w;
    float  sunPdf    = SunLightPdf(sunRadius);

    for (int iter = 0; iter < 32; iter++) {
        if (bounce >= MAX_BOUNCES) break;

        RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
        if (push.hasGeometry != 0u) {
            q.TraceRayInline(tlas, RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
            q.Proceed();
        }
        
        if (push.hasGeometry != 0u && q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            uint partIdx = q.CommittedInstanceID();
            uint primId  = q.CommittedPrimitiveIndex();
            float2 b     = q.CommittedTriangleBarycentrics();
            float3 bary  = float3(1.0 - b.x - b.y, b.x, b.y);
            float  t     = q.CommittedRayT();
            
            float3 hitPos = ray.Origin + ray.Direction * t;
            float3 N, T, B, albedo;
            float  surfSubsurface;
            float3 subsurfaceRadius, subsurfaceColor;
            
            PartData    part = push.parts[partIdx];
            InstanceData inst = push.instances[part.instanceIdx];
            DisneyMaterial mat = GetMaterial(part, inst, primId, bary, N, T, B, albedo,
                                             surfSubsurface, subsurfaceRadius, subsurfaceColor);
            float3 V = -ray.Direction;
            bounce++;
            
            // Emissive (first hit only)
            if (bounce == 1) {
                MaterialData mdata = push.materials[part.materialIdx];
                float3 emissive = mdata.emissiveColor.rgb;
                if (mdata.emissiveTexIndex != 0xFFFFFFFF) {
                    uint e0, e1, e2;
                    if ((part.flags & 1u) != 0u) {
                        e0 = part.indices[primId * 3 + 0]; e1 = part.indices[primId * 3 + 1]; e2 = part.indices[primId * 3 + 2];
                    } else {
                        uint dw0 = part.indices[(primId * 3 + 0) / 2];
                        uint dw1 = part.indices[(primId * 3 + 1) / 2];
                        uint dw2 = part.indices[(primId * 3 + 2) / 2];
                        e0 = ((primId * 3 + 0) % 2 == 0) ? (dw0 & 0xFFFF) : (dw0 >> 16);
                        e1 = ((primId * 3 + 1) % 2 == 0) ? (dw1 & 0xFFFF) : (dw1 >> 16);
                        e2 = ((primId * 3 + 2) % 2 == 0) ? (dw2 & 0xFFFF) : (dw2 >> 16);
                    }
                    float2 uv0e = float2(part.vertices[e0].u, part.vertices[e0].v);
                    float2 uv1e = float2(part.vertices[e1].u, part.vertices[e1].v);
                    float2 uv2e = float2(part.vertices[e2].u, part.vertices[e2].v);
                    float2 uve  = uv0e * bary.x + uv1e * bary.y + uv2e * bary.z;
                    emissive *= bindless.textures[mdata.emissiveTexIndex].SampleLevel(tex_sampler, uve, 0).rgb;
                }
                radiance += throughput * emissive;
            }
            
            // ---- Random-Walk SSS decision ----
            // Only attempt SSS if the material has a non-zero subsurface weight and
            // we are hitting the surface from outside (V · N > 0).
            bool doSSS = (surfSubsurface > 0.0) && (dot(V, N) > 0.0) && (rand(seed) < surfSubsurface);
            
            if (doSSS) {
                // Sigma_t = 1 / mean_free_path
                float3 sigma_t = float3(
                    1.0 / max(subsurfaceRadius.x, 1e-4),
                    1.0 / max(subsurfaceRadius.y, 1e-4),
                    1.0 / max(subsurfaceRadius.z, 1e-4));
                
                float3 sssRadiance = RandomWalkSSS(hitPos, -V, N, sigma_t, subsurfaceColor, seed);
                radiance += throughput * sssRadiance;
                
                // After SSS we terminate this path — the walk has already accounted for
                // indirect bounces inside the object.
                break;
            }
            
            // ---- NEE (Next Event Estimation) ----
            // We shoot a deterministic shadow ray toward each light — no PDF division required
            // because we are not importance-sampling the light direction stochastically here.
            // MIS applies only between the NEE strategy and the BSDF-bounce strategy when a
            // BSDF ray happens to escape to sky — that is handled at the env-miss branch below.
            float3 directLight = float3(0.0);
            for (uint i = 0; i < push.lightCount; i++) {
                LightData light = push.lights[i];
                float lightType = light.direction.w;
                float3 L;
                float attenuation = 1.0;
                
                if (lightType == 0.0) {
                    L = normalize(light.direction.xyz);
                } else {
                    float3 delta = light.position.xyz - hitPos;
                    float  dist  = length(delta);
                    L = delta / dist;
                    float range   = light.position.w;
                    float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                    attenuation   = (falloff * falloff) / (dist * dist + 1.0);
                }
                
                RayDesc shadowRay;
                shadowRay.Origin    = hitPos + N * 0.001;
                shadowRay.Direction = L;
                shadowRay.TMin      = 0.001;
                shadowRay.TMax      = lightType == 0.0 ? 10000.0 : length(light.position.xyz - hitPos) - 0.001;
                
                RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> sq;
                if (push.hasGeometry != 0u) {
                    sq.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, shadowRay);
                    sq.Proceed();
                }
                
                if (push.hasGeometry == 0u || sq.CommittedStatus() != COMMITTED_TRIANGLE_HIT) {
                    float3 bsdf  = EvaluateDisneyBSDF(mat, N, V, L, T, B);
                    float  dotNL = clamp(dot(N, L), 0.001, 1.0);
                    directLight += bsdf * light.color.rgb * light.color.w * attenuation * dotNL;
                }
            }
            radiance += throughput * directLight;
            
            if (any(isnan(radiance))) break;
            
            // ---- Indirect bounce ----
            float  pdf;
            float3 nextDir = SampleDisneyBSDF(mat, N, V, T, B, float2(rand(seed), rand(seed)), pdf);
            if (pdf <= 0.0) break;
            
            float3 bsdf   = EvaluateDisneyBSDF(mat, N, V, nextDir, T, B);
            float  dotNL  = clamp(dot(N, nextDir), 0.0, 1.0);
            throughput   *= (bsdf * dotNL) / pdf;
            lastBsdfPdf   = pdf;
            
            float lum = max(throughput.x, max(throughput.y, throughput.z));
            if (!(lum > 0.0)) break;
            
            if (bounce > 2) {
                float rrProb = min(lum, 0.95);
                if (rrProb < 0.001 || rand(seed) > rrProb) break;
                throughput /= rrProb;
            }
            if (any(isnan(throughput))) break;
            
            ray.Origin    = hitPos + N * 0.001;
            ray.Direction = nextDir;
            ray.TMax      = 10000.0;
        } else {
            // Ray missed — sample sky
            float3 dir = ray.Direction;
            bool   hitSun = HitsSun(dir, sunDir, sunRadius);
            
            if (bounce == 0) {
                radiance += throughput * GetSkyRadiance(dir, sunDir, push.sky);
            } else if (hitSun) {
                float3 skyNoSun = GetSkyRadianceNoSun(dir, sunDir, push.sky);
                float3 sunDisk  = float3(push.sky.intensityTurbidityAlbedoPad.x) * 1000.0;
                float  misW     = MISWeight(lastBsdfPdf, sunPdf);
                radiance += throughput * (skyNoSun + misW * sunDisk);
            } else {
                radiance += throughput * GetSkyRadianceNoSun(dir, sunDir, push.sky);
            }
            break;
        }
    }
    
    if (any(isnan(radiance)) || any(isinf(radiance))) radiance = float3(0.0);
    
    float3 oldColor = push.frameCount == 0 ? float3(0.0) : accumulationBuffer[dispatchThreadID.xy].rgb;
    float3 newColor = lerp(oldColor, radiance, 1.0 / float(push.frameCount + 1));
    accumulationBuffer[dispatchThreadID.xy] = float4(newColor, 1.0);
    
    float  exposure = push.camera->cameraPosition.w;
    float3 exposed  = newColor * exposure;
    float a = 2.51f; float b2 = 0.03f; float c = 2.43f; float d = 0.59f; float e = 0.14f;
    float3 mapped = clamp((exposed*(a*exposed+b2))/(exposed*(c*exposed+d)+e), 0.0, 1.0);
    mapped = pow(max(mapped, 0.0), float3(1.0/2.2));
    outputBuffer[dispatchThreadID.xy] = float4(mapped, 1.0);
}
