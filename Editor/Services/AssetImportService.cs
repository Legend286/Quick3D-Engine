// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.Assets;
using static Engine.CBindings.Log;

namespace Engine.Editor.Services;

/// <summary>
/// Runs asset cooking and thumbnail generation without blocking the editor UI.
/// </summary>
public sealed class AssetImportService : ObservableObject
{
    private int _running;
    private bool _isActive;
    private bool _isIndeterminate;
    private double _progress;
    private double _progressMaximum = 1;
    private string _statusMessage = string.Empty;

    /// <summary>Gets the process-wide editor import service.</summary>
    public static AssetImportService Shared { get; } = new();

    /// <summary>Fired on the UI thread after an import (including thumbnails) completes successfully.</summary>
    public event Action? ImportCompleted;

    /// <summary>Gets whether an import is currently active.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    /// <summary>Gets whether the active stage has measurable progress.</summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    /// <summary>Gets the completed work count for the active stage.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Gets the total work count for the active stage.</summary>
    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, value);
    }

    /// <summary>Gets the current import stage description.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    internal bool TryStart(
        string sourceFile,
        string targetDirectory,
        string assetType,
        float scaleX,
        float scaleY,
        float scaleZ)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;

        SetProgress("Preparing import", 0, 1, true, true);
        _ = Task.Run(
            () => RunImportAsync(
                sourceFile,
                targetDirectory,
                assetType,
                scaleX,
                scaleY,
                scaleZ));
        return true;
    }

    private async Task RunImportAsync(
        string sourceFile,
        string targetDirectory,
        string assetType,
        float scaleX,
        float scaleY,
        float scaleZ)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);

            string cookExecutable = FindCookExecutable()
                ?? throw new FileNotFoundException(
                    "engine_cook executable was not found.");
            string basisuPath = Path.Combine(
                Path.GetDirectoryName(cookExecutable) ?? string.Empty,
                "basisu");
            string basisuFlag = File.Exists(basisuPath)
                ? $" --basisu-path \"{basisuPath}\""
                : string.Empty;

            var processInfo = new ProcessStartInfo
            {
                FileName = cookExecutable,
                Arguments =
                    $"\"{sourceFile}\" \"{targetDirectory}\" " +
                    $"-scale {scaleX.ToString(CultureInfo.InvariantCulture)} " +
                    $"{scaleY.ToString(CultureInfo.InvariantCulture)} " +
                    $"{scaleZ.ToString(CultureInfo.InvariantCulture)}" +
                    basisuFlag,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = new StringBuilder();
            var errors = new StringBuilder();
            using Process process = new() { StartInfo = processInfo };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                    return;

                output.AppendLine(args.Data);
                ParseProgress(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data != null)
                    errors.AppendLine(args.Data);
            };

            SetProgress("Cooking asset", 0, 1, true);
            if (!process.Start())
                throw new InvalidOperationException(
                    "engine_cook could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Import failed (code {process.ExitCode}): " +
                    errors.ToString().Trim());
            }

            if (assetType == "Model")
            {
                await GenerateModelThumbnailsAsync(
                    sourceFile,
                    targetDirectory).ConfigureAwait(false);
            }

            Info($"Import succeeded:\n{output}", "Editor");
            SetProgress("Import complete", 1, 1, false);
            Dispatcher.UIThread.Post(() => ImportCompleted?.Invoke());
        }
        catch (Exception ex)
        {
            string message = $"Import failed: {ex.Message}";
            Error(message, "Editor");
            SetProgress(message, 1, 1, false);
        }
        finally
        {
            await Task.Delay(2500).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => IsActive = false);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void ParseProgress(string line)
    {
        if (!line.StartsWith("[PROGRESS]", StringComparison.Ordinal))
            return;

        string[] parts = line[10..].Trim().Split('|');
        if (parts.Length != 3 ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double current) ||
            !double.TryParse(
                parts[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double maximum))
        {
            return;
        }

        SetProgress(
            $"{parts[0]} ({current:0} of {maximum:0})",
            current,
            Math.Max(1, maximum),
            false);
    }

    private static string? FindCookExecutable()
    {
        string? directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string directPath = Path.Combine(directory, "engine_cook");
            if (File.Exists(directPath))
                return directPath;

            string outputPath =
                Path.Combine(directory, "out", "engine_cook");
            if (File.Exists(outputPath))
                return outputPath;

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null || parent.FullName == directory)
                break;
            directory = parent.FullName;
        }

        return null;
    }

    private async Task GenerateModelThumbnailsAsync(
        string sourceFile,
        string contentDirectory)
    {
        var recentCutoff = DateTime.Now.Subtract(TimeSpan.FromMinutes(2));
        string[] modelPaths = Directory.Exists(contentDirectory)
            ? Directory.EnumerateFiles(contentDirectory, "*.mdl", SearchOption.AllDirectories)
                .Where(path => new FileInfo(path).LastWriteTime >= recentCutoff || new FileInfo(path).CreationTime >= recentCutoff)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        if (modelPaths.Length == 0)
            return;

        try
        {
            var models = modelPaths
                .Select(path => (
                    Path: path,
                    Definition: ModelLoader.ReadDefinition(path)))
                .ToArray();
            int thumbnailCount = models.Length + models.Sum(m => m.Definition.Parts.Length);
            int completed = 0;
            SetProgress(
                $"Generating thumbnails (0 of {thumbnailCount})",
                0,
                thumbnailCount,
                false);

            var tasks = new List<Task>();

            foreach (var model in models)
            {
                tasks.Add(Task.Run(async () => 
                {
                    await ThumbnailGenerator
                        .GetOrGenerateThumbnailAsync(model.Path, "Model")
                        .ConfigureAwait(false);
                    int c = Interlocked.Increment(ref completed);
                    SetProgress(
                        $"Generating thumbnails ({c} of {thumbnailCount})",
                        c,
                        thumbnailCount,
                        false);
                }));

                for (int i = 0; i < model.Definition.Parts.Length; ++i)
                {
                    int partIndex = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        await ThumbnailGenerator
                            .GetOrGenerateThumbnailAsync(
                                model.Path,
                                "Model",
                                modelPartIndex: partIndex)
                            .ConfigureAwait(false);
                        int c = Interlocked.Increment(ref completed);
                        SetProgress(
                            $"Generating thumbnails ({c} of {thumbnailCount})",
                            c,
                            thumbnailCount,
                            false);
                    }));
                }
            }
            
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn(
                $"Thumbnail generation failed: {ex.Message}",
                "Editor");
        }
    }

    private void SetProgress(
        string message,
        double progress,
        double maximum,
        bool indeterminate,
        bool activate = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (activate)
                IsActive = true;
            StatusMessage = message;
            Progress = progress;
            ProgressMaximum = Math.Max(1, maximum);
            IsIndeterminate = indeterminate;
        });
    }
}
