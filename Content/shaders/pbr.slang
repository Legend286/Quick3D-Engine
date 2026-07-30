// pbr.slang

#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct BindlessHeap {
    Texture2D textures[4096];
};
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;
[[vk::binding(2, 0)]] SamplerState tex_sampler;

struct VertexOutput {
    float4 position : SV_Position;
    float3 worldPos : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    uint materialIdx : BLENDINDICES0;
};

[shader("vertex")]
VertexOutput vertexMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) 
{
    VertexOutput output;
    
    PartData part = push.parts[iid];
    InstanceData inst = push.instances[part.instanceIdx];
    
    uint index = part.indices[vid];
    
    Vertex v = part.vertices[index];
    
    float3 vPos =
        float3(v.px, v.py, v.pz) +
        part.localOffset.xyz;
    float3 vNorm = float3(v.nx, v.ny, v.nz);
    float4 vTang = float4(v.tx, v.ty, v.tz, v.tw);
    
    float4 worldPos = mul(inst.modelMatrix, float4(vPos, 1.0));
    output.worldPos = worldPos.xyz;
    output.position = mul(push.camera->viewProj, worldPos);
    output.normal = normalize(mul((float3x3)inst.modelMatrix, vNorm));
    output.tangent = float4(normalize(mul((float3x3)inst.modelMatrix, vTang.xyz)), vTang.w);
    output.uv = float2(v.u, v.v);
    output.materialIdx = part.materialIdx;
    
    return output;
}

// Constants
#include "disney_bsdf.slang"

// Noise Functions for Material Masking
float hash(float3 p) {
    p = frac(p * float3(0.1311, 0.2811, 0.3811));
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float noise3D(float3 x) {
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(lerp(hash(i + float3(0,0,0)), hash(i + float3(1,0,0)), f.x),
             lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
        lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x),
             lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
}

float voronoi3D(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    float minDist = 1.0;
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                float3 neighbor = float3(x, y, z);
                float3 point = float3(hash(i + neighbor), hash(i + neighbor + 13.5), hash(i + neighbor + 27.1));
                float3 diff = neighbor + point - f;
                float dist = length(diff);
                minDist = min(minDist, dist);
            }
        }
    }
    return minDist;
}

float fbm3D(float3 p, int octaves) {
    float val = 0.0;
    float amp = 0.5;
    float3 pos = p;
    for (int i = 0; i < 4; i++) {
        if (i >= octaves) break;
        val += amp * noise3D(pos);
        pos *= 2.0;
        amp *= 0.5;
    }
    return val;
}

float turbulence3D(float3 p, int octaves) {
    float val = 0.0;
    float amp = 0.5;
    float3 pos = p;
    for (int i = 0; i < 4; i++) {
        if (i >= octaves) break;
        val += amp * abs(noise3D(pos) * 2.0 - 1.0);
        pos *= 2.0;
        amp *= 0.5;
    }
    return val;
}

float4x4 GetDirectionalShadowMatrix(uint cascadeIndex)
{
    if (cascadeIndex == 1)
        return push.directionalShadowViewProj1;
    if (cascadeIndex == 2)
        return push.directionalShadowViewProj2;
    if (cascadeIndex == 3)
        return push.directionalShadowViewProj3;
    return push.directionalShadowViewProj;
}

float GetDirectionalShadowSplit(uint cascadeIndex)
{
    if (cascadeIndex == 1)
        return push.directionalShadowSplits.y;
    if (cascadeIndex == 2)
        return push.directionalShadowSplits.z;
    if (cascadeIndex == 3)
        return push.directionalShadowSplits.w;
    return push.directionalShadowSplits.x;
}

uint GetDirectionalShadowTextureIndex(uint cascadeIndex)
{
    if (cascadeIndex == 1)
        return (uint)push.directionalShadowTextureIndices.y;
    if (cascadeIndex == 2)
        return (uint)push.directionalShadowTextureIndices.z;
    if (cascadeIndex == 3)
        return (uint)push.directionalShadowTextureIndices.w;
    return (uint)push.directionalShadowTextureIndices.x;
}

