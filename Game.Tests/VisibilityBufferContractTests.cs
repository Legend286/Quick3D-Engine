// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies the visibility-buffer storage and raster ABI.</summary>
public sealed class VisibilityBufferContractTests
{
    [Fact]
    public void GraphicsPipelineDescriptor_AppendsMrtFields()
    {
        Assert.Equal(
            52,
            Marshal.OffsetOf<RhiNative.GraphicsPipelineDesc>(
                    nameof(RhiNative.GraphicsPipelineDesc.ColorFormat1))
                .ToInt32());
        Assert.Equal(
            64,
            Marshal.OffsetOf<RhiNative.GraphicsPipelineDesc>(
                    nameof(RhiNative.GraphicsPipelineDesc.ColorAttachmentCount))
                .ToInt32());
        Assert.Equal(72, Marshal.SizeOf<RhiNative.GraphicsPipelineDesc>());
    }

    [Fact]
    public void VisibilityFormats_HaveExpectedStorageWidths()
    {
        Assert.Equal(7, (int)RhiNative.TextureFormat.Rg16Unorm);
        Assert.Equal(8, (int)RhiNative.TextureFormat.Rg32Uint);
        Assert.Equal(
            8u,
            RhiTexture.GetUncompressedBytesPerPixel(
                RhiNative.TextureFormat.Rg32Uint));
        Assert.Equal(
            4u,
            RhiTexture.GetUncompressedBytesPerPixel(
                RhiNative.TextureFormat.Rg16Unorm));

        string nativeHeader = ReadRepositoryFile(
            "engine_c",
            "rhi",
            "rhi.h");
        string metalBackend = ReadRepositoryFile(
            "engine_c",
            "rhi",
            "rhi_metal.mm");
        Assert.Contains("RHI_FORMAT_RG16_UNORM            = 7", nativeHeader);
        Assert.Contains("RHI_FORMAT_RG32_UINT             = 8", nativeHeader);
        Assert.Contains("MTLPixelFormatRG16Unorm", metalBackend);
        Assert.Contains("MTLPixelFormatRG32Uint", metalBackend);
        Assert.Contains("desc->color_attachment_count", metalBackend);
    }

    [Fact]
    public void VisibilityShader_StoresPartPrimitiveAndBarycentricXy()
    {
        string source = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_buffer.slang");
        string culling = ReadRepositoryFile(
            "Content",
            "shaders",
            "cull.slang");

        Assert.Contains("uint2 identifiers : SV_Target0", source);
        Assert.Contains("float2 barycentrics : SV_Target1", source);
        Assert.Contains("uint primitiveIndex : SV_PrimitiveID", source);
        Assert.Contains("float3 barycentrics : SV_Barycentrics", source);
        Assert.Contains("PartData part = push.parts[rasterInstanceIndex]", source);
        Assert.Contains("part.instanceIdx", source);
        Assert.Contains("input.rasterInstanceIndex", source);
        Assert.Contains("primitiveIndex", source);
        Assert.Contains("saturate(barycentrics.xy)", source);
        Assert.Contains("cmd.baseInstance = partIdx", culling);
    }

