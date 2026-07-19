// pbr_sky.slang
// Renders the Nishita Sky model into the background of the PBR pass.

#include "scene_data.slang"
#include "nishita_sky.slang"

struct VertexOutput {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vertexMain(uint vid : SV_VertexID)
{
    VertexOutput output;
    
    // Draw a single fullscreen triangle (CCW winding)
    float x = (vid == 1) ? 3.0 : -1.0;
    float y = (vid == 2) ? 3.0 : -1.0;
    
    output.position = float4(x, y, 0.99999, 1.0);
    
    output.uv = float2((x + 1.0) * 0.5, 1.0 - (y + 1.0) * 0.5);
    
    return output;
}

[push_constant]
ConstantBuffer<ScenePushData> push;

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    // Reconstruct view direction from UV
    // NDC coords
    float x = input.uv.x * 2.0 - 1.0;
    float y = (1.0 - input.uv.y) * 2.0 - 1.0; // Invert Y back
    
    // Unproject to world space
    float4 target = mul(push.camera->invViewProj, float4(x, y, 1.0, 1.0));
    float3 worldPos = target.xyz / target.w;
    float3 viewDir = normalize(worldPos - push.camera->cameraPosition.xyz);
    
    float3 sunDir = push.sky.sunDirAndRadius.xyz;
    float3 skyRadiance = GetSkyRadiance(viewDir, sunDir, push.sky);
    
    // Match PbrPass.cs exposure and tonemapping
    skyRadiance *= push.camera->cameraPosition.w;
    
    // HDR tonemapping (ACES approximation)
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    skyRadiance = clamp((skyRadiance*(a*skyRadiance+b))/(skyRadiance*(c*skyRadiance+d)+e), 0.0, 1.0);
    
    // Gamma correction
    skyRadiance = pow(skyRadiance, float3(1.0/2.2, 1.0/2.2, 1.0/2.2));
    
    return float4(skyRadiance, 1.0f);
}
