// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;

namespace Engine.Editor.ViewModels;

public partial class ConsolePanelViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<ConsoleEntryViewModel> Entries { get; } = new();

    private readonly DispatcherTimer _timer;
    private const int BatchCap = 64;

    public ConsolePanelViewModel()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Span<EngineLog.EngineLogRecord> batch = stackalloc EngineLog.EngineLogRecord[BatchCap];
        unsafe
        {
            // The P/Invoke ABI wants a managed pointer to the first record.
            // `Unsafe.AsPointer<T>(ref T)` returns a `void*` that the C#
            // marshaller can copy across into the ABI call. Equivalent to
            // `&batch[0]`, but the explicit cast works across Roslyn
            // versions - some still refuse the address-of bare-index
            // expression on a Span<T> without a fixed-block scope.
            var pBatch = (EngineLog.EngineLogRecord*)
                System.Runtime.CompilerServices.Unsafe.AsPointer(ref batch[0]);
            int drained = EngineLog.EngineLogDrain(pBatch, batch.Length);
            for (int i = 0; i < drained; ++i)
            {
                Entries.Add(new ConsoleEntryViewModel(batch[i]));
            }
            while (Entries.Count > 4096)
                Entries.RemoveAt(0);
        }
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

    public ConsoleEntryViewModel(EngineLog.EngineLogRecord rec)
    {
        Level = LevelToString(rec.Level);
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampNs / 1_000_000L)
                                  .ToString("HH:mm:ss.fff");
        Module = rec.Module != IntPtr.Zero ? PtrToString(rec.Module) ?? "" : "";
        Source = rec.File != IntPtr.Zero
            ? $"{PtrToString(rec.File) ?? ""}:{rec.Line}"
            : $"line {rec.Line}";
        Message = rec.Msg != IntPtr.Zero ? PtrToString(rec.Msg) ?? "" : "";
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
