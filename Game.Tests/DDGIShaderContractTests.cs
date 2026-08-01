// SPDX-License-Identifier: MIT

using Xunit;

namespace Engine.Game.Tests;

public sealed class DDGIShaderContractTests
{
    private static string ResolveShader(string fileName)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "shaders", fileName);
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException(
            $"DDGI shader fixture '{fileName}' was not found.");
    }

    [Fact]
    public void Placement_UsesStableAdaptiveGpuResidency()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_probe_placement.slang"));

        Assert.DoesNotContain("NodeIntersectsScene", source);
        Assert.DoesNotContain("AabbIntersectsAabb", source);
        Assert.Contains("push.useSceneTlas == 0u", source);
        Assert.Contains("push.probeRequests[requestIndex]", source);
        Assert.Contains("push.probePositions[slot]", source);
        Assert.Contains(
            "push.gridToProbeIndex[request.gridCellIndex]",
            source);
        Assert.Contains("push.probeStates[slot]", source);
        Assert.Contains("push.volumeState[0]", source);
        Assert.Contains("push.volumeState[0] = 0x44444749u", source);
        Assert.Contains("maxRelocationClassifications", source);
        Assert.Contains("pendingClassification", source);
        Assert.Contains("geometryRevision", source);
        Assert.Contains("sceneBake", source);
        Assert.Contains("active = push.useSceneTlas != 0u", source);
        Assert.Contains("push.useSceneTlas != 0u", source);
        Assert.Contains("geometryNear", source);
        Assert.Contains("directions[14]", source);
        Assert.DoesNotContain("CommittedTriangleFrontFace", source);
        Assert.Contains("bool needsRelocation", source);
        Assert.Contains("awayPosition", source);
        Assert.Contains("throughPosition", source);
        Assert.Contains("OverlapsMeshPartBounds", source);
        Assert.Contains("instance.firstPartIndex", source);
        Assert.Contains("part.localOffset.xyz", source);
        Assert.Contains("awayGeometryDistance >= clearance * 0.75", source);
        Assert.Contains("throughGeometryDistance >= clearance * 0.75", source);
        Assert.Contains("awayValid || throughValid", source);
        Assert.Contains("flags |= relocated ? 16u : 0u", source);
        Assert.Contains("boundsPriority = geometryNear && overlapsMeshBounds", source);
        Assert.Contains("skyOnly = !sceneBake && !geometryNear", source);
        Assert.Contains("flags |= boundsPriority ? 64u : 0u", source);
        Assert.Contains("flags |= skyOnly ? 128u : 0u", source);
        Assert.Contains("retainedSkyHistory", source);
        Assert.Contains("kInitialRadianceAccumulation = 1u << 13u", source);
        Assert.Contains("kRadianceConvergenceMask |", source);
        Assert.Contains("kInitialRadianceAccumulation);", source);
        Assert.DoesNotContain("MatchesDensityStride", source);
        Assert.Contains("clipmapLevelCount", source);
        Assert.Contains("RegisterProbeKey", source);
        Assert.Contains("push.probeWorldKeys[slot]", source);
        Assert.Contains("push.worldProbeHash[hashIndex]", source);
        Assert.Contains("(!needsClassification || retainedSkyHistory)", source);
        Assert.DoesNotContain("push.probeDrawArgs", source);

        string cache = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIWorldProbeCache.cs");
        Assert.Contains("Dictionary<ProbeKey, int>", cache);
        Assert.Contains("AddActiveClipmaps(cameraPosition)", cache);
        Assert.Contains("AddSceneBakeBatch(Math.Clamp", cache);
        Assert.Contains(
            "if (canClassifySceneBake && sceneBakeRequestBudget > 0)",
            cache);
        Assert.Contains("MinimumSceneBakeRequestsPerFrame = 1", cache);
        Assert.Contains("DefaultSceneBakeRequestsPerFrame = 16", cache);
        Assert.Contains("MaxSceneBakeRequestsPerFrame = 64", cache);
        Assert.DoesNotContain("_slots.Remove", cache);

        string reset = File.ReadAllText(
            ResolveShader("ddgi_probe_reset.slang"));
        Assert.Contains(
            "push.probeDrawArgs[0] = push.probeCapacity * 24u",
            reset);
    }

    [Fact]
    public void Schedule_SelectsUpdatesWithoutCpuProbeState()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_probe_schedule.slang"));
        string pluginSource = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIRendererPlugin.cs");

        Assert.Contains("push.probeUpdateQueue[updateCount++]", source);
        Assert.Contains("push.probeCounter[1] = updateCount", source);
        Assert.Contains("push.probeRequests[candidateIndex]", source);
        Assert.Contains("state.lastUpdateFrame", source);
        Assert.Contains(
            "if (ready && !dirty && !lightingDirty && !converging)",
            source);
        Assert.Contains("sceneBake && !ready", source);
        Assert.Contains("uint selected[128]", source);
        Assert.Contains("min(push.maxUpdates, 128u)", source);
        Assert.Contains("fineLevelWeight", source);
        Assert.Contains("lightingDirty", source);
        Assert.Contains("push.requestCount + push.persistentCount", source);
        Assert.Contains("push.persistentStart + persistentOffset", source);
        Assert.Contains("push.allocatedProbeCount", source);
        Assert.Contains("boundsPriority ? 2500.0", source);
        Assert.Contains("skyOnly && ready && !skyDirty", source);
        Assert.Contains("converging ? 8000.0", source);
        Assert.Contains("push.radianceRevision >> 16u", source);
        Assert.DoesNotContain("refreshInterval", source);
        Assert.DoesNotContain("baseRefresh", source);
        string schedulePass = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIProbeSchedulePass.cs");
        Assert.Contains("PersistentScanWindow = 8192", schedulePass);
        Assert.Contains("GetPersistentScanStart", schedulePass);
        Assert.Contains("AdvancePersistentScan", schedulePass);
        string updateSource = File.ReadAllText(
            ResolveShader("ddgi_probe_update.slang"));
        Assert.Contains("minimumProbeClearance", updateSource);
        Assert.Contains("nearestGeometryDistance", updateSource);
        Assert.Contains("state.flags |= 8u", updateSource);
        Assert.DoesNotContain("EvaluateFrameUpdates", pluginSource);
        Assert.DoesNotContain("RefreshProbeSnapshots", pluginSource);
        Assert.DoesNotContain("TryReadActiveProbeCount", pluginSource);
        Assert.DoesNotContain("UploadLightsSnapshot", pluginSource);
        Assert.Contains("context.SceneGpuDataProvider", pluginSource);
        Assert.Contains(
            "!ReferenceEquals(_atlasScene, context.Scene)",
            pluginSource);
        Assert.Contains("public bool HasPendingWork", pluginSource);
        Assert.Contains("_atlas.RadianceRefreshActive", pluginSource);
        string viewport = ReadRepositoryFile(
            "Editor", "ViewModels", "ViewportPanelViewModel.cs");
        Assert.Contains("_gameLoop.HasPendingRenderWork", viewport);
    }

    [Fact]
    public void Sampling_UsesGpuVolumeStateAndContributesToPbr()
    {
        string sampling = File.ReadAllText(
            ResolveShader("ddgi_sampling.slang"));
        string pbr = ReadRepositoryFile(
            "Content", "shaders", "pbr.slang");
        string pass = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "PbrPass.cs");

        Assert.Contains("push.DDGIVolumeState[0]", sampling);
        Assert.Contains("push.DDGIProbePositions[sparseIdx].w > 0.5", sampling);
        Assert.Contains("SamplePersistentLevel", sampling);
        Assert.Contains("LookupPersistentProbe", sampling);
        Assert.Contains(
            "float3 gridSpace = worldPosition / cellSize;",
            sampling);
        Assert.DoesNotContain(
            "worldPosition / cellSize - 0.5",
            sampling);
        Assert.Contains("push.DDGIProbeWorldKeys[probeIndex]", sampling);
        Assert.Contains("push.DDGIWorldProbeHash[hashIndex]", sampling);
        Assert.Contains(
            "combinedConfidence +=",
            sampling);
        Assert.DoesNotContain("gridOffset", sampling);
        Assert.Contains("AtlasCoordinate", sampling);
        Assert.Contains("kVisibilityTileResolution = 4u", sampling);
        Assert.Contains("kVisibilityTexelsPerProbe = 16u", sampling);
        Assert.Contains("OctahedralEncode", sampling);
        Assert.Contains("LoadDirectionalVisibility", sampling);
        Assert.Contains("worldPosition - probePos", sampling);
        Assert.Contains("EvaluateDDGIShading(", pbr);
        Assert.Contains("result.irradiance * albedo *", sampling);
        Assert.DoesNotContain(
            "result.irradiance * albedo * ambientOcclusion",
            sampling);
        Assert.DoesNotContain("SampleIndirectDiffuse(input.worldPos, N)", pbr);
        Assert.Contains("mat.occlusionTexIndex", pbr);
        Assert.Contains("(1.0 - metallic)", sampling);
        Assert.DoesNotContain("ao = rma.r", pbr);
        Assert.DoesNotContain("(1.0 - mat.metallic)", pbr);
        Assert.Contains(
            "ambient + Lo + indirectDiffuse + emissive",
            pbr);
        Assert.Contains(
            "ApplyDDGIAmbientPolicy(ambient, ddgiShading)",
            pbr);
        Assert.Contains("SparseLayoutReady()", sampling);
        Assert.Contains("ApplyDDGIConsumerDebug", sampling);
        Assert.Contains("EvaluateDDGILightingOnly", sampling);
        Assert.Contains(
            "color = ApplyDDGIConsumerDebug(color, ddgiShading)",
            pbr);
        Assert.DoesNotContain("debugView == 13u", pbr);
        Assert.Contains("_ddgiAtlas.ConsumerFlags", pass);
        Assert.Contains("lightingOnlyMaterial.baseColor = float3(1.0)", pbr);
        Assert.Contains("lightingOnlyLo +", pbr);
        Assert.DoesNotContain("Lo / max(albedo", pbr);
        Assert.Contains("pbrPush.DDGIVolumeState = volumeState.DeviceAddress", pass);

        string clustered = ReadRepositoryFile(
            "Plugins", "Renderer.Clustered", "ClusteredRendererPlugin.cs");
        Assert.Contains("var cliArgs = context.ShaderCliArgs", clustered);
        Assert.DoesNotContain("EnsureDdgiShaderFeature", clustered);
        Assert.DoesNotContain("result.Add(\"DDGI_PLUGIN=1\")", clustered);

        string pluginCatalog = ReadRepositoryFile(
            "Editor", "Services", "PluginCatalogService.cs");
        int contextPublication = pluginCatalog.IndexOf(
            "PublishActiveShaderContext();",
            pluginCatalog.IndexOf("internal void SetEnabled", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int pluginActivation = pluginCatalog.IndexOf(
            "LoadPlugin(plugin);",
            contextPublication,
            StringComparison.Ordinal);
        Assert.True(contextPublication >= 0);
        Assert.True(pluginActivation > contextPublication);

        string appBootstrap = ReadRepositoryFile(
            "Editor", "App.axaml.cs");
        string metalBackend = ReadRepositoryFile(
            "engine_c", "rhi", "rhi_metal.mm");
        Assert.Contains("File.Delete(slangDiagnostics)", appBootstrap);
        Assert.Contains(
            "fopen(\"out/logs/slang_diagnostics.txt\", \"a\")",
            metalBackend);
        Assert.Contains("entry: %s", metalBackend);
        Assert.Contains("stage: %s", metalBackend);
        Assert.Contains("include_directories (%zu)", metalBackend);
        Assert.Contains("compiler_arguments (%zu)", metalBackend);

        string cook = ReadRepositoryFile("Cook", "main.cpp");
        string materialLoader = ReadRepositoryFile(
            "engine_cs", "Engine.Assets", "MaterialLoader.cs");
        Assert.Contains("mat.occlusionTexture.index", cook);
        Assert.Contains("\\\"rma_contains_ao\\\"", cook);
        Assert.Contains("\\\"occlusion_texture\\\"", cook);
        Assert.Contains("HasNonZeroRedChannel", cook);
        Assert.Contains("shares_rma_occlusion_source", cook);
        Assert.Contains("using neutral AO", cook);
        Assert.Contains("public bool RmaContainsAo", materialLoader);
        Assert.DoesNotContain(
            "public bool RmaContainsAo { get; set; } = true",
            materialLoader);
        Assert.Contains("public RhiTexture? OcclusionTexture", materialLoader);
    }

    [Fact]
    public void ViewportDebugModes_AreExclusiveAndDoNotCaptureCameraKeys()
    {
        string viewModel = ReadRepositoryFile(
            "Editor", "ViewModels", "ViewportPanelViewModel.cs");
        string view = ReadRepositoryFile(
            "Editor", "Views", "ViewportPanelView.axaml");
        string viewCode = ReadRepositoryFile(
            "Editor", "Views", "ViewportPanelView.axaml.cs");
        string plugin = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIRendererPlugin.cs");
        string gameLoopContract = ReadRepositoryFile(
            "engine_cs", "Engine.RHI", "IGameLoop.cs");

        Assert.Contains("SetActiveDebugView(value)", viewModel);
        Assert.DoesNotContain("\"DDGI Indirect\"", viewModel);
        Assert.DoesNotContain("DdgiIndirect", gameLoopContract);
        Assert.Contains("\"DDGI Indirect\"", plugin);
        Assert.Contains("DDGIVolumeRegistry.ShowIndirect", plugin);
        Assert.DoesNotContain("keepOverlays", viewModel);
        Assert.Contains("x:Name=\"DebugViewButton\"", view);
        Assert.Contains("ItemsSource=\"{Binding DebugViewToggles}\"", view);
        Assert.Contains("Content=\"{Binding ToggleName}\"", view);
        Assert.True(CountOccurrences(view, "Focusable=\"False\"") >= 2);
        Assert.Equal(2, CountOccurrences(view, "IsTabStop=\"False\""));
        Assert.Contains("e.AddedItems.Count == 0", viewCode);
    }

    [Fact]
    public void Update_UsesSkyFallbackWhenTlasIsUnavailable()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_probe_update.slang"));

        Assert.Contains("push.UseSceneTlas != 0u", source);
        Assert.Contains("push.probeCounter[1]", source);
        Assert.Contains("push.probeUpdateQueue[probeOrdinal]", source);
        Assert.Contains("state.lastUpdateFrame = push.frameNumber", source);
        Assert.Contains("#include \"nishita_sky.slang\"", source);
        Assert.Contains("state.lightRevision != push.radianceRevision", source);
        Assert.Contains("kRadianceConvergenceSteps = 3u", source);
        Assert.Contains("kInitialRadianceAccumulation = 1u << 13u", source);
        Assert.Contains("StratifiedSphereDirection", source);
        Assert.Contains("probeIndex ^ HashUint(frameNumber", source);
        Assert.Contains("accumulatedBatchCount / (accumulatedBatchCount + 1.0)", source);
        Assert.Contains("state.flags &= ~16u", source);
        Assert.Contains("kVisibilityLobePower = 4.0", source);
        Assert.Contains("state.lightRevision = push.radianceRevision", source);
        Assert.Contains("GetSkyRadiance(", source);
        Assert.Contains("diffuseSky.sunDirAndRadius.w = 0.0", source);
        Assert.Contains("diffuseSky.sunDirAndRadius.xyz", source);
        Assert.Contains(
            "sunAngularRadius > 0.0",
            ReadRepositoryFile("Content", "shaders", "nishita_sky.slang"));
        Assert.DoesNotContain("ambientBounce", source);
        Assert.DoesNotContain("float3 skySky", source);
        Assert.Contains("kVisibilityTileResolution = 4u", source);
        Assert.Contains("kVisibilityTexelsPerProbe = 16u", source);
        Assert.Contains("OctahedralDecode", source);
        Assert.Contains("g_rayDirection[rayIdx]", source);
        Assert.Contains("g_rayDistance[rayIdx]", source);
        Assert.Contains("sparseIdx * kVisibilityTexelsPerProbe", source);
        Assert.Contains("uint2 coordinate0 = AtlasCoordinate", source);
        Assert.Contains("irradiance[coordinate0]", source);
        Assert.Contains("EvaluateHitSurface", source);
        Assert.Contains("CommittedTriangleBarycentrics", source);
        Assert.Contains("material.albedoTexIndex", source);
        Assert.Contains("diffuseReflectance", source);
    }

    [Fact]
    public void DebugDraw_UsesCurrentGeometryDrivenRequests()
    {
        string source = File.ReadAllText(
            ResolveShader("ddgi_debug.slang"));
        Assert.Contains("uint requestIdx = vid / 24u", source);
        Assert.Contains("Push.ProbeRequests[requestIdx]", source);
        Assert.Contains("Push.ProbePositions[probeIdx]", source);
        Assert.Contains("bool resident = request.gridCellIndex", source);
        Assert.Contains("Push.GridToProbeIndex[request.gridCellIndex]", source);
        Assert.Contains("bool ready = Push.ProbePositions[probeIdx].w > 0.5", source);
        Assert.Contains("float3(1.0, 0.05, 0.02)", source);
        Assert.Contains("float3(0.05, 1.0, 0.12)", source);
        Assert.DoesNotContain("float3(0.95, 0.15, 1.0)", source);
        Assert.DoesNotContain("float3(0.18, 0.32, 0.65)", source);
        Assert.DoesNotContain("float3(0.10, 0.85, 1.0)", source);
        Assert.DoesNotContain("StructuredBuffer<float4> Probes", source);
        string passSource = File.ReadAllText(ResolvePassSource());
        string pluginSource = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIRendererPlugin.cs");
        Assert.Contains(
            "sink.Draw((uint)_atlas.RequestCount * 24u",
            passSource);
        Assert.DoesNotContain("sink.DrawIndirect(", passSource);
        Assert.DoesNotContain("_atlas.ProbeDrawArgs", passSource);
        Assert.Contains(
            "ProbeRequests = _atlas.ProbeRequests.DeviceAddress",
            passSource);
        Assert.Contains(
            "ProbePositions = _atlas.ProbePositions.DeviceAddress",
            passSource);
        Assert.Contains("Push.ProbeStates[probeIdx]", source);
        Assert.Contains("GridToProbeIndex = _atlas.GridToProbeIndex.DeviceAddress",
            passSource);
        Assert.Contains("uint clipmapLevel", source);
        Assert.Contains("Push.ShowStatusColors != 0u", source);
        Assert.Contains("dirty || converging", source);
        Assert.Contains("EvaluateTwoBandSH", source);
        Assert.Contains("IrradianceCoordinate", source);
        Assert.Contains("bindless.textures[Push.IrradianceBindlessIndex]", source);
        Assert.Contains("ShowProbeStatusColors", passSource);
        Assert.Contains("sink.BindHeap(1, _atlas.SharedHeap)", passSource);
        Assert.Contains("RegisterDebugViewToggle", pluginSource);
        Assert.Contains("_showProbeStatusColors = true", ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIVolumeRegistry.cs"));
        Assert.Contains("Probe status colours", pluginSource);
        string menuService = ReadRepositoryFile(
            "Editor", "Services", "DynamicMenuService.cs");
        string viewModel = ReadRepositoryFile(
            "Editor", "ViewModels", "ViewportPanelViewModel.cs");
        Assert.Contains("SetActiveDebugView", menuService);
        Assert.Contains("_activeDebugView", menuService);
        Assert.Contains("ObservableCollection<string> DebugViews", viewModel);
        Assert.Contains("string.IsNullOrWhiteSpace(value)", viewModel);
        Assert.Contains("_lastValidDebugView", viewModel);
        Assert.Contains("Array.IndexOf(", viewModel);
        Assert.Contains("SelectedDebugView) < 0", viewModel);
        Assert.DoesNotContain("FallbackProbeCount", source);
        Assert.Contains("plan.AddPostPass(debug)", pluginSource);
        Assert.DoesNotContain(
            "if (_gpuPlanReady && _atlas != null",
            pluginSource);
        Assert.Contains("ProbeUpdateQueue = _atlas.ProbeUpdateQueue.DeviceAddress",
            File.ReadAllText(ResolveUpdatePassSource()));
        Assert.Contains("IGpuWorkTimingSource", File.ReadAllText(
            ResolveUpdatePassSource()));
        Assert.Contains("TryGetSubmittedUnitCount", File.ReadAllText(
            ResolveUpdatePassSource()));
        Assert.Contains("(uint)admittedCount", File.ReadAllText(
            ResolveUpdatePassSource()));
        Assert.Contains("RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Vertex",
            File.ReadAllText(ResolveAtlasSource()));
        Assert.Contains("VisibilityTexelsPerProbe",
            File.ReadAllText(ResolveAtlasSource()));
        Assert.Contains("WorldProbeHashCapacity",
            File.ReadAllText(ResolveAtlasSource()));
        Assert.DoesNotContain("TryReadActiveProbeCount", File.ReadAllText(ResolveAtlasSource()));
        Assert.DoesNotContain("UploadSparseLayout", File.ReadAllText(ResolveAtlasSource()));
    }

    [Fact]
    public void CameraProvider_RemainsBoundToViewportRenderer()
    {
        string rendererSource = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "Renderer.cs");
        string gameRendererSource = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "GameRenderer.cs");
        string thumbnailSource = ReadRepositoryFile(
            "Editor", "Services", "ThumbnailGenerator.cs");

        Assert.Contains("bool registerAsActive = true", rendererSource);
        Assert.Contains("ActiveCameraProvider = this", rendererSource);
        Assert.Contains("IActiveCameraDataProvider? ActiveCameraProvider", ReadRepositoryFile(
            "engine_cs", "Engine.RenderGraph", "RendererPluginContracts.cs"));
        Assert.Contains("context.ActiveCameraProvider", ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIRendererPlugin.cs"));
        Assert.Contains("registerAsActive: enableImGui", gameRendererSource);
        Assert.Contains("_participatesInGlobalExtensions", rendererSource);
        Assert.Contains(
            "EnableGlobalExtensions =",
            rendererSource);
        Assert.Contains(
            "context.EnableGlobalExtensions",
            ReadRepositoryFile(
                "Plugins", "Renderer.Clustered",
                "ClusteredRendererPlugin.cs"));
        Assert.Contains("registerAsActive: false", gameRendererSource);
        Assert.Equal(2, CountOccurrences(
            gameRendererSource,
            "registerAsActive: false"));
        Assert.Contains("var world = new EcsWorld()", thumbnailSource);
        Assert.DoesNotContain("null!,\n                enableImGui: false", thumbnailSource);
        Assert.Contains(
            "BlockingCollection<ThumbnailRequest>",
            thumbnailSource);
        Assert.Contains("new Thread(WorkerMain)", thumbnailSource);
        Assert.Contains(
            "TaskCreationOptions.RunContinuationsAsynchronously",
            thumbnailSource);
        Assert.DoesNotContain(
            "GenerateThumbnailOnRenderThread",
            thumbnailSource);

        string catalogSource = ReadRepositoryFile(
            "Editor", "Services", "PluginCatalogService.cs");
        int projectSwitch = catalogSource.IndexOf(
            "public void SetProjectRoot", StringComparison.Ordinal);
        int availability = catalogSource.IndexOf(
            "AvailabilityChanged?.Invoke()", projectSwitch,
            StringComparison.Ordinal);
        int shaderContext = catalogSource.IndexOf(
            "PublishActiveShaderContext();",
            projectSwitch,
            StringComparison.Ordinal);
        Assert.True(shaderContext > projectSwitch);
        Assert.True(shaderContext < availability);
    }

    [Fact]
    public void DdgiPassesShareOneFrameCachedRaytracingScene()
    {
        string plugin = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIRendererPlugin.cs");
        string placement = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIProbePlacementPass.cs");
        string update = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIProbeUpdatePass.cs");
        string cache = ReadRepositoryFile(
            "engine_cs", "Engine.RenderGraph", "RaytracingSceneCache.cs");

        Assert.Contains("_sceneCache = new RaytracingSceneCache", plugin);
        Assert.DoesNotContain("Task.Run", plugin);
        Assert.DoesNotContain("ConstructPassWithTimeout", plugin);
        Assert.DoesNotContain("new RaytracingSceneCache", placement);
        Assert.DoesNotContain("new RaytracingSceneCache", update);
        Assert.Contains("_lastUpdateFrame == frameNumber", cache);
        Assert.Contains("context.FrameNumber", placement);
        Assert.Contains("context.FrameNumber", update);
        Assert.Contains(
            "canClassifySceneBake: tlasInfo.SceneTlas != null",
            placement);
        Assert.Contains("CalculateSceneBakeRequestBudget", placement);

        string renderer = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "Renderer.cs");
        int beginBudgetFrame = renderer.IndexOf(
            "_gpuWorkScheduler.BeginFrame(_renderedFrameCount)",
            StringComparison.Ordinal);
        int executeGraph = renderer.IndexOf(
            "_graphExecutor.Execute(_plan", beginBudgetFrame,
            StringComparison.Ordinal);
        Assert.True(beginBudgetFrame >= 0);
        Assert.True(executeGraph > beginBudgetFrame);

        string sceneCache = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "RasterSceneGpuCache.cs");
        Assert.Contains("light.Direction", sceneCache);
        Assert.Contains("light.Color", sceneCache);
        Assert.Contains("light.ShapeParams", sceneCache);
        Assert.Contains("_lightRevision++", sceneCache);
        Assert.Contains("_skyRevision++", sceneCache);
        Assert.Contains("CurrentSkySunDirectionAndRadius", sceneCache);
        Assert.Contains("CurrentSkyAtmosphereParameters", sceneCache);
        string schedulePass = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIProbeSchedulePass.cs");
        Assert.Contains("TrackRadianceRevision", schedulePass);
        Assert.Contains("ConsumeRadianceRefreshAllowance", schedulePass);
        Assert.Contains("allocatedProbeCount * 2", File.ReadAllText(
            ResolveAtlasSource()));
    }

    [Fact]
    public void SceneGeometryChanges_RestartWholeSceneProbeBake()
    {
        string sceneCache = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "RasterSceneGpuCache.cs");
        string placement = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIProbePlacementPass.cs");
        string worldCache = ReadRepositoryFile(
            "Plugins", "Renderer.DDGI", "DDGIWorldProbeCache.cs");

        Assert.Contains("UpdateGeometryState(frameData)", sceneCache);
        Assert.Contains("CurrentGeometryRevision", sceneCache);
        Assert.Contains("TryGetSceneBounds", sceneCache);
        Assert.Contains("instance.EntityIdLow", sceneCache);
        Assert.Contains("foreach (PartData part in frameData.Parts)", sceneCache);
        Assert.Contains("_sceneGpuData?.CurrentGeometryRevision", placement);
        Assert.Contains("_atlas.WorldCache.PrepareFrame", placement);
        Assert.Contains(
            "geometryRevision != _bakeGeometryRevision",
            worldCache);
        Assert.Contains("_bakeLevel = _clipmapLevelCount - 1", worldCache);
    }

    [Fact]
    public void RendererExtensionUnload_RetiresPassesBeforeGpuTeardown()
    {
        string catalog = ReadRepositoryFile(
            "Editor", "Services", "PluginCatalogService.cs");
        int methodStart = catalog.IndexOf(
            "private void UnloadPlugin", StringComparison.Ordinal);
        int methodEnd = catalog.IndexOf(
            "private string GetConfigurationPath", methodStart,
            StringComparison.Ordinal);
        string unload = catalog[methodStart..methodEnd];

        int enqueue = unload.IndexOf(
            "renderer.EnqueueRenderThreadAction", StringComparison.Ordinal);
        int remove = unload.IndexOf(
            "owner.RemoveExtensionPlugin(extensionRenderer)", enqueue,
            StringComparison.Ordinal);
        int shutdown = unload.IndexOf(
            "runtime.Instance.Shutdown()", remove,
            StringComparison.Ordinal);
        int contextUnload = unload.IndexOf(
            "runtime.Context.Unload()", shutdown,
            StringComparison.Ordinal);

        Assert.True(enqueue >= 0);
        Assert.True(remove > enqueue);
        Assert.True(shutdown > remove);
        Assert.True(contextUnload > shutdown);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException(
            $"Repository file '{Path.Combine(parts)}' was not found.");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            ++count;
            offset += value.Length;
        }
        return count;
    }

    private static string ResolveUpdatePassSource()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "DDGIProbeUpdatePass.cs");
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException("DDGIProbeUpdatePass.cs was not found.");
    }

    private static string ResolveAtlasSource()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "DDGIAtlasResources.cs");
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException("DDGIAtlasResources.cs was not found.");
    }

    private static string ResolvePassSource()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(
                dir, "Plugins", "Renderer.DDGI", "DDGIDebugPass.cs");
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        throw new FileNotFoundException("DDGIDebugPass.cs was not found.");
    }
}
