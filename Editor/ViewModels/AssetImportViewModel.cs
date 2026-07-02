using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

public partial class AssetImportViewModel : ObservableObject
{
    [ObservableProperty]
    private string _sourceFile = string.Empty;

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private bool _importMaterials = true;

    [ObservableProperty]
    private bool _importTextures = true;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public bool ImportSucceeded { get; set; }
    public string ImportedSceneName { get; set; } = string.Empty;
}
