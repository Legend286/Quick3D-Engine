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
    
    float3 vPos = float3(v.px, v.py, v.pz);
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

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target 
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
    }

    if (mat.topMaskType != 0) {
        float mask = 0.0;
        float3 p = input.worldPos * 10.0;
        if (mat.topMaskType == 1) { // 3D Perlin Noise
            mask = smoothstep(0.3, 0.7, noise3D(p));
        } else if (mat.topMaskType == 2) { // Voronoi / Cellular
            mask = smoothstep(0.1, 0.8, voronoi3D(p));
        } else if (mat.topMaskType == 3) { // FBM Noise
            mask = smoothstep(0.2, 0.8, fbm3D(p, 3));
        } else if (mat.topMaskType == 4) { // Turbulence Noise
            mask = smoothstep(0.2, 0.8, turbulence3D(p, 3));
        } else if (mat.topMaskType == 5) { // Cavity
            float3 dNdx = ddx(input.normal);
            float3 dNdy = ddy(input.normal);
            float curvature = length(dNdx) + length(dNdy);

            float cavityFromCurvature = smoothstep(0.02, 0.4, curvature);
            float cavityFromAO = 1.0 - ao;
            mask = clamp(cavityFromCurvature * 0.7 + cavityFromAO * 0.8, 0.0, 1.0);
        } else if (mat.topMaskType == 6) { // Height
            mask = smoothstep(-0.5, 0.5, input.worldPos.y);
        } else if (mat.topMaskType == 7 && mat.topMaskTexIndex != 0xFFFFFFFF) { // Texture Mask
            mask = bindless.textures[mat.topMaskTexIndex].Sample(tex_sampler, input.uv).r;
        }

        albedo = lerp(albedo, mat.topColor.rgb, mask);
        metallic = lerp(metallic, mat.topMetallic, mask);
        roughness = lerp(roughness, mat.topRoughness, mask);
    }


    float3 N = normalize(input.normal);

    
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
    
    float3 V = normalize(push.camera->cameraPosition.xyz - input.worldPos);
    
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

    for (uint i = 0; i < push.lightCount; i++) {
        LightData light = push.lights[i];
        float lightType = light.direction.w;
        
        float3 L;
        float attenuation = 1.0;
        
        if (lightType == 0.0) { // Directional
            L = normalize(light.direction.xyz);
        } else { // Point or Spot
            float3 delta = light.position.xyz - input.worldPos;
            float dist = length(delta);
            L = delta / dist;
            
            // Attenuation
            float range = light.position.w;
            float falloff = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
            attenuation = (falloff * falloff) / (dist * dist + 1.0);
            
            if (lightType == 2.0) { // Spot
                float theta = dot(L, normalize(-light.direction.xyz));
                float epsilon = light.spotParams.x - light.spotParams.y;
                float intensity = clamp((theta - light.spotParams.y) / epsilon, 0.0, 1.0);
                attenuation *= intensity * intensity;
            }
        }
        
        float3 radiance = light.color.rgb * light.color.w * attenuation;
        
        float3 bsdf = EvaluateDisneyBSDF(dmat, N, V, L, T, B);
        
        float dotNL = clamp(dot(N, L), 0.001, 1.0);
        Lo += bsdf * radiance * dotNL;
    }
    
    float3 ambient = float3(0.03, 0.03, 0.03) * albedo * ao;
    
    float3 emissive = mat.emissiveColor.rgb;
    if (mat.emissiveTexIndex != 0xFFFFFFFF) {
        float3 emTex = bindless.textures[mat.emissiveTexIndex].Sample(tex_sampler, input.uv).rgb;
        emissive *= emTex;
    }
    
    float3 color = ambient + Lo + emissive;
    
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
