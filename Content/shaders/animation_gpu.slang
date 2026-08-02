// SPDX-License-Identifier: MIT
#include "scene_data.slang"

struct LocalTransformGpu
{
    float4 rotation;
    float4 translation;
    float4 scale;
};

struct SkeletonAssetGpu
{
    uint boneOffset;
    uint boneCount;
    uint hierarchyLevelOffset;
    uint hierarchyLevelCount;
    uint inverseBindOffset;
    uint referencePoseOffset;
    uint rootBoneIndex;
    uint flags;
};

struct BoneMetadataGpu
{
    int parentIndex;
    uint hierarchyDepth;
    uint nameHash;
    uint flags;
};

struct HierarchyLevelGpu
{
    uint boneIndexOffset;
    uint boneCount;
};

struct AnimationClipGpu
{
    uint sampleOffset;
    uint frameCount;
    uint boneCount;
    uint sampleRate;
    float duration;
    uint rootMotionOffset;
    uint eventOffset;
    uint eventCount;
    uint flags;
    uint skeletonId;
};

struct GpuAnimatorState
{
    uint skeletonId;
    uint entityId;
    uint baseClipId;
    uint targetClipId;
    float baseTime;
    float targetTime;
    float playbackRate;
    float transitionTime;
    float transitionDuration;
    float transitionWeight;
    uint layerOffset;
    uint layerCount;
    uint flags;
    uint generation;
    uint outputPoseOffset;
    uint outputMatrixOffset;
    uint currentSkinnedVertexOffset;
    uint previousSkinnedVertexOffset;
};

struct SkinSourceVertexGpu
{
    float3 position;
    float3 normal;
    float2 texcoord;
    float4 tangent;
    uint4 boneIndices;
    float4 boneWeights;
};

struct SkinWorkItemGpu
{
    SkinSourceVertexGpu* sourceVertices;
    Vertex* outputVertices;
    float4x4* skinMatrices;
    uint vertexCount;
    uint boneCount;
    float3 outputOffset;
    uint outputPad;
};

struct AnimationDispatchData
{
    SkeletonAssetGpu* skeletons;
    BoneMetadataGpu* bones;
    HierarchyLevelGpu* hierarchyLevels;
    uint* hierarchyBoneIndices;
    float4x4* inverseBindMatrices;
    LocalTransformGpu* referencePose;
    AnimationClipGpu* clips;
    LocalTransformGpu* samples;
    GpuAnimatorState* states;
    LocalTransformGpu* localPoses;
    float4x4* globalMatrices;
    float4x4* skinMatrices;
    SkinWorkItemGpu* skinWorkItems;
    uint skinWorkItemCount;
    uint skinPad;
    float deltaTime;
    uint stateCount;
    uint skeletonTableCount;
    uint clipTableCount;
    uint pad;
};

[push_constant]
ConstantBuffer<AnimationDispatchData> push;

float4 NormalizeQuaternion(float4 value)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1.0e-8
        ? value * rsqrt(lengthSquared)
        : float4(0.0, 0.0, 0.0, 1.0);
}

LocalTransformGpu IdentityTransform()
{
    LocalTransformGpu result;
    result.rotation = float4(0.0, 0.0, 0.0, 1.0);
    result.translation = float4(0.0, 0.0, 0.0, 0.0);
    result.scale = float4(1.0, 1.0, 1.0, 0.0);
    return result;
}

LocalTransformGpu InterpolateTransform(
    LocalTransformGpu first,
    LocalTransformGpu second,
    float alpha)
{
    float4 secondRotation = second.rotation;
    if (dot(first.rotation, secondRotation) < 0.0)
        secondRotation = -secondRotation;

    LocalTransformGpu result;
    result.rotation = NormalizeQuaternion(lerp(first.rotation, secondRotation, alpha));
    result.translation = float4(
        lerp(first.translation.xyz, second.translation.xyz, alpha),
        0.0);
    result.scale = float4(
        lerp(first.scale.xyz, second.scale.xyz, alpha),
        0.0);
    return result;
}

float4x4 ComposeTransform(LocalTransformGpu transform)
{
    float4 q = NormalizeQuaternion(transform.rotation);
    float x2 = q.x + q.x;
    float y2 = q.y + q.y;
    float z2 = q.z + q.z;
    float xx = q.x * x2;
    float xy = q.x * y2;
    float xz = q.x * z2;
    float yy = q.y * y2;
    float yz = q.y * z2;
    float zz = q.z * z2;
    float wx = q.w * x2;
    float wy = q.w * y2;
    float wz = q.w * z2;
    float3 scale = transform.scale.xyz;
    float3 translation = transform.translation.xyz;

    return float4x4(
        (1.0 - (yy + zz)) * scale.x,
        (xy - wz) * scale.y,
        (xz + wy) * scale.z,
        translation.x,
        (xy + wz) * scale.x,
        (1.0 - (xx + zz)) * scale.y,
        (yz - wx) * scale.z,
        translation.y,
        (xz - wy) * scale.x,
        (yz + wx) * scale.y,
        (1.0 - (xx + yy)) * scale.z,
        translation.z,
        0.0,
        0.0,
        0.0,
        1.0);
}

uint ResolveFrame(uint frame, uint frameCount, bool looping)
{
    if (frameCount == 0u)
        return 0u;
    return looping ? frame % frameCount : min(frame, frameCount - 1u);
}

