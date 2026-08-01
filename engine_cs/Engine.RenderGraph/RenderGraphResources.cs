// SPDX-License-Identifier: MIT
// Process-wide sentinel handles and cross-plugin surface types that
// live below Engine.Renderer's surface so plugins compile against a
// renderer-free contract set. Each entry here is either a
// ResourceHandle sentinel the executor recognises on swapchain bind,
// a plugin-host method contract an IEnginePluginHost impl must
// satisfy, or an interface plugins implement to expose state the
// canonical clustered plan consults.
//
// Plugin assemblies that want a self-contained lifecycle reference
// these types directly (via `using Engine.RenderGraph;`) without
// needing a ProjectReference to Engine.Renderer.

using System.Numerics;
using Engine.Plugins;
using Engine.RHI;

namespace Engine.RenderGraph;

/// <summary>Process-wide singleton resource-handle constants. The render
/// graph executor binds swapchain-derived resources against these
/// IDs before each frame; pass Setup() declares them via
/// <c>builder.Write(handle, ResourceState.RenderTarget)</c> so the
/// compile barrier-inference accepts the binding.</summary>
public static class RenderGraphResources
{
    public static readonly ResourceHandle BackBufferHandle = new(0x80000000);
    public static readonly ResourceHandle DepthBufferHandle = new(0x80000001);
    public static readonly ResourceHandle OutlineMaskHandle = new(0x80000002);
    public static readonly ResourceHandle VisibilityIdentifiersHandle =
        new(0x80000020);
    public static readonly ResourceHandle VisibilityBarycentricsHandle =
        new(0x80000021);
    public static readonly ResourceHandle VisibilityReconstructionHandle =
        new(0x80000022);
    public static readonly ResourceHandle VisibilityReferenceHandle =
        new(0x80000023);
    private const uint ShadowCascadeHandleBase = 0x80000003;

    public static ResourceHandle GetShadowPageHandle(int pageIndex)
        => new(ShadowCascadeHandleBase + (uint)pageIndex);
}

/// <summary>Camera-pose service exposed by the host. Plugin
/// implementations call <see cref="IActiveCameraDataProvider.TryGetViewportCameraData"/>
/// to obtain world-space camera position + view / inverse-view
/// matrices without naming <c>Engine.Renderer.CameraData</c> or
/// reaching into the host's <c>Renderer</c> type.</summary>
public static class CameraPose
{
    public static bool TryGetMatrix(
        IEnginePluginHost host,
        uint width, uint height,
        out Vector3 cameraPosition,
        out Matrix4x4 viewProjection,
        out Matrix4x4 inverseViewProjection)
    {
        if (host == null)
        {
            cameraPosition = default;
            viewProjection = Matrix4x4.Identity;
            inverseViewProjection = Matrix4x4.Identity;
            return false;
        }
        return host.TryGetViewportCameraData(
            width, height,
            out cameraPosition,
            out viewProjection,
            out inverseViewProjection);
    }
}

/// <summary>Plugin-owned DDGI atlas contract. The DDGI plugin exposes
/// its probe atlas textures + sparse-placement buffers via this
/// interface; the canonical clustered plan consults it to wire
/// atlas + SSBO bindings into the PBR pass without naming
/// Engine.DDGI types. Implementations return null / false / zero
/// when the plugin is not loaded.</summary>
public readonly record struct DDGIAtlasResourceHandles(
    ResourceHandle ProbePositions,
    ResourceHandle GridToProbeIndex,
    ResourceHandle ProbeWorldKeys,
    ResourceHandle WorldProbeHash,
    ResourceHandle ProbeCounter,
    ResourceHandle ProbeDrawArgs,
    ResourceHandle ProbeStates,
    ResourceHandle ProbeUpdateQueue,
    ResourceHandle VolumeState,
    ResourceHandle Irradiance,
    ResourceHandle Visibility);

/// <summary>Persistent RHI objects owned by the DDGI plugin and imported
/// into the host render graph for barrier tracking.</summary>
public readonly record struct DDGIAtlasExternalResources(
    RhiBuffer ProbePositions,
    RhiBuffer GridToProbeIndex,
    RhiBuffer ProbeWorldKeys,
    RhiBuffer WorldProbeHash,
    RhiBuffer ProbeCounter,
    RhiBuffer ProbeDrawArgs,
    RhiBuffer ProbeStates,
    RhiBuffer ProbeUpdateQueue,
    RhiBuffer VolumeState,
    RhiTexture Irradiance,
    RhiTexture Visibility);

public interface IDDGIAtlasProvider
{
    /// <summary>Gets plugin-owned flags consumed by the shader include.</summary>
    uint ConsumerFlags { get; }

