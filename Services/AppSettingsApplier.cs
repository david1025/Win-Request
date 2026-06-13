using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRequest.Models;

namespace WinRequest.Services;

public static class AppSettingsApplier
{
    public static void ApplyToRoot(FrameworkElement root, AppSettings settings)
    {
        root.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public static void ApplyEditorFont(Control control, AppSettings settings)
    {
        control.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily)
            ? "Consolas"
            : settings.FontFamily.Trim());
        control.FontSize = settings.TextSize < 9 ? 9 : settings.TextSize;
    }
}