    [Fact]
    public void VisibilityPass_UsesMrtAndSharedIndirectCommands()
    {
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityBufferPass.cs");
        string plugin = ReadRepositoryFile(
            "Plugins",
            "Renderer.Clustered",
            "ClusteredRendererPlugin.cs");
        string renderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs");
        string gameRenderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GameRenderer.cs");

        Assert.Contains("RhiPipeline.CreateGraphicsMrt", pass);
        Assert.Contains("LoadShaderSource(", pass);
        Assert.Contains("foreach (string includeDir in includeDirs)", pass);
        Assert.Contains("RhiNative.TextureFormat.Rg32Uint", pass);
        Assert.Contains("RhiNative.TextureFormat.Rg16Unorm", pass);
        Assert.Contains("VisibilityIdentifiersHandle", pass);
        Assert.Contains("VisibilityBarycentricsHandle", pass);
        Assert.Contains("_owner.EnsurePrepared(sink, context)", pass);
        Assert.Contains("_owner.DrawCommandBuffer", pass);
        Assert.Contains("sink.DrawIndirect(", pass);
        Assert.Contains("context.EnableVisibilityBuffer", plugin);
        Assert.Contains("CreateVisibilityBufferPass()", plugin);
        Assert.Contains("CreateComputePass()", plugin);
        Assert.Contains("DirectLightLoopThreshold = 8", ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "PbrPass.cs"));
        Assert.Contains("EnsureVisibilityBufferResources()", renderer);
        Assert.Contains("_visibilityIdentifiersTexture", renderer);
        Assert.Contains("_visibilityBarycentricsTexture", renderer);
        Assert.Contains("enableVisibilityBuffer: false", gameRenderer);
    }

    [Fact]
    public void ClusteredSpotLights_UseConservativeConeBounds()
    {
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "cluster_lights.slang");

        Assert.Contains("float capRadius = light.position.w * tanTheta", shader);
        Assert.Contains("float boundsRadius = sqrt(", shader);
        Assert.Contains("halfRange * halfRange + capRadius * capRadius", shader);
        Assert.DoesNotContain(
            "intersects = FiniteConeIntersectsCluster",
            shader);
    }

    [Fact]
    public void PbrShadowBias_UsesInterpolatedVertexNormal()
    {
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "pbr.slang");

        Assert.Contains("float3 geometricNormal,", shader);
        Assert.Contains(
            "EvaluateDirectionalShadow(\n            worldPos,\n            geometricNormal,",
            shader);
        Assert.Contains(
            "worldPos,\n            geometricNormal,\n            L);",
            shader);
        Assert.Contains(
            "dmat,\n            N,\n            vertexNormal,",
            shader);
        Assert.DoesNotContain(
            "visibility = EvaluateDirectionalShadow(worldPos, N, L)",
            shader);
    }

    [Fact]
    public void VisibilityDebugView_ReadsBothAttachmentsAndDepth()
    {
        Assert.Equal(13, (int)ViewportDebugView.VisibilityBuffer);
        Assert.Equal(14, (int)ViewportDebugView.ReconstructedPosition);
        Assert.Equal(15, (int)ViewportDebugView.ReconstructedNormal);
        Assert.Equal(16, (int)ViewportDebugView.ReconstructedUv);
        Assert.Equal(17, (int)ViewportDebugView.ReconstructedMaterial);
        Assert.Equal(18, (int)ViewportDebugView.ReconstructedInstance);
        Assert.Equal(19, (int)ViewportDebugView.ReconstructedTangent);
        Assert.Equal(20, (int)ViewportDebugView.VisibilityPbr);
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityBufferDebugPass.cs");
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_buffer_debug.slang");
        string common = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_debug_common.slang");
        string viewModel = ReadRepositoryFile(
            "Editor",
            "ViewModels",
            "ViewportPanelViewModel.cs");
        string pathTracer = ReadRepositoryFile(
            "Content",
            "shaders",
            "path_tracer.slang");

        Assert.Contains("ViewportDebugView.VisibilityBuffer", pass);
        Assert.Contains("VisibilityIdentifiersHandle", pass);
        Assert.Contains("VisibilityBarycentricsHandle", pass);
        Assert.Contains("DepthBufferHandle", pass);
        Assert.Contains("sink.BindTexture(0, identifiers)", pass);
        Assert.Contains("sink.BindTexture(1, barycentrics)", pass);
        Assert.Contains("sink.BindTexture(2, depth)", pass);
        Assert.Contains("sink.BindTexture(3, reconstruction)", pass);
        Assert.Contains("sink.BindTexture(4, reconstruction)", pass);
        Assert.DoesNotContain("VisibilityReferenceHandle", pass);
        Assert.Contains("Texture2D<uint2> visibilityIdentifiers", shader);
        Assert.Contains("Texture2D<float2> visibilityBarycentrics", shader);
        Assert.Contains("Texture2D<float> sceneDepth", shader);
        Assert.Contains("Texture2D<float4> referenceOutput", shader);
        Assert.Contains("1.0 - barycentricXY.x - barycentricXY.y", shader);
        Assert.Contains("pixel.x < dimensions.x / 2u", shader);
        Assert.Contains("reference - reconstructed", shader);
        Assert.Contains("ErrorHeatMap(error)", shader);
        Assert.Contains("float3 ErrorHeatMap(float value)", shader);
        Assert.Contains("push.mode >= 14u && push.mode <= 19u", shader);
        Assert.Contains("push.mode == 20u", shader);
        Assert.DoesNotContain("ReconstructionErrorAmplification", shader);
        Assert.Contains("pixelX < width / 2u", common);
        Assert.Contains("identifiers.x", common);
        Assert.Contains("identifiers.y", common);
        Assert.Contains("VisibilityIdentifierColor", common);
        Assert.Contains("identifiers.x ^ 0x9e3779b9u", common);
        Assert.Contains("identifiers.y ^ 0x85ebca6bu", common);
        Assert.Contains("float saturation =", common);
        Assert.Contains("\"Visibility Buffer\" =>", viewModel);
        Assert.Contains("debugView == 13u", pathTracer);
        Assert.Contains("VisibilityDebugColor(", pathTracer);
        Assert.Contains("debugView == 19u", pathTracer);
    }

    [Fact]
    public void VisibilityReconstruction_UsesEightByEightComputeTiles()
    {
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_reconstruct.slang");
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityReconstructionPass.cs");
        string plugin = ReadRepositoryFile(
            "Plugins",
            "Renderer.Clustered",
            "ClusteredRendererPlugin.cs");
        string renderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs");
        string sceneData = ReadRepositoryFile(
            "Content",
            "shaders",
            "scene_data.slang");
        string visibility = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_buffer.slang");
        string pbr = ReadRepositoryFile(
            "Content",
            "shaders",
            "pbr.slang");

        Assert.Contains("[numthreads(8, 8, 1)]", shader);
        Assert.Contains("RWTexture2D<float4> reconstructionOutput", shader);
        Assert.Contains("visibilityIdentifiers : register(t4)", shader);
        Assert.Contains("visibilityBarycentrics : register(t5)", shader);
        Assert.Contains("sceneDepth : register(t6)", shader);
        Assert.Contains("[[vk::binding(0, 0)]]\nRWTexture2D<float4>", shader);
        Assert.Contains("reconstructionOutput : register(u0)", shader);
        Assert.Contains("LoadPartVertexIndex(part, triangleOffset)", shader);
        Assert.Contains("part.localOffset.xyz", shader);
        Assert.Contains("instance.modelMatrix", shader);
        Assert.Contains("debugView == 14u", shader);
        Assert.Contains("debugView == 19u", shader);
        Assert.Contains("ThreadGroupSize = 8", pass);
        Assert.Contains("IsReconstructionView", pass);
        Assert.Contains("sink.Dispatch(", pass);
        Assert.Contains("sink.BindTexture(0, output)", pass);
        Assert.Contains("sink.BindHeap(1, _owner.BindlessHeap)", pass);
        Assert.Contains("SampleGrad(textureSampler, uv, uvDx, uvDy)", shader);
        Assert.Contains("material.normalTexIndex", shader);
        Assert.DoesNotContain(
            "CreateVisibilityReconstructionPass()",
            plugin);
        Assert.Contains("VisibilityReconstructionHandle", renderer);
        Assert.Contains("RhiTexture.CreateStorage(", renderer);
        Assert.Contains("LoadPartVertexIndex", sceneData);
        Assert.Contains("LoadPartVertexIndex(part, vertexIndex)", visibility);
        Assert.Contains("LoadPartVertexIndex(part, vid)", pbr);
        Assert.Contains(
            "LoadPartVertexIndex(part, vid)",
            ReadRepositoryFile("Content", "shaders", "shadow_depth.slang"));
        Assert.Contains(
            "LoadPartVertexIndex(part, vertexId)",
            ReadRepositoryFile(
                "Content",
                "shaders",
                "outline_selection_depth.slang"));
        string picking = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityPickingPass.cs");
        Assert.Contains("VisibilityIdentifiersHandle", picking);
        Assert.Contains("CopyTextureToBuffer", picking);
        Assert.Contains("ReadMapped<VisibilityIdentifiers>", picking);
    }

    [Fact]
    public void VisibilityReference_MatchesForwardTangentSpacePreparation()
    {
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityReferencePass.cs");
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_reference.slang");
        string plugin = ReadRepositoryFile(
            "Plugins",
            "Renderer.Clustered",
            "ClusteredRendererPlugin.cs");
        string renderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs");

        Assert.Contains("RhiNative.TextureFormat.Rgba16Float", pass);
        Assert.Contains("enableDepthWrite: false", pass);
        Assert.Contains("RhiNative.LoadOp.Load", pass);
        Assert.Contains("_owner.DrawCommandBuffer", pass);
        Assert.Contains("sink.BindHeap(1, _owner.BindlessHeap)", pass);
        Assert.Contains("BuildReferenceTangentFrame", shader);
        Assert.Contains("material.normalTexIndex", shader);
        Assert.Contains(".Sample(textureSampler, input.uv)", shader);
        Assert.Contains("float3x3(tangent, bitangent, normal)", shader);
        Assert.DoesNotContain("CreateVisibilityReferencePass()", plugin);
        Assert.Contains("VisibilityReferenceHandle", renderer);
        Assert.Contains("_visibilityReferenceTexture", renderer);
        Assert.Contains("bool referenceRequired = false", renderer);
        Assert.Contains("else if (!referenceRequired)", renderer);
    }

    [Fact]
    public void VisibilityPbr_DeduplicatesClusterLightsPerComputeTile()
    {
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_shade.slang");
        string pbr = ReadRepositoryFile(
            "Content",
            "shaders",
            "pbr.slang");
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityShadingPass.cs");
        string plugin = ReadRepositoryFile(
            "Plugins",
            "Renderer.Clustered",
            "ClusteredRendererPlugin.cs");
        string debugShader = ReadRepositoryFile(
            "Content",
            "shaders",
            "visibility_buffer_debug.slang");
        string viewModel = ReadRepositoryFile(
            "Editor",
            "ViewModels",
            "ViewportPanelViewModel.cs");
        string renderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs");
        string pbrPass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "PbrPass.cs");
        string visibilityPass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityBufferPass.cs");
        string referencePass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "VisibilityReferencePass.cs");

        Assert.Contains("[numthreads(8, 8, 1)]", shader);
        Assert.Contains("visibilityIdentifiers : register(t4)", shader);
        Assert.Contains("visibilityBarycentrics : register(t5)", shader);
        Assert.Contains("sceneDepth : register(t6)", shader);
        Assert.Contains("[[vk::binding(0, 0)]]\nRWTexture2D<float4>", shader);
        Assert.Contains("visibilityShadingOutput : register(u0)", shader);
        Assert.Contains("sink.BindTexture(0, output)", pass);
        Assert.Contains("groupshared uint g_tileDepthSliceMask", shader);
        Assert.Contains("groupshared uint g_tileHasGeometry", shader);
        Assert.Contains("groupshared uint g_tileLightHash", shader);
        Assert.Contains("kTileLightCapacity = 1024u", shader);
        Assert.Contains("InterlockedCompareExchange", shader);
        Assert.True(
            shader.IndexOf(
                "if (g_tileHasGeometry == 0u)",
                StringComparison.Ordinal) <
            shader.IndexOf(
                "for (uint hashIndex = lane;",
                StringComparison.Ordinal));
        Assert.Contains("ClusterRecord record", shader);
        Assert.Contains("uint directLightCount = min(", shader);
        Assert.Contains("VisibilityTileLightCount", shader);
        Assert.Contains("if (useClusteredLights)", pbrPass);
        Assert.Contains("ShadePbrSurface(", shader);
        Assert.Contains("EvaluateVisibilitySky", shader);
        Assert.Contains("#ifdef VISIBILITY_COMPUTE", pbr);
        Assert.Contains("SampleGrad(", pbr);
        Assert.Contains("ThreadGroupSize = 8", pass);
        Assert.Contains("_owner.SetupShadingReads(builder)", pass);
        Assert.Contains("_owner.BindShadingResources(sink)", pass);
        Assert.Contains("sink.Dispatch(", pass);
        Assert.Contains("CreateVisibilityShadingPass()", plugin);
        Assert.Contains("push.mode == 20u", debugShader);
        Assert.Contains("length(reference - reconstructed) * 8.0", debugShader);
        Assert.Contains("ErrorHeatMap(error)", debugShader);
        Assert.DoesNotContain("float3 compared = lerp", debugShader);
        Assert.Contains("if (debugView == 20u)\n        ao = 1.0;", pbr);
        Assert.DoesNotContain("if (!visibilityView &&", visibilityPass);
        Assert.Contains("if (_useVisibilityOpaque)\n            return;", pbrPass);
        Assert.Contains(
            "view != ViewportDebugView.VisibilityBuffer",
            pass);
        Assert.Contains(
            "!VisibilityReconstructionPass.IsReconstructionView(view)",
            pass);
        Assert.Contains("IsComparisonView(debugView)", referencePass);
        Assert.Contains("if (push.mode <= 12u)", debugShader);
        Assert.True(
            renderer.IndexOf(
                "new VisibilityBufferDebugPass(",
                StringComparison.Ordinal) <
            renderer.IndexOf(
                "new OutlineSelectionDepthPass(",
                StringComparison.Ordinal));
        Assert.DoesNotContain("\"Visibility PBR\",", viewModel);
        Assert.DoesNotContain("\"Reconstructed UV\",", viewModel);
        Assert.Contains("\"Visibility Buffer Renderer\"", viewModel);
        Assert.DoesNotContain(
            "\"Clustered Forward Renderer\"",
            viewModel);
        Assert.Contains("!DebugViews.Contains(value)", viewModel);
        Assert.Contains("DebugViews.Contains(label)", viewModel);
    }

    [Fact]
    public void Outline_UsesInstanceIdsAndSelectedDepth()
    {
        string depthPass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "OutlineSelectionDepthPass.cs");
        string compositePass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "OutlineCompositePass.cs");
        string shader = ReadRepositoryFile(
            "Content",
            "shaders",
            "outline_composite.slang");

        Assert.Contains("FindSelectedInstance", depthPass);
        Assert.Contains("instance.FirstPartIndex", depthPass);
        Assert.Contains("TryGetSelectionScissor", depthPass);
        Assert.Contains("OutlineSelectionDepthHandle", depthPass);
        Assert.Contains("VisibilityIdentifiersHandle", compositePass);
        Assert.Contains("DepthBufferHandle", compositePass);
        Assert.Contains("OutlineSelectionDepthHandle", compositePass);
        Assert.Contains("VisibilityMatchesSelection", shader);
        Assert.Contains("SelectionIsOccluded", shader);
        Assert.Contains(
            "visibilityIdentifiers : register(t0)",
            shader);
        Assert.Contains("sceneDepth : register(t1)", shader);
        Assert.Contains("selectionDepth : register(t2)", shader);
        Assert.Contains("sink.BindTexture(0, identifiers)", compositePass);
        Assert.Contains("sink.BindTexture(1, sceneDepth)", compositePass);
        Assert.Contains("sink.BindTexture(2, selectionDepth)", compositePass);
        Assert.DoesNotContain("new OutlineMaskPass(", ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs"));
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        for (int depth = 0; depth < 10; ++depth)
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException(
            $"Repository file '{Path.Combine(parts)}' was not found.");
    }
}