    /// <summary>Returns the bindless slots the plugin has assigned to
    /// its irradiance + visibility atlases, or (0u, 0u) when not loaded.
    /// Both slots are stable across frames; the plugin only reissues
    /// them when the atlas has been resized (typically never).</summary>
    (uint IrradianceBindlessIndex, uint VisibilityBindlessIndex)
        GetAtlasBindlessSlots();

    /// <summary>Returns the volatile positions StructuredBuffer +
    /// the coarse-grid indirection StructuredBuffer + the atomic
    /// placement counter. Returns false when the plugin is not
    /// loaded. Consumer passes bind these at fixed shader register
    /// slots (t5 = positions, t6 = gridToProbe, t7 = counter) so
    /// the shader can read them without going through the bindless
    /// texture heap (the heap currently exposes textures only).</summary>
    bool TryGetSparseBuffers(
        out Engine.RHI.RhiBuffer probePositions,
        out Engine.RHI.RhiBuffer gridToProbeIndex,
        out Engine.RHI.RhiBuffer probeCounter);

    /// <summary>Returns the persistent world-cell keys and hash table used
    /// to find the finest built probe independently of clipmap residency.</summary>
    bool TryGetPersistentLookup(
        out Engine.RHI.RhiBuffer probeWorldKeys,
        out Engine.RHI.RhiBuffer worldProbeHash,
        out uint hashCapacity);

    /// <summary>Returns GPU-owned scheduling and scrolling-volume buffers.</summary>
    bool TryGetGpuProbeState(
        out Engine.RHI.RhiBuffer probeStates,
        out Engine.RHI.RhiBuffer probeUpdateQueue,
        out Engine.RHI.RhiBuffer volumeState);

    /// <summary>Gets stable graph handles and the matching persistent RHI
    /// resources used to bind DDGI's external resources before execution.</summary>
    DDGIAtlasResourceHandles ResourceHandles { get; }
    bool TryGetExternalResources(out DDGIAtlasExternalResources resources);

    /// <summary>Returns the volumetric grid origin / extent / count so
    /// the consumer shader can map any shading point back to its
    /// eight corner probes via trilinear interpolation. Returns
    /// false when the plugin is not loaded.</summary>
    bool TryGetProbeVolume(
        out Vector3 origin,
        out Vector3 extent,
        out Vector3I gridResolution);

    /// <summary>Returns true when GPU probe resources are available.
    /// Render-graph ordering guarantees placement completes before consumers.</summary>
    bool IsSparseLayoutReady { get; }

    /// <summary>Returns true while scene-wide probe work needs more frames.</summary>
    bool HasPendingWork { get; }

    /// <summary>Octahedral ray fan size per probe. Matched by the
    /// plugin's compute kernel so shader-side hardcoded values stay
    /// in sync. Returns 0 when the plugin is not loaded.</summary>
    int RaysPerProbe { get; }

    /// <summary>Max probes updated per frame so probe refresh amortises
    /// across frames within the Gi budget. Matched by the plugin's
    /// scheduler. Returns 0 when the plugin is not loaded.</summary>
    int MaxProbesPerFrame { get; }

}

/// <summary>Process-wide cross-plugin lookup for the currently enabled
/// DDGI plugin's atlas resources. Plugins register/unregister against
/// this surface so the canonical clustered plan wires atlas bindings
/// into the PBR pass without naming the DDGI plugin's concrete types.
/// Last-writer-wins is acceptable because at most one DDGI plugin
/// instance is loaded at a time; a second registration logs a warning
/// and overwrites so a misconfigured manifest is observable.</summary>
public static class DDGIAtlasProviderRegistry
{
    private static IDDGIAtlasProvider? _active;
    private static string? _activePluginId;

    public static IDDGIAtlasProvider? Active => _active;

    public static string? ActivePluginId => _activePluginId;

    public static void Register(
        string pluginId,
        IDDGIAtlasProvider provider)
    {
        if (provider == null) return;
        if (!string.IsNullOrEmpty(_activePluginId) &&
            !string.Equals(
                _activePluginId, pluginId,
                StringComparison.Ordinal))
        {
            Engine.CBindings.Log.Warn(
                $"[DDGI] atlas provider already registered by " +
                $"'{_activePluginId}'; overwriting with '{pluginId}'.",
                "DDGI");
        }
        _active = provider;
        _activePluginId = pluginId;
    }

    public static void Unregister(string pluginId)
    {
        if (!string.Equals(
                _activePluginId, pluginId,
                StringComparison.Ordinal))
            return;
        _active = null;
        _activePluginId = null;
    }

    public static void Invalidate()
        => Unregister(_activePluginId ?? string.Empty);
}

/// <summary>Lightweight int-tuple mirror of <see cref="DDGIVolumeRegistry.GridResolution"/>
/// since the registry itself lives inside the plugin assembly.</summary>
public readonly record struct Vector3I(int X, int Y, int Z)
{
    public int Volume => X * Y * Z;
}
