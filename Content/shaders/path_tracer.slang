// SPDX-License-Identifier: MIT

#include "scene_data.slang"
#include "disney_bsdf.slang"
#include "nishita_sky.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct BindlessHeap {
    Texture2D textures[4096];
};
[binding(1, 0)] ParameterBlock<BindlessHeap> bindless;
[binding(2, 0)] SamplerState tex_sampler;

RWTexture2D<float4> accumulationBuffer : register(u3);
RWTexture2D<float4> outputBuffer : register(u4);
[binding(5, 0)] RaytracingAccelerationStructure tlas;

// RNG based on PCG
uint pcg_hash(uint input) {
    uint state = input * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint seed) {
    seed = pcg_hash(seed);
    return float(seed) / 4294967296.0;
}

struct RayPayload {
    bool hit;
    float t;
    uint instanceId;
    uint primitiveId;
    float2 barycentrics;
};

// Returns Disney material from hit geometry
DisneyMaterial GetMaterial(uint instanceId, uint primitiveId, float2 barycentrics, out float3 hitNormal, out float3 hitTangent, out float3 hitBitangent, out float3 albedo) {
    // TLAS instance id corresponds to instance array index directly (as setup in PathTracerPass)
    InstanceData inst = push.instances[instanceId];
    
    // For simplicity, we assume one part per instance for now (or we would need to map instance/primitive to the correct part).
    // The BVH builds a BLAS per mesh, and a mesh has 1 part.
    PartData part = push.parts[inst.firstPartIndex];
    
    uint i0 = part.indices[primitiveId * 3 + 0];
    uint i1 = part.indices[primitiveId * 3 + 1];
    uint i2 = part.indices[primitiveId * 3 + 2];
    
    Vertex v0 = part.vertices[i0];
    Vertex v1 = part.vertices[i1];
    Vertex v2 = part.vertices[i2];
    
    float w = 1.0 - barycentrics.x - barycentrics.y;
    
    float3 localNormal = normalize(v0.normal * w + v1.normal * barycentrics.x + v2.normal * barycentrics.y);
    float3 localTangent = normalize(v0.tangent.xyz * w + v1.tangent.xyz * barycentrics.x + v2.tangent.xyz * barycentrics.y);
    float tangentW = v0.tangent.w;
    float2 uv = v0.uv * w + v1.uv * barycentrics.x + v2.uv * barycentrics.y;
    
    hitNormal = normalize(mul((float3x3)inst.modelMatrix, localNormal));
    hitTangent = normalize(mul((float3x3)inst.modelMatrix, localTangent));
    // Guard against degenerate tangents (covers both zero and NaN)
    if (!(dot(hitTangent, hitTangent) > 0.001)) {
        float3 up = abs(hitNormal.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        hitTangent = normalize(cross(up, hitNormal));
    }
    hitBitangent = cross(hitNormal, hitTangent) * tangentW;
    if (!(dot(hitBitangent, hitBitangent) > 0.001)) {
        hitBitangent = cross(hitNormal, hitTangent);
    }
    hitBitangent = normalize(hitBitangent);
    hitTangent = normalize(cross(hitBitangent, hitNormal));
    
    MaterialData mat = push.materials[part.materialIdx];
    
    float3 baseColor = mat.baseColor.rgb;
    if (mat.albedoTexIndex != 0xFFFFFFFF) {
        baseColor *= bindless.textures[mat.albedoTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
    }
    albedo = baseColor;
    
    float metallic = mat.metallic;
    float roughness = mat.roughness;
    if (mat.rmaTexIndex != 0xFFFFFFFF) {
        float3 rma = bindless.textures[mat.rmaTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
        roughness *= rma.g;
        metallic *= rma.b;
    }
    
    if (mat.normalTexIndex != 0xFFFFFFFF) {
        float3 normalMap = bindless.textures[mat.normalTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        float3x3 TBN = float3x3(hitTangent, hitBitangent, hitNormal);
        hitNormal = normalize(mul(normalMap, TBN));
        
        hitBitangent = cross(hitNormal, hitTangent) * tangentW;
        if (!(dot(hitBitangent, hitBitangent) > 0.001)) hitBitangent = cross(hitNormal, hitTangent);
        hitBitangent = normalize(hitBitangent);
        hitTangent = normalize(cross(hitBitangent, hitNormal));
    }
    
    DisneyMaterial dmat;
    dmat.baseColor = baseColor;
    dmat.metallic = metallic;
    dmat.subsurface = 0.0;
    dmat.specular = 0.5;
    dmat.roughness = max(roughness, 0.01);
    dmat.specularTint = 0.0;
    dmat.anisotropic = 0.0;
    dmat.sheen = 0.0;
    dmat.sheenTint = 0.5;
    dmat.clearcoat = 0.0;
    dmat.clearcoatGloss = 1.0;
    
    return dmat;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 dispatchThreadID : SV_DispatchThreadID) {
    if (dispatchThreadID.x >= uint(push.resolution.x) || dispatchThreadID.y >= uint(push.resolution.y)) return;

    // ---- No-geometry fast path: pure sky, no ray tracing at all ----
    if (push.hasGeometry == 0u) {
        float2 inUV = (float2(dispatchThreadID.xy) + 0.5) / push.resolution;
        float2 ndc = inUV * 2.0 - 1.0;
        ndc.y = -ndc.y;

        float3 rayOrigin = push.camera->cameraPosition.xyz;
        float4 target = mul(push.camera->invViewProj, float4(ndc.x, ndc.y, 1.0, 1.0));
        float3 worldPos = target.xyz / target.w;
        float3 rayDir = normalize(worldPos - rayOrigin);

        float3 skyColor = GetSkyRadiance(rayDir, push.sky.sunDirection, push.sky);

        // Diagnostic: tint top-left 16x16 quadrant magenta to prove dispatch works
        if (dispatchThreadID.x < 16u && dispatchThreadID.y < 16u) {
            skyColor = float3(1.0, 0.0, 1.0); // bright magenta
        }

        outputBuffer[dispatchThreadID.xy] = float4(skyColor, 1.0);
        return;
    }
    // ---- End no-geometry fast path ----

    // ---- Debug mode: hit-distance visualization (fast, no BSDF) ----
    if ((push.debugFlags & 1u) != 0u) {
        float2 inUV = (float2(dispatchThreadID.xy) + 0.5) / push.resolution;
        float2 ndc = inUV * 2.0 - 1.0;
        ndc.y = -ndc.y;

        float3 rayOrigin = push.camera->cameraPosition.xyz;
        float4 target = mul(push.camera->invViewProj, float4(ndc.x, ndc.y, 1.0, 1.0));
        float3 worldPos = target.xyz / target.w;
        float3 rayDir = normalize(worldPos - rayOrigin);
        RayDesc dbgRay;
        dbgRay.Origin = rayOrigin;
        dbgRay.TMin = 0.001;
        dbgRay.Direction = rayDir;
        dbgRay.TMax = 10000.0;
        
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> dbgQ;
        dbgQ.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, dbgRay);
        dbgQ.Proceed();
        
        float3 dbgColor;
        if (dbgQ.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            float t = dbgQ.CommittedRayT();
            float depth = saturate(t / 20.0); // 0=near(white), 1=far(black)
            dbgColor = float3(1.0 - depth, 1.0 - depth, 1.0 - depth);
            
            // Tint by instance ID for debugging
            uint instId = dbgQ.CommittedInstanceID();
            if ((instId & 1u) != 0u) dbgColor.rb *= 0.8;
            if ((instId & 2u) != 0u) dbgColor.gb *= 0.8;
        } else {
            dbgColor = GetSkyRadiance(rayDir, push.sky.sunDirection, push.sky);
        }
        outputBuffer[dispatchThreadID.xy] = float4(dbgColor, 1.0);
        return;
    }
    // ---- End debug mode ----
    
    uint seed = dispatchThreadID.y * uint(push.resolution.x) + dispatchThreadID.x + push.frameCount * 719393;
    
    float2 pixelCenter = float2(dispatchThreadID.xy) + float2(rand(seed), rand(seed));
    float2 inUV = pixelCenter / push.resolution;
    float2 ndc = inUV * 2.0 - 1.0;
    ndc.y = -ndc.y;
    
    float3 rayOrigin = push.camera->cameraPosition.xyz;
    float4 target = mul(push.camera->invViewProj, float4(ndc.x, ndc.y, 1.0, 1.0));
    float3 worldPos = target.xyz / target.w;
    float3 rayDir = normalize(worldPos - rayOrigin);
    
    float3 throughput = float3(1.0);
    float3 radiance = float3(0.0);
    
    RayDesc ray;
    ray.Origin = rayOrigin;
    ray.TMin = 0.001;
    ray.Direction = rayDir;
    ray.TMax = 10000.0;
    
    const int MAX_BOUNCES = 4;
    
    for (int bounce = 0; bounce < MAX_BOUNCES; bounce++) {
        RayQuery<RAY_FLAG_NONE> q;
        q.TraceRayInline(tlas, RAY_FLAG_NONE, 0xFF, ray);
        q.Proceed();
        
        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            uint instId = q.CommittedInstanceID();
            uint primId = q.CommittedPrimitiveIndex();
            float2 bary = q.CommittedTriangleBarycentrics();
            float t = q.CommittedRayT();
            
            float3 hitPos = ray.Origin + ray.Direction * t;
            float3 N, T, B, albedo;
            
            DisneyMaterial mat = GetMaterial(instId, primId, bary, N, T, B, albedo);
            
            // Direct Lighting (Next Event Estimation)
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
                    float dist = length(delta);
                    L = delta / dist;
                    float range = light.position.w;
                    float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
                    attenuation = (falloff * falloff) / (dist * dist + 1.0);
                }
                
                // Shadow Ray
                RayDesc shadowRay;
                shadowRay.Origin = hitPos + N * 0.001;
                shadowRay.Direction = L;
                shadowRay.TMin = 0.001;
                shadowRay.TMax = lightType == 0.0 ? 10000.0 : length(light.position.xyz - hitPos) - 0.001;
                
                RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> sq;
                sq.TraceRayInline(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH, 0xFF, shadowRay);
                sq.Proceed();
                
                if (sq.CommittedStatus() != COMMITTED_TRIANGLE_HIT) {
                    float3 bsdf = EvaluateDisneyBSDF(mat, N, -ray.Direction, L, T, B);
                    float dotNL = clamp(dot(N, L), 0.001, 1.0);
                    directLight += bsdf * light.color.rgb * light.color.w * attenuation * dotNL;
                }
            }
            
            radiance += throughput * directLight;
            
            // Emissive
            MaterialData mdata = push.materials[push.parts[push.instances[instId].firstPartIndex].materialIdx];
            float3 emissive = mdata.emissiveColor.rgb;
            if (mdata.emissiveTexIndex != 0xFFFFFFFF) {
                // Approximate UV
                float w = 1.0 - bary.x - bary.y;
                float2 uv = push.parts[push.instances[instId].firstPartIndex].vertices[push.parts[push.instances[instId].firstPartIndex].indices[primId * 3 + 0]].uv * w
                          + push.parts[push.instances[instId].firstPartIndex].vertices[push.parts[push.instances[instId].firstPartIndex].indices[primId * 3 + 1]].uv * bary.x
                          + push.parts[push.instances[instId].firstPartIndex].vertices[push.parts[push.instances[instId].firstPartIndex].indices[primId * 3 + 2]].uv * bary.y;
                emissive *= bindless.textures[mdata.emissiveTexIndex].SampleLevel(tex_sampler, uv, 0).rgb;
            }
            if (bounce == 0) {
                radiance += throughput * emissive;
            }
            
            // Indirect Bounce (Sample BSDF)
            float pdf;
            float3 nextDir = SampleDisneyBSDF(mat, N, -ray.Direction, T, B, float2(rand(seed), rand(seed)), pdf);
            
            if (pdf <= 0.0) break;
            
            float3 bsdf = EvaluateDisneyBSDF(mat, N, -ray.Direction, nextDir, T, B);
            float dotNL = clamp(dot(N, nextDir), 0.0, 1.0);
            
            throughput *= (bsdf * dotNL) / pdf;
            
            // Terminate on zero throughput (prevents NaN)
            float lum = max(throughput.x, max(throughput.y, throughput.z));
            if (!(lum > 0.0)) break;

            // Russian Roulette
            if (bounce > 2) {
                float rrProb = min(lum, 0.95);
                if (rrProb < 0.001 || rand(seed) > rrProb) break;
                throughput /= rrProb;
            }
            
            ray.Origin = hitPos + N * 0.001;
            ray.Direction = nextDir;
            ray.TMax = 10000.0;
        } else {
            // Nishita physically-based sky
            radiance += throughput * GetSkyRadiance(rayDir, push.sky.sunDirection, push.sky);
            break;
        }
    }
    
    // Accumulation — guard against uninitialized texture memory on frame 0
    float3 oldColor = push.frameCount == 0 ? float3(0.0, 0.0, 0.0) : accumulationBuffer[dispatchThreadID.xy].rgb;
    float3 newColor = lerp(oldColor, radiance, 1.0 / float(push.frameCount + 1));
    accumulationBuffer[dispatchThreadID.xy] = float4(newColor, 1.0);
    
    // Tonemapping
    float exposure = push.camera->cameraPosition.w;
    float3 exposed = newColor * exposure;
    
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    float3 mapped = clamp((exposed*(a*exposed+b))/(exposed*(c*exposed+d)+e), 0.0, 1.0);
    mapped = pow(mapped, float3(1.0/2.2, 1.0/2.2, 1.0/2.2));
    
    outputBuffer[dispatchThreadID.xy] = float4(mapped, 1.0);
}
