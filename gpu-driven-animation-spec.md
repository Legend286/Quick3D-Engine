Q3DE GPU Animation, Compute Skinning and Ray-Tracing Integration Specification
1. Purpose

This subsystem provides a predominantly GPU-driven skeletal animation pipeline for Q3DE.

It supports:

animation import from GLB and other asset formats;
extracted root motion;
GPU animation-state advancement;
GPU clip sampling;
animation crossfades;
layered animation;
per-bone masks;
additive animation;
GPU skeleton hierarchy evaluation;
compute skinning;
current and previous skinned vertex positions;
motion-vector generation;
raster visibility-buffer rendering;
hardware ray-tracing BLAS updates;
shared raster and ray-tracing surface reconstruction;
CPU-authoritative gameplay events and root motion.

The initial implementation deliberately excludes:

animation LOD;
reduced skeletons;
crowd-specific animation formats;
cloth;
muscle simulation;
motion matching;
full-body IK;
network replication policy.

The architecture must leave room for these features without requiring a redesign.

2. Design Principles
2.1 CPU controls intent

The CPU remains authoritative for:

gameplay state;
requested animation transitions;
animation events;
root-motion application;
network-relevant animation state;
entity transforms;
collision and character-controller movement.

The CPU does not evaluate full poses or skin meshes under normal operation.

2.2 GPU performs pose work

The GPU handles:

animation time advancement;
dense clip sampling;
interpolation;
crossfading;
layering;
bone-mask application;
additive animation;
local-to-global hierarchy evaluation;
skin-matrix generation;
vertex skinning;
previous-position preservation;
dynamic bounds calculation;
optional procedural pose modifications.
2.3 Animation data uses structured buffers

Animation data should not be represented as conventional image textures.

Use structured or byte-address buffers so that the implementation supports:

variable clip lengths;
arbitrary skeleton sizes;
compression;
bindless addressing;
unified raster and compute access;
clean memory suballocation;
straightforward CPU asset streaming.

Textures may later be added as a specialised crowd-animation path.

2.4 Raster and ray tracing share geometry identity

Raster visibility hits and ray-tracing hits must both resolve to a canonical SurfaceHit.

struct SurfaceHit
{
    uint instanceID;
    uint geometryID;
    uint primitiveID;
    float2 barycentrics;
};

Both paths must invoke the same surface reconstruction and material evaluation functions.

3. High-Level Pipeline
Asset import
    ↓
Skeleton construction
    ↓
Animation-track extraction
    ↓
Root-motion extraction
    ↓
Animation resampling and compression
    ↓
GPU asset upload

Per frame:

CPU gameplay and animation commands
    ↓
GPU animator-state update
    ↓
GPU clip sampling
    ↓
GPU transition and layer blending
    ↓
GPU procedural pose stage
    ↓
GPU hierarchy evaluation
    ↓
GPU skin-matrix generation
    ↓
GPU compute skinning
    ↓
GPU skinned-bounds reduction
    ↓
    ├── Raster visibility pass
    ├── Shadow passes
    ├── Motion-vector reconstruction
    └── Ray-tracing BLAS update
                ↓
       Hardware ray traversal
                ↓
       Shared surface reconstruction
4. Coordinate and Transform Conventions

Q3DE must define one canonical transform convention.

Example:

Handedness:            right-handed
Matrix storage:        column-major
Vector convention:     column vectors
Local-to-parent:       parentGlobal * local
Skin matrix:           boneGlobal * inverseBindMatrix
Quaternion layout:     x, y, z, w

The importer must convert source assets into this convention.

All animation data stored on the GPU must already use engine-native conventions. Shaders must not contain file-format-specific coordinate conversions.

5. Asset Model
5.1 Skeleton asset

A skeleton is an immutable GPU asset shared by all compatible character instances.

struct SkeletonAssetGPU
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
5.2 Bone metadata
struct BoneMetadataGPU
{
    int parentIndex;
    uint hierarchyDepth;
    uint nameHash;
    uint flags;
};

parentIndex is local to the skeleton.

The root bone uses:

parentIndex = -1;
5.3 Reference pose
struct LocalTransform
{
    float4 rotation;
    float4 translation;
    float4 scale;
};

Suggested packing:

rotation.xyz/w      quaternion
translation.xyz     local translation
translation.w       unused or metadata
scale.xyz           local scale
scale.w             unused or metadata

The first implementation can use three float4 values per transform.

Later compression may reduce this to:

32-bit or 48-bit quaternion;
16-bit bounded translation;
omitted scale for scale-free tracks;
constant-track elimination.
5.4 Inverse bind matrices

Store one inverse bind matrix per bone.

StructuredBuffer<float4x4> InverseBindMatrices;

If nonuniform scaling is not required, a packed affine 3×4 representation can later replace the full 4×4 matrix.

6. Animation Clip Format
6.1 Initial dense format

The first version should use densely sampled local transforms.

Advantages:

deterministic indexing;
no per-track key search;
coherent GPU memory access;
easy interpolation;
easy blending;
simple tooling;
predictable compute cost.
struct AnimationClipGPU
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
    uint skeletonID;
};
6.2 Sample layout

Store samples frame-major:

clip
    frame 0
        bone 0
        bone 1
        bone 2
    frame 1
        bone 0
        bone 1
        bone 2

Indexing:

uint sampleIndex =
    clip.sampleOffset +
    frameIndex * clip.boneCount +
    boneIndex;

Frame-major ordering is preferable when processing one character pose because adjacent threads reading adjacent bones access contiguous memory.

6.3 Sample-rate policy

Recommended initial import rate:

Default:         30 Hz
High quality:    60 Hz

