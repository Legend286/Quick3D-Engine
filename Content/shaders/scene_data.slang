// scene_data.slang
// Shared data structures for PBR and Path Tracing

struct CameraData {
    float4x4 viewProj;
    float4x4 invViewProj;
    float4 cameraPosition; // w = exposure
    float4 cameraForward;
};

struct Vertex {
    float px, py, pz;
    float nx, ny, nz;
    float u, v;
    float tx, ty, tz, tw;
};

struct PartData {
    float4 aabbMin;
    float4 aabbMax;
    Vertex* vertices;
    uint* indices;
    uint indexCount;
    uint materialIdx;
    uint instanceIdx;
    uint flags;
};

struct InstanceData {
    float4x4 modelMatrix;
    float4 aabbMin;
    float4 aabbMax;
    uint partCount;
    uint firstPartIndex;
    uint entityIdLow;
    uint entityIdHigh;
};

struct MaterialData {
    // Base Layer
    float4 baseColor;
    float metallic;
    float roughness;
    uint albedoTexIndex;
    uint normalTexIndex;

    // Top Layer
    float4 topColor;
    float topMetallic;
    float topRoughness;
    uint topMaskType;
    uint topMaskTexIndex;


    // Common/Other
    float4 emissiveColor;
    uint emissiveTexIndex;
    uint rmaTexIndex;
    float subsurface;
    float clearcoat;
    
    float4 subsurfaceRadius;
    float4 subsurfaceColor;
    float clearcoatRoughness;
    uint _pad1;
    uint _pad2;
    uint _pad3;
};

struct LightData {
    float4 position;   // w = range
    float4 direction;  // w = type (0=Dir, 1=Point, 2=Spot)
    float4 color;      // w = intensity
    float4 spotParams; // x = innerCone, y = outerCone
};

struct SkyParams {
    float4 sunDirAndRadius;
    float4 intensityTurbidityAlbedoPad;
};

struct ScenePushData {
    PartData* parts;
    InstanceData* instances;
    MaterialData* materials;
    CameraData* camera;
    LightData* lights;
    uint lightCount;
    uint frameCount;
    float4 resolution;
    uint debugFlags;
    uint hasGeometry;
    SkyParams sky;
};
