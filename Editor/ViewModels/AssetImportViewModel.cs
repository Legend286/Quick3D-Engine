// SPDX-License-Identifier: MIT
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

/// <summary>One animation discovered in a glTF/GLB source asset.</summary>
public sealed partial class AnimationImportOption : ObservableObject
{
    /// <summary>Gets the source clip name passed back to the cooker.</summary>
    public string Name { get; }

    /// <summary>Gets the clip duration reported by glTF.</summary>
    public double DurationSeconds { get; }

    /// <summary>Gets the number of animated channels in the clip.</summary>
    public int ChannelCount { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    public AnimationImportOption(
        string name,
        double durationSeconds,
        int channelCount)
    {
        Name = name;
        DurationSeconds = durationSeconds;
        ChannelCount = channelCount;
    }

    /// <summary>Gets the compact clip summary shown in the import dialog.</summary>
    public string Summary =>
        $"{Name}  ·  {DurationSeconds:0.##}s  ·  {ChannelCount} channel(s)";
}

/// <summary>Machine-readable result returned by the cooker inspect operation.</summary>
public sealed class AssetImportInspection
{
    /// <summary>Gets whether the source contains renderable mesh primitives.</summary>
    [JsonPropertyName("has_mesh")]
    public bool HasMesh { get; init; }

    /// <summary>Gets whether the source contains a glTF skin.</summary>
    [JsonPropertyName("has_skeleton")]
    public bool HasSkeleton { get; init; }

    /// <summary>Gets the source animation clips in stable source order.</summary>
    [JsonPropertyName("animations")]
    public IReadOnlyList<AssetImportAnimation> Animations { get; init; } =
        Array.Empty<AssetImportAnimation>();
}

/// <summary>One animation entry returned by cooker inspection.</summary>
public sealed class AssetImportAnimation
{
    /// <summary>Gets the source clip name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the source clip duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double DurationSeconds { get; init; }

    /// <summary>Gets the source channel count.</summary>
    [JsonPropertyName("channels")]
    public int ChannelCount { get; init; }
}

/// <summary>State and selections for the model/animation import dialog.</summary>
public sealed partial class AssetImportViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceFile = string.Empty;

    [ObservableProperty]
    private string _assetType = "Model";

    [ObservableProperty]
    private bool _uniformScale = true;

    [ObservableProperty]
    private float _scaleX = 1.0f;

    [ObservableProperty]
    private float _scaleY = 1.0f;

    [ObservableProperty]
    private float _scaleZ = 1.0f;

    [ObservableProperty]
    private bool _importMesh = true;

    [ObservableProperty]
    private bool _importSkeleton;

    [ObservableProperty]
    private bool _hasMesh;

    [ObservableProperty]
    private bool _hasSkeleton;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private string _inspectionMessage = "Choose a GLB/GLTF file to inspect.";

    [ObservableProperty]
    private bool _importMaterials = true;

    [ObservableProperty]
    private bool _importTextures = true;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    /// <summary>Gets the discovered source animations and their selections.</summary>
    public ObservableCollection<AnimationImportOption> Animations { get; } = new();

    /// <summary>Gets whether at least one source animation was discovered.</summary>
    public bool HasAnimations => Animations.Count > 0;

    /// <summary>Applies cooker inspection metadata to the dialog state.</summary>
    public void ApplyInspection(AssetImportInspection inspection)
    {
        HasMesh = inspection.HasMesh;
        HasSkeleton = inspection.HasSkeleton;
        ImportMesh = inspection.HasMesh;
        ImportSkeleton = inspection.HasSkeleton;
        Animations.Clear();
        foreach (AssetImportAnimation animation in inspection.Animations)
        {
            Animations.Add(new AnimationImportOption(
                animation.Name,
                animation.DurationSeconds,
                animation.ChannelCount));
        }
        OnPropertyChanged(nameof(HasAnimations));
        InspectionMessage = inspection.Animations.Count == 0
            ? inspection.HasSkeleton
                ? "Skeleton found; no animation clips were found."
                : "No skeleton or animation clips were found."
            : $"Found {inspection.Animations.Count} animation clip(s).";
    }

    /// <summary>Clears inspection results after an inspection failure.</summary>
    public void ClearInspection(string message)
    {
        HasMesh = false;
        HasSkeleton = false;
        ImportMesh = false;
        ImportSkeleton = false;
        Animations.Clear();
        OnPropertyChanged(nameof(HasAnimations));
        InspectionMessage = message;
    }

    /// <summary>Gets the names of all clips selected for cooking.</summary>
    public IReadOnlyList<string> SelectedAnimationNames()
        => Animations
            .Where(animation => animation.IsSelected)
            .Select(animation => animation.Name)
            .ToArray();

    partial void OnScaleXChanged(float value)
    {
        if (UniformScale)
        {
            ScaleY = value;
            ScaleZ = value;
        }
    }
}
