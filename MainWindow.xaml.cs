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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ContentFrame.Navigate(typeof(WorkspacePage));
        MainNav.SelectedItem = WorkspaceNavItem;
        _ = ApplySavedSettingsAsync();
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

    private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        string tag = item.Tag?.ToString() ?? "";
        if (tag == "settings")
            ContentFrame.Navigate(typeof(SettingsPage));
        else
            ContentFrame.Navigate(typeof(WorkspacePage));
    }
}
