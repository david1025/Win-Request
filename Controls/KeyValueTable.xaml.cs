using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest.Controls;

public sealed partial class KeyValueTable : UserControl
{
    private bool _suppressChanged;
    private Func<List<string>>? _variableNamesProvider;
    private Grid? _autoCompleteHostGrid;
    private readonly List<VariableAutoComplete> _autoCompletes = new();

    public KeyValueTable()
    {
        InitializeComponent();
    }

    /// <summary>Raised whenever the user edits a key, value, enabled checkbox, or deletes a row.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>
    /// Enable {{variable}} autocomplete on Value fields.
    /// Call once after InitializeComponent with a function that returns current variable names.
    /// </summary>
    public void EnableVariableAutoComplete(Grid hostGrid, Func<List<string>> getVariableNames)
    {
        _autoCompleteHostGrid = hostGrid;
        _variableNamesProvider = getVariableNames;
    }

    // ── Public API ──────────────────────────────────────────────────

    public void SetItems(IEnumerable<KeyValuePairItem> items)
    {
        _suppressChanged = true;
        RowsPanel.Children.Clear();
        DisposeAutoCompletes();

        foreach (var item in items)
            AddRow(item.Enabled, item.Key, item.Value);

        // Always leave at least one empty row for convenience
        if (RowsPanel.Children.Count == 0)
            AddRow(true, "", "");

        _suppressChanged = false;
    }

    public List<KeyValuePairItem> GetItems()
    {
        var result = new List<KeyValuePairItem>();
        foreach (UIElement child in RowsPanel.Children)
        {
            if (child is not Grid row)
                continue;

            var checkBox = FindChild<CheckBox>(row, "RowCheckBox");
            var keyBox = FindChild<TextBox>(row, "RowKeyBox");
            var valueBox = FindChild<TextBox>(row, "RowValueBox");

            if (keyBox == null)
                continue;

            string key = keyBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key))
                continue;

            result.Add(new KeyValuePairItem
            {
                Key = key,
                Value = valueBox?.Text ?? "",
                Enabled = checkBox?.IsChecked ?? true
            });
        }
        return result;
    }

    // ── Row factory ─────────────────────────────────────────────────

    private void AddRow(bool enabled, string key, string value)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var checkBox = new CheckBox
        {
            Name = "RowCheckBox",
            IsChecked = enabled,
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        checkBox.Checked += Row_FieldChanged;
        checkBox.Unchecked += Row_FieldChanged;
        Grid.SetColumn(checkBox, 0);

        var keyBox = new TextBox
        {
            Name = "RowKeyBox",
            Text = key,
            PlaceholderText = "Key",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 32,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        keyBox.TextChanged += Row_FieldChanged;
        Grid.SetColumn(keyBox, 1);

        var valueBox = new TextBox
        {
            Name = "RowValueBox",
            Text = value,
            PlaceholderText = "Value",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 32,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        valueBox.TextChanged += Row_FieldChanged;
        Grid.SetColumn(valueBox, 2);

        // Attach {{variable}} autocomplete to value box when provider is set
        if (_variableNamesProvider != null && _autoCompleteHostGrid != null)
        {
            var ac = new VariableAutoComplete(valueBox, _autoCompleteHostGrid, _variableNamesProvider);
            _autoCompletes.Add(ac);
        }

        var deleteButton = new Button
        {
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 32,
            MinHeight = 32,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        deleteButton.Content = new FontIcon { Glyph = "\uE74D", FontSize = 11 };
        deleteButton.Click += DeleteRow_Click;
        ToolTipService.SetToolTip(deleteButton, "删除");
        Grid.SetColumn(deleteButton, 3);

        row.Children.Add(checkBox);
        row.Children.Add(keyBox);
        row.Children.Add(valueBox);
        row.Children.Add(deleteButton);

        RowsPanel.Children.Add(row);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void Row_FieldChanged(object sender, object e)
    {
        if (!_suppressChanged)
            ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        // Walk up to find the Grid row
        if (btn.Parent is Grid row)
            RowsPanel.Children.Remove(row);

        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeAutoCompletes()
    {
        foreach (var ac in _autoCompletes)
            ac.Dispose();
        _autoCompletes.Clear();
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        AddRow(true, "", "");
        ItemsChanged?.Invoke(this, EventArgs.Empty);

        // Focus the new key box
        if (RowsPanel.Children.LastOrDefault() is Grid lastRow)
        {
            var keyBox = FindChild<TextBox>(lastRow, "RowKeyBox");
            keyBox?.Focus(FocusState.Programmatic);
        }
    }

    // ── Helper ──────────────────────────────────────────────────────

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T element && element.Name == name)
            return element;

        int count = VisualTreeHelperGetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            var result = FindChild<T>(child, name);
            if (result != null)
                return result;
        }
        return null;
    }

    private static int VisualTreeHelperGetChildrenCount(DependencyObject parent)
    {
        try { return Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); }
        catch { return 0; }
    }
}