float SampleDirectionalShadowCascade(
    uint cascadeIndex,
    float3 worldPos,
    float3 normal,
    float3 lightDirection)
{
    float4 shadowClip = mul(
        GetDirectionalShadowMatrix(cascadeIndex),
        float4(worldPos, 1.0));
    if (shadowClip.w <= 0.0)
        return 1.0;

    float3 shadowNdc = shadowClip.xyz / shadowClip.w;
    float2 localUv = shadowNdc.xy * float2(0.5, -0.5) + 0.5;
    if (any(localUv < 0.0) || any(localUv > 1.0) ||
        shadowNdc.z < 0.0 || shadowNdc.z > 1.0)
        return 1.0;

    float2 shadowUv = localUv;
    float biasScale = 1.0 + cascadeIndex * 0.75;
    float bias = max(
        0.0015 * biasScale *
            (1.0 - saturate(dot(normal, lightDirection))),
        0.0002 * biasScale);
    float visibility = 0.0;
    float texelSize = push.directionalShadowParams.y;
    float2 tileMin = float2(texelSize * 0.5);
    float2 tileMax = float2(1.0 - texelSize * 0.5);
    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            float2 sampleUv = clamp(
                shadowUv + float2(x, y) * texelSize,
                tileMin,
                tileMax);
            uint textureIndex =
                GetDirectionalShadowTextureIndex(cascadeIndex);
            float sampledDepth = bindless.textures[textureIndex].SampleLevel(
                tex_sampler,
                sampleUv,
                0.0).x;
            visibility += shadowNdc.z - bias <= sampledDepth ? 1.0 : 0.0;
        }
    }
    return visibility / 9.0;
}

float EvaluateDirectionalShadow(float3 worldPos, float3 normal, float3 lightDirection)
{
    if (push.directionalShadowParams.x < 0.5)
        return 1.0;

    float2 horizontalOffset =
        worldPos.xz - push.camera->cameraPosition.xz;
    float horizontalDistance = length(horizontalOffset);
    if (horizontalDistance > push.directionalShadowSplits.w)
        return 1.0;

    uint cascadeIndex =
        horizontalDistance <= push.directionalShadowSplits.x ? 0 :
        horizontalDistance <= push.directionalShadowSplits.y ? 1 :
        horizontalDistance <= push.directionalShadowSplits.z ? 2 : 3;
    float visibility = SampleDirectionalShadowCascade(
        cascadeIndex,
        worldPos,
        normal,
        lightDirection);

    if (cascadeIndex < 3) {
        float cascadeStart = cascadeIndex == 0
            ? 0.0
            : GetDirectionalShadowSplit(cascadeIndex - 1);
        float cascadeEnd = GetDirectionalShadowSplit(cascadeIndex);
        float blendWidth = max((cascadeEnd - cascadeStart) * 0.03, 0.5);
        float blend = saturate(
            (horizontalDistance - (cascadeEnd - blendWidth)) / blendWidth);
        if (blend > 0.0) {
            float nextVisibility = SampleDirectionalShadowCascade(
                cascadeIndex + 1,
                worldPos,
                normal,
                lightDirection);
            visibility = lerp(visibility, nextVisibility, blend);
        }
    }
    return visibility;
}

uint GetPointShadowFace(float3 fromLight)
{
    float3 absoluteDirection = abs(fromLight);
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
        return fromLight.x >= 0.0 ? 0 : 1;
    if (absoluteDirection.y >= absoluteDirection.z)
        return fromLight.y >= 0.0 ? 2 : 3;
    return fromLight.z >= 0.0 ? 4 : 5;
}

