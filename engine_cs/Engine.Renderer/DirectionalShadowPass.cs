// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

internal sealed class DirectionalShadowState
{
    public const int CascadeCount = 4;
    public const uint PageSize = ShadowAtlas.PageSize;
    public const float MaxDistance = 500.0f;

    public ShadowAtlas Atlas { get; }
    public ShadowAtlasAllocation[] Cascades { get; } =
        new ShadowAtlasAllocation[CascadeCount];
    public RhiTexture[] Textures { get; } = new RhiTexture[CascadeCount];
    public uint[] TextureSlots { get; } = new uint[CascadeCount];
    public Matrix4x4[] ViewProjections { get; } =
    {
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
    };
    public Vector4 Parameters { get; set; }
    public Vector4 Splits { get; set; }
    public bool[] ValidCascades { get; } = new bool[CascadeCount];
    public long[] LastUpdatedFrames { get; } =
        { -1, -1, -1, -1 };
    public int[] SceneSignatures { get; } = new int[CascadeCount];
    public float[] ProjectionRadii { get; } = new float[CascadeCount];
    public float ProjectionCenterY { get; set; } = float.NaN;

    private readonly RhiBindlessHeap _bindlessHeap;

    public DirectionalShadowState(
        RhiDevice device,
        RhiBindlessHeap bindlessHeap)
    {
        _bindlessHeap = bindlessHeap;
        Atlas = new ShadowAtlas(device);
        for (int cascadeIndex = 0;
             cascadeIndex < CascadeCount;
             ++cascadeIndex)
        {
            Cascades[cascadeIndex] = Atlas.AllocateDedicatedPage();
            Textures[cascadeIndex] = Cascades[cascadeIndex].Texture;
            TextureSlots[cascadeIndex] =
                _bindlessHeap.Register(Textures[cascadeIndex]);
        }
    }

    public void Dispose()
    {
        foreach (uint slot in TextureSlots)
            _bindlessHeap.Release(slot);
        Atlas.Dispose();
    }
}

internal sealed class DirectionalShadowPass : RenderPass, IDisposable
{
    private const ulong DrawIndirectCommandSizeBytes = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowCullPushData
    {
        public ulong Instances;
        public ulong Parts;
        public ulong DrawCommands;
        public ulong Reserved;
        public Matrix4x4 CascadeViewProjection;
        public Vector4 CascadeCullRegion;
        public uint PartCount;
        public uint RequiredInstanceFlags;
        public uint RejectedInstanceFlags;
        public uint Pad0;
    }

    private readonly RhiDevice _device;
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly DirectionalShadowState _state;
    private readonly GpuWorkScheduler _workScheduler;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiShader _cullShader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiPipeline _cullPipeline;
    private RhiBuffer _drawCommandBuffer;
    private readonly Matrix4x4[] _candidateViewProjections =
        new Matrix4x4[DirectionalShadowState.CascadeCount];
    private readonly int[] _candidateSceneSignatures =
        new int[DirectionalShadowState.CascadeCount];
    private float _candidateMaximumCasterDisplacement;
    private readonly long[] _renderedFrames = new long[16];
    private readonly int[] _renderedCascadeCounts = new int[16];
    private int _nextCascadeToUpdate;

