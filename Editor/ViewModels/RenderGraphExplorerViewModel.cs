// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Engine.Editor.Views;
using Engine.RHI;

namespace Engine.Editor.ViewModels;

public partial class RenderGraphPassRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Accent { get; init; }
    [ObservableProperty] private string _queue = string.Empty;
    [ObservableProperty] private string _cpuTime = string.Empty;
    [ObservableProperty] private string _gpuTime = string.Empty;
    [ObservableProperty] private string _accessSummary = string.Empty;
    [ObservableProperty] private string _barrierSummary = string.Empty;
    [ObservableProperty] private string _dependencySummary = string.Empty;
    [ObservableProperty] private double _timingBarWidth;
    [ObservableProperty] private bool _isExpanded;
}

public sealed class RenderGraphResourceRowViewModel
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Size { get; init; }
    public required string AliasOffset { get; init; }
    public required string Lifetime { get; init; }
}

public sealed class RenderGraphFrameRowViewModel
{
    public required string Frame { get; init; }
    public required string CpuTime { get; init; }
    public required string GpuTime { get; init; }
    public double CpuBarWidth { get; init; }
    public double GpuBarWidth { get; init; }
}

public sealed class RenderGraphBudgetRowViewModel
{
    public required string Name { get; init; }
    public required string Budget { get; init; }
    public required string EstimatedCost { get; init; }
    public required string Admitted { get; init; }
    public required string Deferred { get; init; }
}

public sealed class RenderGraphShadowFaceRowViewModel
{
    public required RenderGraphShadowFaceDiagnostics Diagnostics { get; init; }
    public required string Light { get; init; }
    public required string Face { get; init; }
    public required string State { get; init; }
    public required string StaticTile { get; init; }
    public required string DynamicTile { get; init; }
    public required string Resolution { get; init; }
}

public partial class RenderGraphExplorerViewModel : ObservableObject, IDisposable
{
    private static readonly string[] PassAccents =
    {
        "#55D6BE",
        "#F6C177",
        "#7AA2F7",
        "#E88DAD",
        "#A6E3A1",
        "#CBA6F7",
    };

    private readonly ViewportPanelViewModel _viewport;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private string _frameText = "Frame --";
    [ObservableProperty] private string _planText = "Plan --";
    [ObservableProperty] private string _cpuText = "-- ms";
    [ObservableProperty] private string _gpuText = "pending";
    [ObservableProperty] private string _heapText = "0 B";
    [ObservableProperty] private string _graphMemoryText = "0 B";
    [ObservableProperty] private string _pauseButtonText = "Pause";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _passCountText = "0 passes";
    [ObservableProperty] private string _resourceCountText = "0 resources";
    [ObservableProperty] private string _barrierCountText = "0 barriers";
    [ObservableProperty] private string _shadowMemoryText = "not active";
    [ObservableProperty] private string _shadowResidencyText = "0 pages";
    [ObservableProperty] private string _shadowLightCountText = "0 lights";
    private long _lastHistoryFrame = -1;
    private long _lastPlanVersion = -1;
    private ShadowAtlasInspectorWindow? _shadowInspector;

    public ObservableCollection<RenderGraphPassRowViewModel> Passes { get; } = new();
    public ObservableCollection<RenderGraphResourceRowViewModel> Resources { get; } = new();
    public ObservableCollection<RenderGraphFrameRowViewModel> FrameHistory { get; } = new();
    public ObservableCollection<RenderGraphBudgetRowViewModel> Budgets { get; } = new();
    public ObservableCollection<RenderGraphShadowFaceRowViewModel> ShadowFaces { get; } = new();

