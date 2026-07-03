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
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private bool _isNewProjectMode = false;
    
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void SetNewProjectMode(bool mode)
    {
        IsNewProjectMode = mode;
        StatusMessage = string.Empty;
    }
}
