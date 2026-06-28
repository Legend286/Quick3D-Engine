// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;

namespace Engine.Editor.ViewModels;

public partial class ConsolePanelViewModel : ObservableObject, IDisposable
{
    private readonly List<ConsoleEntryViewModel> _allEntries = new();
    private int _filterLevel = -1; // -1 = show all
    private string _searchText = string.Empty;
    private bool _autoScroll = true;
    private int _totalCount;

    public ObservableCollection<ConsoleEntryViewModel> FilteredEntries { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
                ReapplyFilter();
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (SetProperty(ref _autoScroll, value))
                OnAutoScrollChanged(value);
        }
    }

    public string AutoScrollIconColor => AutoScroll ? "#7ebae0" : "#4a4f5b";
    public string CountText => _totalCount > 0 ? $"{_totalCount:N0} entries" : "";

    /// <summary>Called by the view after items are added, to scroll to bottom
    /// when auto-scroll is enabled.</summary>
    public event Action? ScrollToEndRequested;

    // Filter button colors
    public string AllBg => _filterLevel == -1 ? "#2d3a4a" : "#141820";
    public string AllFg => _filterLevel == -1 ? "#e0e4ec" : "#5a5f6b";
    public string ErrorBg => _filterLevel == EngineLog.EngineLogError ? "#4a1a1a" : "#141820";
    public string ErrorFg => _filterLevel == EngineLog.EngineLogError ? "#ff6b6b" : "#5a5f6b";
    public string WarnBg => _filterLevel == EngineLog.EngineLogWarn ? "#4a3a1a" : "#141820";
    public string WarnFg => _filterLevel == EngineLog.EngineLogWarn ? "#ffc766" : "#5a5f6b";
    public string InfoBg => _filterLevel == EngineLog.EngineLogInfo ? "#1a3a4a" : "#141820";
    public string InfoFg => _filterLevel == EngineLog.EngineLogInfo ? "#7ebae0" : "#5a5f6b";
    public string DebugBg => _filterLevel == EngineLog.EngineLogDebug ? "#1a2a1a" : "#141820";
    public string DebugFg => _filterLevel == EngineLog.EngineLogDebug ? "#6bcc6b" : "#5a5f6b";
    public string TraceBg => _filterLevel == EngineLog.EngineLogTrace ? "#2a1a3a" : "#141820";
    public string TraceFg => _filterLevel == EngineLog.EngineLogTrace ? "#b06bcc" : "#5a5f6b";

    private readonly DispatcherTimer _timer;
    private const int BatchCap = 64;

    public ConsolePanelViewModel()
    {
        // Enable DEBUG-level logging for engine/editor modules so the console
        // shows verbose output. Without this, the engine's default INFO level
        // would filter out DEBUG and TRACE messages.
        SetModuleLogLevels();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void SetModuleLogLevels()
    {
        string[] modules = { "Renderer", "Game", "Editor", "Build" };
        foreach (var m in modules)
        {
            var ptr = Marshal.StringToHGlobalAnsi(m);
            try { EngineLog.EngineLogSetModuleLevel(ptr, EngineLog.EngineLogDebug); }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }

    public void SetFilter(int level)
    {
        _filterLevel = level;
        OnPropertyChanged(nameof(AllBg)); OnPropertyChanged(nameof(AllFg));
        OnPropertyChanged(nameof(ErrorBg)); OnPropertyChanged(nameof(ErrorFg));
        OnPropertyChanged(nameof(WarnBg)); OnPropertyChanged(nameof(WarnFg));
        OnPropertyChanged(nameof(InfoBg)); OnPropertyChanged(nameof(InfoFg));
        OnPropertyChanged(nameof(DebugBg)); OnPropertyChanged(nameof(DebugFg));
        OnPropertyChanged(nameof(TraceBg)); OnPropertyChanged(nameof(TraceFg));
        ReapplyFilter();
    }

    public void Clear()
    {
        _allEntries.Clear();
        FilteredEntries.Clear();
        _totalCount = 0;
        OnPropertyChanged(nameof(CountText));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Span<EngineLog.EngineLogRecord> batch = stackalloc EngineLog.EngineLogRecord[BatchCap];
        unsafe
        {
            var pBatch = (EngineLog.EngineLogRecord*)
                Unsafe.AsPointer(ref batch[0]);
            int drained = EngineLog.EngineLogDrain(pBatch, batch.Length);
            if (drained > 0)
            {
                for (int i = 0; i < drained; ++i)
                {
                    var entry = new ConsoleEntryViewModel(batch[i]);
                    _allEntries.Add(entry);
                    if (MatchesFilter(entry))
                        FilteredEntries.Add(entry);
                    EngineLog.EngineLogFreeRecord(ref batch[i]);
                }
                while (_allEntries.Count > 4096)
                {
                    var removed = _allEntries[0];
                    _allEntries.RemoveAt(0);
                    FilteredEntries.Remove(removed);
                }
                _totalCount = _allEntries.Count;
                OnPropertyChanged(nameof(CountText));
                if (_autoScroll)
                    ScrollToEndRequested?.Invoke();
            }
        }
    }

    private bool MatchesFilter(ConsoleEntryViewModel entry)
    {
        if (_filterLevel >= 0 && entry.RawLevel != _filterLevel)
            return false;
        if (!string.IsNullOrEmpty(_searchText))
        {
            if (!entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
                !entry.Module.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
                !entry.Source.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
                !entry.Level.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private void ReapplyFilter()
    {
        FilteredEntries.Clear();
        foreach (var entry in _allEntries)
        {
            if (MatchesFilter(entry))
                FilteredEntries.Add(entry);
        }
    }

    private void OnAutoScrollChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoScrollIconColor));
    }

    /// <summary>Stops the drain timer. Called by MainWindow.OnClosed to
    /// release the panel before the engine logger shuts down.</summary>
    public void DisposeOnClose()
    {
        if (_timer.IsEnabled)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
        }
    }

    public void Dispose() => DisposeOnClose();
}