    public unsafe DirectionalShadowPass(
        RhiDevice device,
        string contentRoot,
        RasterSceneGpuCache sceneCache,
        DirectionalShadowState state,
        GpuWorkScheduler workScheduler,
        Renderer renderer)
    {
        Name = "Directional Shadows";
        _device = device;
        _sceneCache = sceneCache;
        _state = state;
        _workScheduler = workScheduler;

        string source = renderer.LoadShaderSource("shaders/shadow_depth.slang", contentRoot);
        _vertexShader = RhiShader.FromSource(
            device,
            source,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _fragmentShader = RhiShader.FromSource(
            device,
            source,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateDepthOnly(device, _vertexShader, _fragmentShader);
        _cullShader = RhiShader.FromSource(
            device,
            renderer.LoadShaderSource("shaders/shadow_cull.slang", contentRoot),
            "computeMain",
            RhiNative.ShaderStage.Compute,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _cullPipeline = RhiPipeline.CreateCompute(device, _cullShader);
        _drawCommandBuffer = RhiBuffer.Create(
            device,
            4096 * DrawIndirectCommandSizeBytes,
            RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        for (int cascadeIndex = 0;
             cascadeIndex < DirectionalShadowState.CascadeCount;
             ++cascadeIndex)
        {
            builder.Write(
                Renderer.GetDirectionalShadowMapHandle(cascadeIndex),
                ResourceState.DepthStencil);
        }
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        uint width = Math.Max(context.Width, 1);
        uint height = Math.Max(context.Height, 1);
        _sceneCache.Prepare(context.FrameNumber, (float)width / height, width, height);

        SceneFrameData frameData = _sceneCache.FrameData;
        if (frameData.Instances.Count == 0 ||
            frameData.Parts.Count == 0 ||
            !TryBuildCascadeMatrices(
                frameData,
                _candidateViewProjections,
                out Vector4 candidateSplits,
                out uint lightIndex,
                out _candidateMaximumCasterDisplacement))
        {
            _state.Parameters = Vector4.Zero;
            return;
        }

        _workScheduler.BeginFrame(context.FrameNumber);
        ComputeCascadeSceneSignatures(
            frameData,
            _candidateViewProjections,
            _candidateSceneSignatures);
        Span<int> dirtyCascades =
            stackalloc int[DirectionalShadowState.CascadeCount];
        int dirtyCascadeCount = CollectDirtyCascades(
            _candidateSceneSignatures,
            _candidateViewProjections,
            dirtyCascades);
        if (dirtyCascadeCount == 0)
            return;

        int scheduledCascadeCount = Math.Min(
            dirtyCascadeCount,
            _workScheduler.GetUnitAllowance(
                GpuWorkDomain.Shadows));
        if (!_workScheduler.TryAdmit(
                GpuWorkDomain.Shadows,
                scheduledCascadeCount))
            return;
        _workScheduler.Defer(
            GpuWorkDomain.Shadows,
            dirtyCascadeCount - scheduledCascadeCount);
        dirtyCascadeCount = scheduledCascadeCount;

        EnsureDrawCommandBuffer(
            (ulong)dirtyCascadeCount *
            (ulong)frameData.Parts.Count *
            DrawIndirectCommandSizeBytes);

        sink.BeginComputePass("Directional Shadow Culling");
        sink.BindPipeline(_cullPipeline);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(_drawCommandBuffer, 2);
        for (int workIndex = 0;
             workIndex < dirtyCascadeCount;
             ++workIndex)
        {
            int cascadeIndex = dirtyCascades[workIndex];
            ulong drawOffset = GetDrawCommandOffset(
                workIndex,
                frameData.Parts.Count);
            ShadowCullPushData cullPush = new()
            {
                Instances = _sceneCache.InstanceBuffer.DeviceAddress,
                Parts = _sceneCache.PartBuffer.DeviceAddress,
                DrawCommands =
                    _drawCommandBuffer.DeviceAddress + drawOffset,
                CascadeViewProjection =
                    _candidateViewProjections[cascadeIndex],
                CascadeCullRegion = BuildCascadeCullRegion(
                    frameData.Camera,
                    candidateSplits,
                    cascadeIndex,
                    _candidateMaximumCasterDisplacement),
                PartCount = (uint)frameData.Parts.Count,
            };
            sink.PushConstants(
                0,
                (uint)sizeof(ShadowCullPushData),
                (IntPtr)(&cullPush));
            sink.Dispatch(
                ((uint)frameData.Parts.Count + 63) / 64,
                1,
                1);
        }
        sink.EndComputePass();

        for (int workIndex = 0;
             workIndex < dirtyCascadeCount;
             ++workIndex)
        {
            int cascadeIndex = dirtyCascades[workIndex];
            ScenePushData push = _sceneCache.PushData;
            push.DirectionalShadowViewProj =
                _candidateViewProjections[cascadeIndex];
            sink.BeginDepthOnlyPass(
                _state.Textures[cascadeIndex],
                RhiNative.LoadOp.Clear);
            sink.SetViewport(
                0,
                0,
                DirectionalShadowState.PageSize,
                DirectionalShadowState.PageSize);
            sink.SetScissor(
                0,
                0,
                DirectionalShadowState.PageSize,
                DirectionalShadowState.PageSize);
            sink.BindPipeline(_pipeline);
            sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
            sink.UseBuffer(_sceneCache.PartBuffer, 1);
            sink.UseBuffer(_drawCommandBuffer, 1);
            foreach (var mesh in frameData.UniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }
            sink.PushConstants(
                0,
                (uint)sizeof(ScenePushData),
                (IntPtr)(&push));
            sink.DrawIndirect(
                _drawCommandBuffer,
                GetDrawCommandOffset(
                    workIndex,
                    frameData.Parts.Count),
                (uint)frameData.Parts.Count,
                (uint)DrawIndirectCommandSizeBytes);
            sink.EndPass();

            _state.ValidCascades[cascadeIndex] = true;
            _state.LastUpdatedFrames[cascadeIndex] =
                context.FrameNumber;
            _state.SceneSignatures[cascadeIndex] =
                _candidateSceneSignatures[cascadeIndex];
            _state.ViewProjections[cascadeIndex] =
                _candidateViewProjections[cascadeIndex];
            _state.Splits = SetComponent(
                _state.Splits,
                cascadeIndex,
                GetComponent(candidateSplits, cascadeIndex));
        }
        _state.Parameters = new Vector4(
            Array.TrueForAll(_state.ValidCascades, valid => valid)
                ? 1.0f
                : 0.0f,
            1.0f / DirectionalShadowState.PageSize,
            lightIndex,
            DirectionalShadowState.MaxDistance);
        int historyIndex = (int)(context.FrameNumber & 15);
        _renderedFrames[historyIndex] = context.FrameNumber;
        _renderedCascadeCounts[historyIndex] = dirtyCascadeCount;
        _nextCascadeToUpdate =
            (dirtyCascades[dirtyCascadeCount - 1] + 1) %
            DirectionalShadowState.CascadeCount;
    }

    public bool TryGetRenderedCascadeCount(long frameNumber, out int count)
    {
        int historyIndex = (int)(frameNumber & 15);
        count = _renderedCascadeCounts[historyIndex];
        return _renderedFrames[historyIndex] == frameNumber;
    }

    private int CollectDirtyCascades(
        int[] sceneSignatures,
        Matrix4x4[] candidates,
        Span<int> dirtyCascades)
    {
        int dirtyCascadeCount = 0;
        for (int cascadeOffset = 0;
             cascadeOffset < DirectionalShadowState.CascadeCount;
             ++cascadeOffset)
        {
            int cascadeIndex =
                (_nextCascadeToUpdate + cascadeOffset) %
                DirectionalShadowState.CascadeCount;
            bool dirty =
                !_state.ValidCascades[cascadeIndex] ||
                _state.SceneSignatures[cascadeIndex] !=
                    sceneSignatures[cascadeIndex] ||
                !MatrixNearlyEqual(
                    _state.ViewProjections[cascadeIndex],
                    candidates[cascadeIndex]);
            if (!dirty)
                continue;
            dirtyCascades[dirtyCascadeCount++] = cascadeIndex;
        }
        return dirtyCascadeCount;
    }

    private static bool MatrixNearlyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        const float epsilon = 1e-5f;
        return
            MathF.Abs(left.M11 - right.M11) <= epsilon &&
            MathF.Abs(left.M12 - right.M12) <= epsilon &&
            MathF.Abs(left.M13 - right.M13) <= epsilon &&
            MathF.Abs(left.M14 - right.M14) <= epsilon &&
            MathF.Abs(left.M21 - right.M21) <= epsilon &&
            MathF.Abs(left.M22 - right.M22) <= epsilon &&
            MathF.Abs(left.M23 - right.M23) <= epsilon &&
            MathF.Abs(left.M24 - right.M24) <= epsilon &&
            MathF.Abs(left.M31 - right.M31) <= epsilon &&
            MathF.Abs(left.M32 - right.M32) <= epsilon &&
            MathF.Abs(left.M33 - right.M33) <= epsilon &&
            MathF.Abs(left.M34 - right.M34) <= epsilon &&
            MathF.Abs(left.M41 - right.M41) <= epsilon &&
            MathF.Abs(left.M42 - right.M42) <= epsilon &&
            MathF.Abs(left.M43 - right.M43) <= epsilon &&
            MathF.Abs(left.M44 - right.M44) <= epsilon;
    }

    private static void ComputeCascadeSceneSignatures(
        SceneFrameData frameData,
        Matrix4x4[] cascadeViewProjections,
        int[] signatures)
    {
        Parallel.For(
            0,
            DirectionalShadowState.CascadeCount,
            cascadeIndex =>
        {
            var hash = new HashCode();
            int overlappingInstanceCount = 0;
            foreach (InstanceData instance in frameData.Instances)
            {
                if (!IntersectsClipVolume(
                        instance,
                        cascadeViewProjections[cascadeIndex]))
                {
                    continue;
                }

                hash.Add(instance.ModelMatrix);
                hash.Add(instance.EntityIdLow);
                hash.Add(instance.EntityIdHigh);
                overlappingInstanceCount++;
            }
            hash.Add(overlappingInstanceCount);
            signatures[cascadeIndex] = hash.ToHashCode();
        });
    }

    private static bool IntersectsClipVolume(
        InstanceData instance,
        Matrix4x4 viewProjection)
    {
        Vector3 localMin = new(
            instance.AabbMin.X,
            instance.AabbMin.Y,
            instance.AabbMin.Z);
        Vector3 localMax = new(
            instance.AabbMax.X,
            instance.AabbMax.Y,
            instance.AabbMax.Z);
        bool outsideLeft = true;
        bool outsideRight = true;
        bool outsideBottom = true;
        bool outsideTop = true;
        bool outsideNear = true;
        bool outsideFar = true;
        for (int cornerIndex = 0; cornerIndex < 8; ++cornerIndex)
        {
            Vector3 localCorner = new(
                (cornerIndex & 1) == 0 ? localMin.X : localMax.X,
                (cornerIndex & 2) == 0 ? localMin.Y : localMax.Y,
                (cornerIndex & 4) == 0 ? localMin.Z : localMax.Z);
            Vector4 worldCorner = Vector4.Transform(
                new Vector4(localCorner, 1.0f),
                instance.ModelMatrix);
            Vector4 clipCorner = Vector4.Transform(
                worldCorner,
                viewProjection);
            outsideLeft &= clipCorner.X < -clipCorner.W;
            outsideRight &= clipCorner.X > clipCorner.W;
            outsideBottom &= clipCorner.Y < -clipCorner.W;
            outsideTop &= clipCorner.Y > clipCorner.W;
            outsideNear &= clipCorner.Z < 0.0f;
            outsideFar &= clipCorner.Z > clipCorner.W;
        }

        return !(outsideLeft ||
            outsideRight ||
            outsideBottom ||
            outsideTop ||
            outsideNear ||
            outsideFar);
    }

    private static float GetComponent(Vector4 value, int index)
        => index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => value.W,
        };

    private static Vector4 SetComponent(Vector4 value, int index, float component)
        => index switch
        {
            0 => value with { X = component },
            1 => value with { Y = component },
            2 => value with { Z = component },
            _ => value with { W = component },
        };

    private bool TryBuildCascadeMatrices(
        SceneFrameData frameData,
        Matrix4x4[] viewProjections,
        out Vector4 splits,
        out uint shadowLightIndex,
        out float maximumCasterDisplacement)
    {
        splits = Vector4.Zero;
        shadowLightIndex = 0;
        maximumCasterDisplacement = 0.0f;

        LightData shadowLight = default;
        bool found = false;
        for (int i = 0; i < frameData.Lights.Count; ++i)
        {
            LightData candidate = frameData.Lights[i];
            if (candidate.Direction.W == 0.0f && candidate.ShapeParams.W >= 0.5f)
            {
                shadowLight = candidate;
                shadowLightIndex = (uint)i;
                found = true;
                break;
            }
        }
        if (!found)
            return false;

        Vector3 cameraPosition = new(
            frameData.Camera.CameraPosition.X,
            frameData.Camera.CameraPosition.Y,
            frameData.Camera.CameraPosition.Z);
        ReadOnlySpan<float> cascadeRadii =
            stackalloc float[DirectionalShadowState.CascadeCount]
            {
                5.0f,
                25.0f,
                125.0f,
                DirectionalShadowState.MaxDistance,
            };
        splits = new Vector4(
            cascadeRadii[0],
            cascadeRadii[1],
            cascadeRadii[2],
            cascadeRadii[3]);

        GetSceneHeightBounds(
            frameData,
            out float sceneMinimumY,
            out float sceneMaximumY);
        if (!float.IsFinite(_state.ProjectionCenterY))
        {
            _state.ProjectionCenterY =
                (sceneMinimumY + sceneMaximumY) * 0.5f;
        }

        Vector3 lightToScene = Vector3.Normalize(new Vector3(
            shadowLight.Direction.X,
            shadowLight.Direction.Y,
            shadowLight.Direction.Z));
        float verticalDirection = MathF.Abs(lightToScene.Y);
        if (verticalDirection > 1e-4f)
        {
            float horizontalDirection = new Vector2(
                lightToScene.X,
                lightToScene.Z).Length();
            maximumCasterDisplacement =
                (sceneMaximumY - sceneMinimumY) *
                horizontalDirection /
                verticalDirection;
        }
        else
        {
            maximumCasterDisplacement = float.PositiveInfinity;
        }
        Vector3 referenceUp =
            MathF.Abs(Vector3.Dot(lightToScene, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitX
                : Vector3.UnitY;
        Vector3 lightRight = Vector3.Normalize(Vector3.Cross(referenceUp, lightToScene));
        Vector3 lightUp = Vector3.Normalize(Vector3.Cross(lightToScene, lightRight));

        Span<Vector3> cascadeCorners = stackalloc Vector3[8];
        for (int cascadeIndex = 0;
             cascadeIndex < DirectionalShadowState.CascadeCount;
             ++cascadeIndex)
        {
            BuildHorizontalCascadeCorners(
                cameraPosition,
                cascadeRadii[cascadeIndex],
                sceneMinimumY,
                sceneMaximumY,
                cascadeCorners);

            viewProjections[cascadeIndex] = BuildStableMatrix(
                cascadeCorners,
                lightToScene,
                lightRight,
                lightUp,
                _state.ProjectionCenterY,
                ref _state.ProjectionRadii[cascadeIndex]);
        }
        return true;
    }

    private static void GetSceneHeightBounds(
        SceneFrameData frameData,
        out float minimumY,
        out float maximumY)
    {
        minimumY = float.PositiveInfinity;
        maximumY = float.NegativeInfinity;
        foreach (InstanceData instance in frameData.Instances)
        {
            Vector3 localMin = new(
                instance.AabbMin.X,
                instance.AabbMin.Y,
                instance.AabbMin.Z);
            Vector3 localMax = new(
                instance.AabbMax.X,
                instance.AabbMax.Y,
                instance.AabbMax.Z);
            for (int cornerIndex = 0; cornerIndex < 8; ++cornerIndex)
            {
                Vector3 corner = new(
                    (cornerIndex & 1) == 0 ? localMin.X : localMax.X,
                    (cornerIndex & 2) == 0 ? localMin.Y : localMax.Y,
                    (cornerIndex & 4) == 0 ? localMin.Z : localMax.Z);
                float worldY = Vector3.Transform(
                    corner,
                    instance.ModelMatrix).Y;
                minimumY = MathF.Min(minimumY, worldY);
                maximumY = MathF.Max(maximumY, worldY);
            }
        }

        if (!float.IsFinite(minimumY) || !float.IsFinite(maximumY))
        {
            minimumY = -DirectionalShadowState.MaxDistance;
            maximumY = DirectionalShadowState.MaxDistance;
        }
        float heightPadding = MathF.Max(
            2.0f,
            (maximumY - minimumY) * 0.02f);
        minimumY -= heightPadding;
        maximumY += heightPadding;
    }

    private static void BuildHorizontalCascadeCorners(
        Vector3 cameraPosition,
        float radius,
        float minimumY,
        float maximumY,
        Span<Vector3> corners)
    {
        int cornerIndex = 0;
        for (int y = 0; y < 2; ++y)
        {
            for (int z = -1; z <= 1; z += 2)
            {
                for (int x = -1; x <= 1; x += 2)
                {
                    corners[cornerIndex++] = new Vector3(
                        cameraPosition.X + x * radius,
                        y == 0 ? minimumY : maximumY,
                        cameraPosition.Z + z * radius);
                }
            }
        }
    }

    private static Matrix4x4 BuildStableMatrix(
        ReadOnlySpan<Vector3> corners,
        Vector3 lightToScene,
        Vector3 lightRight,
        Vector3 lightUp,
        float projectionCenterY,
        ref float stableRadius)
    {
        Vector3 center = Vector3.Zero;
        foreach (Vector3 corner in corners)
            center += corner;
        center /= corners.Length;
        center.Y = projectionCenterY;

        float requiredRadius = 1.0f;
        foreach (Vector3 corner in corners)
        {
            requiredRadius = MathF.Max(
                requiredRadius,
                Vector3.Distance(center, corner));
        }
        requiredRadius =
            MathF.Ceiling(requiredRadius * 16.0f) / 16.0f;
        if (stableRadius <= 0.0f)
        {
            stableRadius = requiredRadius;
        }
        else if (requiredRadius > stableRadius)
        {
            stableRadius = MathF.Ceiling(
                MathF.Max(requiredRadius, stableRadius * 1.25f) *
                16.0f) / 16.0f;
        }
        float radius = stableRadius;

        float worldUnitsPerTexel =
            (2.0f * radius) / DirectionalShadowState.PageSize;
        float rightOffset =
            MathF.Round(
                Vector3.Dot(center, lightRight) /
                worldUnitsPerTexel) *
            worldUnitsPerTexel;
        float upOffset =
            MathF.Round(
                Vector3.Dot(center, lightUp) /
                worldUnitsPerTexel) *
            worldUnitsPerTexel;
        center += lightRight *
            (rightOffset - Vector3.Dot(center, lightRight));
        center += lightUp *
            (upOffset - Vector3.Dot(center, lightUp));

        Vector3 eye = center - lightToScene * (radius * 2.0f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, lightUp);
        Matrix4x4 projection = Matrix4x4.CreateOrthographicOffCenter(
            -radius,
            radius,
            -radius,
            radius,
            0.1f,
            radius * 4.0f);
        return view * projection;
    }

    private static Vector4 BuildCascadeCullRegion(
        CameraData camera,
        Vector4 splits,
        int cascadeIndex,
        float maximumCasterDisplacement)
    {
        if (cascadeIndex == 0)
            return new Vector4(
                camera.CameraPosition.X,
                camera.CameraPosition.Z,
                0.0f,
                maximumCasterDisplacement);

        float innerRadius = GetComponent(splits, cascadeIndex - 1);
        float innerStart = cascadeIndex == 1
            ? 0.0f
            : GetComponent(splits, cascadeIndex - 2);
        float blendWidth = MathF.Max(
            (innerRadius - innerStart) * 0.03f,
            0.5f);
        return new Vector4(
            camera.CameraPosition.X,
            camera.CameraPosition.Z,
            MathF.Max(innerRadius - blendWidth, 0.0f),
            maximumCasterDisplacement);
    }

    private void EnsureDrawCommandBuffer(ulong requiredSize)
    {
        if (_drawCommandBuffer.Size >= requiredSize)
            return;

        ulong newSize = _drawCommandBuffer.Size;
        while (newSize < requiredSize)
            newSize *= 2;
        _drawCommandBuffer.Dispose();
        _drawCommandBuffer = RhiBuffer.Create(
            _device,
            newSize,
            RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
    }

    internal static ulong GetDrawCommandOffset(
        int workIndex,
        int partCount)
        => checked(
            (ulong)workIndex *
            (ulong)partCount *
            DrawIndirectCommandSizeBytes);

    public void Dispose()
    {
        _drawCommandBuffer.Dispose();
        _cullPipeline.Dispose();
        _cullShader.Dispose();
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
        _state.Dispose();
    }
}
