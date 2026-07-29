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
    uint flags;
    uint pad0;
    uint pad1;
    uint pad2;
};

struct MaterialData {
    // Base Layer
    float4 baseColor;
    float metallic;
    float roughness;
    uint albedoTexIndex;
    uint normalTexIndex;

    // Layer 1 / Top Layer
    float4 topColor;
    float topMetallic;
    float topRoughness;
    uint topMaskType;
    uint topMaskTexIndex;

    // Layer 2 / Secondary Layer
    float4 layer2Color;
    float layer2Metallic;
    float layer2Roughness;
    uint layer2MaskType;
    uint layer2MaskTexIndex;

    // Common/Other
    float4 emissiveColor;
    uint emissiveTexIndex;
    uint rmaTexIndex;
    float subsurface;
    float clearcoat;
    
    float4 subsurfaceRadius;
    float4 subsurfaceColor;
    float clearcoatRoughness;
    float noiseScale;
    float noiseThresholdMin;
    float noiseThresholdMax;

    float layer2NoiseScale;
    float layer2NoiseThresholdMin;
    float layer2NoiseThresholdMax;
    uint _pad0;
};

struct LightData {
    float4 position;   // w = range
    float4 direction;  // w = type (0=Dir, 1=Point, 2=Spot)
    float4 color;      // w = intensity
    float4 shapeParams; // x = innerCone, y = outerCone, z = sourceRadius, w = flags
};

struct SkyParams {
    float4 sunDirAndRadius;
    float4 intensityTurbidityAlbedoPad;
};

struct ClusterRecord {
    uint offset;
    uint count;
};

struct PunctualShadowFaceData {
    float4x4 viewProjection;
    float4 staticUvScaleBias;
    float4 dynamicUvScaleBias;
    float4 textureIndicesAndFlags;
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
    ClusterRecord* clusterRecords;
    uint* clusterLightIndices;
    float4 clusterGrid;
    float4 clusterParams;
    float4x4 directionalShadowViewProj;
    float4 directionalShadowParams;
    float4x4 directionalShadowViewProj1;
    float4x4 directionalShadowViewProj2;
    float4x4 directionalShadowViewProj3;
    float4 directionalShadowSplits;
    float4 directionalShadowTextureIndices;
    PunctualShadowFaceData* punctualShadowFaces;
    uint punctualShadowFaceCount;
    uint punctualShadowPad;
};
