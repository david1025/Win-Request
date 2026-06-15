using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WinRequest.Converters;

/// <summary>
/// Converts a boolean value to Visibility (true → Visible, false → Collapsed).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

/// <summary>
/// Converts a boolean value to inverse Visibility (true → Collapsed, false → Visible).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => value is Visibility.Collapsed;
}

/// <summary>
/// Converts an HTTP method string to a colored SolidColorBrush for method badges.
/// </summary>
public sealed class MethodToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Get = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush Post = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Put = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush Delete = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Patch = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x93, 0x33, 0xEA));
    private static readonly SolidColorBrush Head = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush Options = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush Connect = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush Default = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B));

    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        return (value?.ToString()?.ToUpperInvariant()) switch
        {
            "GET" => Get,
            "POST" => Post,
            "PUT" => Put,
            "DELETE" => Delete,
            "PATCH" => Patch,
            "HEAD" => Head,
            "OPTIONS" => Options,
            "CONNECT" => Connect,
            _ => Default
        };
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotImplementedException();
}
