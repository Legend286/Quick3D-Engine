using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

public partial class AssetImportViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceFile = string.Empty;

    [ObservableProperty]
    private string _assetType = "Model"; // "Model" or "Texture"

    [ObservableProperty]
    private bool _uniformScale = true;

    [ObservableProperty]
    private float _scaleX = 1.0f;

    [ObservableProperty]
    private float _scaleY = 1.0f;

    [ObservableProperty]
    private float _scaleZ = 1.0f;

    partial void OnScaleXChanged(float value)
    {
        if (UniformScale)
        {
            ScaleY = value;
            ScaleZ = value;
        }
    }

    [ObservableProperty]
    private bool _importMaterials = true;

    [ObservableProperty]
    private bool _importTextures = true;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _targetDirectory = string.Empty;
}