public partial class ConsoleEntryViewModel : ObservableObject
{
    public string Level { get; }
    public string Timestamp { get; }
    public string Module { get; }
    public string Source { get; }
    public string Message { get; }
    public int RawLevel { get; }

    // Source navigation
    public string SourceFilePath { get; }
    public int SourceLine { get; }
    public int SourceColumn { get; }
    public bool HasSource { get; }
    public string SourceTooltip { get; }

    // Color bindings
    public string LevelBg { get; }
    public string LevelColor { get; }
    public string SourceColor { get; }
    public string MessageColor { get; }

    public ConsoleEntryViewModel(EngineLog.EngineLogRecord rec)
    {
        RawLevel = rec.Level;
        Level = LevelToString(rec.Level);
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampNs / 1_000_000L)
                                  .ToLocalTime()
                                  .ToString("HH:mm:ss.fff");
        Module = rec.Module != IntPtr.Zero ? PtrToString(rec.Module) ?? "" : "";
        Message = rec.Msg != IntPtr.Zero ? PtrToString(rec.Msg) ?? "" : "";

        // Parse source file:line:column info
        string rawFile = rec.File != IntPtr.Zero ? PtrToString(rec.File) ?? "" : "";
        SourceFilePath = rawFile;
        SourceLine = rec.Line;
        SourceColumn = 0;
        HasSource = !string.IsNullOrEmpty(rawFile) && rec.Line > 0;

        // Also try to extract file:line info from the message body for C# build errors
        if (!HasSource)
        {
            var parsed = ParseSourceFromMessage(Message);
            if (parsed.HasValue)
            {
                SourceFilePath = parsed.Value.file;
                SourceLine = parsed.Value.line;
                SourceColumn = parsed.Value.column;
                HasSource = true;
            }
        }

        if (HasSource)
        {
            string fileName = Path.GetFileName(SourceFilePath);
            Source = SourceColumn > 0
                ? $"{fileName}:{SourceLine}:{SourceColumn}"
                : $"{fileName}:{SourceLine}";
            SourceTooltip = $"Click to open: {SourceFilePath}:{SourceLine}";
        }
        else
        {
            Source = rec.Line > 0 ? $"line {rec.Line}" : "";
            SourceTooltip = "";
        }

        // Color scheme
        (LevelBg, LevelColor, SourceColor, MessageColor) = rec.Level switch
        {
            EngineLog.EngineLogFatal => ("#5a1a1a", "#ff4444", "#ff6666", "#ff8888"),
            EngineLog.EngineLogError => ("#3a1a1a", "#ff6b6b", "#ff8888", "#e0a0a0"),
            EngineLog.EngineLogWarn => ("#3a2a1a", "#ffc766", "#dda040", "#d0c0a0"),
            EngineLog.EngineLogInfo => ("#1a2a3a", "#7ebae0", "#5a90b0", "#a0c0d0"),
            EngineLog.EngineLogDebug => ("#1a2a1a", "#6bcc6b", "#4a9040", "#90c090"),
            EngineLog.EngineLogTrace => ("#1a1a2a", "#b06bcc", "#704090", "#a090b0"),
            _ => ("#1a1a1a", "#888888", "#666666", "#888888"),
        };
    }

    /// <summary>
    /// Parse C# / dotnet build error messages to extract file:line:column.
    /// Matches patterns like: "file.cs(42,10): error CS####: message"
    /// </summary>
    private static (string file, int line, int column)? ParseSourceFromMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return null;

        // Pattern 1: /path/to/File.cs(42,10): error CS####:
        var m1 = System.Text.RegularExpressions.Regex.Match(
            msg, @"([^( \t]+\.cs)\((\d+),(\d+)\)");
        if (m1.Success)
        {
            return (m1.Groups[1].Value,
                    int.Parse(m1.Groups[2].Value),
                    int.Parse(m1.Groups[3].Value));
        }

        // Pattern 2: /path/to/File.cs:42:10
        var m2 = System.Text.RegularExpressions.Regex.Match(
            msg, @"([^( \t]+\.cs):(\d+):(\d+)");
        if (m2.Success)
        {
            return (m2.Groups[1].Value,
                    int.Parse(m2.Groups[2].Value),
                    int.Parse(m2.Groups[3].Value));
        }

        return null;
    }

    private static string LevelToString(int level) => level switch
    {
        EngineLog.EngineLogFatal => "FATAL",
        EngineLog.EngineLogError => "ERROR",
        EngineLog.EngineLogWarn => "WARN",
        EngineLog.EngineLogInfo => "INFO",
        EngineLog.EngineLogDebug => "DEBUG",
        EngineLog.EngineLogTrace => "TRACE",
        _ => "?",
    };

    private static string? PtrToString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(p);
    }
}
