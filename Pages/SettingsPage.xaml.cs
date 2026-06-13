using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly WorkspaceStorage _storage = new();
    private readonly GitHubUpdateService _updateService = new();
    private ApiWorkspace _workspace = new();
    private bool _isLoading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPage_Loaded;
        _workspace = await _storage.LoadAsync();
        LoadSettings(_workspace.Settings);
    }

    private void LoadSettings(AppSettings settings)
    {
        _isLoading = true;
        SelectTheme(settings.Theme);
        FontFamilyBox.Text = settings.FontFamily;
        TextSizeBox.Value = settings.TextSize;
        GitHubOwnerBox.Text = settings.GitHubOwner;
        GitHubRepositoryBox.Text = settings.GitHubRepository;
        _isLoading = false;
    }

    private void ApplyEditor()
    {
        if (_isLoading)
            return;

        _workspace.Settings.Theme = GetSelectedTheme();
        _workspace.Settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text)
            ? "Consolas"
            : FontFamilyBox.Text.Trim();
        _workspace.Settings.TextSize = double.IsNaN(TextSizeBox.Value) ? 13 : TextSizeBox.Value;
        _workspace.Settings.GitHubOwner = GitHubOwnerBox.Text.Trim();
        _workspace.Settings.GitHubRepository = GitHubRepositoryBox.Text.Trim();
        App.Current.ApplySettings(_workspace.Settings);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        await _storage.SaveAsync(_workspace);
        StatusText.Text = "设置已保存。工作台编辑器会在打开或切换请求时使用新的字体设置。";
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        await _storage.SaveAsync(_workspace);
        UpdateProgressRing.Visibility = Visibility.Visible;
        UpdateProgressRing.IsActive = true;
        UpdateResultText.Text = "正在检查 GitHub 最新 Release...";

        var result = await _updateService.CheckLatestReleaseAsync(
            _workspace.Settings.GitHubOwner,
            _workspace.Settings.GitHubRepository);

        UpdateProgressRing.IsActive = false;
        UpdateProgressRing.Visibility = Visibility.Collapsed;
        if (result.IsSuccess)
        {
            string title = string.IsNullOrWhiteSpace(result.Name) ? result.TagName : $"{result.Name} ({result.TagName})";
            UpdateResultText.Text = $"最新版本：{title}\n发布时间：{result.PublishedAt}\n地址：{result.HtmlUrl}";
        }
        else
        {
            UpdateResultText.Text = $"检查失败：{result.Message}";
        }
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e) => ApplyEditor();
    private void SettingsText_Changed(object sender, TextChangedEventArgs e) => ApplyEditor();
    private void TextSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyEditor();

    private void SelectTheme(string theme)
    {
        foreach (ComboBoxItem item in ThemeComboBox.Items.Cast<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedItem = item;
                return;
            }
        }
        ThemeComboBox.SelectedIndex = 0;
    }

    private string GetSelectedTheme()
    {
        return ThemeComboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "System"
            : "System";
    }
}
