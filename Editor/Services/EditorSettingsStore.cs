// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;

namespace Engine.Editor.Services;

internal sealed class EditorSettings
{
    public string LastProjectDirectory { get; set; } = string.Empty;
}

internal static class EditorSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true };

    public static string LastProjectDirectory
    {
        get
        {
            EditorSettings settings = Load();
            return Directory.Exists(settings.LastProjectDirectory)
                ? settings.LastProjectDirectory
                : string.Empty;
        }
    }

    public static void RememberProject(string projectRoot)
    {
        try
        {
            string fullPath = Path.GetFullPath(projectRoot);
            string directory =
                Path.GetDirectoryName(fullPath) ?? fullPath;
            Save(new EditorSettings
            {
                LastProjectDirectory = directory
            });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static EditorSettings Load()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path))
            return new EditorSettings();

        try
        {
            return JsonSerializer.Deserialize<EditorSettings>(
                       File.ReadAllText(path))
                   ?? new EditorSettings();
        }
        catch
        {
            return new EditorSettings();
        }
    }

    private static void Save(EditorSettings settings)
    {
        string path = GetSettingsPath();
        string directory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Editor settings directory is unavailable.");
        Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings,
            SerializerOptions);
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporaryPath, path, true);
    }

    private static string GetSettingsPath()
        => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Quick3D",
            "editor.local.json");
}
