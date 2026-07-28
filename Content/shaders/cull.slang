#include "scene_data.slang"

struct DrawIndirectCommand {
    uint vertexCount;
    uint instanceCount;
    uint vertexStart;
    uint baseInstance;
};

struct CullPushData {
    InstanceData* instances;
    PartData* parts;
    DrawIndirectCommand* drawCmds;
    uint* drawCount;
    float4 frustumPlanes[6];
    uint instanceCount;
};

[push_constant]
ConstantBuffer<CullPushData> push;

int TestAABB(float3 bmin, float3 bmax, float4 planes[6]) {
    int result = 2;
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
            return 0;
        }
        
        if (dot(plane.xyz, n) + plane.w < 0.0) {
            result = 1;
        }
    }
    return result;
}

void TransformAABB(float3 localMin, float3 localMax, float4x4 modelMatrix, out float3 worldMin, out float3 worldMax)
{
    float3 corners[8] = {
        float3(localMin.x, localMin.y, localMin.z),
        float3(localMax.x, localMin.y, localMin.z),
        float3(localMin.x, localMax.y, localMin.z),
        float3(localMax.x, localMax.y, localMin.z),
        float3(localMin.x, localMin.y, localMax.z),
        float3(localMax.x, localMin.y, localMax.z),
        float3(localMin.x, localMax.y, localMax.z),
        float3(localMax.x, localMax.y, localMax.z)
    };

    float3 first = mul(modelMatrix, float4(corners[0], 1.0)).xyz;
    worldMin = first;
    worldMax = first;

    for (int i = 1; i < 8; ++i) {
        float3 p = mul(modelMatrix, float4(corners[i], 1.0)).xyz;
        worldMin = min(worldMin, p);
        worldMax = max(worldMax, p);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint instIdx = threadId.x;
    if (instIdx >= push.instanceCount) return;

    InstanceData inst = push.instances[instIdx];
    float3 instWorldMin;
    float3 instWorldMax;
    TransformAABB(inst.aabbMin.xyz, inst.aabbMax.xyz, inst.modelMatrix, instWorldMin, instWorldMax);
    bool visible = TestAABB(instWorldMin, instWorldMax, push.frustumPlanes) != 0;
    
    for (uint p = 0; p < inst.partCount; ++p) {
        uint partIdx = inst.firstPartIndex + p;
        PartData part = push.parts[partIdx];
        
        DrawIndirectCommand cmd;
        cmd.vertexCount = visible ? part.indexCount : 0;
        cmd.instanceCount = visible ? 1 : 0;
        cmd.vertexStart = 0;
        cmd.baseInstance = partIdx; 
        
        push.drawCmds[partIdx] = cmd;
    }
}
