// scene_data.slang
// Shared data structures for PBR and Path Tracing

struct CameraData {
    float4x4 viewProj;
    float4x4 invViewProj;
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
    uint pad0;
};

struct InstanceData {
    float4x4 modelMatrix;
    float4 aabbMin;
    float4 aabbMax;
    uint partCount;
    uint firstPartIndex;
    uint pad1;
    uint pad2;
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

struct SkyParams {
    float3 sunDirection;
    float sunAngularRadius;   // radians, ~0.00465 for real sun
    float sunIntensity;
    float turbidity;          // 2-6 typical, 2=clear, 6=hazy
    float groundAlbedo;       // 0-1, typical 0.1
    float3 pad_sky;
};

struct ScenePushData {
    PartData* parts;
    InstanceData* instances;
    MaterialData* materials;
    CameraData* camera;
    LightData* lights;
    uint lightCount;
    uint frameCount;
    float2 resolution;
    uint debugFlags;
    uint hasGeometry;
    SkyParams sky;
};
