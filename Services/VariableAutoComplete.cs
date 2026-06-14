using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinRequest.Services;

/// <summary>
/// Provides {{variable}} autocomplete for TextBox controls.
/// When user types "{{", an overlay ListView appears with matching environment variable suggestions.
/// Uses a simple Grid overlay instead of Popup for reliability in WinUI 3.
/// </summary>
public sealed class VariableAutoComplete : IDisposable
{
    private readonly TextBox _textBox;
    private readonly Grid _hostGrid;
    private readonly Func<List<string>> _getVariableNames;
    private Border? _overlayBorder;
    private ListView? _listView;
    private List<string> _currentSuggestions = new();
    private bool _isApplying;

    public VariableAutoComplete(TextBox textBox, Grid hostGrid, Func<List<string>> getVariableNames)
    {
        _textBox = textBox;
        _hostGrid = hostGrid;
        _getVariableNames = getVariableNames;
        _textBox.TextChanged += OnTextChanged;
        _textBox.PreviewKeyDown += OnPreviewKeyDown;
        _textBox.LostFocus += OnLostFocus;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplying)
            return;

        string text = _textBox.Text;
        int cursorPos = _textBox.SelectionStart;
        int triggerPos = FindTriggerPosition(text, cursorPos);

        if (triggerPos < 0)
        {
            HideOverlay();
            return;
        }

        string partial = text.Substring(triggerPos + 2, cursorPos - triggerPos - 2);
        var allVars = _getVariableNames();
        _currentSuggestions = string.IsNullOrEmpty(partial)
            ? allVars
            : allVars.Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_currentSuggestions.Count == 0)
        {
            HideOverlay();
            return;
        }

        ShowOverlay();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_overlayBorder == null || _overlayBorder.Visibility != Visibility.Visible || _currentSuggestions.Count == 0)
            return;

        int index = _listView?.SelectedIndex ?? -1;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                index = Math.Min(index + 1, _currentSuggestions.Count - 1);
                if (_listView != null) _listView.SelectedIndex = index;
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                index = Math.Max(index - 1, 0);
                if (_listView != null) _listView.SelectedIndex = index;
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
            case Windows.System.VirtualKey.Tab:
                if (index >= 0 && index < _currentSuggestions.Count)
                {
                    ApplySuggestion(_currentSuggestions[index]);
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.Escape:
                HideOverlay();
                e.Handled = true;
                break;
        }
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Check if focus moved to our ListView
            var focused = FocusManager.GetFocusedElement(_textBox.XamlRoot);
            if (focused is not ListViewItem)
                HideOverlay();
        };
        timer.Start();
    }

    private void ApplySuggestion(string variableName)
    {
        string text = _textBox.Text;
        int cursorPos = _textBox.SelectionStart;
        int triggerPos = FindTriggerPosition(text, cursorPos);

        if (triggerPos < 0)
            return;

        _isApplying = true;
        string insertion = "{{" + variableName + "}}";
        string newText = text[..triggerPos] + insertion + text[cursorPos..];
        _textBox.Text = newText;
        _textBox.SelectionStart = triggerPos + insertion.Length;
        _textBox.SelectionLength = 0;
        _isApplying = false;

        HideOverlay();
        _textBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Finds the start index of an open "{{" trigger before the cursor.
    /// Returns -1 if no open trigger exists.
    /// </summary>
    private static int FindTriggerPosition(string text, int cursorPos)
    {
        for (int i = cursorPos - 2; i >= 0; i--)
        {
            if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
            {
                string between = text.Substring(i + 2, cursorPos - (i + 2));
                return between.Contains("}}") ? -1 : i;
            }
            if (i + 1 < text.Length && text[i] == '}' && text[i + 1] == '}')
                return -1;
        }
        return -1;
    }

    private void ShowOverlay()
    {
        if (_overlayBorder == null)
            CreateOverlay();

        if (_listView == null)
            return;

        _listView.Items.Clear();
        foreach (string name in _currentSuggestions)
        {
            var item = new ListViewItem
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE8AC", FontSize = 12,
                            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] },
                        new TextBlock
                        {
                            Text = name,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 13
                        }
                    }
                },
                Padding = new Thickness(8, 4, 8, 4),
                MinHeight = 28
            };
            item.Tapped += (_, _) => ApplySuggestion(name);
            _listView.Items.Add(item);
        }

        if (_currentSuggestions.Count > 0)
            _listView.SelectedIndex = 0;

        // Position overlay below the TextBox
        try
        {
            var transform = _textBox.TransformToVisual(_hostGrid);
            var point = transform.TransformPoint(new Point(0, _textBox.ActualHeight + 2));

            _overlayBorder!.Margin = new Thickness(point.X, point.Y, 0, 0);
            _overlayBorder.Width = Math.Max(220, Math.Min(_textBox.ActualWidth, 400));
        }
        catch
        {
            _overlayBorder!.Margin = new Thickness(0);
            _overlayBorder.Width = 220;
        }

        _overlayBorder.Visibility = Visibility.Visible;
    }

    private void CreateOverlay()
    {
        _listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Padding = new Thickness(2),
            MaxHeight = 220,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6)
        };

        _overlayBorder = new Border
        {
            Child = _listView,
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true
        };

        // Add to host grid (last child = on top by z-order)
        _hostGrid.Children.Add(_overlayBorder);
        Grid.SetColumnSpan(_overlayBorder, 99);
    }

    private void HideOverlay()
    {
        if (_overlayBorder != null)
            _overlayBorder.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        _textBox.TextChanged -= OnTextChanged;
        _textBox.PreviewKeyDown -= OnPreviewKeyDown;
        _textBox.LostFocus -= OnLostFocus;
        HideOverlay();
        if (_overlayBorder != null)
            _hostGrid.Children.Remove(_overlayBorder);
    }
}