The importer may use a higher internal sampling rate while determining whether lower-rate baking remains within an error threshold.

The runtime must interpolate between adjacent samples, so 30 Hz does not imply 30 Hz visible animation.

6.4 Clip flags
enum AnimationClipFlags : uint
{
    AnimationClip_Looping             = 1 << 0,
    AnimationClip_HasRootMotion       = 1 << 1,
    AnimationClip_HasScaleTracks      = 1 << 2,
    AnimationClip_Additive            = 1 << 3,
    AnimationClip_InPlace             = 1 << 4,
    AnimationClip_HasEvents           = 1 << 5
};
7. Import Pipeline
7.1 Import stages

For every imported animated asset:

Import the node hierarchy.
Identify the skin joints.
Build a compact skeleton containing only required bones.
Preserve any required non-skin parent nodes.
Convert transforms to Q3DE conventions.
Read inverse bind matrices.
Construct the reference pose.
Import animation channels.
Evaluate missing channels from the reference pose.
Select the root-motion source.
Sample each clip at the chosen rate.
Extract root motion.
Convert the remaining clip to in-place animation.
Detect constant tracks.
Generate animation events.
Validate skinning and hierarchy output.
Serialize the cooked animation asset.
7.2 Missing animation channels

GLB clips may omit channels that remain constant.

For every frame and bone:

rotation =
    trackHasRotation
        ? sampleRotationTrack()
        : referencePose.rotation;

translation =
    trackHasTranslation
        ? sampleTranslationTrack()
        : referencePose.translation;

scale =
    trackHasScale
        ? sampleScaleTrack()
        : referencePose.scale;
7.3 Quaternion continuity

Before interpolation, adjacent imported quaternion keys must be hemisphere-corrected:

if (dot(previousQuaternion, currentQuaternion) < 0.0)
{
    currentQuaternion = -currentQuaternion;
}

This avoids interpolation taking the long path.

7.4 Import validation

The asset cooker must detect:

invalid parent indices;
hierarchy cycles;
mismatched inverse-bind counts;
missing joints;
non-finite transforms;
zero-length quaternions;
clip/skeleton incompatibility;
excessive bone influence counts;
weights that do not sum to approximately one;
unsupported shear transforms.
8. Root-Motion Extraction
8.1 Goal

Gameplay movement remains CPU authoritative, while full-pose evaluation remains GPU driven.

Root motion is extracted during import into a compact CPU-readable curve.

8.2 Root-motion source

Each skeleton asset identifies a root-motion bone, commonly:

the skeleton root;
pelvis;
hips;
a dedicated motion node.

The importer must permit an asset-specific override.

8.3 Extracted components

Root-motion policy should be configurable per clip:

enum RootMotionComponentFlags : uint
{
    RootMotion_TranslationX = 1 << 0,
    RootMotion_TranslationY = 1 << 1,
    RootMotion_TranslationZ = 1 << 2,
    RootMotion_Yaw          = 1 << 3,
    RootMotion_Pitch        = 1 << 4,
    RootMotion_Roll         = 1 << 5
};

Typical character configuration:

Extract horizontal translation
Extract yaw
Preserve vertical movement in pose or character controller
Discard pitch and roll
8.4 Root-motion sample format
struct RootMotionSample
{
    float3 translation;
    float yaw;
};

Store either absolute root transforms or per-frame cumulative transforms.

Absolute cumulative samples are easier for arbitrary time queries:

RootMotionTransform SampleRootMotion(
    clip,
    animationTime);

Frame delta:

delta =
    inverse(SampleRootMotion(previousTime)) *
    SampleRootMotion(currentTime);
8.5 Looping clips

When a looping clip crosses its end:

previousTime = duration - ε
currentTime  = small positive time

The root-motion delta must include:

previousTime → clip end
clip start   → currentTime

Store the total clip root-motion transform so wrapping remains continuous.

8.6 In-place pose conversion

After extracting root motion, remove the selected components from the animation pose.

For example:

sampledRoot.translation.xz -= extractedTranslation.xz;
sampledRoot.rotation =
    inverse(extractedYawRotation) *
    sampledRoot.rotation;

The resulting GPU clip animates in place while the CPU moves the entity.

8.7 CPU runtime root-motion state
struct RootMotionPlaybackStateCPU
{
    uint entityID;
    uint clipID;

    float previousTime;
    float currentTime;

    bool looped;
};

The CPU evaluates root-motion curves only for gameplay-relevant animators.

The CPU must not wait for GPU animation state readback to determine root motion.

Therefore, CPU and GPU animation clocks must be advanced from the same commands and delta time.

9. Animation Events
9.1 Event ownership

Animation events remain CPU side.

Examples:

footsteps;
melee hit windows;
sound triggers;
particle triggers;
weapon attachment changes;
transition requests.
9.2 Event format
struct AnimationEvent
{
    float normalisedTime;
    uint eventHash;
    uint payloadOffset;
    uint flags;
};
9.3 Event evaluation

The CPU checks the interval traversed during the frame:

previous time → current time

Looping intervals are split around the clip boundary.

Animation events should not require GPU readback.

10. GPU Animator Instance State

Each animated entity has persistent GPU state.

struct GPUAnimatorState
{
    uint skeletonID;
    uint entityID;

