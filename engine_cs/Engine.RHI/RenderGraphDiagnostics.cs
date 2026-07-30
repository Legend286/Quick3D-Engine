// SPDX-License-Identifier: MIT

namespace Engine.RHI;

/// <summary>
/// Immutable renderer diagnostics transferred across the game hot-reload boundary.
/// </summary>
public sealed record RenderGraphDiagnosticsSnapshot(
    long PlanVersion,
    long FrameNumber,
    double CpuRecordMilliseconds,
    double? GpuFrameMilliseconds,
    ulong TransientHeapBytes,
    ulong TotalResourceBytes,
    GpuResourceAllocationDiagnostics[] Allocations,
    RenderGraphBudgetDiagnostics[] Budgets,
    RenderGraphPassDiagnostics[] Passes,
    RenderGraphResourceDiagnostics[] Resources,
    RenderGraphShadowDiagnostics? Shadows);

/// <summary>
/// One live committed GPU allocation captured independently of graph aliases.
/// </summary>
public sealed record GpuResourceAllocationDiagnostics(
    long AllocationId,
    string Name,
    string Kind,
    string Category,
    ulong SizeBytes);

/// <summary>
/// Shadow-atlas residency and cache state for the captured frame.
/// </summary>
public sealed record RenderGraphShadowDiagnostics(
    ulong BudgetBytes,
    ulong AllocatedBytes,
    int PageCount,
    int PunctualLightCount,
    RenderGraphShadowFaceDiagnostics[] Faces);

/// <summary>
/// Stable atlas allocation and cache state for one punctual-light face.
/// </summary>
public sealed record RenderGraphShadowFaceDiagnostics(
    ulong EntityId,
    int LightIndex,
    int FaceIndex,
    bool CameraRelevant,
    bool TransformPending,
    bool StaticValid,
    bool DynamicValid,
    int UpdateIntervalFrames,
    int FramesSinceUpdate,
    float VisualPriority,
    int StaticPageIndex,
    int StaticSlotIndex,
    int DynamicPageIndex,
    int DynamicSlotIndex,
    uint TileX,
    uint TileY,
    uint TileSize);

/// <summary>
/// Per-domain GPU work-budget state for the captured frame.
/// </summary>
public sealed record RenderGraphBudgetDiagnostics(
    string Name,
    double BudgetMilliseconds,
    double EstimatedUnitMilliseconds,
    int MaximumUnits,
    int AdmittedUnits,
    int DeferredUnits,
    long TotalAdmittedUnits,
    long TotalDeferredUnits);

/// <summary>
/// Timing and dependency data for one render pass.
/// </summary>
public sealed record RenderGraphPassDiagnostics(
    string Name,
    string Queue,
    double CpuMilliseconds,
    double? GpuMilliseconds,
    string[] Dependencies,
    RenderGraphAccessDiagnostics[] Accesses,
    RenderGraphBarrierDiagnostics[] Barriers);

/// <summary>
/// Resource access declared by a render pass.
/// </summary>
public sealed record RenderGraphAccessDiagnostics(
    ulong ResourceId,
    string ResourceName,
    string Access,
    string State);

/// <summary>
/// Resource transition emitted before a render pass.
/// </summary>
public sealed record RenderGraphBarrierDiagnostics(
    ulong ResourceId,
    string ResourceName,
    string Before,
    string After);

/// <summary>
/// Render-graph resource allocation and aliasing data.
/// </summary>
public sealed record RenderGraphResourceDiagnostics(
    ulong ResourceId,
    string Name,
    string Kind,
    ulong SizeBytes,
    ulong AliasOffsetBytes,
    string AliasGroup,
    int FirstPassIndex,
    int LastPassIndex,
    int AccessCount);
