#include "scene_data.slang"

struct DrawIndirectCommand {
    uint vertexCount;
    uint instanceCount;
    uint vertexStart;
    uint baseInstance;
};

struct PunctualShadowCullJob {
    float4x4 viewProjection;
    uint requiredInstanceFlags;
    uint rejectedInstanceFlags;
    uint pad0;
    uint pad1;
};

struct PunctualShadowCullPushData {
    InstanceData* instances;
    PartData* parts;
    DrawIndirectCommand* drawCommands;
    PunctualShadowCullJob* jobs;
    uint partCount;
    uint jobCount;
    uint pad0;
    uint pad1;
};

[push_constant]
ConstantBuffer<PunctualShadowCullPushData> push;

bool IntersectsViewProjection(
    PartData part,
    InstanceData instance,
    float4x4 viewProjection)
{
    float3 localMin = part.aabbMin.xyz;
    float3 localMax = part.aabbMax.xyz;
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

    bool outsideLeft = true;
    bool outsideRight = true;
    bool outsideBottom = true;
    bool outsideTop = true;
    bool outsideNear = true;
    bool outsideFar = true;
    for (uint cornerIndex = 0; cornerIndex < 8; ++cornerIndex) {
        float4 worldPosition = mul(
            instance.modelMatrix,
            float4(corners[cornerIndex], 1.0));
        float4 clipPosition = mul(viewProjection, worldPosition);
        outsideLeft = outsideLeft && clipPosition.x < -clipPosition.w;
        outsideRight = outsideRight && clipPosition.x > clipPosition.w;
        outsideBottom = outsideBottom && clipPosition.y < -clipPosition.w;
        outsideTop = outsideTop && clipPosition.y > clipPosition.w;
        outsideNear = outsideNear && clipPosition.z < 0.0;
        outsideFar = outsideFar && clipPosition.z > clipPosition.w;
    }

    return !(outsideLeft || outsideRight || outsideBottom ||
        outsideTop || outsideNear || outsideFar);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint partIndex = threadId.x;
    uint jobIndex = threadId.y;
    if (partIndex >= push.partCount ||
        jobIndex >= push.jobCount)
        return;

    PunctualShadowCullJob job = push.jobs[jobIndex];
    PartData part = push.parts[partIndex];
    InstanceData instance = push.instances[part.instanceIdx];
    bool flagsAccepted =
        (instance.flags & job.requiredInstanceFlags) ==
            job.requiredInstanceFlags &&
        (instance.flags & job.rejectedInstanceFlags) == 0;
    bool visible = flagsAccepted && IntersectsViewProjection(
        part,
        instance,
        job.viewProjection);

    uint commandIndex = jobIndex * push.partCount + partIndex;
    DrawIndirectCommand command;
    command.vertexCount = visible ? part.indexCount : 0;
    command.instanceCount = visible ? 1 : 0;
    command.vertexStart = 0;
    command.baseInstance = partIndex;
    push.drawCommands[commandIndex] = command;
}