    uint baseClipID;
    uint targetClipID;

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
10.1 Animator flags
enum GPUAnimatorFlags : uint
{
    Animator_Active             = 1 << 0,
    Animator_Looping            = 1 << 1,
    Animator_InTransition       = 1 << 2,
    Animator_Paused             = 1 << 3,
    Animator_UseDualQuaternion  = 1 << 4,
    Animator_NeedsSkinning      = 1 << 5,
    Animator_NeedsBLASUpdate    = 1 << 6,
    Animator_ResetHistory       = 1 << 7
};
10.2 Generation field

The generation protects against stale GPU work when entity slots are recycled.

A command applies only when:

command.generation == animator.generation;
11. CPU-to-GPU Animation Commands

The CPU submits compact commands rather than complete poses.

enum AnimationCommandType : uint
{
    AnimationCommand_Play,
    AnimationCommand_Crossfade,
    AnimationCommand_Stop,
    AnimationCommand_SetRate,
    AnimationCommand_SetTime,
    AnimationCommand_AddLayer,
    AnimationCommand_RemoveLayer,
    AnimationCommand_SetLayerWeight,
    AnimationCommand_ResetHistory
};
struct AnimationCommandGPU
{
    uint animatorIndex;
    uint generation;
    uint commandType;
    uint clipOrLayerID;

    float value0;
    float value1;
    float value2;
    float value3;
};

Examples:

Play:
    clipOrLayerID = clip
    value0 = start time
    value1 = playback rate

Crossfade:
    clipOrLayerID = target clip
    value0 = transition duration
    value1 = target start time

SetLayerWeight:
    clipOrLayerID = layer index
    value0 = new weight

Commands should be uploaded through a persistently mapped upload ring or equivalent RHI mechanism.

12. Animation Layers
12.1 Layer state
struct GPUAnimationLayer
{
    uint clipID;
    uint boneMaskID;

    float time;
    float playbackRate;

    float weight;
    float transitionWeight;

    uint blendMode;
    uint flags;
};
12.2 Blend modes
enum AnimationBlendMode : uint
{
    AnimationBlend_Override,
    AnimationBlend_AdditiveLocal,
    AnimationBlend_AdditiveMeshSpace
};

Initial implementation should support:

override;
local-space additive.

Mesh-space additive may be implemented later.

12.3 Bone masks
struct BoneMaskGPU
{
    uint weightOffset;
    uint boneCount;
};

Weights may initially use one float per bone:

StructuredBuffer<float> BoneMaskWeights;

Later packing may use UNORM8.

12.4 Layer order

Layers are evaluated in stable array order:

base pose
    ↓
override layer 0
    ↓
override layer 1
    ↓
additive layer 0
    ↓
procedural modifications

The precise ordering must be explicit and deterministic.

13. GPU Pass Sequence
Pass 1: Apply animation commands
Inputs
animator states;
command buffer;
command count.
Outputs
updated animator states;
optional dirty animator list.
Dispatch
One thread per command

Commands targeting the same animator should be ordered or pre-compacted by the CPU.

For the first implementation, avoid unordered simultaneous writes to one animator.

Pass 2: Advance animator clocks
Inputs
animator state;
frame delta time;
clip metadata.
Outputs
updated clip times;
transition weights;
completed transition state.
Dispatch
One thread per active animator

Pseudo-code:

if (!state.Active || state.Paused)
    return;

state.baseTime += deltaTime * state.playbackRate;
state.baseTime = ResolveClipTime(state.baseClipID, state.baseTime);

if (state.InTransition)
{
    state.targetTime += deltaTime * state.playbackRate;
    state.targetTime =
        ResolveClipTime(state.targetClipID, state.targetTime);

    state.transitionTime += deltaTime;

    state.transitionWeight =
        saturate(
            state.transitionTime /
            max(state.transitionDuration, epsilon));

    if (state.transitionWeight >= 1.0)
    {
        state.baseClipID = state.targetClipID;
        state.baseTime = state.targetTime;
        state.InTransition = false;
    }
}

The GPU does not send transition-complete events back synchronously.

The CPU maintains equivalent high-level timing when gameplay needs to know transition status.

Pass 3: Build animation work lists

Produce compact lists for:

active animators;
animators requiring pose evaluation;
meshes requiring skinning;
meshes requiring BLAS update.

This may initially be performed on the CPU.

The preferred long-term path uses GPU compaction and indirect dispatch arguments.

struct AnimatorWorkItem
{
    uint animatorIndex;
    uint skeletonID;
    uint outputPoseOffset;
    uint outputMatrixOffset;
};
Pass 4: Sample and blend local poses
Dispatch
One thread per animator-bone pair

Linear thread mapping:

uint workItemIndex = dispatchThreadID / maxBonesPerGroup;
uint boneIndex = dispatchThreadID % maxBonesPerGroup;

A work-item table provides the actual skeleton bone count.

Frame sampling
float framePosition = time * clip.sampleRate;

uint frame0 = floor(framePosition);
uint frame1 = frame0 + 1;
float alpha = frac(framePosition);

Looping:

frame0 %= clip.frameCount;
frame1 %= clip.frameCount;

Non-looping:

frame0 = min(frame0, clip.frameCount - 1);
frame1 = min(frame1, clip.frameCount - 1);
Transform interpolation
LocalTransform Interpolate(
    LocalTransform a,
    LocalTransform b,
    float alpha)
{
    LocalTransform result;

    float4 qb = b.rotation;

    if (dot(a.rotation, qb) < 0.0)
        qb = -qb;

    result.rotation =
        normalize(lerp(a.rotation, qb, alpha));

    result.translation.xyz =
        lerp(a.translation.xyz, b.translation.xyz, alpha);

    result.scale.xyz =
        lerp(a.scale.xyz, b.scale.xyz, alpha);

    return result;
}

nlerp is suitable for the first implementation.

Transition blending
basePose = SampleClip(baseClip);
targetPose = SampleClip(targetClip);

pose = BlendLocalTransforms(
    basePose,
    targetPose,
    transitionWeight);
Override layers
float effectiveWeight =
    layer.weight *
    boneMaskWeight;

pose = BlendLocalTransforms(
    pose,
    layerPose,
    effectiveWeight);
Additive layers

For additive local transforms stored relative to a reference pose:

float4 additiveRotation =
    normalize(
        lerp(
            IdentityQuaternion,
            layerDelta.rotation,
            effectiveWeight));

pose.rotation =
    normalize(
        additiveRotation *
        pose.rotation);

pose.translation.xyz +=
    layerDelta.translation.xyz *
    effectiveWeight;

pose.scale.xyz *=
    lerp(
        float3(1.0),
        layerDelta.scale.xyz,
        effectiveWeight);
Output
RWStructuredBuffer<LocalTransform> BlendedLocalPoses;
Pass 5: Procedural pose modifications

This pass runs after animation blending but before hierarchy evaluation.

Initial hooks:

look-at rotation;
aim offset;
recoil;
breathing;
simple spring bones;
foot-placement corrections;
externally supplied bone overrides.
struct ProceduralBoneOverride
{
    uint animatorIndex;
    uint boneIndex;
    uint mode;
    uint flags;

