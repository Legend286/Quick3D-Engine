// cull.slang
// Compute shader for frustum culling and generating indirect draw commands.

struct DrawIndirectCommand {
    uint vertexCount;
    uint instanceCount;
    uint vertexStart;
    uint baseInstance;
};

struct PartData {
    float4 aabbMin;
    float4 aabbMax;
    uint64_t vertices;
    uint64_t indices;
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

struct CullPushData {
    InstanceData* instances;
    PartData* parts;
    DrawIndirectCommand* drawCmds;
    uint* drawCount;
    float4 frustumPlanes[6]; // Left, Right, Bottom, Top, Near, Far (normals point inwards)
    uint instanceCount;
};

[push_constant]
ConstantBuffer<CullPushData> push;

// Returns 0 if outside, 1 if intersecting, 2 if fully inside
int TestAABB(float3 bmin, float3 bmax, float4 planes[6]) {
    int result = 2; // Assume inside
    for (int i = 0; i < 6; ++i) {
        float4 plane = planes[i];
        
        float3 p = float3(
            plane.x > 0.0 ? bmax.x : bmin.x,
            plane.y > 0.0 ? bmax.y : bmin.y,
            plane.z > 0.0 ? bmax.z : bmin.z
        );
        
        float3 n = float3(
            plane.x > 0.0 ? bmin.x : bmax.x,
            plane.y > 0.0 ? bmin.y : bmax.y,
            plane.z > 0.0 ? bmin.z : bmax.z
        );
        
        if (dot(plane.xyz, p) + plane.w < 0.0) {
            return 0; // Outside
        }
        
        if (dot(plane.xyz, n) + plane.w < 0.0) {
            result = 1; // Intersecting
        }
    }
    return result;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint instIdx = threadId.x;
    if (instIdx >= push.instanceCount) return;

    InstanceData inst = push.instances[instIdx];
    
    for (uint p = 0; p < inst.partCount; ++p) {
        uint partIdx = inst.firstPartIndex + p;
        PartData part = push.parts[partIdx];
        
        DrawIndirectCommand cmd;
        cmd.vertexCount = part.indexCount;
        cmd.instanceCount = 1;
        cmd.vertexStart = 0;
        cmd.baseInstance = partIdx; 
        
        push.drawCmds[partIdx] = cmd;
    }
}
