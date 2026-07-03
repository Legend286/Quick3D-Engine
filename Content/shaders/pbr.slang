// pbr.slang

struct CameraData {
    float4x4 viewProj;
    float4 cameraPosition; // w = exposure
};

struct Vertex {
    float3 position;
    float3 normal;
    float2 uv;
    float4 tangent;
};

struct PartData {
    float4 aabbMin;
    float4 aabbMax;
    Vertex* vertices;
    uint* indices;
    uint indexCount;
    uint materialIdx;
    uint instanceIdx;
};

struct InstanceData {
    float4x4 modelMatrix;
    float4 aabbMin;
    float4 aabbMax;
    uint partCount;
    uint firstPartIndex;
};

struct MaterialData {
    float4 baseColor;
    float4 emissiveColor;
    float metallic;
    float roughness;
    uint albedoTexIndex;
    uint normalTexIndex;
    uint rmaTexIndex;
    uint emissiveTexIndex;
    uint _pad0;
    uint _pad1;
};

struct LightData {
    float4 position;   // w = range
    float4 direction;  // w = type (0=Dir, 1=Point, 2=Spot)
    float4 color;      // w = intensity
    float4 spotParams; // x = innerCone, y = outerCone
};

struct PbrPushData {
    PartData* parts;
    InstanceData* instances;
    MaterialData* materials;
    CameraData* camera;
    LightData* lights;
    uint lightCount;
    uint _pad0;
};
[push_constant]
ConstantBuffer<PbrPushData> push;

struct BindlessHeap {
    Texture2D textures[4096];
};
[binding(2, 0)] ParameterBlock<BindlessHeap> bindless;
[binding(1, 0)] SamplerState tex_sampler;

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
    
    float4 worldPos = mul(inst.modelMatrix, float4(v.position, 1.0));
    output.worldPos = worldPos.xyz;
    output.position = mul(push.camera->viewProj, worldPos);
    output.normal = normalize(mul((float3x3)inst.modelMatrix, v.normal));
    output.tangent = float4(normalize(mul((float3x3)inst.modelMatrix, v.tangent.xyz)), v.tangent.w);
    output.uv = v.uv;
    output.materialIdx = part.materialIdx;
    
    return output;
}

// Constants
static const float PI = 3.14159265359;

// GGX/Towbridge-Reitz normal distribution
float D_GGX(float dotNH, float roughness) {
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float denom = dotNH * dotNH * (alpha2 - 1.0) + 1.0;
    return alpha2 / (PI * denom * denom);
}

// Schlick-Beckmann geometry shadowing
float G_SchlicksmithGGX(float dotNL, float dotNV, float roughness) {
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    float GL = dotNL / (dotNL * (1.0 - k) + k);
    float GV = dotNV / (dotNV * (1.0 - k) + k);
    return GL * GV;
}

// Fresnel Schlick
float3 F_Schlick(float cosTheta, float3 F0) {
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
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
        metallic *= rma.b;
    }

    float3 N = normalize(input.normal);
    
    if (mat.normalTexIndex != 0xFFFFFFFF) {
        float3 T = normalize(input.tangent.xyz);
        float3 B = cross(N, T) * input.tangent.w;
        float3x3 TBN = float3x3(T, B, N);
        
        float3 normalMap = bindless.textures[mat.normalTexIndex].Sample(tex_sampler, input.uv).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        N = normalize(mul(normalMap, TBN));
    }
    
    float3 V = normalize(push.camera->cameraPosition.xyz - input.worldPos);

    float dotNV = clamp(dot(N, V), 0.001, 1.0);

    float3 F0 = float3(0.04, 0.04, 0.04); 
    F0 = lerp(F0, albedo, metallic);

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
        
        float3 H = normalize(V + L);
        float dotNL = clamp(dot(N, L), 0.001, 1.0);
        float dotNH = clamp(dot(N, H), 0.0, 1.0);

        // Cook-Torrance BRDF
        float NDF = D_GGX(dotNH, roughness);   
        float G   = G_SchlicksmithGGX(dotNL, dotNV, roughness);      
        float3 F  = F_Schlick(max(dot(H, V), 0.0), F0);       
        
        float3 numerator    = NDF * G * F;
        float denominator   = 4.0 * dotNV * dotNL + 0.001;
        float3 specular     = numerator / denominator;
        
        float3 kS = F;
        float3 kD = float3(1.0, 1.0, 1.0) - kS;
        kD *= 1.0 - metallic;	  

        float3 radiance = light.color.rgb * light.color.w * attenuation;
        Lo += (kD * albedo / PI + specular) * radiance * dotNL;
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