    LocalTransform transform;
    float weight;
};

Override modes:

replace local transform
blend local transform
add local rotation
add local translation

Full IK is not required initially, but this pass provides the extension point.

Pass 6: Evaluate skeleton hierarchy

This is the primary dependency-sensitive pass.

Recommended initial implementation

Use one threadgroup per animator.

Each threadgroup:

loads local transforms;
evaluates hierarchy levels in order;
stores global transforms;
calculates skin matrices.
Precomputed hierarchy levels

For each skeleton:

struct HierarchyLevel
{
    uint boneIndexOffset;
    uint boneCount;
};
StructuredBuffer<uint> HierarchyBoneIndices;
StructuredBuffer<HierarchyLevel> HierarchyLevels;
Algorithm
groupshared float4x4 sharedGlobalMatrices[MAX_BONES];

For every hierarchy depth:

for (uint level = 0;
     level < skeleton.hierarchyLevelCount;
     ++level)
{
    HierarchyLevel levelInfo =
        HierarchyLevels[
            skeleton.hierarchyLevelOffset + level];

    for (uint i = groupThreadID;
         i < levelInfo.boneCount;
         i += THREADGROUP_SIZE)
    {
        uint boneIndex =
            HierarchyBoneIndices[
                levelInfo.boneIndexOffset + i];

        BoneMetadata bone =
            BoneMetadataBuffer[
                skeleton.boneOffset + boneIndex];

        float4x4 local =
            ComposeTRS(
                localPose[boneIndex]);

        if (bone.parentIndex < 0)
        {
            sharedGlobalMatrices[boneIndex] = local;
        }
        else
        {
            sharedGlobalMatrices[boneIndex] =
                mul(
                    sharedGlobalMatrices[
                        bone.parentIndex],
                    local);
        }
    }

    GroupMemoryBarrierWithGroupSync();
}

After all levels:

for (uint boneIndex = groupThreadID;
     boneIndex < skeleton.boneCount;
     boneIndex += THREADGROUP_SIZE)
{
    float4x4 inverseBind =
        InverseBindMatrices[
            skeleton.inverseBindOffset +
            boneIndex];

    float4x4 skinMatrix =
        mul(
            sharedGlobalMatrices[boneIndex],
            inverseBind);

    SkinMatrices[
        animator.outputMatrixOffset +
        boneIndex] = skinMatrix;
}
Threadgroup sizing

Suggested starting point:

64 threads per group
Maximum initial skeleton size: 256 bones

Larger skeletons may use global-memory intermediate storage or a separate path.

Pass 7: Preserve previous skinned data

Motion vectors require current and previous deformed positions.

Use ping-pong skinned buffers:

SkinnedPositionBufferA
SkinnedPositionBufferB

For frame N:

currentBuffer  = buffers[N & 1];
previousBuffer = buffers[(N + 1) & 1];

No explicit copy is necessary if each frame writes the complete current buffer.

The animator state stores offsets into both buffers.

For newly visible, newly spawned, teleported or reset characters:

previous position = current position

This prevents invalid motion streaks.

Pass 8: Compute skinning
Dispatch
One thread per skinned vertex
Static source data
struct SkinSourceVertex
{
    float3 position;
    uint packedNormal;

    uint packedTangent;
    uint packedWeights;

    uint4 boneIndices;
};

The exact vertex layout may be split into separate streams.

Recommended streams:

StaticPositionBuffer
StaticNormalTangentBuffer
BoneIndexBuffer
BoneWeightBuffer
StaticAttributeBuffer

UVs and material attributes do not need to be copied into the skinned output.

Output data
struct SkinnedVertexOutput
{
    float3 position;
    uint packedNormal;

