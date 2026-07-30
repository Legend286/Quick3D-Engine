// SPDX-License-Identifier: MIT
using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

public partial class WelcomeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _projectName = "NewGame";

    [ObservableProperty]
    private string _organization = "My Company";

    [ObservableProperty]
    private string _targetDirectory =
        Services.EditorSettingsStore.LastProjectDirectory;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    [ObservableProperty]
    private bool _isNewProjectMode = false;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void SetNewProjectMode(bool mode)
    {
        IsNewProjectMode = mode;
        StatusMessage = string.Empty;
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
    }
}
