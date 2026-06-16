using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest.Pages;

public sealed partial class WorkspacePage : Page
{
    private const double ExpandedSidebarWidth = 300;
    private const double ExpandedSidebarMinWidth = 260;
    private const double CompactSidebarWidth = 52;
    private readonly WorkspaceStorage _storage = new();
    private readonly RequestExecutionService _executor = new();
    private readonly OpenApiImporter _importer = new();
    private readonly PostmanImporter _postmanImporter = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly ObservableCollection<RequestHistoryEntry> _historyItems = new();
    private ApiWorkspace _workspace = new();
    private ApiCollection? _currentCollection;
    private ApiRequest? _currentRequest;
    private readonly List<ApiRequest> _unsavedRequests = new();
    private readonly Dictionary<string, string> _historyTabMap = new();
    private readonly HashSet<string> _expandedTreeNodeIds = new();
    private bool _hasLoadedCollectionTree;
    private bool _isCollectionTreeFilterActive;
    private CollectionNode? _selectedNode;
    private CollectionNode? _contextMenuNode;
    private CancellationTokenSource? _sendCancellation;
    private bool _isLoadingEditor;
    private bool _isUpdatingTabs;
    private bool _isSyncingQueryUrl;
    private bool _isNavigatingToSettings;
    private bool _isLoadingInlineSettings;
    private bool _isApplyingBodyHighlight;
    private string _rightClickTabId = "";
    private VariableAutoComplete? _urlAutoComplete;
    private readonly DispatcherTimer _highlightDebounceTimer;
    private EnvironmentProfile? _editingEnvironment;

    public WorkspacePage()
    {
        _highlightDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _highlightDebounceTimer.Tick += (_, _) =>
        {
            _highlightDebounceTimer.Stop();
            RefreshBodyHighlight();
        };

        InitializeComponent();
        MainNav.SelectedItem = CollectionsNavItem;
        HeadersTable.ItemsChanged += Table_ItemsChanged;
        QueryTable.ItemsChanged += Table_ItemsChanged;
        FormDataTable.ItemsChanged += Table_ItemsChanged;
        UrlEncodedTable.ItemsChanged += Table_ItemsChanged;
        EnvironmentTable.ItemsChanged += EnvironmentTable_ItemsChanged;

        // Enable {{variable}} autocomplete on all relevant controls
        EnvironmentTable.EnableVariableAutoComplete(AutoCompleteHost, GetEnvironmentVariableNames);
        HeadersTable.EnableVariableAutoComplete(AutoCompleteHost, GetEnvironmentVariableNames);
        QueryTable.EnableVariableAutoComplete(AutoCompleteHost, GetEnvironmentVariableNames);
        FormDataTable.EnableVariableAutoComplete(AutoCompleteHost, GetEnvironmentVariableNames);
        UrlEncodedTable.EnableVariableAutoComplete(AutoCompleteHost, GetEnvironmentVariableNames);

        Loaded += WorkspacePage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isNavigatingToSettings = true;
        MainNav.SelectedItem = CollectionsNavItem;
        _isNavigatingToSettings = false;
    }