    uint packedTangent;
};

For visibility rendering and BLAS updates, positions are essential.

Normals and tangents may either be:

compute skinned and stored;
reconstructed or skinned during material shading.

Recommended initial implementation:

Compute-skin positions, normals and tangents once.

This avoids repeating normal and tangent skinning in every material-shading pass.

Matrix skinning
float4 skinnedPosition = 0.0;
float3 skinnedNormal = 0.0;
float3 skinnedTangent = 0.0;

for (uint influence = 0;
     influence < MAX_INFLUENCES;
     ++influence)
{
    uint boneIndex =
        source.boneIndices[influence];

    float weight =
        DecodeWeight(
            source.weights,
            influence);

    float4x4 skin =
        SkinMatrices[
            animator.outputMatrixOffset +
            boneIndex];

    skinnedPosition +=
        mul(
            skin,
            float4(source.position, 1.0)) *
        weight;

    skinnedNormal +=
        mul(
            (float3x3)skin,
            source.normal) *
        weight;

    skinnedTangent +=
        mul(
            (float3x3)skin,
            source.tangent.xyz) *
        weight;
}

Finalise:

output.position =
    skinnedPosition.xyz;

output.normal =
    normalize(skinnedNormal);

output.tangent.xyz =
    normalize(
        skinnedTangent -
        output.normal *
        dot(output.normal, skinnedTangent));

output.tangent.w =
    source.tangent.w;
Nonuniform scale

The simple (float3x3)skinMatrix normal transform is not correct under arbitrary nonuniform scaling.

Initial options:

prohibit animated nonuniform scale;
calculate normal matrices per bone;
use inverse-transpose transforms.

Recommended initial policy:

Support uniform scale only in animated skeleton tracks.
Reject or warn about nonuniform animated scale.
Pass 9: Skinned bounds reduction

Calculate updated object-space bounds for:

culling;
TLAS instance bounds;
debugging;
conservative raster scheduling.
Two-stage reduction
Each threadgroup reduces a range of skinned vertices.
A second pass reduces group bounds into one mesh bound.
struct AABB
{
    float3 minimum;
    float3 maximum;
};

Bounds may be conservatively expanded by a small epsilon.

14. Visibility-Buffer Integration
14.1 Visibility-pass requirements

The visibility pass consumes:

current skinned positions;
object transforms;
index buffer;
draw metadata.

It writes a compact hit identity.

A possible packed visibility record:

struct PackedVisibility
{
    uint instanceAndFlags;
    uint primitiveAndGeometry;
    uint packedBarycentrics;
};

Exact packing depends on scene limits.

14.2 Canonical triangle identity

Raster primitive IDs may be local to:

a draw;
a meshlet;
a geometry partition.

Resolve them using a geometry table.

struct GeometryRecord
{
    uint indexOffset;
    uint vertexOffset;
    uint triangleBase;
    uint materialTableOffset;

    uint positionOffset;
    uint normalTangentOffset;
    uint staticAttributeOffset;
    uint flags;
};

Canonical ID:

uint canonicalPrimitiveID =
    geometry.triangleBase +
    localPrimitiveID;
14.3 Barycentrics

If hardware raster barycentrics are available, store them directly.

Otherwise reconstruct them during deferred shading from:

projected triangle positions;
pixel position;
triangle vertex indices.

For maximum cross-platform support, the visibility record should permit either approach.

14.4 Shared surface resolver
SurfaceData ResolveSurfaceHit(
    SurfaceHit hit,
    SurfaceDerivatives derivatives);

Responsibilities:

resolve instance;
resolve geometry;
resolve triangle indices;
fetch skinned position/normal/tangent streams;
fetch static UVs and other attributes;
interpolate by barycentrics;
calculate or accept derivatives;
resolve material;
construct material inputs.
15. Motion Vectors
15.1 Required data

Motion vectors use:

current skinned object-space vertex positions;
previous skinned object-space vertex positions;
current object transform;
previous object transform;
current view-projection matrix;
previous view-projection matrix.
15.2 Surface reconstruction

From visibility-buffer triangle identity:

float3 currentObjectPosition =
    InterpolateTriangle(
        currentPosition0,
        currentPosition1,
        currentPosition2,
        barycentrics);

float3 previousObjectPosition =
    InterpolateTriangle(
        previousPosition0,
        previousPosition1,
        previousPosition2,
        barycentrics);

Then:

float4 currentClip =
    CurrentViewProjection *
    CurrentObjectTransform *
    float4(currentObjectPosition, 1.0);

float4 previousClip =
    PreviousViewProjection *
    PreviousObjectTransform *
    float4(previousObjectPosition, 1.0);

Convert to NDC:

float2 currentNDC =
    currentClip.xy / currentClip.w;

float2 previousNDC =
    previousClip.xy / previousClip.w;

float2 velocity =
    currentNDC - previousNDC;
15.3 Disocclusion limitation

Using the current frame's triangle identity to fetch the same previous triangle is standard and useful, but does not perfectly represent:

topology changes;
disappearing triangles;
severe deformation;
newly revealed surfaces.

TAA and temporal denoisers must still perform disocclusion tests.

15.4 History reset

Reset previous data when:

animator is created;
entity teleports;
mesh changes;
skeleton changes;
animation time jumps;
animation is externally posed;
a character becomes active after not being updated.
16. Hardware Ray-Tracing Integration
16.1 Correct BLAS contents

A triangle BLAS must reference:

current skinned vertex positions;
triangle indices.

The BLAS does not store only a visibility ID.

Attributes remain outside the BLAS.

BLAS traversal data:
    triangle positions
    indices or implicit triangle order

External shading data:
    UVs
    normals
    tangents
    materials
    vertex colours
    skin metadata
16.2 BLAS geometry table
struct RTGeometryRecord
{
    uint q3deGeometryID;
    uint triangleBase;
    uint indexOffset;
    uint vertexOffset;