    public RenderGraphExplorerViewModel(ViewportPanelViewModel viewport)
    {
        _viewport = viewport;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    [RelayCommand]
    private void TogglePaused()
    {
        IsPaused = !IsPaused;
        PauseButtonText = IsPaused ? "Resume" : "Pause";
        if (!IsPaused)
            Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        RenderGraphDiagnosticsSnapshot? snapshot = _viewport.GameLoop?.GetRenderGraphDiagnostics();
        if (snapshot == null)
        {
            HasData = false;
            Passes.Clear();
            Resources.Clear();
            Budgets.Clear();
            ShadowFaces.Clear();
            return;
        }

        HasData = true;
        if (snapshot.PlanVersion != _lastPlanVersion)
        {
            _lastPlanVersion = snapshot.PlanVersion;
            _lastHistoryFrame = -1;
            Passes.Clear();
            Resources.Clear();
            FrameHistory.Clear();
            Budgets.Clear();
            ShadowFaces.Clear();
        }

        PlanText = $"Plan {snapshot.PlanVersion:N0}";
        FrameText = $"Frame {snapshot.FrameNumber:N0}";
        CpuText = $"{snapshot.CpuRecordMilliseconds:0.000} ms";
        GpuText = snapshot.GpuFrameMilliseconds is double gpu
            ? $"{gpu:0.000} ms"
            : "pending";
        HeapText = $"{FormatBytes(snapshot.TransientHeapBytes)} transient";
        GraphMemoryText = FormatBytes(snapshot.TotalResourceBytes);
        PassCountText = $"{snapshot.Passes.Length} passes";
        ResourceCountText =
            $"{snapshot.Resources.Length} graph | " +
            $"{snapshot.Allocations.Length} allocated";
        BarrierCountText =
            $"{snapshot.Passes.Sum(pass => pass.Barriers.Length)} barriers";

        string filter = FilterText.Trim();
        RenderGraphPassDiagnostics[] visiblePasses = snapshot.Passes
            .Where(pass =>
                filter.Length == 0 ||
                pass.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                pass.Accesses.Any(access =>
                    access.ResourceName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        double maxTiming = Math.Max(
            visiblePasses.Select(pass => pass.GpuMilliseconds ?? pass.CpuMilliseconds)
                .DefaultIfEmpty()
                .Max(),
            0.001);
        bool samePassTopology =
            Passes.Count == visiblePasses.Length &&
            Passes.Select(pass => pass.Name)
                .SequenceEqual(visiblePasses.Select(pass => pass.Name));
        var expandedPasses = Passes
            .Where(pass => pass.IsExpanded)
            .Select(pass => pass.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!samePassTopology)
            Passes.Clear();

        for (int i = 0; i < visiblePasses.Length; ++i)
        {
            RenderGraphPassDiagnostics pass = visiblePasses[i];
            string accessSummary = pass.Accesses.Length == 0
                ? "No declared resources"
                : string.Join(
                    Environment.NewLine,
                    pass.Accesses.Select(access =>
                        $"{access.Access,-9} {access.ResourceName}  [{access.State}]"));
            string barrierSummary = pass.Barriers.Length == 0
                ? "No barriers"
                : string.Join(
                    Environment.NewLine,
                    pass.Barriers.Select(barrier =>
                        $"{barrier.ResourceName}: {barrier.Before} -> {barrier.After}"));
            string dependencySummary = pass.Dependencies.Length == 0
                ? "No upstream writers"
                : string.Join(Environment.NewLine, pass.Dependencies);

            RenderGraphPassRowViewModel row;
            if (samePassTopology)
            {
                row = Passes[i];
            }
            else
            {
                row = new RenderGraphPassRowViewModel
                {
                    Name = pass.Name,
                    Accent = PassAccents[i % PassAccents.Length],
                    IsExpanded = expandedPasses.Contains(pass.Name),
                };
                Passes.Add(row);
            }

            row.Queue = pass.Queue;
            row.CpuTime = $"{pass.CpuMilliseconds:0.000} ms CPU";
            row.GpuTime = pass.GpuMilliseconds is double passGpu
                ? $"{passGpu:0.000} ms GPU"
                : snapshot.GpuFrameMilliseconds is double
                    ? "GPU not sampled"
                    : "GPU pending";
            row.AccessSummary = accessSummary;
            row.BarrierSummary = barrierSummary;
            row.DependencySummary = dependencySummary;
            row.TimingBarWidth = Math.Max(
                3.0,
                320.0 * (pass.GpuMilliseconds ?? pass.CpuMilliseconds) / maxTiming);
        }

        Resources.Clear();
        foreach (RenderGraphResourceDiagnostics resource in snapshot.Resources.Where(resource =>
                     filter.Length == 0 ||
                     resource.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     resource.AliasGroup.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Resources.Add(new RenderGraphResourceRowViewModel
            {
                Name = resource.Name,
                Kind = resource.Kind,
                Size = FormatBytes(resource.SizeBytes),
                AliasOffset = resource.AliasGroup == "-"
                    ? $"offset 0x{resource.AliasOffsetBytes:X}"
                    : $"{resource.AliasGroup} at 0x{resource.AliasOffsetBytes:X}",
                Lifetime =
                    $"passes {resource.FirstPassIndex}-{resource.LastPassIndex} | {resource.AccessCount} accesses",
            });
        }
        foreach (GpuResourceAllocationDiagnostics allocation in
                 snapshot.Allocations.Where(allocation =>
                     filter.Length == 0 ||
                     allocation.Name.Contains(
                         filter,
                         StringComparison.OrdinalIgnoreCase) ||
                     allocation.Category.Contains(
                         filter,
                         StringComparison.OrdinalIgnoreCase)))
        {
            Resources.Add(new RenderGraphResourceRowViewModel
            {
                Name = allocation.Name,
                Kind =
                    $"{allocation.Category} / {allocation.Kind}",
                Size = FormatBytes(allocation.SizeBytes),
                AliasOffset = "committed",
                Lifetime =
                    $"live allocation #{allocation.AllocationId:N0}",
            });
        }

        Budgets.Clear();
        foreach (RenderGraphBudgetDiagnostics budget in snapshot.Budgets)
        {
            Budgets.Add(new RenderGraphBudgetRowViewModel
            {
                Name = budget.Name,
                Budget = $"{budget.BudgetMilliseconds:0.00} ms",
                EstimatedCost = $"{budget.EstimatedUnitMilliseconds:0.000} ms/unit",
                Admitted =
                    $"{budget.AdmittedUnits} / {budget.TotalAdmittedUnits:N0}",
                Deferred =
                    $"{budget.DeferredUnits} / {budget.TotalDeferredUnits:N0}",
            });
        }

        ShadowFaces.Clear();
        if (snapshot.Shadows is RenderGraphShadowDiagnostics shadows)
        {
            ShadowMemoryText =
                $"{FormatBytes(shadows.AllocatedBytes)} / " +
                FormatBytes(shadows.BudgetBytes);
            ShadowResidencyText =
                $"{shadows.PageCount} pages | {shadows.Faces.Length} faces";
            ShadowLightCountText =
                $"{shadows.PunctualLightCount} punctual lights";
            foreach (RenderGraphShadowFaceDiagnostics face in shadows.Faces)
            {
                string state = face.TransformPending
                    ? "transform queued"
                    : !face.StaticValid || !face.DynamicValid
                        ? "warming"
                        : face.CameraRelevant
                            ? "resident / visible"
                            : "resident / culled";
                ShadowFaces.Add(new RenderGraphShadowFaceRowViewModel
                {
                    Diagnostics = face,
                    Light =
                        $"L{face.LightIndex} | entity {face.EntityId}",
                    Face = face.FaceIndex.ToString(),
                    State = state,
                    StaticTile =
                        $"P{face.StaticPageIndex}:S{face.StaticSlotIndex} " +
                        $"{face.TileX},{face.TileY}",
                    DynamicTile =
                        $"P{face.DynamicPageIndex}:S{face.DynamicSlotIndex}",
                    Resolution = $"{face.TileSize} px",
                });
            }
        }
        else
        {
            ShadowMemoryText = "not active";
            ShadowResidencyText = "0 pages";
            ShadowLightCountText = "0 lights";
        }

        if (snapshot.FrameNumber != _lastHistoryFrame)
        {
            _lastHistoryFrame = snapshot.FrameNumber;
            double gpuMilliseconds = snapshot.GpuFrameMilliseconds ?? 0.0;
            FrameHistory.Insert(0, new RenderGraphFrameRowViewModel
            {
                Frame = $"#{snapshot.FrameNumber:N0}",
                CpuTime = $"{snapshot.CpuRecordMilliseconds:0.000}",
                GpuTime = snapshot.GpuFrameMilliseconds is double
                    ? $"{gpuMilliseconds:0.000}"
                    : "--",
                CpuBarWidth = Math.Clamp(snapshot.CpuRecordMilliseconds * 12.0, 2.0, 110.0),
                GpuBarWidth = Math.Clamp(gpuMilliseconds * 12.0, 0.0, 110.0),
            });
            while (FrameHistory.Count > 60)
                FrameHistory.RemoveAt(FrameHistory.Count - 1);
        }
    }

    public void ShowShadowInspector(
        RenderGraphShadowFaceRowViewModel row,
        Window? owner)
    {
        if (_shadowInspector == null)
        {
            _shadowInspector = new ShadowAtlasInspectorWindow();
            _shadowInspector.Closed += (_, _) =>
                _shadowInspector = null;
        }
        _shadowInspector.ShowTile(
            _viewport,
            row.Diagnostics,
            owner);
    }

    partial void OnFilterTextChanged(string value)
    {
        if (!IsPaused)
            Refresh();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!IsPaused)
            Refresh();
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024ul * 1024ul * 1024ul)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        if (bytes >= 1024ul * 1024ul)
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        if (bytes >= 1024ul)
            return $"{bytes / 1024.0:0.00} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _shadowInspector?.Close();
        _shadowInspector = null;
    }
}
