using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRequest.Pages;
using WinRequest.Services;

namespace WinRequest;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceStorage _storage = new();

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ContentFrame.Navigate(typeof(WorkspacePage));
        _ = ApplySavedSettingsAsync();
    }

    private void SetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinRequest.ico");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch
        {
        }
    }

    private async System.Threading.Tasks.Task ApplySavedSettingsAsync()
    {
        try
        {
            var workspace = await _storage.LoadAsync();
            if (Content is FrameworkElement root)
                AppSettingsApplier.ApplyToRoot(root, workspace.Settings);
        }
        catch
        {
        }
    }
}