    uint materialOffset;
    uint flags;
};

Map the ray-tracing API's:

InstanceID
GeometryIndex
PrimitiveIndex
Barycentrics

into:

SurfaceHit hit;

Example:

RTGeometryRecord rtGeometry =
    RTGeometryTable[
        instance.rtGeometryOffset +
        geometryIndex];

hit.instanceID =
    instance.customInstanceID;

hit.geometryID =
    rtGeometry.q3deGeometryID;

hit.primitiveID =
    primitiveIndex;

hit.barycentrics =
    hardwareBarycentrics;
16.3 BLAS update order

For animated meshes:

animation evaluation
    ↓
compute skinning
    ↓
UAV/storage write barrier
    ↓
BLAS update/refit
    ↓
acceleration-structure barrier
    ↓
TLAS update if required
    ↓
ray tracing
16.4 Refit versus rebuild

Use BLAS update/refit when:

topology is unchanged;
vertex count is unchanged;
index buffer is unchanged;
deformation is moderate.

Periodic rebuild may be necessary when deformation causes poor BVH quality.

Initial policy:

Refit every animated frame.
Permit an engine-controlled periodic rebuild interval.

Measure before choosing a fixed rebuild cadence.

16.5 BLAS update eligibility

Only update a character BLAS when needed by active RT effects.

Potential relevance flags:

enum RTAnimationRelevanceFlags
{
    RTRelevant_PrimaryVisibility = 1 << 0,
    RTRelevant_Reflections       = 1 << 1,
    RTRelevant_GI                = 1 << 2,
    RTRelevant_Shadows           = 1 << 3
};

LOD and RT relevance culling are deferred, but the data model should support them.

16.6 Ray-hit shading

The ray hit returns compact identity and barycentrics.

The closest-hit or ray-query consumer should avoid loading all attributes unless shading is required.

Examples:

opaque shadow ray: terminate without material reconstruction;
alpha-tested shadow ray: fetch UV and opacity material only;
reflection ray: reconstruct full surface;
GI visibility ray: potentially fetch only material classification.
17. Shared Raster and RT Surface Reconstruction
17.1 Common interface
struct SurfaceResolveInput
{
    SurfaceHit hit;
    uint rayOrRasterFlags;
    float3 rayDirection;
    float coneWidth;
};
struct SurfaceData
{
    float3 worldPosition;
    float3 geometricNormal;
    float3 shadingNormal;

    float4 tangent;
    float2 uv0;
    float2 uv1;

    uint materialID;
    uint instanceID;
    uint primitiveID;
};
17.2 Attribute interpolation
float3 weights =
    float3(
        1.0 - bary.x - bary.y,
        bary.x,
        bary.y);
value =
    value0 * weights.x +
    value1 * weights.y +
    value2 * weights.z;

Perspective-correct raster barycentrics must be used for raster interpolation.

Ray-tracing triangle barycentrics correspond directly to the triangle hit and require no screen-space perspective correction.

17.3 Static versus skinned streams

For animated geometry:

Position:       skinned stream
Normal:         skinned stream
Tangent:        skinned stream
UV:             static stream
Vertex colour:  static stream
Material ID:    primitive or geometry table
18. GPU Memory Organisation
18.1 Immutable asset buffers
SkeletonAssetBuffer
BoneMetadataBuffer
HierarchyLevelBuffer
HierarchyBoneIndexBuffer
ReferencePoseBuffer
InverseBindMatrixBuffer
AnimationClipBuffer
AnimationSampleBuffer
BoneMaskBuffer
RootMotionBuffer
AnimationEventBuffer
StaticSkinVertexBuffers
IndexBuffers
18.2 Persistent runtime buffers
AnimatorStateBuffer
AnimationLayerBuffer
ProceduralOverrideBuffer
LocalPoseBuffer
GlobalPoseBuffer or temporary shared memory
SkinMatrixBuffer
CurrentSkinnedVertexBuffer
PreviousSkinnedVertexBuffer
SkinnedBoundsBuffer
18.3 Transient frame buffers
AnimationCommandBuffer
ActiveAnimatorWorkList
SkinningWorkList
BLASUpdateWorkList
IndirectDispatchArguments
BoundsReductionScratch
18.4 Buffer suballocation

Use stable handles plus offsets rather than one GPU allocation per entity.

Recommended allocators:

Immutable animation heap
Persistent animator-state pool
Persistent skinned-vertex heap
Transient frame ring
18.5 Alignment

The RHI must expose alignment requirements for:

structured-buffer offsets;
storage-buffer offsets;
indirect arguments;
acceleration-structure scratch;
device-address access.

Do not hardcode one API's alignment assumptions into asset formats.

19. Frame Graph Integration

Suggested frame-graph passes:

UploadAnimationCommands
ApplyAnimationCommands
AdvanceAnimatorStates
BuildAnimationWorkLists
SampleAndBlendLocalPoses
ApplyProceduralPose
EvaluateSkeletonHierarchy
ComputeSkinning
ReduceSkinnedBounds
UpdateAnimatedBLAS
UpdateTLAS
VisibilityPass
RayTracingPasses
DeferredMaterialShading
MotionVectorPass

Some passes may be fused after profiling.

19.1 Resource dependencies
AnimationSampleBuffer
    → SampleAndBlendLocalPoses

LocalPoseBuffer
    → ApplyProceduralPose
    → EvaluateSkeletonHierarchy

SkinMatrixBuffer
    → ComputeSkinning

SkinnedVertexBuffer
    → VisibilityPass
    → ShadowPasses
    → UpdateAnimatedBLAS
    → MotionVectorPass
    → SurfaceResolver

BLAS
    → RayTracingPasses
19.2 Queue policy

Initial implementation should run all animation and skinning work on the graphics queue.

Async compute may later be enabled when:

there is useful overlap;
BLAS dependencies permit it;
ownership transfer costs are acceptable;
profiling demonstrates a benefit.

Do not begin with async compute purely for architectural novelty.

20. Synchronisation Requirements

The RHI must represent these transitions:

20.1 Animation output to skinning
Local pose storage writes
    → hierarchy reads

Skin-matrix storage writes
    → skinning reads
20.2 Skinning to raster
Compute storage writes to skinned vertices
    → vertex/mesh shader reads
20.3 Skinning to BLAS update
Compute storage writes to positions
    → acceleration-structure build reads
20.4 BLAS update to ray tracing
Acceleration-structure writes
    → ray-tracing acceleration-structure reads
20.5 Motion-history ping-pong

The previous buffer must remain readable until all temporal consumers for the frame finish.

21. Mesh and Instance Data
21.1 Skinned mesh asset
struct SkinnedMeshAssetGPU
{
    uint skeletonID;

