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

    public static readonly DependencyProperty KeyPlaceholderTextProperty =
        DependencyProperty.Register(nameof(KeyPlaceholderText), typeof(string), typeof(KeyValueTable), new PropertyMetadata("Key"));

    public static readonly DependencyProperty ValuePlaceholderTextProperty =
        DependencyProperty.Register(nameof(ValuePlaceholderText), typeof(string), typeof(KeyValueTable), new PropertyMetadata("Value"));

    public static readonly DependencyProperty DescriptionPlaceholderTextProperty =
        DependencyProperty.Register(nameof(DescriptionPlaceholderText), typeof(string), typeof(KeyValueTable), new PropertyMetadata("Description"));

    public KeyValueTable()
    {
        InitializeComponent();
    }

    public event EventHandler? ItemsChanged;

    public string KeyPlaceholderText
    {
        get => (string)GetValue(KeyPlaceholderTextProperty);
        set => SetValue(KeyPlaceholderTextProperty, value);
    }

    public string ValuePlaceholderText
    {
        get => (string)GetValue(ValuePlaceholderTextProperty);
        set => SetValue(ValuePlaceholderTextProperty, value);
    }

    public string DescriptionPlaceholderText
    {
        get => (string)GetValue(DescriptionPlaceholderTextProperty);
        set => SetValue(DescriptionPlaceholderTextProperty, value);
    }

    public void EnableVariableAutoComplete(Grid hostGrid, Func<List<string>> getVariableNames)
    {
        _autoCompleteHostGrid = hostGrid;
        _variableNamesProvider = getVariableNames;
    }

    public void SetItems(IEnumerable<KeyValuePairItem> items)
    {
        _suppressChanged = true;
        RowsPanel.Children.Clear();
        DisposeAutoCompletes();

        foreach (var item in items)
            AddRow(item.Enabled, item.Key, item.Value, item.Description);

        if (RowsPanel.Children.Count == 0)
            AddRow(true, "", "", "");

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
            var descriptionBox = FindChild<TextBox>(row, "RowDescriptionBox");

            if (keyBox == null)
                continue;

            string key = keyBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key))
                continue;

            result.Add(new KeyValuePairItem
            {
                Key = key,
                Value = valueBox?.Text ?? "",
                Description = descriptionBox?.Text ?? "",
                Enabled = checkBox?.IsChecked ?? true
            });
        }
        return result;
    }

    private void AddRow(bool enabled, string key, string value, string description)
    {
        var row = new Grid
        {
            ColumnSpacing = 0,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WorkbenchSubtleStrokeBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var checkBox = new CheckBox
        {
            Name = "RowCheckBox",
            IsChecked = enabled,
            Style = (Style)Resources["CellCheckBoxStyle"]
        };
        checkBox.Checked += Row_FieldChanged;
        checkBox.Unchecked += Row_FieldChanged;
        Grid.SetColumn(checkBox, 0);

        var keyBox = CreateCellTextBox("RowKeyBox", key, KeyPlaceholderText, true);
        keyBox.TextChanged += Row_FieldChanged;
        Grid.SetColumn(keyBox, 1);

        var valueBox = CreateCellTextBox("RowValueBox", value, ValuePlaceholderText, true);
        valueBox.TextChanged += Row_FieldChanged;
        Grid.SetColumn(valueBox, 2);

        var descriptionBox = CreateCellTextBox("RowDescriptionBox", description, DescriptionPlaceholderText, false);
        descriptionBox.TextChanged += Row_FieldChanged;
        Grid.SetColumn(descriptionBox, 3);

        if (_variableNamesProvider != null && _autoCompleteHostGrid != null)
        {
            var ac = new VariableAutoComplete(valueBox, _autoCompleteHostGrid, _variableNamesProvider);
            _autoCompletes.Add(ac);
        }

        var deleteButton = new Button
        {
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 28,
            MinHeight = 28,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        deleteButton.Content = new FontIcon { Glyph = "\uE74D", FontSize = 11 };
        deleteButton.Click += DeleteRow_Click;
        ToolTipService.SetToolTip(deleteButton, "Delete");
        Grid.SetColumn(deleteButton, 4);

        row.Children.Add(checkBox);
        row.Children.Add(keyBox);
        row.Children.Add(valueBox);
        row.Children.Add(descriptionBox);
        row.Children.Add(deleteButton);

        RowsPanel.Children.Add(row);
    }

    private TextBox CreateCellTextBox(string name, string text, string placeholder, bool mono)
    {
        var textBox = new TextBox
        {
            Name = name,
            Text = text,
            PlaceholderText = placeholder,
            Style = (Style)Resources["CellTextBoxStyle"]
        };

        if (mono)
            textBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");

        return textBox;
    }

    private void Row_FieldChanged(object sender, object e)
    {
        if (!_suppressChanged)
            ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

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
        AddRow(true, "", "", "");
        ItemsChanged?.Invoke(this, EventArgs.Empty);

        if (RowsPanel.Children.LastOrDefault() is Grid lastRow)
        {
            var keyBox = FindChild<TextBox>(lastRow, "RowKeyBox");
            keyBox?.Focus(FocusState.Programmatic);
        }
    }

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