float SamplePunctualShadowLayer(
    PunctualShadowFaceData face,
    float4 uvScaleBias,
    uint textureIndex,
    float3 shadowNdc,
    float bias,
    bool valid)
{
    if (!valid)
        return 1.0;
    float2 localUv =
        shadowNdc.xy * float2(0.5, -0.5) + 0.5;
    float2 atlasUv =
        localUv * uvScaleBias.xy + uvScaleBias.zw;
    float2 texelSize = float2(1.0 / 4096.0);
    float2 tileMinimum =
        uvScaleBias.zw + texelSize * 0.5;
    float2 tileMaximum =
        uvScaleBias.zw + uvScaleBias.xy -
        texelSize * 0.5;
    float visibility = 0.0;
    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            float sampledDepth =
                bindless.textures[textureIndex].SampleLevel(
                    tex_sampler,
                    clamp(
                        atlasUv + float2(x, y) * texelSize,
                        tileMinimum,
                        tileMaximum),
                    0.0).x;
            visibility +=
                shadowNdc.z - bias <= sampledDepth
                    ? 1.0
                    : 0.0;
        }
    }
    return visibility / 9.0;
}

float EvaluatePunctualShadow(
    uint lightIndex,
    LightData light,
    float3 worldPos,
    float3 normal,
    float3 lightDirection)
{
    uint firstRecordIndex = lightIndex * 6;
    if (firstRecordIndex >= push.punctualShadowFaceCount)
        return 1.0;
    PunctualShadowFaceData firstFace =
        push.punctualShadowFaces[firstRecordIndex];
    uint faceIndex = light.direction.w == 1.0
        ? GetPointShadowFace(
            worldPos - firstFace.committedLightPosition.xyz)
        : 0;
    uint recordIndex = firstRecordIndex + faceIndex;
    if (recordIndex >= push.punctualShadowFaceCount)
        return 1.0;
    PunctualShadowFaceData face =
        push.punctualShadowFaces[recordIndex];
    float4 shadowClip = mul(
        face.viewProjection,
        float4(worldPos, 1.0));
    if (shadowClip.w <= 0.0)
        return 1.0;
    float3 shadowNdc = shadowClip.xyz / shadowClip.w;
    if (any(shadowNdc.xy < -1.0) ||
        any(shadowNdc.xy > 1.0) ||
        shadowNdc.z < 0.0 ||
        shadowNdc.z > 1.0)
        return 1.0;
    float bias = max(
        0.0015 *
            (1.0 - saturate(dot(normal, lightDirection))),
        0.00025);
    float staticVisibility = SamplePunctualShadowLayer(
        face,
        face.staticUvScaleBias,
        (uint)face.textureIndicesAndFlags.x,
        shadowNdc,
        bias,
        face.textureIndicesAndFlags.z >= 0.5);
    float dynamicVisibility = SamplePunctualShadowLayer(
        face,
        face.dynamicUvScaleBias,
        (uint)face.textureIndicesAndFlags.y,
        shadowNdc,
        bias,
        face.textureIndicesAndFlags.w >= 0.5);
    return min(staticVisibility, dynamicVisibility);
}

float3 EvaluateLightContribution(uint lightIndex, LightData light, float3 worldPos, DisneyMaterial dmat, float3 N, float3 V, float3 T, float3 B)
{
    float lightType = light.direction.w;
    float3 L;
    float attenuation = 1.0;

    if (lightType == 0.0) {
        L = normalize(-light.direction.xyz);
    } else {
        float3 delta = light.position.xyz - worldPos;
        float dist = max(length(delta), 0.001);
        L = delta / dist;

        float range = max(light.position.w, 0.001);
        float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
        attenuation = (falloff * falloff) / (dist * dist + 1.0);

        if (lightType == 2.0) {
            float theta = dot(L, normalize(-light.direction.xyz));
            float epsilon = max(light.shapeParams.x - light.shapeParams.y, 0.001);
            float intensity = clamp((theta - light.shapeParams.y) / epsilon, 0.0, 1.0);
            attenuation *= intensity * intensity;
        }
    }

    float3 radiance = light.color.rgb * light.color.w * attenuation;
    float3 bsdf = EvaluateDisneyBSDF(dmat, N, V, L, T, B);
    float dotNL = clamp(dot(N, L), 0.001, 1.0);
    float visibility = 1.0;
    if (lightType == 0.0 && light.shapeParams.w >= 0.5 &&
        lightIndex == (uint)push.directionalShadowParams.z)
        visibility = EvaluateDirectionalShadow(worldPos, N, L);
    else if (lightType != 0.0 &&
        light.shapeParams.w >= 0.5 &&
        push.punctualShadowFaceCount > 0)
        visibility = EvaluatePunctualShadow(
            lightIndex,
            light,
            worldPos,
            N,
            L);
    return bsdf * radiance * dotNL * visibility;
}