    uint sourceVertexOffset;
    uint vertexCount;

    uint indexOffset;
    uint indexCount;

    uint geometryOffset;
    uint geometryCount;

    uint maxInfluences;
    uint flags;
};
21.2 Animated mesh instance
struct AnimatedMeshInstanceGPU
{
    uint animatorIndex;
    uint meshAssetID;
    uint sceneInstanceID;
    uint materialOverrideOffset;

    uint currentVertexOffset;
    uint previousVertexOffset;

    uint blasHandleIndex;
    uint flags;
};

One animator may drive multiple skinned mesh components if they share the same skeleton pose.

Examples:

body;
clothes;
armour;
hair;
equipment.

Skin matrices should be evaluated once and reused by all attached meshes.

22. Bone Influences
22.1 Initial maximum

Recommended initial maximum:

4 influences per vertex

Eight influences may later be supported for high-quality assets.

22.2 Weight format

Suggested:

Bone indices: 4 × uint16
Weights:      4 × UNORM16 or UNORM8

At import, normalise weights and ensure:

sum(weights) = 1.0;

If quantisation changes the sum, renormalise in the shader or correct the largest packed weight offline.

22.3 Influence pruning

When source assets exceed the supported count:

retain the strongest influences;
discard weaker influences;
renormalise;
report import statistics.
23. Attachment and Socket Transforms

Gameplay may need selected bone transforms for:

weapons;
cameras;
particle emitters;
audio emitters;
hitboxes.

Reading all bones back is unacceptable.

23.1 CPU-evaluated gameplay sockets

For critical attachments, the CPU may evaluate only selected bone chains using the same clip times.

This avoids GPU readback.

23.2 GPU attachment transforms

For visual-only attachments, GPU skin hierarchy output can write selected socket matrices into a compact buffer consumed by rendering.

struct GPUSocketRequest
{
    uint animatorIndex;
    uint boneIndex;
    uint outputIndex;
    uint flags;
};
RWStructuredBuffer<float4x4> GPUSocketMatrices;
23.3 Delayed readback

Noncritical CPU consumers may use asynchronous readback with one or more frames of latency.

Do not use synchronous GPU readback for attachment transforms.

24. Dual-Quaternion Extension

The initial implementation should use matrix skinning.

The architecture should reserve a flag for dual-quaternion skinning.

Dual-quaternion mode requires:

rigid bone transforms;
no nonuniform scale;
dual-quaternion pose conversion;
antipodality correction;
normalisation after weighted blending.

The animation sampling and hierarchy stages can remain TRS based.

Only the final skin transform representation and skinning kernel need to change.

25. Error Handling and Fallbacks
25.1 Asset errors

On invalid animation assets:

emit detailed cooker diagnostics;
bind the reference pose;
avoid crashing GPU work;
mark the asset as degraded.
25.2 Runtime allocation failure

If skinned-buffer allocation fails:

render the reference pose;
disable BLAS updates for the instance;
record a diagnostic counter.
25.3 Unsupported skeleton size

If a skeleton exceeds the threadgroup path's maximum:

use a global-memory fallback hierarchy pass;
or reject the asset during cooking.

The fallback is preferable for tooling robustness.

26. Debugging and Tooling

Provide an animation debug view showing:

skeleton hierarchy;
local bone axes;
current clip;
clip time;
transition source and target;
transition weight;
active layers;
layer masks;
extracted root-motion path;
current and previous skinned positions;
computed bounds;
BLAS update status;
animator GPU slot;
skinning vertex count.
26.1 GPU validation counters
struct AnimationDebugCounters
{
    uint invalidAnimatorReferences;
    uint invalidClipReferences;
    uint invalidBoneReferences;
    uint nonFinitePoseTransforms;

    uint zeroLengthQuaternions;
    uint invalidSkinWeights;
    uint hierarchyFailures;
    uint skinnedVertexOverflow;
};
26.2 Pose capture

Permit copying one selected animator's:

local pose;
global matrices;
skin matrices;
skinned vertices;

to a staging buffer for debugging.

This should be opt-in, not active globally.

27. Profiling Counters

Track:

Active animator count
Pose evaluations
Bones sampled
Layers evaluated
Skeleton hierarchy dispatch time
Skinning dispatch time
Vertices skinned
Skinned-buffer bytes written
Bounds reduction time
Animated BLAS count
BLAS refit time
BLAS rebuild time
Animation command count
History resets

GPU timing must separate:

animation;
skinning;
BLAS updates;
visibility rendering;
RT traversal and shading.
28. Initial Performance Strategy

Before adding LOD, optimise the full-quality path through:

compact active-animator lists;
frame-major animation samples;
coherent animator-bone dispatch;
one hierarchy evaluation per shared skeleton pose;
skin once, consume in many passes;
static UV and material streams;
skinned output containing only dynamic attributes;
indirect dispatch where useful;
avoiding CPU/GPU synchronisation;
BLAS updates only for RT-relevant meshes.

Do not optimise by prematurely packing every field.

Establish a correct baseline and collect bandwidth measurements.

29. Suggested Public Engine API
29.1 Asset handles
using SkeletonHandle = Handle<SkeletonAsset>;
using AnimationClipHandle = Handle<AnimationClip>;
using BoneMaskHandle = Handle<BoneMask>;
using SkinnedMeshHandle = Handle<SkinnedMeshAsset>;
29.2 Animator component
struct AnimatorComponent
{
    SkeletonHandle skeleton;

