#include "scene_data.slang"

struct DrawIndirectCommand {
    uint vertexCount;
    uint instanceCount;
    uint vertexStart;
    uint baseInstance;
};

struct ShadowCullPushData {
    InstanceData* instances;
    PartData* parts;
    DrawIndirectCommand* drawCommands;
    uint* reserved;
    float4x4 cascadeViewProjection;
    float4 cascadeCullRegion;
    uint partCount;
    uint requiredInstanceFlags;
    uint rejectedInstanceFlags;
    uint pad0;
};

[push_constant]
ConstantBuffer<ShadowCullPushData> push;

bool IntersectsCascade(PartData part, InstanceData instance)
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
    float maximumHorizontalDistance = 0.0;
    for (uint cornerIndex = 0; cornerIndex < 8; ++cornerIndex) {
        float4 worldPosition = mul(
            instance.modelMatrix,
            float4(corners[cornerIndex], 1.0));
        maximumHorizontalDistance = max(
            maximumHorizontalDistance,
            length(
                worldPosition.xz -
                push.cascadeCullRegion.xy));
        float4 clipPosition = mul(
            push.cascadeViewProjection,
            worldPosition);
        outsideLeft = outsideLeft && clipPosition.x < -clipPosition.w;
        outsideRight = outsideRight && clipPosition.x > clipPosition.w;
        outsideBottom = outsideBottom && clipPosition.y < -clipPosition.w;
        outsideTop = outsideTop && clipPosition.y > clipPosition.w;
        outsideNear = outsideNear && clipPosition.z < 0.0;
        outsideFar = outsideFar && clipPosition.z > clipPosition.w;
    }

    bool overlapsClip = !(outsideLeft || outsideRight || outsideBottom ||
        outsideTop || outsideNear || outsideFar);
    float requiredInnerDistance = max(
        push.cascadeCullRegion.z - push.cascadeCullRegion.w,
        0.0);
    bool canReachSampledBand =
        push.cascadeCullRegion.z <= 0.0 ||
        maximumHorizontalDistance >= requiredInnerDistance;
    return overlapsClip && canReachSampledBand;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint partIndex = threadId.x;
    if (partIndex >= push.partCount)
        return;

    PartData part = push.parts[partIndex];
    InstanceData instance = push.instances[part.instanceIdx];
    bool flagsAccepted =
        (instance.flags & push.requiredInstanceFlags) ==
            push.requiredInstanceFlags &&
        (instance.flags & push.rejectedInstanceFlags) == 0;
    bool visible = flagsAccepted && IntersectsCascade(part, instance);

    DrawIndirectCommand command;
    command.vertexCount = visible ? part.indexCount : 0;
    command.instanceCount = visible ? 1 : 0;
    command.vertexStart = 0;
    command.baseInstance = partIndex;
    push.drawCommands[partIndex] = command;
}