[shader("fragment")]
float4 fragmentMain(
    VertexOutput input,
    float3 barycentrics : SV_Barycentrics) : SV_Target
{
    MaterialData mat = push.materials[input.materialIdx];
    
    float3 albedo = mat.baseColor.rgb;
    if (mat.albedoTexIndex != 0xFFFFFFFF) {
        float4 texColor = bindless.textures[mat.albedoTexIndex].Sample(tex_sampler, input.uv);
        albedo *= texColor.rgb;
    }
    
    float metallic = mat.metallic;
    float roughness = mat.roughness;
    float ao = 1.0;
    
    if (mat.rmaTexIndex != 0xFFFFFFFF) {
        float3 rma = bindless.textures[mat.rmaTexIndex].Sample(tex_sampler, input.uv).rgb;
        ao = rma.r;
        roughness *= rma.g;
        metallic *= rma.b;
    }

    // Blend Layer 2 (evaluated first under Layer 1)
    if (mat.layer2MaskType != 0) {
        float mask2 = 0.0;
        float scale2 = mat.layer2NoiseScale > 0.0 ? mat.layer2NoiseScale : 10.0;
        float tMin2 = mat.layer2NoiseThresholdMin;
        float tMax2 = mat.layer2NoiseThresholdMax > tMin2 ? mat.layer2NoiseThresholdMax : tMin2 + 0.01;
        float3 p2 = input.worldPos * scale2;
        if (mat.layer2MaskType == 1) { // 3D Perlin Noise
            mask2 = smoothstep(tMin2, tMax2, noise3D(p2));
        } else if (mat.layer2MaskType == 2) { // Voronoi / Cellular
            mask2 = smoothstep(tMin2, tMax2, voronoi3D(p2));
        } else if (mat.layer2MaskType == 3) { // FBM Noise
            mask2 = smoothstep(tMin2, tMax2, fbm3D(p2, 3));
        } else if (mat.layer2MaskType == 4) { // Turbulence Noise
            mask2 = smoothstep(tMin2, tMax2, turbulence3D(p2, 3));
        } else if (mat.layer2MaskType == 5) { // Cavity
            float3 dNdx = ddx(input.normal);
            float3 dNdy = ddy(input.normal);
            float curvature = length(dNdx) + length(dNdy);
            float cavityFromCurvature = smoothstep(0.02, 0.4, curvature);
            float cavityFromAO = 1.0 - ao;
            mask2 = clamp(cavityFromCurvature * 0.7 + cavityFromAO * 0.8, 0.0, 1.0);
        } else if (mat.layer2MaskType == 6) { // Height
            mask2 = smoothstep(-0.5, 0.5, input.worldPos.y);
        } else if (mat.layer2MaskType == 7 && mat.layer2MaskTexIndex != 0xFFFFFFFF) { // Texture Mask
            mask2 = bindless.textures[mat.layer2MaskTexIndex].Sample(tex_sampler, input.uv).r;
        }

        albedo = lerp(albedo, mat.layer2Color.rgb, mask2);
        metallic = lerp(metallic, mat.layer2Metallic, mask2);
        roughness = lerp(roughness, mat.layer2Roughness, mask2);
    }

    // Blend Layer 1 / Top Layer (evaluated over Layer 2 and Base Layer)
    if (mat.topMaskType != 0) {
        float mask1 = 0.0;
        float scale1 = mat.noiseScale > 0.0 ? mat.noiseScale : 10.0;
        float tMin1 = mat.noiseThresholdMin;
        float tMax1 = mat.noiseThresholdMax > tMin1 ? mat.noiseThresholdMax : tMin1 + 0.01;
        float3 p1 = input.worldPos * scale1;
        if (mat.topMaskType == 1) { // 3D Perlin Noise
            mask1 = smoothstep(tMin1, tMax1, noise3D(p1));
        } else if (mat.topMaskType == 2) { // Voronoi / Cellular
            mask1 = smoothstep(tMin1, tMax1, voronoi3D(p1));
        } else if (mat.topMaskType == 3) { // FBM Noise
            mask1 = smoothstep(tMin1, tMax1, fbm3D(p1, 3));
        } else if (mat.topMaskType == 4) { // Turbulence Noise
            mask1 = smoothstep(tMin1, tMax1, turbulence3D(p1, 3));
        } else if (mat.topMaskType == 5) { // Cavity
            float3 dNdx = ddx(input.normal);
            float3 dNdy = ddy(input.normal);
            float curvature = length(dNdx) + length(dNdy);
            float cavityFromCurvature = smoothstep(0.02, 0.4, curvature);
            float cavityFromAO = 1.0 - ao;
            mask1 = clamp(cavityFromCurvature * 0.7 + cavityFromAO * 0.8, 0.0, 1.0);
        } else if (mat.topMaskType == 6) { // Height
            mask1 = smoothstep(-0.5, 0.5, input.worldPos.y);
        } else if (mat.topMaskType == 7 && mat.topMaskTexIndex != 0xFFFFFFFF) { // Texture Mask
            mask1 = bindless.textures[mat.topMaskTexIndex].Sample(tex_sampler, input.uv).r;
        }

        albedo = lerp(albedo, mat.topColor.rgb, mask1);
        metallic = lerp(metallic, mat.topMetallic, mask1);
        roughness = lerp(roughness, mat.topRoughness, mask1);
    }


    float3 N = normalize(input.normal);
    float3 vertexNormal = N;

    
    // Build guarded tangent frame — runs before normal mapping so TBN is always valid
    float3 T = normalize(input.tangent.xyz);
    if (!(dot(T, T) > 0.001)) {
        float3 up = abs(N.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        T = normalize(cross(up, N));
    }
    float3 B = cross(N, T) * input.tangent.w;
    if (!(dot(B, B) > 0.001)) {
        B = cross(N, T);
    }
    B = normalize(B);
    T = normalize(cross(B, N));
    
    if (mat.normalTexIndex != 0xFFFFFFFF) {
        float3x3 TBN = float3x3(T, B, N);
        
        float3 normalMap = bindless.textures[mat.normalTexIndex].Sample(tex_sampler, input.uv).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        N = normalize(mul(normalMap, TBN));
        
        // Rebuild B/T from new N for BSDF use
        B = cross(N, T) * input.tangent.w;
        if (!(dot(B, B) > 0.001)) B = cross(N, T);
        B = normalize(B);
        T = normalize(cross(B, N));
    }
    
    float3 emissive = mat.emissiveColor.rgb;
    if (mat.emissiveTexIndex != 0xFFFFFFFF) {
        float3 emTex = bindless.textures[mat.emissiveTexIndex].Sample(tex_sampler, input.uv).rgb;
        emissive *= emTex;
    }

    uint debugView = push.debugFlags & 0xFFu;
    if (debugView == 1u) {
        float edgeDistance = min(
            barycentrics.x,
            min(barycentrics.y, barycentrics.z));
        float edgeWidth = max(fwidth(edgeDistance), 0.0001);
        float edge = 1.0 - smoothstep(
            edgeWidth,
            edgeWidth * 1.75,
            edgeDistance);
        return float4(edge.xxx, 1.0);
    }
    if (debugView == 2u) {
        float depth = saturate(input.position.z);
        float3 nearColor = float3(0.02, 0.08, 0.24);
        float3 midColor = float3(0.05, 0.85, 0.72);
        float3 farColor = float3(1.0, 0.35, 0.08);
        float3 depthColor = depth < 0.5
            ? lerp(nearColor, midColor, depth * 2.0)
            : lerp(midColor, farColor, depth * 2.0 - 1.0);
        return float4(depthColor, 1.0);
    }
    if (debugView == 3u)
        return float4(vertexNormal * 0.5 + 0.5, 1.0);
    if (debugView == 4u)
        return float4(N * 0.5 + 0.5, 1.0);
    if (debugView == 5u)
        return float4(albedo, 1.0);
    if (debugView == 6u)
        return float4(ao, roughness, metallic, 1.0);
    if (debugView == 8u)
        return float4(frac(abs(input.worldPos) * 0.1), 1.0);
    if (debugView == 9u)
        return float4(emissive, 1.0);
    if (debugView == 10u)
        return float4(frac(input.uv), 0.0, 1.0);
    if (debugView == 11u)
        return float4(T * 0.5 + 0.5, 1.0);
    if (debugView == 12u)
        return float4(B * 0.5 + 0.5, 1.0);

    float3 perspectiveV = normalize(
        push.camera->cameraPosition.xyz - input.worldPos);
    float3 orthographicV = normalize(
        -push.camera->cameraForward.xyz);
    float3 V = normalize(lerp(
        perspectiveV,
        orthographicV,
        saturate(push.camera->cameraForward.w)));
    
    // Construct DisneyMaterial
    DisneyMaterial dmat;
    dmat.baseColor = albedo;
    dmat.metallic = metallic;
    dmat.subsurface = 0.0;
    dmat.specular = 0.5; // Fixed default specular (re-mapped to 0.04 internally)
    dmat.roughness = roughness;
    dmat.specularTint = 0.0;
    dmat.anisotropic = 0.0;
    dmat.sheen = 0.0;
    dmat.sheenTint = 0.5;
    dmat.clearcoat = 0.0;
    dmat.clearcoatGloss = 1.0;
    
    float3 Lo = float3(0.0);

    bool useClusters = push.clusterGrid.w > 0.0 && push.clusterParams.y > 0.0;
    if (useClusters) {
        uint gridX = (uint)push.clusterGrid.x;
        uint gridY = (uint)push.clusterGrid.y;
        uint gridZ = (uint)push.clusterGrid.z;
        float tileSize = push.clusterParams.x;
        uint tileX = min((uint)(input.position.x / tileSize), gridX - 1);
        uint tileY = min((uint)(input.position.y / tileSize), gridY - 1);
        uint tileZ = min((uint)(input.position.z * (float)gridZ), gridZ - 1);
        uint clusterIndex = tileZ * gridX * gridY + tileY * gridX + tileX;
        ClusterRecord record = push.clusterRecords[clusterIndex];

        for (uint i = 0; i < record.count; i++) {
            uint lightIndex = push.clusterLightIndices[record.offset + i];
            Lo += EvaluateLightContribution(lightIndex, push.lights[lightIndex], input.worldPos, dmat, N, V, T, B);
        }
    } else {
        for (uint i = 0; i < push.lightCount; i++) {
            Lo += EvaluateLightContribution(i, push.lights[i], input.worldPos, dmat, N, V, T, B);
        }
    }
    
    float3 ambient = float3(0.03, 0.03, 0.03) * albedo * ao;
    
    float3 color = ambient + Lo + emissive;
    if (debugView == 7u) {
        color =
            Lo / max(albedo, float3(0.04)) +
            float3(0.03) * ao +
            emissive;
    }
    
    // Apply exposure
    color *= push.camera->cameraPosition.w;
    
    // HDR tonemapping (ACES approximation)
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    color = clamp((color*(a*color+b))/(color*(c*color+d)+e), 0.0, 1.0);
    
    // Gamma correction
    color = pow(color, float3(1.0/2.2, 1.0/2.2, 1.0/2.2));
    
    return float4(color, 1.0);
}