    AnimationClipHandle currentClip;
    AnimationClipHandle targetClip;

    float playbackRate;
    float transitionDuration;

    bool looping;
    bool applyRootMotion;

    GPUAnimatorHandle gpuAnimator;
};
29.3 Runtime commands
void PlayAnimation(
    Entity entity,
    AnimationClipHandle clip,
    bool looping,
    float startTime = 0.0f);

void CrossfadeAnimation(
    Entity entity,
    AnimationClipHandle clip,
    float duration,
    float targetStartTime = 0.0f);

void SetAnimationRate(
    Entity entity,
    float playbackRate);

void SetAnimationTime(
    Entity entity,
    float time,
    bool resetMotionHistory);

void SetAnimationLayer(
    Entity entity,
    uint layerIndex,
    AnimationClipHandle clip,
    BoneMaskHandle mask,
    float weight,
    AnimationBlendMode mode);
29.4 Root motion
RootMotionDelta EvaluateRootMotion(
    AnimationClipHandle clip,
    float previousTime,
    float currentTime,
    bool looping);
30. RHI Requirements

The Q3DE RHI must support:

structured/storage buffers;
read-write buffers;
indirect compute dispatch;
buffer device addresses where supported;
resource barriers;
compute-to-vertex visibility;
compute-to-AS-build visibility;
BLAS update/refit;
TLAS instance updates;
ray hit instance IDs;
geometry indices;
primitive indices;
hardware triangle barycentrics;
persistent or efficient upload buffers;
timestamp queries.

Bindless resources are desirable but not mandatory for the first version.

31. Metal-Specific Notes

For Metal:

animation data should use device buffers;
compute-skinned position buffers must use storage modes compatible with both compute and acceleration-structure construction;
use explicit resource usage declarations where required;
schedule compute skinning before acceleration-structure refit;
use Metal acceleration-structure refit/update facilities where valid;
ray intersection results expose primitive identity and barycentric coordinates;
indirect command buffers are optional, not a prerequisite.

The engine-level design must remain API neutral.

32. Recommended Implementation Phases
Phase 1: Basic GPU playback

Implement:

skeleton import;
dense local-TRS clips;
GPU clip sampling;
GPU hierarchy evaluation;
one base clip;
compute skinning;
raster visibility rendering.
Phase 2: Temporal correctness

Add:

previous skinned positions;
motion vectors;
history resets;
current and previous entity transforms.
Phase 3: CPU-authoritative gameplay integration

Add:

root-motion extraction;
CPU root-motion evaluation;
animation events;
CPU-to-GPU command stream;
synchronised CPU/GPU animation clocks.
Phase 4: Blending

Add:

crossfades;
override layers;
bone masks;
local additive layers.
Phase 5: Ray tracing

Add:

RT-compatible skinned position buffers;
animated BLAS refits;
TLAS update integration;
ray-hit SurfaceHit creation;
shared raster/RT surface resolver.
Phase 6: Procedural animation

Add:

look-at;
aim offsets;
recoil;
socket output;
procedural bone overrides.
Phase 7: Compression and optimisation

Add:

constant-track elimination;
packed quaternions;
quantised translations;
packed bone masks;
GPU work-list compaction;
indirect dispatch;
optional dual-quaternion skinning.
33. Acceptance Criteria

The system is considered functionally complete when:

A GLB skeleton and animation clip import correctly.
Animation playback occurs without CPU pose evaluation.
Two clips can crossfade smoothly.
A masked upper-body layer can blend over locomotion.
An additive recoil layer can affect selected bones.
Root motion moves the CPU-owned entity transform.
The GPU pose remains in place after root-motion extraction.
Current and previous skinned buffers produce correct motion vectors.
The visibility buffer renders animated geometry correctly.
Material shading reconstructs animated surfaces using triangle identity and barycentrics.
An animated BLAS refits from compute-skinned positions.
Ray-traced surfaces use the same geometry and material resolver as raster surfaces.
Newly spawned and teleported characters do not produce invalid velocity.
No synchronous GPU readback is required during normal animation playback.
Multiple mesh components can share one evaluated skeleton pose.
34. Final Architecture Summary
CPU
    gameplay state
    animation intent
    root-motion curve sampling
    animation events
    entity transform authority
    compact GPU commands
                │
                ▼
GPU animator state
    clip clocks
    transitions
    layers
                │
                ▼
Dense local-TRS sampling
                │
                ▼
Pose blending
    crossfades
    masks
    additive layers
                │
                ▼
Procedural local-pose stage
                │
                ▼
Skeleton hierarchy evaluation
                │
                ▼
Skin matrices
                │
                ▼
Compute skinning
    current positions
    previous positions
    normals
    tangents
                │
       ┌────────┼──────────────┐
       ▼        ▼              ▼
Visibility   Motion vectors   BLAS refit
buffer                          │
       │                        ▼
       │                 Hardware ray hit
       │                        │
       └──────────┬─────────────┘
                  ▼
          Canonical SurfaceHit
                  │
                  ▼
      Shared surface reconstruction
                  │
                  ▼
        Shared material evaluation

The central design rule is:

The CPU decides what the character is doing; the GPU constructs and skins the complete visible pose.

The second central rule is:

Rasterisation and ray tracing produce different kinds of intersections, but both resolve into the same engine-level surface identity.