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
    
    float3 corners[8] = {
        float3(inst.aabbMin.x, inst.aabbMin.y, inst.aabbMin.z),
        float3(inst.aabbMax.x, inst.aabbMin.y, inst.aabbMin.z),
        float3(inst.aabbMin.x, inst.aabbMax.y, inst.aabbMin.z),
        float3(inst.aabbMax.x, inst.aabbMax.y, inst.aabbMin.z),
        float3(inst.aabbMin.x, inst.aabbMin.y, inst.aabbMax.z),
        float3(inst.aabbMax.x, inst.aabbMin.y, inst.aabbMax.z),
        float3(inst.aabbMin.x, inst.aabbMax.y, inst.aabbMax.z),
        float3(inst.aabbMax.x, inst.aabbMax.y, inst.aabbMax.z)
    };
    
    float3 worldMin = float3(1000000.0, 1000000.0, 1000000.0);
    float3 worldMax = float3(-1000000.0, -1000000.0, -1000000.0);
    
    for (int c = 0; c < 8; ++c) {
        float4 wc = mul(inst.modelMatrix, float4(corners[c], 1.0));
        worldMin = min(worldMin, wc.xyz);
        worldMax = max(worldMax, wc.xyz);
    }

    int instStatus = TestAABB(worldMin, worldMax, push.frustumPlanes);
    if (instStatus > 0) {
        for (uint p = 0; p < inst.partCount; ++p) {
            uint partIdx = inst.firstPartIndex + p;
            PartData part = push.parts[partIdx];
            
            bool isVisible = true;
            
            if (instStatus == 1) {
                // Intersecting, we must test the part's AABB
                float3 pcorners[8] = {
                    float3(part.aabbMin.x, part.aabbMin.y, part.aabbMin.z),
                    float3(part.aabbMax.x, part.aabbMin.y, part.aabbMin.z),
                    float3(part.aabbMin.x, part.aabbMax.y, part.aabbMin.z),
                    float3(part.aabbMax.x, part.aabbMax.y, part.aabbMin.z),
                    float3(part.aabbMin.x, part.aabbMin.y, part.aabbMax.z),
                    float3(part.aabbMax.x, part.aabbMin.y, part.aabbMax.z),
                    float3(part.aabbMin.x, part.aabbMax.y, part.aabbMax.z),
                    float3(part.aabbMax.x, part.aabbMax.y, part.aabbMax.z)
                };
                
                float3 pworldMin = float3(1000000.0, 1000000.0, 1000000.0);
                float3 pworldMax = float3(-1000000.0, -1000000.0, -1000000.0);
                
                for (int pc = 0; pc < 8; ++pc) {
                    float4 pwc = mul(inst.modelMatrix, float4(pcorners[pc], 1.0));
                    pworldMin = min(pworldMin, pwc.xyz);
                    pworldMax = max(pworldMax, pwc.xyz);
                }
                
                isVisible = TestAABB(pworldMin, pworldMax, push.frustumPlanes) > 0;
            }
            
            if (isVisible) {
                uint cmdIdx = 0;
                
                DrawIndirectCommand cmd;
                cmd.vertexCount = part.indexCount;
                cmd.instanceCount = 1;
                cmd.vertexStart = 0;
                cmd.baseInstance = partIdx; 
                
                push.drawCmds[partIdx] = cmd;
            } else {
                DrawIndirectCommand cmd;
                cmd.vertexCount = 0;
                cmd.instanceCount = 0;
                cmd.vertexStart = 0;
                cmd.baseInstance = 0;
                
                push.drawCmds[partIdx] = cmd;
            }
        }
    }
}
