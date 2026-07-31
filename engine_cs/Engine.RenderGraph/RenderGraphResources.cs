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
    private const uint ShadowCascadeHandleBase = 0x80000003;

    public static ResourceHandle GetShadowPageHandle(int pageIndex)
        => new(ShadowCascadeHandleBase + (uint)pageIndex);
}

/// <summary>Camera-pose service exposed by the host. Plugin
/// implementations call <see cref="IEnginePluginHost.TryGetActiveCameraData"/>
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
        return host.TryGetActiveCameraData(
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
public interface IDDGIAtlasProvider
{
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

    /// <summary>Returns the volumetric grid origin / extent / count so
    /// the consumer shader can map any shading point back to its
    /// eight corner probes via trilinear interpolation. Returns
    /// false when the plugin is not loaded.</summary>
    bool TryGetProbeVolume(
        out Vector3 origin,
        out Vector3 extent,
        out Vector3I gridResolution);

    /// <summary>Returns true if the sparse layout has been populated;
    /// consumer shaders fall back to zero contribution until the
    /// placement pass has written accepted probes into the SSBO.</summary>
    bool IsSparseLayoutReady { get; }

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