LocalTransformGpu SampleClip(
    AnimationClipGpu clip,
    float time,
    uint boneIndex,
    bool looping,
    LocalTransformGpu fallback)
{
    if (clip.frameCount == 0u ||
        clip.boneCount == 0u ||
        boneIndex >= clip.boneCount)
        return fallback;

    float framePosition = max(time, 0.0) * float(clip.sampleRate);
    uint frame0 = ResolveFrame(uint(floor(framePosition)), clip.frameCount, looping);
    uint frame1 = ResolveFrame(frame0 + 1u, clip.frameCount, looping);
    float alpha = frac(framePosition);
    LocalTransformGpu first = push.samples[
        clip.sampleOffset + frame0 * clip.boneCount + boneIndex];
    LocalTransformGpu second = push.samples[
        clip.sampleOffset + frame1 * clip.boneCount + boneIndex];
    return InterpolateTransform(first, second, alpha);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void buildAnimationMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint animatorIndex = dispatchThreadId.x;
    if (animatorIndex >= push.stateCount)
        return;

    GpuAnimatorState state = push.states[animatorIndex];
    if ((state.flags & 1u) == 0u ||
        state.skeletonId >= push.skeletonTableCount ||
        state.baseClipId >= push.clipTableCount)
        return;

    SkeletonAssetGpu skeleton = push.skeletons[state.skeletonId];
    AnimationClipGpu clip = push.clips[state.baseClipId];
    bool looping = (state.flags & 2u) != 0u ||
        (clip.flags & 1u) != 0u;
    LocalTransformGpu* pose = push.localPoses + state.outputPoseOffset;
    float4x4* global = push.globalMatrices + state.outputMatrixOffset;
    float4x4* skin = push.skinMatrices + state.outputMatrixOffset;

    for (uint bone = 0u; bone < skeleton.boneCount; ++bone)
    {
        LocalTransformGpu reference = push.referencePose[
            skeleton.referencePoseOffset + bone];
        pose[bone] = SampleClip(
            clip,
            state.baseTime,
            bone,
            looping,
            reference);
    }

    for (uint level = 0u; level < skeleton.hierarchyLevelCount; ++level)
    {
        HierarchyLevelGpu levelInfo = push.hierarchyLevels[
            skeleton.hierarchyLevelOffset + level];
        for (uint item = 0u; item < levelInfo.boneCount; ++item)
        {
            uint bone = push.hierarchyBoneIndices[
                levelInfo.boneIndexOffset + item];
            if (bone >= skeleton.boneCount)
                continue;
            BoneMetadataGpu metadata = push.bones[skeleton.boneOffset + bone];
            float4x4 local = ComposeTransform(pose[bone]);
            global[bone] = metadata.parentIndex < 0 ||
                metadata.parentIndex >= int(skeleton.boneCount)
                ? local
                : mul(global[metadata.parentIndex], local);
        }
    }

    for (uint bone = 0u; bone < skeleton.boneCount; ++bone)
    {
        skin[bone] = mul(
            global[bone],
            push.inverseBindMatrices[skeleton.inverseBindOffset + bone]);
    }

}

[shader("compute")]
[numthreads(64, 1, 1)]
void skinMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint workIndex = dispatchThreadId.y;
    if (workIndex >= push.skinWorkItemCount)
        return;

    SkinWorkItemGpu work = push.skinWorkItems[workIndex];
    uint vertexIndex = dispatchThreadId.x;
    if (vertexIndex >= work.vertexCount)
        return;

    SkinSourceVertexGpu source = work.sourceVertices[vertexIndex];
    uint4 boneIndices = source.boneIndices;
    float4 weights = max(source.boneWeights, 0.0);
    float weightSum = weights.x + weights.y + weights.z + weights.w;
    if (weightSum <= 1.0e-6)
    {
        boneIndices = uint4(0u, 0u, 0u, 0u);
        weights = float4(1.0, 0.0, 0.0, 0.0);
    }
    else
    {
        weights /= weightSum;
    }

    float4 sourceTangent = float4(source.tangent.xyz, 0.0);
    float4 position = float4(0.0, 0.0, 0.0, 0.0);
    float4 normal = float4(0.0, 0.0, 0.0, 0.0);
    float4 tangent = float4(0.0, 0.0, 0.0, 0.0);
    bool validVertex = true;
    for (uint influence = 0u; influence < 4u; ++influence)
    {
        float weight = weights[influence];
        if (weight <= 1.0e-6)
            continue;
        uint boneIndex = boneIndices[influence];
        if (boneIndex >= work.boneCount)
        {
            validVertex = false;
            break;
        }
        float4x4 matrix = work.skinMatrices[boneIndex];
        position += mul(matrix, float4(source.position, 1.0)) * weight;
        normal += mul(matrix, float4(source.normal, 0.0)) * weight;
        tangent += mul(matrix, sourceTangent) * weight;
    }

    if (!validVertex)
    {
        position = float4(source.position, 1.0);
        normal = float4(source.normal, 0.0);
        tangent = sourceTangent;
    }

    Vertex output;
    position.xyz += work.outputOffset;
    output.px = position.x;
    output.py = position.y;
    output.pz = position.z;
    output.nx = normalize(normal.xyz).x;
    output.ny = normalize(normal.xyz).y;
    output.nz = normalize(normal.xyz).z;
    output.u = source.texcoord.x;
    output.v = source.texcoord.y;
    float3 normalizedTangent = normalize(tangent.xyz);
    output.tx = normalizedTangent.x;
    output.ty = normalizedTangent.y;
    output.tz = normalizedTangent.z;
    output.tw = source.tangent.w;
    work.outputVertices[vertexIndex] = output;
}