    private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isNavigatingToSettings)
            return;

        if (args.IsSettingsSelected)
        {
            _isNavigatingToSettings = true;
            SelectSidebarSection("");
            ShowInlineSettings();
            _isNavigatingToSettings = false;
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item)
            return;
        HideInlineSettings();
        SelectSidebarSection(item.Tag?.ToString() ?? "collections");
    }

    private void OpenSettingsSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        ShowInlineSettings();
    }

    private void SelectSidebarSection(string section)
    {
        var panelMap = new Dictionary<string, Grid>
        {
            ["collections"] = CollectionsSidebarPanel,
            ["environments"] = EnvironmentsSidebarPanel,
            ["history"] = HistorySidebarPanel
        };

        // If section is empty or unknown, collapse all panels
        if (string.IsNullOrEmpty(section) || !panelMap.ContainsKey(section))
        {
            foreach (var item in panelMap)
                item.Value.Visibility = Visibility.Collapsed;
            // Also collapse the legacy settings sidebar panel
            SettingsSidebarPanel.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(section))
                return;
            section = "collections";
        }

        foreach (var item in panelMap)
            item.Value.Visibility = item.Key == section ? Visibility.Visible : Visibility.Collapsed;
        SettingsSidebarPanel.Visibility = Visibility.Collapsed;

        // Hide environment overlay when switching sections
        if (section != "environments")
        {
            EnvironmentOverlayPanel.Visibility = Visibility.Collapsed;
            if (_editingEnvironment != null)
            {
                ApplyEnvironmentEditor();
                _editingEnvironment = null;
            }
            RequestEditorArea.Visibility = Visibility.Visible;
        }
    }

    private void ShowInlineSettings()
    {
        LoadInlineSettings();
        RequestEditorArea.Visibility = Visibility.Collapsed;
        EnvironmentOverlayPanel.Visibility = Visibility.Collapsed;
        SettingsOverlayPanel.Visibility = Visibility.Visible;
        // Keep the compact navigation rail visible, but hide the sidebar content.
        RootGrid.ColumnDefinitions[0].MinWidth = CompactSidebarWidth;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(CompactSidebarWidth);
        RootGrid.ColumnDefinitions[1].Width = new GridLength(0);
    }

    private void HideInlineSettings()
    {
        SettingsOverlayPanel.Visibility = Visibility.Collapsed;
        EnvironmentOverlayPanel.Visibility = Visibility.Collapsed;
        RequestEditorArea.Visibility = Visibility.Visible;
        // Restore the full navigation/sidebar columns.
        RootGrid.ColumnDefinitions[0].MinWidth = ExpandedSidebarMinWidth;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(ExpandedSidebarWidth);
        RootGrid.ColumnDefinitions[1].Width = GridLength.Auto;
    }

    private void LoadInlineSettings()
    {
        _isLoadingInlineSettings = true;
        var settings = _workspace.Settings;
        InlineFontFamilyBox.Text = settings.FontFamily;
        InlineTextSizeBox.Value = settings.TextSize < 9 ? 13 : settings.TextSize;
        InlineGitHubOwnerBox.Text = settings.GitHubOwner;
        InlineGitHubRepositoryBox.Text = settings.GitHubRepository;
        SelectInlineTheme(settings.Theme);
        _isLoadingInlineSettings = false;
    }

    private void SelectInlineTheme(string theme)
    {
        foreach (ComboBoxItem item in InlineThemeComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.OrdinalIgnoreCase))
            {
                InlineThemeComboBox.SelectedItem = item;
                return;
            }
        }
        InlineThemeComboBox.SelectedIndex = 0;
    }

    private void ApplyInlineSettings()
    {
        if (_isLoadingInlineSettings)
            return;

        if (InlineThemeComboBox == null || InlineFontFamilyBox == null || InlineTextSizeBox == null
            || InlineGitHubOwnerBox == null || InlineGitHubRepositoryBox == null || _workspace?.Settings == null)
            return;

        _workspace.Settings.Theme = InlineThemeComboBox.SelectedItem is ComboBoxItem ti
            ? ti.Tag?.ToString() ?? "System" : "System";
        _workspace.Settings.FontFamily = string.IsNullOrWhiteSpace(InlineFontFamilyBox.Text)
            ? "Consolas" : InlineFontFamilyBox.Text.Trim();
        _workspace.Settings.TextSize = double.IsNaN(InlineTextSizeBox.Value) ? 13 : InlineTextSizeBox.Value;
        _workspace.Settings.GitHubOwner = InlineGitHubOwnerBox.Text.Trim();
        _workspace.Settings.GitHubRepository = InlineGitHubRepositoryBox.Text.Trim();
        App.Current.ApplySettings(_workspace.Settings);
    }

    private void InlineSettings_Changed(object sender, SelectionChangedEventArgs e) => ApplyInlineSettings();
    private void InlineSettingsText_Changed(object sender, TextChangedEventArgs e) => ApplyInlineSettings();
    private void InlineTextSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyInlineSettings();

    private async void InlineSaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyInlineSettings();
        await PersistWorkspaceAsync();
        ApplyEditorFonts();
        InlineSettingsStatusText.Text = "设置已保存。工作台编辑器会在打开或切换请求时使用新的字体设置。";
    }

    private async void InlineCheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyInlineSettings();
        await PersistWorkspaceAsync();
        InlineUpdateProgressRing.Visibility = Visibility.Visible;
        InlineUpdateProgressRing.IsActive = true;
        InlineUpdateResultText.Text = "正在检查 GitHub 最新 Release...";

        var result = await _updateService.CheckLatestReleaseAsync(
            _workspace.Settings.GitHubOwner,
            _workspace.Settings.GitHubRepository);

        InlineUpdateProgressRing.IsActive = false;
        InlineUpdateProgressRing.Visibility = Visibility.Collapsed;
        if (result.IsSuccess)
        {
            string title = string.IsNullOrWhiteSpace(result.Name) ? result.TagName : $"{result.Name} ({result.TagName})";
            InlineUpdateResultText.Text = $"最新版本：{title}\n发布时间：{result.PublishedAt}\n地址：{result.HtmlUrl}";
        }
        else
        {
            InlineUpdateResultText.Text = $"检查失败：{result.Message}";
        }
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        _isNavigatingToSettings = true;
        MainNav.SelectedItem = CollectionsNavItem;
        _isNavigatingToSettings = false;
        HideInlineSettings();
        SelectSidebarSection("collections");
    }

    private async void WorkspacePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspacePage_Loaded;
        _workspace = await _storage.LoadAsync();
        App.Current.ApplySettings(_workspace.Settings);
        ApplyEditorFonts();
        _isLoadingEditor = true;
        RefreshEnvironmentList();
        _isLoadingEditor = false;

        // Initialize {{variable}} autocomplete on URL box
        _urlAutoComplete = new VariableAutoComplete(UrlBox, AutoCompleteHost, GetEnvironmentVariableNames);

        _currentCollection = _workspace.Collections.FirstOrDefault();
        RefreshCollectionSelector();
        RefreshLists();
        RestoreOpenRequestTabs();
        if (_workspace.OpenRequestTabIds.Count == 0)
            ClearEditor();
    }

    private void ApplyEditorFonts()
    {
        AppSettingsApplier.ApplyEditorFont(BodyBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(ResponseHeadersBox, _workspace.Settings);

        // RichTextBlock for response body
        string ff = string.IsNullOrWhiteSpace(_workspace.Settings.FontFamily) ? "Consolas" : _workspace.Settings.FontFamily.Trim();
        ResponseBodyRichTextBlock.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ff);
        ResponseBodyRichTextBlock.FontSize = _workspace.Settings.TextSize < 9 ? 9 : _workspace.Settings.TextSize;
    }

    private void RefreshCollectionSelector()
    {
        string selectedId = _currentCollection?.Id ?? "";
        _currentCollection = _workspace.Collections.FirstOrDefault(x => x.Id == selectedId)
            ?? _workspace.Collections.FirstOrDefault();
    }

    private void RefreshLists()
    {
        RefreshTree();

        _historyItems.Clear();
        foreach (var item in _workspace.History.OrderByDescending(x => x.Timestamp).Take(200))
            _historyItems.Add(item);

        BuildGroupedHistory();
    }

    private void RefreshHistoryList()
    {
        _historyItems.Clear();
        foreach (var item in _workspace.History.OrderByDescending(x => x.Timestamp).Take(200))
            _historyItems.Add(item);

        BuildGroupedHistory();
    }

    private void RefreshTree()
    {
        bool useDefaultExpansion = !_hasLoadedCollectionTree;
        string filter = RequestSearchBox?.Text.Trim() ?? "";
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);

        if (!_isCollectionTreeFilterActive)
            CaptureExpandedTreeNodes();

        RequestTreeView.RootNodes.Clear();

        foreach (var collection in _workspace.Collections)
        {
            var root = BuildCollectionRoot(collection, filter, useDefaultExpansion);
            RequestTreeView.RootNodes.Add(root);
        }

        _hasLoadedCollectionTree = true;
        _isCollectionTreeFilterActive = hasFilter;

        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
    }

    private TreeViewNode BuildCollectionRoot(ApiCollection collection, string filter, bool useDefaultExpansion)
    {
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        var childItems = new List<TreeViewNode>();
        foreach (var node in collection.Nodes)
        {
            var childItem = BuildTreeItem(node, filter, useDefaultExpansion);
            if (childItem != null)
                childItems.Add(childItem);
        }

        var rootCollectionNode = new CollectionNode
        {
            Id = collection.Id,
            Name = collection.Name,
            IsFolder = true,
            Children = collection.Nodes
        };

        var rootNode = new TreeViewNode
        {
            Content = rootCollectionNode,
            IsExpanded = hasFilter || useDefaultExpansion || _expandedTreeNodeIds.Contains(collection.Id)
        };
        foreach (var child in childItems)
            rootNode.Children.Add(child);

        return rootNode;
    }

    private TreeViewNode? BuildTreeItem(CollectionNode node, string filter, bool useDefaultExpansion)
    {
        if (node.IsFolder)
        {
            var childItems = new List<TreeViewNode>();
            foreach (var child in node.Children)
            {
                var childItem = BuildTreeItem(child, filter, useDefaultExpansion);
                if (childItem != null)
                    childItems.Add(childItem);
            }

            // Show folder if no filter, or if it has matching children, or folder name matches
            bool hasFilter = !string.IsNullOrWhiteSpace(filter);
            bool folderMatches = hasFilter && node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            if (hasFilter && childItems.Count == 0 && !folderMatches)
                return null;

            var folderTvItem = new TreeViewNode
            {
                Content = node,
                IsExpanded = hasFilter || _expandedTreeNodeIds.Contains(node.Id) || (useDefaultExpansion && childItems.Count <= 20)
            };
            foreach (var child in childItems)
                folderTvItem.Children.Add(child);

            return folderTvItem;
        }
        else
        {
            // Request node
            if (!string.IsNullOrWhiteSpace(filter) && !MatchesNodeFilter(node, filter))
                return null;

            return new TreeViewNode { Content = node };
        }
    }

    private void CaptureExpandedTreeNodes()
    {
        foreach (TreeViewNode root in RequestTreeView.RootNodes)
            CaptureExpandedTreeNode(root);
    }

    private void CaptureExpandedTreeNode(TreeViewNode tvNode)
    {
        if (tvNode.Content is CollectionNode node && node.IsFolder)
        {
            if (tvNode.IsExpanded)
                _expandedTreeNodeIds.Add(node.Id);
            else
                _expandedTreeNodeIds.Remove(node.Id);
        }

        foreach (TreeViewNode child in tvNode.Children)
            CaptureExpandedTreeNode(child);
    }

    private static bool MatchesNodeFilter(CollectionNode node, string filter)
    {
        if (node.Request == null)
            return false;
        var req = node.Request;
        return Contains(req.Name, filter) ||
               Contains(req.Method, filter) ||
               Contains(req.Url, filter);
    }

    private void RequestTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_isUpdatingTabs)
            return;

        if (args.InvokedItem is not TreeViewNode tvNode || tvNode.Content is not CollectionNode node)
            return;

        _selectedNode = node;

        var owningCollection = FindOwningCollection(node);
        if (owningCollection != null)
            _currentCollection = owningCollection;

        if (!node.IsFolder && node.Request != null)
            OpenRequestTab(node.Request.Id, true);
    }

    private void SelectRequestInTree(string requestId)
    {
        _isUpdatingTabs = true;
        foreach (TreeViewNode root in RequestTreeView.RootNodes)
        {
            foreach (TreeViewNode child in root.Children)
            {
                if (FindAndSelectNode(child, requestId))
                    break;
            }
        }
        _isUpdatingTabs = false;
    }

    private bool FindAndSelectNode(TreeViewNode tvNode, string requestId)
    {
        if (tvNode.Content is CollectionNode node && !node.IsFolder && node.Request?.Id == requestId)
        {
            RequestTreeView.SelectedNode = tvNode;
            return true;
        }
        foreach (TreeViewNode child in tvNode.Children)
        {
            if (FindAndSelectNode(child, requestId))
                return true;
        }
        return false;
    }
    
    
    private void BuildGroupedHistory()
    {
        HistoryTreeView.RootNodes.Clear();

        string filter = HistorySearchBox?.Text.Trim() ?? "";

        var items = string.IsNullOrEmpty(filter)
            ? _historyItems.ToList()
            : _historyItems.Where(x =>
                (x.Method?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (x.Url?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (x.RequestName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)).ToList();

        var groups = items
            .GroupBy(x => x.Timestamp.Date)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                DateTime today = DateTime.Today;
                string label = g.Key == today ? "Today"
                    : g.Key == today.AddDays(-1) ? "Yesterday"
                    : g.Key.ToString("MMM dd");
                return new HistoryDateGroup
                {
                    DateLabel = label,
                    Count = g.Count(),
                    Entries = g.OrderBy(x => x.Timestamp).Reverse().ToList()
                };
            }).ToList();

        foreach (var group in groups)
        {
            var dateNode = new TreeViewNode
            {
                Content = group,
                IsExpanded = true
            };

            foreach (var entry in group.Entries)
            {
                dateNode.Children.Add(new TreeViewNode { Content = entry });
            }

            HistoryTreeView.RootNodes.Add(dateNode);
        }
    }

    private void HistoryTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node)
            return;

        if (node.Content is RequestHistoryEntry entry)
        {
            OpenHistoryEntryAsTab(entry);
        }
    }

    private void OpenHistoryEntryAsTab(RequestHistoryEntry entry)
    {
        // Check if this history entry already has an open tab
        if (_historyTabMap.TryGetValue(entry.Id, out string? existingId) &&
            !string.IsNullOrEmpty(existingId) &&
            _workspace.OpenRequestTabIds.Contains(existingId))
        {
            _workspace.ActiveRequestTabId = existingId;
            RefreshOpenRequestTabs();
            LoadRequestById(existingId);
            _ = PersistWorkspaceAsync();
            return;
        }

        string baseUrl = entry.Url ?? "";
        try
        {
            var uri = new Uri(baseUrl);
            baseUrl = uri.GetLeftPart(UriPartial.Path);
        }
        catch { }

        var request = new ApiRequest
        {
            Name = $"{entry.Method} {baseUrl}",
            Type = ApiRequestType.Http,
            Method = entry.Method ?? "GET",
            Url = entry.Url ?? "",
            Body = entry.RequestBody ?? ""
        };

        _unsavedRequests.Add(request);
        _historyTabMap[entry.Id] = request.Id;
        _currentRequest = request;
        OpenRequestTab(request.Id, true);
        RefreshLists();
    }
    
    private void LoadEditor(ApiRequest request)
    {
        _isLoadingEditor = true;
        NameBox.Text = request.Name;
        UrlBox.Text = request.Url;
        HeadersTable.SetItems(request.Headers);
        QueryTable.SetItems(request.Query);
        FormDataTable.SetItems(request.FormData);
        UrlEncodedTable.SetItems(request.UrlEncodedData);
        SetBodyText(request.Body);
        BinaryFilePathText.Text = request.BinaryFilePath ?? "";
        SelectBodyType(request.BodyType);
        SelectType(request.Type);
        SelectMethod(request.Method);
        UpdateTypeVisibility(request.Type);
        UpdateBodyVisibility();
        UpdateBreadcrumb();
        UpdateEmptyWorkspaceVisibility();
        _isLoadingEditor = false;
    }

    private void LoadRequestById(string requestId)
    {
        var match = FindRequest(requestId);
        if (match.Request == null)
            return;

        if (match.Collection != null)
            _currentCollection = match.Collection;
        _currentRequest = match.Request;
        _workspace.ActiveRequestTabId = requestId;
        if (match.Collection != null)
        {
            RefreshCollectionSelector();
            RefreshLists();
        }
        LoadEditor(match.Request);
        if (match.Collection != null)
            SelectRequestInList(requestId);
    }

    private void ClearEditor()
    {
        _isLoadingEditor = true;
        NameBox.Text = "";
        UrlBox.Text = "";
        HeadersTable.SetItems(Array.Empty<KeyValuePairItem>());
        QueryTable.SetItems(Array.Empty<KeyValuePairItem>());
        FormDataTable.SetItems(Array.Empty<KeyValuePairItem>());
        UrlEncodedTable.SetItems(Array.Empty<KeyValuePairItem>());
        SetBodyText("");
        BinaryFilePathText.Text = "";
        SelectBodyType(ApiBodyType.None);
        SelectType(ApiRequestType.Http);
        SelectMethod("GET");
        UpdateTypeVisibility(ApiRequestType.Http);
        UpdateBodyVisibility();
        UpdateBreadcrumb();
        UpdateEmptyWorkspaceVisibility();
        _isLoadingEditor = false;
    }

    private void ApplyEditor()
    {
        if (_isLoadingEditor || _currentRequest == null)
            return;

        _currentRequest.Name = NameBox.Text.Trim();
        _currentRequest.Url = UrlBox.Text.Trim();
        _currentRequest.Body = GetBodyText();
        _currentRequest.BodyType = GetSelectedBodyType();
        _currentRequest.Headers = HeadersTable.GetItems();
        _currentRequest.Query = QueryTable.GetItems();
        _currentRequest.FormData = FormDataTable.GetItems();
        _currentRequest.UrlEncodedData = UrlEncodedTable.GetItems();
        _currentRequest.BinaryFilePath = BinaryFilePathText.Text?.Trim() ?? "";

        if (TypeComboBox.SelectedItem is ComboBoxItem typeItem &&
            Enum.TryParse<ApiRequestType>(typeItem.Tag?.ToString(), out var type))
        {
            _currentRequest.Type = type;
        }

        if (MethodComboBox.SelectedItem is ComboBoxItem methodItem)
            _currentRequest.Method = methodItem.Content?.ToString() ?? "GET";
    }

    // Collection name editing removed from toolbar

    private async void AddCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        var collection = new ApiCollection { Name = $"集合 {_workspace.Collections.Count + 1}" };
        _workspace.Collections.Add(collection);
        _currentCollection = collection;
        _currentRequest = null;
        ClearEditor();
        RefreshCollectionSelector();
        RefreshLists();
        await PersistWorkspaceAsync();
    }

    private async void DeleteCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null)
            return;

        var deletedRequestIds = RequestHelpers.GetAllRequestNodes(_currentCollection.Nodes)
            .Where(x => x.Request != null)
            .Select(x => x.Request!.Id)
            .ToHashSet();
        _workspace.Collections.Remove(_currentCollection);
        _workspace.OpenRequestTabIds.RemoveAll(deletedRequestIds.Contains);
        if (deletedRequestIds.Contains(_workspace.ActiveRequestTabId))
            _workspace.ActiveRequestTabId = "";
        if (_workspace.Collections.Count == 0)
            _workspace.Collections.Add(new ApiCollection { Name = "默认集合" });

        _currentCollection = _workspace.Collections.FirstOrDefault();
        _currentRequest = null;
        ClearEditor();
        RefreshCollectionSelector();
        RefreshLists();
        RefreshOpenRequestTabs();
        await PersistWorkspaceAsync();
    }

    private void AddRequestButton_Click(object sender, RoutedEventArgs e) => CreateUnsavedRequestTab();

    private CollectionNode? GetTargetFolder()
    {
        if (_currentCollection == null)
            return null;
        if (_selectedNode != null && _selectedNode.IsFolder)
            return _selectedNode;
        return null;
    }

    private void CreateUnsavedRequestTab()
    {
        var request = new ApiRequest
        {
            Name = "新建请求",
            Type = ApiRequestType.Http,
            Method = "GET",
            Url = "https://"
        };

        _unsavedRequests.Add(request);
        _currentRequest = request;
        OpenRequestTab(request.Id, true);
        RefreshLists();
    }

    private bool IsUnsavedRequest(string requestId)
    {
        return _unsavedRequests.Any(x => string.Equals(x.Id, requestId, StringComparison.Ordinal));
    }

    private ApiRequest? FindUnsavedRequest(string requestId)
    {
        return _unsavedRequests.FirstOrDefault(x => string.Equals(x.Id, requestId, StringComparison.Ordinal));
    }

    private void RemoveUnsavedRequestIfClosed(string requestId)
    {
        if (!_workspace.OpenRequestTabIds.Contains(requestId))
        {
            _unsavedRequests.RemoveAll(x => string.Equals(x.Id, requestId, StringComparison.Ordinal));
            // Clean up history tab mapping
            var staleKey = _historyTabMap.FirstOrDefault(kv => kv.Value == requestId).Key;
            if (!string.IsNullOrEmpty(staleKey))
                _historyTabMap.Remove(staleKey);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _currentCollection ??= _workspace.Collections.FirstOrDefault();
        if (_currentCollection == null)
        {
            _currentCollection = new ApiCollection { Name = "默认集合" };
            _workspace.Collections.Add(_currentCollection);
        }

        string folderName = await ShowInputDialogAsync("新建文件夹", "文件夹名称", "新建文件夹");
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        var folder = new CollectionNode
        {
            Name = folderName,
            IsFolder = true
        };

        var targetFolder = GetTargetFolder();
        var targetList = targetFolder != null ? targetFolder.Children : _currentCollection.Nodes;
        targetList.Add(folder);

        _selectedNode = folder;
        await PersistWorkspaceAsync();
        RefreshLists();
    }

    private async void DeleteRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _currentRequest == null)
            return;

        string deletedId = _currentRequest.Id;

        var parentList = RequestHelpers.FindParentList(_currentCollection.Nodes, deletedId);
        if (parentList != null)
        {
            var node = parentList.FirstOrDefault(x => !x.IsFolder && x.Request?.Id == deletedId);
            if (node != null)
                parentList.Remove(node);
        }

        _workspace.OpenRequestTabIds.Remove(deletedId);
        if (_workspace.ActiveRequestTabId == deletedId)
            _workspace.ActiveRequestTabId = "";

        // Find next request to select
        var allRequests = RequestHelpers.GetAllRequestNodes(_currentCollection.Nodes).ToList();
        _currentRequest = allRequests.Count > 0 ? allRequests[0].Request : null;

        await PersistWorkspaceAsync();
        RefreshLists();
        RefreshOpenRequestTabs();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        else
            ClearEditor();
    }

    private async void DuplicateRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _currentRequest == null)
            return;

        ApplyEditor();

        var parentList = RequestHelpers.FindParentList(_currentCollection.Nodes, _currentRequest.Id);
        if (parentList == null)
            return;

        var originalNode = parentList.FirstOrDefault(x => !x.IsFolder && x.Request?.Id == _currentRequest.Id);
        if (originalNode == null)
            return;

        var clonedNode = RequestHelpers.CloneNode(originalNode);
        clonedNode.Name = string.IsNullOrWhiteSpace(clonedNode.Name) ? "复制的请求" : $"{clonedNode.Name} Copy";
        if (clonedNode.Request != null)
            clonedNode.Request.Name = clonedNode.Name;

        parentList.Add(clonedNode);
        _currentRequest = clonedNode.Request;
        await PersistWorkspaceAsync();
        RefreshLists();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        if (_currentRequest != null)
            OpenRequestTab(_currentRequest.Id, true);
    }

    private async void ImportSwaggerButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        try
        {
            var collection = await _importer.ImportAsync(file.Path);

            _workspace.Collections.Add(collection);
            _currentCollection = collection;
            await PersistWorkspaceAsync();

            var firstRequestNode = RequestHelpers.GetAllRequestNodes(collection.Nodes).FirstOrDefault();
            if (firstRequestNode?.Request != null)
                ExpandCollectionPathToRequest(collection, firstRequestNode.Request.Id);

            RefreshCollectionSelector();
            RefreshLists();

            if (firstRequestNode?.Request != null)
                SelectRequestInTree(firstRequestNode.Request.Id);
        }
        catch (Exception ex)
        {
            DisplayResponseBody(ex.ToString());
        }
    }

    private async void ImportPostmanButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        try
        {
            var collection = await _postmanImporter.ImportAsync(file.Path);

            if (collection.ImportedEnvironment != null &&
                collection.ImportedEnvironment.Variables.Count > 0)
            {
                var env = _workspace.GetActiveEnvironment();
                if (env != null)
                {
                    foreach (var v in collection.ImportedEnvironment.Variables)
                    {
                        var existing = env.Variables.FirstOrDefault(x =>
                            string.Equals(x.Key, v.Key, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                            existing.Value = v.Value;
                        else
                            env.Variables.Add(new KeyValuePairItem
                            {
                                Key = v.Key,
                                Value = v.Value,
                                Enabled = true
                            });
                    }
                }
                collection.ImportedEnvironment = null;
            }

            _workspace.Collections.Add(collection);
            _currentCollection = collection;
            await PersistWorkspaceAsync();

            var firstRequestNode = RequestHelpers.GetAllRequestNodes(collection.Nodes).FirstOrDefault();
            if (firstRequestNode?.Request != null)
                ExpandCollectionPathToRequest(collection, firstRequestNode.Request.Id);

            RefreshCollectionSelector();
            RefreshLists();
            RefreshEnvironmentList();

            if (firstRequestNode?.Request != null)
                SelectRequestInTree(firstRequestNode.Request.Id);
        }
        catch (Exception ex)
        {
            DisplayResponseBody(ex.ToString());
        }
    }

    private void ExpandCollectionPathToRequest(ApiCollection collection, string requestId)
    {
        _expandedTreeNodeIds.Add(collection.Id);
        AddExpandedFolderPath(collection.Nodes, requestId);
    }

    private bool AddExpandedFolderPath(IEnumerable<CollectionNode> nodes, string requestId)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder)
            {
                if (node.Request?.Id == requestId)
                    return true;
                continue;
            }

            if (AddExpandedFolderPath(node.Children, requestId))
            {
                _expandedTreeNodeIds.Add(node.Id);
                return true;
            }
        }

        return false;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest == null)
            return;

        ApplyEditor();
        ApplyEnvironmentEditor();

        // Check if the request already exists in a collection
        var match = FindRequest(_currentRequest.Id);
        bool isInCollection = match.Collection != null && match.Request != null;

        if (isInCollection)
        {
            // Already in collection — save directly, update node name
            if (_currentCollection != null)
            {
                var node = RequestHelpers.FindNodeById(_currentCollection.Nodes, _currentRequest.Id);
                if (node != null)
                    node.Name = string.IsNullOrWhiteSpace(_currentRequest.Name) ? "Untitled" : _currentRequest.Name;
            }
            await PersistWorkspaceAsync();
            RefreshHistoryList();
            SelectRequestInTree(_currentRequest.Id);
        }
        else
        {
            // Temporary / unsaved request — show dialog to pick target
            var target = await ShowSaveTargetDialogAsync();
            if (target == null)
                return;

            SaveCurrentRequestToTarget(target);
            await PersistWorkspaceAsync();
            RefreshCollectionSelector();
            RefreshLists();
            RefreshOpenRequestTabs();
            SelectRequestInTree(_currentRequest.Id);
        }
    }

    private void SaveCurrentRequestToTarget(SaveTarget target)
    {
        if (_currentRequest == null)
            return;

        RemoveRequestFromCollections(_currentRequest.Id);

        var requestNode = new CollectionNode
        {
            Name = string.IsNullOrWhiteSpace(_currentRequest.Name) ? "Untitled" : _currentRequest.Name,
            IsFolder = false,
            Request = _currentRequest
        };

        var targetList = target.Folder != null ? target.Folder.Children : target.Collection.Nodes;
        targetList.Add(requestNode);
        _unsavedRequests.RemoveAll(x => string.Equals(x.Id, _currentRequest.Id, StringComparison.Ordinal));
        _currentCollection = target.Collection;
        _workspace.ActiveRequestTabId = _currentRequest.Id;
    }

    private void RemoveRequestFromCollections(string requestId)
    {
        foreach (var collection in _workspace.Collections)
        {
            var parentList = RequestHelpers.FindParentList(collection.Nodes, requestId);
            var node = parentList?.FirstOrDefault(x => !x.IsFolder && x.Request?.Id == requestId);
            if (parentList != null && node != null)
            {
                parentList.Remove(node);
                return;
            }
        }
    }

    private async Task<SaveTarget?> ShowSaveTargetDialogAsync()
    {
        if (_workspace.Collections.Count == 0)
            _workspace.Collections.Add(new ApiCollection { Name = "默认集合" });

        var tree = new TreeView
        {
            SelectionMode = TreeViewSelectionMode.Single,
            MinHeight = 260,
            MaxHeight = 420
        };

        foreach (var collection in _workspace.Collections)
        {
            var root = new TreeViewNode
            {
                Content = new SaveTarget(collection, null),
                IsExpanded = true
            };
            AddFolderTargets(root, collection, collection.Nodes);
            tree.RootNodes.Add(root);
        }

        if (tree.RootNodes.Count > 0)
            tree.SelectedNode = tree.RootNodes[0];

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "保存请求到 Collection",
            Content = tree,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && tree.SelectedNode?.Content is SaveTarget target
            ? target
            : null;
    }

    private static void AddFolderTargets(TreeViewNode parent, ApiCollection collection, IEnumerable<CollectionNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder)
                continue;

            var child = new TreeViewNode
            {
                Content = new SaveTarget(collection, node),
                IsExpanded = true
            };
            AddFolderTargets(child, collection, node.Children);
            parent.Children.Add(child);
        }
    }

    private sealed class SaveTarget
    {
        public SaveTarget(ApiCollection collection, CollectionNode? folder)
        {
            Collection = collection;
            Folder = folder;
        }

        public ApiCollection Collection { get; }
        public CollectionNode? Folder { get; }
        public override string ToString() => Folder?.Name ?? Collection.Name;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sendCancellation != null)
        {
            _sendCancellation.Cancel();
            ResponseSummaryText.Text = "正在取消...";
            return;
        }

        if (_currentRequest == null)
            return;

        ApplyEditor();
        ApplyEnvironmentEditor();
        await PersistWorkspaceAsync();
        await SendCurrentAsync();
    }

    private async Task SendCurrentAsync()
    {
        if (_currentRequest == null)
            return;

        if (_sendCancellation != null)
            return;

        _sendCancellation = new CancellationTokenSource();
        SendProgressRing.Visibility = Visibility.Visible;
        SendProgressRing.IsActive = true;
        SendButtonText.Text = "Cancel";
        ResponseSummaryText.Text = "发送中...";
        SetResponseMetrics(null, null, null, false);
        RichTextHelper.ApplyPlainText(ResponseBodyRichTextBlock, "");
        ResponseHeadersBox.Text = "";

        try
        {
            ApiRequest resolvedRequest = EnvironmentVariableResolver.Resolve(_currentRequest, _workspace.GetActiveVariables());
            ApiResponse response = await _executor.ExecuteAsync(resolvedRequest, _sendCancellation.Token);
            if (_sendCancellation.IsCancellationRequested)
            {
                ResponseSummaryText.Text = "已取消";
                return;
            }

            string displayBody = JsonFormatService.TryFormat(response.Body, out string formattedResponse)
                ? formattedResponse
                : response.Body;

            ResponseSummaryText.Text = response.Summary;
            SetResponseMetrics(response.StatusText, response.ElapsedMilliseconds, response.BodyBytes, response.IsSuccess);
            DisplayResponseBody(displayBody);
            ResponseHeadersBox.Text = response.Headers;

            _workspace.History.Add(new RequestHistoryEntry
            {
                RequestName = _currentRequest.Name,
                Type = _currentRequest.Type,
                Method = _currentRequest.Method,
                Url = RequestHelpers.BuildUrl(resolvedRequest),
                StatusCode = response.StatusCode,
                ElapsedMilliseconds = response.ElapsedMilliseconds,
                IsSuccess = response.IsSuccess,
                RequestBody = resolvedRequest.Body,
                ResponseHeaders = response.Headers,
                ResponseBody = response.Body
            });

            if (_workspace.History.Count > 500)
                _workspace.History = _workspace.History.OrderByDescending(x => x.Timestamp).Take(500).ToList();

            await PersistWorkspaceAsync();
            RefreshHistoryList();
        }
        finally
        {
            _sendCancellation.Dispose();
            _sendCancellation = null;
            SendButtonText.Text = "Send";
            SendProgressRing.IsActive = false;
            SendProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingEditor)
            return;

        ApplyEditor();
        if (_currentRequest != null)
        {
            UpdateTypeVisibility(_currentRequest.Type);
            UpdateBreadcrumb();
        }
    }

    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyEditor();
    private void Table_ItemsChanged(object? sender, EventArgs e)
    {
        if (_isLoadingEditor || _currentRequest == null)
            return;

        // Only save the specific table's data without calling full ApplyEditor.
        // This avoids triggering SyncUrlToQueryTable -> QueryTable.SetItems -> row recreation -> focus loss.
        if (ReferenceEquals(sender, HeadersTable))
            _currentRequest.Headers = HeadersTable.GetItems();
        else if (ReferenceEquals(sender, QueryTable))
        {
            _currentRequest.Query = QueryTable.GetItems();
            SyncQueryTableToUrl();
        }
        else if (ReferenceEquals(sender, FormDataTable))
            _currentRequest.FormData = FormDataTable.GetItems();
        else if (ReferenceEquals(sender, UrlEncodedTable))
            _currentRequest.UrlEncodedData = UrlEncodedTable.GetItems();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyEditor();
        UpdateCurrentTabHeader();
        if (ReferenceEquals(sender, NameBox))
            UpdateBreadcrumb();
    }

    private void BodyBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingBodyHighlight)
            return;

        ApplyEditor();
        ScheduleBodyHighlight();
    }

    private void ScheduleBodyHighlight()
    {
        _highlightDebounceTimer.Stop();
        _highlightDebounceTimer.Start();
    }

    private string GetBodyText()
    {
        if (BodyBox?.Document == null)
            return "";

        BodyBox.Document.GetText(TextGetOptions.None, out string text);
        return NormalizeRichEditText(text);
    }

    private void SetBodyText(string text)
    {
        if (BodyBox?.Document == null)
            return;

        try
        {
            _isApplyingBodyHighlight = true;
            BodyBox.Document.SetText(TextSetOptions.None, text ?? "");
        }
        finally
        {
            _isApplyingBodyHighlight = false;
        }
        RefreshBodyHighlight();
    }

    private static string NormalizeRichEditText(string text)
    {
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private void UpdateCurrentTabHeader()
    {
        if (_currentRequest == null)
            return;

        // Update the tab header text to reflect the current request name
        foreach (object tab in OpenRequestTabView.TabItems)
        {
            if (tab is TabViewItem item &&
                string.Equals(item.Tag?.ToString(), _currentRequest.Id, StringComparison.Ordinal))
            {
                item.Header = _currentRequest.Name;
                break;
            }
        }

        // Keep the tree node name in sync
        if (_currentCollection != null)
        {
            var node = RequestHelpers.FindNodeById(_currentCollection.Nodes, _currentRequest.Id);
            if (node != null)
                node.Name = _currentRequest.Name;
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyEditor();
        SyncUrlToQueryTable();
    }

    /// <summary>
    /// Parse query params from URL and update the QueryTable.
    /// Only calls SetItems if the parsed items actually differ, to avoid unnecessary row recreation and focus loss.
    /// </summary>
    private void SyncUrlToQueryTable()
    {
        if (_isSyncingQueryUrl || _isLoadingEditor)
            return;

        _isSyncingQueryUrl = true;
        try
        {
            string url = UrlBox.Text.Trim();
            int qIndex = url.IndexOf('?');
            if (qIndex < 0)
                return;

            string queryString = url[(qIndex + 1)..];
            var pairs = new List<KeyValuePairItem>();
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                foreach (string segment in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eqIndex = segment.IndexOf('=');
                    if (eqIndex < 0)
                    {
                        pairs.Add(new KeyValuePairItem
                        {
                            Key = Uri.UnescapeDataString(segment),
                            Value = "",
                            Enabled = true
                        });
                    }
                    else
                    {
                        pairs.Add(new KeyValuePairItem
                        {
                            Key = Uri.UnescapeDataString(segment[..eqIndex]),
                            Value = Uri.UnescapeDataString(segment[(eqIndex + 1)..]),
                            Enabled = true
                        });
                    }
                }
            }

            // Only rebuild rows if the data actually changed, to preserve focus
            var current = QueryTable.GetItems();
            if (!ItemsEqual(current, pairs))
                QueryTable.SetItems(pairs);
        }
        finally
        {
            _isSyncingQueryUrl = false;
        }
    }

    private static bool ItemsEqual(List<KeyValuePairItem> a, List<KeyValuePairItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal) ||
                !string.Equals(a[i].Value, b[i].Value, StringComparison.Ordinal) ||
                a[i].Enabled != b[i].Enabled)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Read query params from QueryTable and rebuild the URL query string.
    /// </summary>
    private void SyncQueryTableToUrl()
    {
        if (_isSyncingQueryUrl || _isLoadingEditor)
            return;

        _isSyncingQueryUrl = true;
        try
        {
            string url = UrlBox.Text.Trim();
            int qIndex = url.IndexOf('?');
            string baseUrl = qIndex >= 0 ? url[..qIndex] : url;

            var items = QueryTable.GetItems();
            var queryParts = items
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? "")}")
                .ToList();

            string newUrl = queryParts.Count > 0
                ? $"{baseUrl}?{string.Join('&', queryParts)}"
                : baseUrl;

            UrlBox.Text = newUrl;
        }
        finally
        {
            _isSyncingQueryUrl = false;
        }
    }
    private void Editor_CheckChanged(object sender, RoutedEventArgs e) => ApplyEditor();

    private void RequestSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLists();

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildGroupedHistory();



    // CollectionComboBox removed — collection switching via tree root node

    private void OpenRequestTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTabs)
            return;

        if (OpenRequestTabView.SelectedItem is not TabViewItem item)
            return;

        string requestId = item.Tag?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        LoadRequestById(requestId);
        _ = PersistWorkspaceAsync();
    }

    private void OpenRequestTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is not TabViewItem item)
            return;

        string requestId = item.Tag?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        int index = _workspace.OpenRequestTabIds.IndexOf(requestId);
        _workspace.OpenRequestTabIds.Remove(requestId);
        RemoveUnsavedRequestIfClosed(requestId);
        if (_workspace.ActiveRequestTabId == requestId)
        {
            _workspace.ActiveRequestTabId = _workspace.OpenRequestTabIds.Count == 0
                ? ""
                : _workspace.OpenRequestTabIds[Math.Clamp(index, 0, _workspace.OpenRequestTabIds.Count - 1)];
        }

        RefreshOpenRequestTabs();
        if (!string.IsNullOrWhiteSpace(_workspace.ActiveRequestTabId))
            LoadRequestById(_workspace.ActiveRequestTabId);
        else
            ClearEditor();
        _ = PersistWorkspaceAsync();
    }

    private void OpenRequestTab(string requestId, bool select)
    {
        if (FindRequest(requestId).Request == null)
            return;

        if (!_workspace.OpenRequestTabIds.Contains(requestId))
            _workspace.OpenRequestTabIds.Add(requestId);

        if (select)
            _workspace.ActiveRequestTabId = requestId;

        RefreshOpenRequestTabs();
        if (select)
            LoadRequestById(requestId);
        _ = PersistWorkspaceAsync();
    }

    private void RestoreOpenRequestTabs()
    {
        RefreshOpenRequestTabs();
        string activeId = _workspace.ActiveRequestTabId;
        if (FindRequest(activeId).Request == null)
            activeId = _workspace.OpenRequestTabIds.FirstOrDefault() ?? "";

        if (!string.IsNullOrWhiteSpace(activeId))
        {
            _workspace.ActiveRequestTabId = activeId;
            SelectOpenTab(activeId);
            LoadRequestById(activeId);
        }
    }

    private void RefreshOpenRequestTabs()
    {
        _isUpdatingTabs = true;
        _workspace.OpenRequestTabIds = _workspace.OpenRequestTabIds
            .Where(id => FindRequest(id).Request != null)
            .Distinct()
            .ToList();

        OpenRequestTabView.TabItems.Clear();
        foreach (string requestId in _workspace.OpenRequestTabIds)
        {
            var match = FindRequest(requestId);
            if (match.Request == null)
                continue;

            OpenRequestTabView.TabItems.Add(new TabViewItem
            {
                Header = match.Request.Name,
                Tag = requestId,
                IsClosable = true
            });
        }

        SelectOpenTab(_workspace.ActiveRequestTabId);
        _isUpdatingTabs = false;
        UpdateEmptyWorkspaceVisibility();
    }

    private void UpdateEmptyWorkspaceVisibility()
    {
        if (EmptyWorkspacePanel == null || OpenRequestTabHeaderBar == null)
            return;

        bool isEmpty = _workspace.OpenRequestTabIds.Count == 0;
        EmptyWorkspacePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        OpenRequestTabHeaderBar.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EmptyCreateNewButton_Click(object sender, RoutedEventArgs e) => CreateUnsavedRequestTab();

    private void EmptyImportButton_Click(object sender, RoutedEventArgs e) => ImportSwaggerButton_Click(sender, e);

    private void EmptyFindButton_Click(object sender, RoutedEventArgs e)
    {
        HideInlineSettings();
        MainNav.SelectedItem = CollectionsNavItem;
        SelectSidebarSection("collections");
        RequestSearchBox.Focus(FocusState.Programmatic);
        RequestSearchBox.SelectAll();
    }

    private void NewRequestAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CreateUnsavedRequestTab();
        args.Handled = true;
    }

    private void ImportAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ImportSwaggerButton_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        EmptyFindButton_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void SelectOpenTab(string requestId)
    {
        foreach (object tab in OpenRequestTabView.TabItems)
        {
            if (tab is TabViewItem item && string.Equals(item.Tag?.ToString(), requestId, StringComparison.Ordinal))
            {
                OpenRequestTabView.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectRequestInList(string requestId)
    {
        SelectRequestInTree(requestId);
    }

    private (ApiCollection? Collection, ApiRequest? Request) FindRequest(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return (null, null);

        var unsavedRequest = FindUnsavedRequest(requestId);
        if (unsavedRequest != null)
            return (null, unsavedRequest);

        foreach (var collection in _workspace.Collections)
        {
            var node = RequestHelpers.FindNodeById(collection.Nodes, requestId);
            if (node?.Request != null)
                return (collection, node.Request);
        }

        return (null, null);
    }

    private async Task PersistWorkspaceAsync()
    {
        var openTabIds = _workspace.OpenRequestTabIds.ToList();
        string activeRequestId = _workspace.ActiveRequestTabId;
        try
        {
            _workspace.OpenRequestTabIds = openTabIds
                .Where(id => !IsUnsavedRequest(id))
                .ToList();
            if (IsUnsavedRequest(_workspace.ActiveRequestTabId))
                _workspace.ActiveRequestTabId = "";
            await _storage.SaveAsync(_workspace);
        }
        finally
        {
            _workspace.OpenRequestTabIds = openTabIds;
            _workspace.ActiveRequestTabId = activeRequestId;
        }
    }

    private void FormatRequestJsonButton_Click(object sender, RoutedEventArgs e)
    {
        string bodyText = GetBodyText();
        if (IsXmlBodyMode())
        {
            // Format as XML
            if (XmlFormatService.TryFormat(bodyText, out string xmlFormatted))
            {
                SetBodyText(xmlFormatted);
                ApplyEditor();
                RefreshBodyHighlight();
            }
            else
            {
                // Invalid XML — no changes
            }
            return;
        }

        // Format as JSON
        if (JsonFormatService.TryFormat(bodyText, out string formatted))
        {
            SetBodyText(formatted);
            ApplyEditor();
            RefreshBodyHighlight();
        }
        else
        {
            // Invalid JSON — no changes
        }
    }

    private void FormatResponseJsonButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-read current displayed text from the RichTextBlock
        string currentText = GetResponseRichText();
        if (JsonFormatService.TryFormat(currentText, out string formatted))
        {
            DisplayResponseBody(formatted);
        }
        else
        {
            // Invalid JSON — no changes
        }
    }

    private void BodyTypeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            return;

        // Guard: during XAML initialization some toggles may not yet be assigned
        if (BodyNoneRadio == null || BodyFormDataRadio == null ||
            BodyUrlEncodedRadio == null || BodyRawRadio == null || BodyBinaryRadio == null)
            return;

        // Mutual exclusion: uncheck all other body type toggles
        var allToggles = new[] { BodyNoneRadio, BodyFormDataRadio, BodyUrlEncodedRadio, BodyRawRadio, BodyBinaryRadio };
        foreach (var other in allToggles)
        {
            if (other != tb && other.IsChecked == true)
                other.IsChecked = false;
        }

        if (_isLoadingEditor)
            return;
        ApplyEditor();
        UpdateBodyVisibility();
    }

    private void BodyTypeRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        // No action needed on uncheck
    }

    private void RawFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingEditor)
            return;
        ApplyEditor();

        var bodyType = GetSelectedBodyType();
        string bodyText = GetBodyText();
        if (bodyType == ApiBodyType.Json)
        {
            // Auto-format and apply JSON highlighting
            if (JsonFormatService.TryFormat(bodyText, out string formatted) &&
                formatted != bodyText)
            {
                _isLoadingEditor = true;
                SetBodyText(formatted);
                _isLoadingEditor = false;
                if (_currentRequest != null)
                    _currentRequest.Body = formatted;
            }
            RefreshBodyHighlight();
        }
        else if (bodyType == ApiBodyType.Xml)
        {
            // Auto-format and apply XML highlighting
            if (XmlFormatService.TryFormat(bodyText, out string xmlFormatted) &&
                xmlFormatted != bodyText)
            {
                _isLoadingEditor = true;
                SetBodyText(xmlFormatted);
                _isLoadingEditor = false;
                if (_currentRequest != null)
                    _currentRequest.Body = xmlFormatted;
            }
            RefreshBodyHighlight();
        }
        else
        {
            ApplyPlainBodyTextColor();
        }
    }

    private bool IsJsonBodyMode()
    {
        return RawFormatComboBox.Visibility == Visibility.Visible &&
               RawFormatComboBox.SelectedItem is ComboBoxItem ci &&
               string.Equals(ci.Tag?.ToString(), "Json", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsXmlBodyMode()
    {
        return RawFormatComboBox.Visibility == Visibility.Visible &&
               RawFormatComboBox.SelectedItem is ComboBoxItem ci &&
               string.Equals(ci.Tag?.ToString(), "Xml", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshBodyHighlight()
    {
        if (BodyBox?.Document == null)
            return;

        bool isJson = IsJsonBodyMode();
        bool isXml = IsXmlBodyMode();
        string bodyText = GetBodyText();
        if ((!isJson && !isXml) || string.IsNullOrWhiteSpace(bodyText))
        {
            ApplyPlainBodyTextColor();
            return;
        }

        _isApplyingBodyHighlight = true;
        try
        {
            int selectionStart = BodyBox.Document.Selection.StartPosition;
            int selectionEnd = BodyBox.Document.Selection.EndPosition;
            Color defaultColor = GetDefaultBodyTextColor();

            BodyBox.Document.GetRange(0, bodyText.Length).CharacterFormat.ForegroundColor = defaultColor;

            int position = 0;
            if (isJson)
            {
                foreach (var token in JsonHighlightService.Tokenize(bodyText))
                {
                    ApplyBodyTokenColor(position, token.Text.Length, token.Kind == JsonTokenKind.Whitespace
                        ? defaultColor
                        : JsonHighlightService.GetColor(token.Kind, IsDarkTheme()));
                    position += token.Text.Length;
                }
            }
            else if (isXml)
            {
                foreach (var token in XmlHighlightService.TokenizeDetailed(bodyText))
                {
                    ApplyBodyTokenColor(position, token.Text.Length, token.Kind == XmlTokenKind.Whitespace
                        ? defaultColor
                        : XmlHighlightService.GetColor(token.Kind, IsDarkTheme()));
                    position += token.Text.Length;
                }
            }

            BodyBox.Document.Selection.SetRange(selectionStart, selectionEnd);
        }
        finally
        {
            _isApplyingBodyHighlight = false;
        }
    }

    private void ApplyBodyTokenColor(int start, int length, Color color)
    {
        if (length <= 0)
            return;
        BodyBox.Document.GetRange(start, start + length).CharacterFormat.ForegroundColor = color;
    }

    private void ApplyPlainBodyTextColor()
    {
        string bodyText = GetBodyText();
        if (string.IsNullOrEmpty(bodyText))
            return;
        BodyBox.Document.GetRange(0, bodyText.Length).CharacterFormat.ForegroundColor = GetDefaultBodyTextColor();
    }

    private Color GetDefaultBodyTextColor()
    {
        return BodyBox.Foreground is SolidColorBrush brush
            ? brush.Color
            : (IsDarkTheme() ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
    }

    private bool IsDarkTheme()
    {
        return BodyBox.ActualTheme == ElementTheme.Dark ||
               (BodyBox.ActualTheme == ElementTheme.Default &&
                Application.Current.RequestedTheme == ApplicationTheme.Dark);
    }

    private async void SelectBinaryFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            BinaryFilePathText.Text = file.Path;
            ApplyEditor();
        }
    }

    private void UpdateBodyVisibility()
    {
        if (BodyBox == null)
            return;

        var bodyType = GetSelectedBodyType();
        bool isFormData = bodyType == ApiBodyType.FormData;
        bool isUrlEncoded = bodyType == ApiBodyType.XWwwFormUrlencoded;
        bool isBinary = bodyType == ApiBodyType.Binary;
        bool isRaw = bodyType is ApiBodyType.Raw or ApiBodyType.Json or ApiBodyType.Xml;
        bool isJson = bodyType == ApiBodyType.Json;
        bool isXml = bodyType == ApiBodyType.Xml;
        bool isHighlighted = isJson || isXml;
        bool isWebSocket = _currentRequest?.Type == ApiRequestType.WebSocket;

        BodyBox.Visibility = (isRaw || isWebSocket) ? Visibility.Visible : Visibility.Collapsed;
        FormDataTable.Visibility = (isFormData && !isWebSocket) ? Visibility.Visible : Visibility.Collapsed;
        UrlEncodedTable.Visibility = (isUrlEncoded && !isWebSocket) ? Visibility.Visible : Visibility.Collapsed;
        BinaryInfoPanel.Visibility = (isBinary && !isWebSocket) ? Visibility.Visible : Visibility.Collapsed;
        SelectFileButton.Visibility = (isBinary && !isWebSocket) ? Visibility.Visible : Visibility.Collapsed;
        BinaryFilePathText.Visibility = (isBinary && !isWebSocket && !string.IsNullOrWhiteSpace(BinaryFilePathText.Text)) ? Visibility.Visible : Visibility.Collapsed;
        RawFormatComboBox.Visibility = (isRaw && !isWebSocket) ? Visibility.Visible : Visibility.Collapsed;

        if (isHighlighted)
        {
            RefreshBodyHighlight();
        }
        else
        {
            ApplyPlainBodyTextColor();
        }
    }

    private void SetResponseMetrics(string? status, long? elapsedMilliseconds, long? bodyBytes, bool isSuccess)
    {
        ResponseStatusValueText.Text = string.IsNullOrWhiteSpace(status) ? "-" : status;
        ResponseTimeValueText.Text = elapsedMilliseconds.HasValue ? $"{elapsedMilliseconds.Value} ms" : "-";
        ResponseSizeValueText.Text = bodyBytes.HasValue ? FormatBytes(bodyBytes.Value) : "-";

        ResponseStatusValueText.Foreground = isSuccess
            ? (Brush)Application.Current.Resources["PositiveStatusBrush"]
            : string.IsNullOrWhiteSpace(status)
                ? new SolidColorBrush(Microsoft.UI.Colors.Gray)
                : (Brush)Application.Current.Resources["DangerStatusBrush"];
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private void DisplayResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            RichTextHelper.ApplyPlainText(ResponseBodyRichTextBlock, body);
            return;
        }

        // Try JSON highlighting
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            RichTextHelper.ApplyJsonHighlighting(ResponseBodyRichTextBlock, body);
        }
        else
        {
            RichTextHelper.ApplyPlainText(ResponseBodyRichTextBlock, body);
        }
    }

    private string GetResponseRichText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in ResponseBodyRichTextBlock.Blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is Microsoft.UI.Xaml.Documents.Run run)
                        sb.Append(run.Text);
                }
            }
        }
        return sb.ToString();
    }

    private void SelectBodyType(ApiBodyType bodyType)
    {
        // Set radio button based on body type
        bool isRaw = bodyType is ApiBodyType.Raw or ApiBodyType.Json or ApiBodyType.Xml;
        BodyNoneRadio.IsChecked = bodyType == ApiBodyType.None;
        BodyFormDataRadio.IsChecked = bodyType == ApiBodyType.FormData;
        BodyUrlEncodedRadio.IsChecked = bodyType == ApiBodyType.XWwwFormUrlencoded;
        BodyRawRadio.IsChecked = isRaw;
        BodyBinaryRadio.IsChecked = bodyType == ApiBodyType.Binary;

        // Set raw format dropdown if raw type
        if (isRaw && RawFormatComboBox != null)
        {
            string tag = bodyType.ToString();
            foreach (ComboBoxItem item in RawFormatComboBox.Items)
            {
                if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    RawFormatComboBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private ApiBodyType GetSelectedBodyType()
    {
        if (BodyFormDataRadio.IsChecked == true) return ApiBodyType.FormData;
        if (BodyUrlEncodedRadio.IsChecked == true) return ApiBodyType.XWwwFormUrlencoded;
        if (BodyBinaryRadio.IsChecked == true) return ApiBodyType.Binary;
        if (BodyRawRadio.IsChecked == true)
        {
            if (RawFormatComboBox.SelectedItem is ComboBoxItem item)
            {
                if (string.Equals(item.Tag?.ToString(), "Json", StringComparison.OrdinalIgnoreCase))
                    return ApiBodyType.Json;
                if (string.Equals(item.Tag?.ToString(), "Xml", StringComparison.OrdinalIgnoreCase))
                    return ApiBodyType.Xml;
            }
            return ApiBodyType.Raw;
        }
        return ApiBodyType.None;
    }

    private void ApplyEnvironmentEditor()
    {
        if (_isLoadingEditor || _editingEnvironment == null)
            return;

        _editingEnvironment.Variables = EnvironmentTable.GetItems();
    }

    private void EnvironmentTable_ItemsChanged(object? sender, EventArgs e)
    {
        if (!_isLoadingEditor && _editingEnvironment != null)
            _editingEnvironment.Variables = EnvironmentTable.GetItems();
    }

    /// <summary>Returns current environment variable names for autocomplete.</summary>
    private List<string> GetEnvironmentVariableNames()
    {
        var activeEnv = _workspace.GetActiveEnvironment();
        if (activeEnv == null)
            return new List<string>();
        return activeEnv.Variables
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => x.Key.Trim())
            .Distinct()
            .ToList();
    }

    private void RefreshEnvironmentList()
    {
        EnvironmentListView.ItemsSource = null;
        EnvironmentListView.ItemsSource = _workspace.Environments;
    }

    private void AddEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var env = new EnvironmentProfile { Name = "New Environment" };
        _workspace.Environments.Add(env);
        RefreshEnvironmentList();
        EnvironmentListView.SelectedItem = env;
    }

    private void EnvironmentMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is EnvironmentProfile env)
        {
            _ = RenameEnvironmentAsync(env);
        }
    }

    private void EnvironmentMenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is EnvironmentProfile env)
        {
            var copy = new EnvironmentProfile
            {
                Name = $"{env.Name} (Copy)",
                Variables = env.Variables.Select(v => new KeyValuePairItem
                {
                    Key = v.Key,
                    Value = v.Value,
                    Description = v.Description,
                    Enabled = v.Enabled
                }).ToList()
            };
            _workspace.Environments.Add(copy);
            RefreshEnvironmentList();
            EnvironmentListView.SelectedItem = copy;
        }
    }

    private async Task RenameEnvironmentAsync(EnvironmentProfile env)
    {
        var dialog = new ContentDialog
        {
            Title = "Rename Environment",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var textBox = new TextBox
        {
            Text = env.Name,
            PlaceholderText = "Environment name",
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false
        };
        dialog.Content = textBox;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            env.Name = textBox.Text.Trim();
            if (_editingEnvironment == env)
                EnvTitleText.Text = env.Name;
            RefreshEnvironmentList();
        }
    }

    private void EnvironmentMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not EnvironmentProfile env)
            return;

        if (_workspace.Environments.Count <= 1)
            return;

        _workspace.Environments.Remove(env);

        if (_editingEnvironment == env)
        {
            _editingEnvironment = null;
            EnvironmentOverlayPanel.Visibility = Visibility.Collapsed;
            RequestEditorArea.Visibility = Visibility.Visible;
        }

        RefreshEnvironmentList();
    }

    private void EnvironmentListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentListView.SelectedItem is not EnvironmentProfile env)
            return;

        _editingEnvironment = env;
        _workspace.ActiveEnvironmentId = env.Id;

        _isLoadingEditor = true;
        EnvironmentTable.SetItems(env.Variables);
        _isLoadingEditor = false;

        EnvTitleText.Text = env.Name;
        RequestEditorArea.Visibility = Visibility.Collapsed;
        EnvironmentOverlayPanel.Visibility = Visibility.Visible;
        SettingsOverlayPanel.Visibility = Visibility.Collapsed;
    }

    private void EnvBackButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEnvironmentEditor();
        _editingEnvironment = null;
        EnvironmentOverlayPanel.Visibility = Visibility.Collapsed;
        RequestEditorArea.Visibility = Visibility.Visible;
        EnvironmentListView.SelectedItem = null;
    }

    private async void RenameEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingEnvironment == null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Rename Environment",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var textBox = new TextBox
        {
            Text = _editingEnvironment.Name,
            PlaceholderText = "Environment name",
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false
        };
        dialog.Content = textBox;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            _editingEnvironment.Name = textBox.Text.Trim();
            EnvTitleText.Text = _editingEnvironment.Name;
            RefreshEnvironmentList();
            EnvironmentListView.SelectedItem = _editingEnvironment;
        }
    }

    private void UpdateTypeVisibility(ApiRequestType type)
    {
        bool isWebSocket = type == ApiRequestType.WebSocket;
        bool isHttp = type == ApiRequestType.Http;

        MethodComboBox.IsEnabled = isHttp;
        MethodComboBox.Visibility = isWebSocket ? Visibility.Collapsed : Visibility.Visible;
        WsConnectionStatusPanel.Visibility = isWebSocket ? Visibility.Visible : Visibility.Collapsed;
        MethodColumn.Width = isWebSocket ? new GridLength(112) : new GridLength(92);
        SendButton.Visibility = isWebSocket ? Visibility.Collapsed : Visibility.Visible;
        WsConnectButton.Visibility = isWebSocket ? Visibility.Visible : Visibility.Collapsed;
        BodyOptionsGrid.Visibility = isWebSocket ? Visibility.Collapsed : Visibility.Visible;
        WsMessageActions.Visibility = isWebSocket ? Visibility.Visible : Visibility.Collapsed;
        ResponseTabView.Visibility = isWebSocket ? Visibility.Collapsed : Visibility.Visible;
        WsMessageScrollViewer.Visibility = isWebSocket ? Visibility.Visible : Visibility.Collapsed;
        WsClearButton.Visibility = isWebSocket ? Visibility.Visible : Visibility.Collapsed;
        BodyTabItem.Header = isWebSocket ? "Message" : "Body";
        AuthorizationTabItem.Visibility = isWebSocket ? Visibility.Collapsed : Visibility.Visible;
        UrlBox.PlaceholderText = isWebSocket ? "ws://localhost:8080/ws" : "https://api.example.com/v1/users?page=1";
        if (isWebSocket)
            RequestConfigTabView.SelectedItem = BodyTabItem;

        string badgeColor = type switch
        {
            ApiRequestType.Http => "#16A34A",
            ApiRequestType.WebSocket => "#9333EA",
            _ => "#64748B"
        };
        BreadcrumbTypeBadge.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255,
                Convert.ToByte(badgeColor[1..3], 16),
                Convert.ToByte(badgeColor[3..5], 16),
                Convert.ToByte(badgeColor[5..7], 16)));
        BreadcrumbTypeText.Text = type.ToString().ToUpper();

        if (isWebSocket)
        {
            SelectMethod("CONNECT");
            if (_currentRequest != null)
                _currentRequest.Method = "CONNECT";
            ResetWsStatus();
        }

        UpdateBodyVisibility();
    }

    private void ResetWsStatus()
    {
        if (WsStatusDot == null) return;
        WsStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));
        WsStatusText.Text = "Disconnected";
        WsConnectButton.Content = "Connect";
    }

    private void RemovedUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEditor || _currentRequest == null)
            return;
        // Sync WsUrlBox → UrlBox (and thus _currentRequest.Url)
        _isLoadingEditor = true;
        UrlBox.Text = UrlBox.Text;
        _isLoadingEditor = false;
        _currentRequest.Url = UrlBox.Text.Trim();
    }

    private void UpdateBreadcrumb()
    {
        if (_currentRequest == null)
        {
            BreadcrumbCollectionText.Text = "";
            BreadcrumbNameText.Text = "";
            return;
        }

        string typeLabel = _currentRequest.Type switch
        {
            ApiRequestType.Http => "HTTP",
            ApiRequestType.WebSocket => "WebSocket",
            _ => "HTTP"
        };

        // Build full path: CollectionName / Folder1 / Folder2 / ... / RequestName
        var requestMatch = FindRequest(_currentRequest.Id);
        bool isUnsavedRequest = requestMatch.Request != null && requestMatch.Collection == null;
        string collectionName = isUnsavedRequest ? "" : _currentCollection?.Name ?? "";
        List<string>? nodePath = !isUnsavedRequest && _currentCollection != null
            ? RequestHelpers.GetNodePath(_currentCollection.Nodes, _currentRequest.Id)
            : null;

        // BreadcrumbCollectionText = CollectionName / Folder1 / Folder2  (excludes the request name)
        string pathText;
        if (nodePath != null && nodePath.Count > 1)
        {
            // Path has folders: "CollectionName / Folder1 / Folder2"
            var folderSegments = nodePath.Take(nodePath.Count - 1).ToList();
            if (!string.IsNullOrEmpty(collectionName))
                folderSegments.Insert(0, collectionName);
            pathText = string.Join(" / ", folderSegments);
        }
        else if (!string.IsNullOrEmpty(collectionName))
        {
            // Request is directly in the collection root (no sub-folders)
            pathText = collectionName;
        }
        else
        {
            // Temporary requests do not have a collection path.
            pathText = "";
        }
        BreadcrumbCollectionText.Text = pathText;
        BreadcrumbSeparator.Visibility = string.IsNullOrEmpty(pathText) ? Visibility.Collapsed : Visibility.Visible;

        // Request name (display text)
        BreadcrumbNameText.Text = string.IsNullOrWhiteSpace(_currentRequest.Name) ? "Untitled" : _currentRequest.Name;

        // Sync NameBox text without triggering TextChanged loop
        if (NameBox.Visibility == Visibility.Collapsed && NameBox.Text != _currentRequest.Name)
        {
            _isLoadingEditor = true;
            NameBox.Text = _currentRequest.Name;
            _isLoadingEditor = false;
        }
    }

    private void BreadcrumbTypeButton_Click(object sender, RoutedEventArgs e)
    {
        // The flyout is declared in XAML; this handler is a placeholder
        // in case additional logic is needed before showing the flyout.
    }

    private void BreadcrumbTypeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest == null || sender is not MenuFlyoutItem item)
            return;

        string? tag = item.Tag?.ToString();
        if (string.IsNullOrEmpty(tag) || !Enum.TryParse<ApiRequestType>(tag, out var newType))
            return;

        // Sync NameBox back to model before changing type
        if (NameBox.Visibility == Visibility.Visible)
            CommitNameEdit();

        _currentRequest.Type = newType;
        SelectType(newType);
        UpdateTypeVisibility(newType);
        UpdateBreadcrumb();
        ApplyEditor();
    }

    private void BreadcrumbNameText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_currentRequest == null) return;
        BreadcrumbNameText.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Visible;
        NameBox.Text = _currentRequest.Name;
        NameBox.SelectAll();
        NameBox.Focus(FocusState.Programmatic);
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitNameEdit();
    }

    private void NameBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitNameEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Cancel edit: restore original name
            NameBox.Text = _currentRequest?.Name ?? "";
            CommitNameEdit();
            e.Handled = true;
        }
    }

    private void CommitNameEdit()
    {
        if (_currentRequest == null) return;
        string newName = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            newName = "Untitled";
        _currentRequest.Name = newName;
        NameBox.Visibility = Visibility.Collapsed;
        BreadcrumbNameText.Visibility = Visibility.Visible;
        BreadcrumbNameText.Text = newName;
        UpdateCurrentTabHeader();
    }

    private void WsConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest == null) return;

        bool isConnected = WsConnectButton.Content?.ToString() == "Disconnect";
        if (isConnected)
        {
            // Disconnect
            _sendCancellation?.Cancel();
            WsStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));
            WsStatusText.Text = "Disconnected";
            WsConnectButton.Content = "Connect";
            AppendWsMessage("[Disconnected]", false);
        }
        else
        {
            ApplyEditor();
            string wsUrl = UrlBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(wsUrl))
                _currentRequest.Url = wsUrl;
            // Show connecting state
            WsStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 179, 8));
            WsStatusText.Text = "Connecting...";
            WsConnectButton.Content = "Disconnect";
            AppendWsMessage($"[Connecting to {_currentRequest.Url}]", false);
            _ = ConnectWebSocketAsync();
        }
    }

    private async Task ConnectWebSocketAsync()
    {
        if (_currentRequest == null) return;
        _sendCancellation?.Cancel();
        _sendCancellation = new CancellationTokenSource();
        try
        {
            ApiRequest resolved = EnvironmentVariableResolver.Resolve(_currentRequest, _workspace.GetActiveVariables());
            var response = await _executor.ExecuteAsync(resolved, _sendCancellation.Token);
            WsStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94));
            WsStatusText.Text = "Connected";
            AppendWsMessage($"[Connected] {response.Summary}", false);
            if (!string.IsNullOrWhiteSpace(response.Body))
                AppendWsMessages(response.Body, false);
        }
        catch (Exception ex)
        {
            WsStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
            WsStatusText.Text = "Error";
            WsConnectButton.Content = "Connect";
            AppendWsMessage($"[Error] {ex.Message}", false);
        }
        finally
        {
            _sendCancellation?.Dispose();
            _sendCancellation = null;
        }
    }

    private void WsSendMessageButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        string msg = GetBodyText();
        if (string.IsNullOrWhiteSpace(msg)) return;
        AppendWsMessage($"↑ {msg}", true);
        if (_currentRequest != null)
            _currentRequest.Body = msg;
        _ = ConnectWebSocketAsync();
    }

    private void WsClearButton_Click(object sender, RoutedEventArgs e)
    {
        WsMessageList.Children.Clear();
    }

    private void AppendWsMessage(string text, bool isSent)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isSent
                ? (Brush)Application.Current.Resources["PositiveStatusBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        WsMessageList.Children.Add(tb);
        WsMessageScrollViewer.ChangeView(null, double.MaxValue, null);
    }

    private void AppendWsMessages(string text, bool isSent)
    {
        var messages = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (messages.Length == 0)
        {
            AppendWsMessage(text, isSent);
            return;
        }

        foreach (string message in messages)
            AppendWsMessage($"↓ {message}", isSent);
    }

    private void SelectType(ApiRequestType type)
    {
        foreach (ComboBoxItem item in TypeComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                TypeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectMethod(string method)
    {
        foreach (ComboBoxItem item in MethodComboBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), method, StringComparison.OrdinalIgnoreCase))
            {
                MethodComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static bool Contains(string value, string filter)
    {
        return value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // ── Context menu handlers ──────────────────────────────────────────────

    private void TreeNode_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            var actionPanel = grid.FindName("NodeActionPanel") as UIElement;
            if (actionPanel != null)
            {
                actionPanel.Visibility = Visibility.Visible;

                if (grid.DataContext is TreeViewNode tvNode && tvNode.Content is CollectionNode cn)
                {
                    var addBtn = grid.FindName("NodeAddButton") as UIElement;
                    if (addBtn != null)
                        addBtn.Visibility = cn.IsFolder ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }
    }

    private void TreeNode_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            var actionPanel = grid.FindName("NodeActionPanel") as UIElement;
            if (actionPanel != null)
                actionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void NodeAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionNode node)
            return;

        _contextMenuNode = node;
        _selectedNode = node;

        var owningCollection = FindOwningCollection(node);
        if (owningCollection != null)
            _currentCollection = owningCollection;

        await AddRequestToNode(node);
    }

    private void NodeMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionNode node)
            return;

        _contextMenuNode = node;
        _selectedNode = node;

        var owningCollection = FindOwningCollection(node);
        if (owningCollection != null)
            _currentCollection = owningCollection;

        var flyout = (MenuFlyout)Resources["TreeNodeFlyout"];
        UpdateFlyoutForNode(flyout, node);
        flyout.ShowAt(btn);
    }

    private async Task AddRequestToNode(CollectionNode node)
    {
        if (_currentCollection == null || node == null || !node.IsFolder)
            return;

        var request = new ApiRequest
        {
            Name = "新建请求",
            Type = ApiRequestType.Http,
            Method = "GET",
            Url = "https://"
        };

        var requestNode = new CollectionNode
        {
            Name = request.Name,
            IsFolder = false,
            Request = request
        };

        node.Children.Add(requestNode);
        _currentRequest = request;
        await PersistWorkspaceAsync();
        OpenRequestTab(request.Id, true);
        RefreshLists();
    }

    private void RequestTreeView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element)
            return;

        var treeViewItem = FindAncestor<TreeViewItem>(element);
        if (treeViewItem?.Content is not CollectionNode collectionNode)
            return;

        _contextMenuNode = collectionNode;
        _selectedNode = collectionNode;

        var owningCollection = FindOwningCollection(collectionNode);
        if (owningCollection != null)
            _currentCollection = owningCollection;

        var flyout = (MenuFlyout)Resources["TreeNodeFlyout"];
        UpdateFlyoutForNode(flyout, collectionNode);
        flyout.ShowAt(RequestTreeView, e.GetPosition(RequestTreeView));
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        var current = element;
        while (current != null)
        {
            if (current is T found)
                return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void UpdateFlyoutForNode(MenuFlyout flyout, CollectionNode node)
    {
        bool isFolder = node.IsFolder;
        for (int i = 0; i < flyout.Items.Count; i++)
        {
            var item = flyout.Items[i];
            if (item is MenuFlyoutItem mfi)
            {
                if (mfi.Name == "FlyoutNewRequest" || mfi.Name == "FlyoutNewFolder")
                    mfi.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (item is MenuFlyoutSeparator sep && sep.Name == "FlyoutNewSeparator")
            {
                sep.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async void ContextAddRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _contextMenuNode == null || !_contextMenuNode.IsFolder)
            return;

        var request = new ApiRequest
        {
            Name = "新建请求",
            Type = ApiRequestType.Http,
            Method = "GET",
            Url = "https://"
        };

        var requestNode = new CollectionNode
        {
            Name = request.Name,
            IsFolder = false,
            Request = request
        };

        _contextMenuNode.Children.Add(requestNode);
        _currentRequest = request;
        await PersistWorkspaceAsync();
        OpenRequestTab(request.Id, true);
        RefreshLists();
    }

    private async void ContextAddSubFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _contextMenuNode == null)
            return;

        string folderName = await ShowInputDialogAsync("新建子文件夹", "文件夹名称", "新建文件夹");
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        var folder = new CollectionNode
        {
            Name = folderName,
            IsFolder = true
        };

        var targetList = _contextMenuNode.IsFolder ? _contextMenuNode.Children : _currentCollection.Nodes;
        targetList.Add(folder);

        await PersistWorkspaceAsync();
        RefreshLists();
    }

    private async void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuNode == null)
            return;

        if (IsCollectionRootNode(_contextMenuNode))
        {
            var collection = FindOwningCollection(_contextMenuNode);
            if (collection == null)
                return;

            string newName = await ShowInputDialogAsync("重命名集合", "集合名称", collection.Name);
            if (string.IsNullOrWhiteSpace(newName))
                return;

            collection.Name = newName;
            _contextMenuNode.Name = newName;

            await PersistWorkspaceAsync();
            RefreshLists();
            return;
        }

        string title = _contextMenuNode.IsFolder ? "重命名文件夹" : "重命名请求";
        string nodeNewName = await ShowInputDialogAsync(title, "新名称", _contextMenuNode.Name);
        if (string.IsNullOrWhiteSpace(nodeNewName))
            return;

        _contextMenuNode.Name = nodeNewName;
        if (_contextMenuNode.Request != null)
            _contextMenuNode.Request.Name = nodeNewName;

        await PersistWorkspaceAsync();
        RefreshLists();
        RefreshOpenRequestTabs();
    }

    private async void ContextDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _contextMenuNode == null || _contextMenuNode.Request == null)
            return;

        var clonedNode = RequestHelpers.CloneNode(_contextMenuNode);
        clonedNode.Name = $"{clonedNode.Name} Copy";
        if (clonedNode.Request != null)
            clonedNode.Request.Name = clonedNode.Name;

        // Find the parent list containing this node
        var parentList = FindNodeParentList(_currentCollection.Nodes, _contextMenuNode);
        if (parentList != null)
            parentList.Add(clonedNode);
        else
            _currentCollection.Nodes.Add(clonedNode);

        _currentRequest = clonedNode.Request;
        await PersistWorkspaceAsync();
        RefreshLists();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        if (_currentRequest != null)
            OpenRequestTab(_currentRequest.Id, true);
    }

    private async void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuNode == null)
            return;

        if (IsCollectionRootNode(_contextMenuNode))
        {
            var collection = FindOwningCollection(_contextMenuNode);
            if (collection == null)
                return;

            var deletedRequestIds = RequestHelpers.GetAllRequestNodes(collection.Nodes)
                .Where(x => x.Request != null)
                .Select(x => x.Request!.Id)
                .ToHashSet();
            _workspace.Collections.Remove(collection);
            _workspace.OpenRequestTabIds.RemoveAll(deletedRequestIds.Contains);
            if (deletedRequestIds.Contains(_workspace.ActiveRequestTabId))
                _workspace.ActiveRequestTabId = "";
            if (_workspace.Collections.Count == 0)
                _workspace.Collections.Add(new ApiCollection { Name = "默认集合" });
            _currentCollection = _workspace.Collections.FirstOrDefault();
            if (deletedRequestIds.Contains(_currentRequest?.Id ?? ""))
            {
                _currentRequest = null;
                ClearEditor();
            }

            await PersistWorkspaceAsync();
            RefreshLists();
            RefreshOpenRequestTabs();
            return;
        }

        if (_currentCollection == null)
            return;

        if (_contextMenuNode.IsFolder)
        {
            var parentList = FindNodeParentList(_currentCollection.Nodes, _contextMenuNode);
            if (parentList != null)
                parentList.Remove(_contextMenuNode);
            else
                _currentCollection.Nodes.Remove(_contextMenuNode);

            var deletedIds = RequestHelpers.GetAllRequestNodes(new List<CollectionNode> { _contextMenuNode })
                .Where(x => x.Request != null).Select(x => x.Request!.Id).ToHashSet();
            _workspace.OpenRequestTabIds.RemoveAll(deletedIds.Contains);
            if (deletedIds.Contains(_workspace.ActiveRequestTabId))
                _workspace.ActiveRequestTabId = "";

            if (deletedIds.Contains(_currentRequest?.Id ?? ""))
            {
                _currentRequest = null;
                ClearEditor();
            }

            await PersistWorkspaceAsync();
            RefreshLists();
            RefreshOpenRequestTabs();
        }
        else if (_contextMenuNode.Request != null)
        {
            string deletedId = _contextMenuNode.Request.Id;
            var parentList = FindNodeParentList(_currentCollection.Nodes, _contextMenuNode);
            if (parentList != null)
                parentList.Remove(_contextMenuNode);
            else
                _currentCollection.Nodes.Remove(_contextMenuNode);

            _workspace.OpenRequestTabIds.Remove(deletedId);
            if (_workspace.ActiveRequestTabId == deletedId)
                _workspace.ActiveRequestTabId = "";

            if (_currentRequest?.Id == deletedId)
            {
                _currentRequest = null;
                ClearEditor();
            }

            await PersistWorkspaceAsync();
            RefreshLists();
            RefreshOpenRequestTabs();
        }
    }

    /// <summary>
    /// Find the parent List&lt;CollectionNode&gt; that contains the given node.
    /// </summary>
    private static List<CollectionNode>? FindNodeParentList(List<CollectionNode> nodes, CollectionNode target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
                return nodes;
            var found = FindNodeParentList(node.Children, target);
            if (found != null)
                return found;
        }
        return null;
    }

    private ApiCollection? FindOwningCollection(CollectionNode node)
    {
        if (node == null)
            return _currentCollection;

        var collectionById = _workspace.Collections.FirstOrDefault(c => c.Id == node.Id);
        if (collectionById != null)
            return collectionById;

        foreach (var collection in _workspace.Collections)
        {
            if (IsNodeInTree(collection.Nodes, node))
                return collection;
        }

        return _currentCollection;
    }

    private static bool IsNodeInTree(List<CollectionNode> nodes, CollectionNode target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
                return true;
            if (IsNodeInTree(node.Children, target))
                return true;
        }
        return false;
    }

    private bool IsCollectionRootNode(CollectionNode node)
    {
        return _workspace.Collections.Any(c => c.Id == node.Id);
    }

    private void OpenRequestTabView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element)
            return;

        // Walk up the visual tree to find the TabViewItem
        var tabItem = FindAncestor<TabViewItem>(element);
        if (tabItem == null)
            return;

        _rightClickTabId = tabItem.Tag?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(_rightClickTabId))
            return;

        var flyout = (MenuFlyout)Resources["TabContextMenu"];
        flyout.ShowAt(OpenRequestTabView, e.GetPosition(OpenRequestTabView));
    }

    private void TabContext_Close_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rightClickTabId))
            return;

        CloseTabById(_rightClickTabId);
    }

    private void TabContext_CloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rightClickTabId))
            return;

        var toRemove = _workspace.OpenRequestTabIds.Where(id => id != _rightClickTabId).ToList();
        foreach (var id in toRemove)
        {
            _workspace.OpenRequestTabIds.Remove(id);
            RemoveUnsavedRequestIfClosed(id);
        }

        _workspace.ActiveRequestTabId = _rightClickTabId;
        RefreshOpenRequestTabs();
        LoadRequestById(_rightClickTabId);
        _ = PersistWorkspaceAsync();
    }

    private void TabContext_CloseLeft_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rightClickTabId))
            return;

        int index = _workspace.OpenRequestTabIds.IndexOf(_rightClickTabId);
        if (index <= 0)
            return;

        var toRemove = _workspace.OpenRequestTabIds.Take(index).ToList();
        foreach (var id in toRemove)
        {
            _workspace.OpenRequestTabIds.Remove(id);
            RemoveUnsavedRequestIfClosed(id);
        }

        if (toRemove.Contains(_workspace.ActiveRequestTabId))
            _workspace.ActiveRequestTabId = _rightClickTabId;

        RefreshOpenRequestTabs();
        if (!string.IsNullOrWhiteSpace(_workspace.ActiveRequestTabId))
            LoadRequestById(_workspace.ActiveRequestTabId);
        else
            LoadRequestById(_rightClickTabId);
        _ = PersistWorkspaceAsync();
    }

    private void TabContext_CloseRight_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rightClickTabId))
            return;

        int index = _workspace.OpenRequestTabIds.IndexOf(_rightClickTabId);
        if (index < 0 || index >= _workspace.OpenRequestTabIds.Count - 1)
            return;

        var toRemove = _workspace.OpenRequestTabIds.Skip(index + 1).ToList();
        foreach (var id in toRemove)
        {
            _workspace.OpenRequestTabIds.Remove(id);
            RemoveUnsavedRequestIfClosed(id);
        }

        if (toRemove.Contains(_workspace.ActiveRequestTabId))
            _workspace.ActiveRequestTabId = _rightClickTabId;

        RefreshOpenRequestTabs();
        if (!string.IsNullOrWhiteSpace(_workspace.ActiveRequestTabId))
            LoadRequestById(_workspace.ActiveRequestTabId);
        else
            LoadRequestById(_rightClickTabId);
        _ = PersistWorkspaceAsync();
    }

    private void TabContext_CloseAll_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = _workspace.OpenRequestTabIds.ToList();
        foreach (var id in toRemove)
        {
            _workspace.OpenRequestTabIds.Remove(id);
            RemoveUnsavedRequestIfClosed(id);
        }

        _workspace.ActiveRequestTabId = "";
        RefreshOpenRequestTabs();
        ClearEditor();
        _ = PersistWorkspaceAsync();
    }

    private void CloseTabById(string requestId)
    {
        int index = _workspace.OpenRequestTabIds.IndexOf(requestId);
        _workspace.OpenRequestTabIds.Remove(requestId);
        RemoveUnsavedRequestIfClosed(requestId);

        if (_workspace.ActiveRequestTabId == requestId)
        {
            _workspace.ActiveRequestTabId = _workspace.OpenRequestTabIds.Count == 0
                ? ""
                : _workspace.OpenRequestTabIds[Math.Clamp(index, 0, _workspace.OpenRequestTabIds.Count - 1)];
        }

        RefreshOpenRequestTabs();
        if (!string.IsNullOrWhiteSpace(_workspace.ActiveRequestTabId))
            LoadRequestById(_workspace.ActiveRequestTabId);
        else
            ClearEditor();
        _ = PersistWorkspaceAsync();
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => CreateUnsavedRequestTab();

    /// <summary>
    /// Show a simple input dialog to get user text input.
    /// </summary>
    private async Task<string> ShowInputDialogAsync(string title, string placeholder, string defaultValue)
    {
        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            Text = defaultValue,
            MinWidth = 300,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false
        };
        textBox.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = title,
            Content = textBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? textBox.Text : "";
    }
}
