using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest.Pages;

public sealed partial class WorkspacePage : Page
{
    private readonly WorkspaceStorage _storage = new();
    private readonly RequestExecutionService _executor = new();
    private readonly OpenApiImporter _importer = new();
    private readonly ObservableCollection<RequestHistoryEntry> _historyItems = new();
    private ApiWorkspace _workspace = new();
    private ApiCollection? _currentCollection;
    private ApiRequest? _currentRequest;
    private CollectionNode? _selectedNode;
    private CollectionNode? _contextMenuNode;
    private CancellationTokenSource? _sendCancellation;
    private bool _isLoadingEditor;
    private bool _isUpdatingTabs;
    private bool _isSyncingQueryUrl;
    private string _rightClickTabId = "";
    private VariableAutoComplete? _urlAutoComplete;
    private VariableAutoComplete? _bodyAutoComplete;

    public WorkspacePage()
    {
        InitializeComponent();
        MainNav.SelectedItem = CollectionsNavItem;
        HistoryListView.ItemsSource = _historyItems;
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

    private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            SelectSidebarSection("settings");
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item)
            return;
        SelectSidebarSection(item.Tag?.ToString() ?? "collections");
    }

    private void OpenSettingsSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }

    private void SelectSidebarSection(string section)
    {
        var panelMap = new Dictionary<string, Grid>
        {
            ["collections"] = CollectionsSidebarPanel,
            ["environments"] = EnvironmentsSidebarPanel,
            ["history"] = HistorySidebarPanel,
            ["profile"] = ProfileSidebarPanel,
            ["settings"] = SettingsSidebarPanel
        };

        if (!panelMap.ContainsKey(section))
            section = "collections";

        foreach (var item in panelMap)
            item.Value.Visibility = item.Key == section ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void WorkspacePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspacePage_Loaded;
        _workspace = await _storage.LoadAsync();
        App.Current.ApplySettings(_workspace.Settings);
        ApplyEditorFonts();
        _isLoadingEditor = true;
        EnvironmentTable.SetItems(_workspace.EnvironmentVariables);
        _isLoadingEditor = false;

        // Initialize {{variable}} autocomplete on URL box and Body box
        _urlAutoComplete = new VariableAutoComplete(UrlBox, AutoCompleteHost, GetEnvironmentVariableNames);
        _bodyAutoComplete = new VariableAutoComplete(BodyBox, AutoCompleteHost, GetEnvironmentVariableNames);

        _currentCollection = _workspace.Collections.FirstOrDefault();
        RefreshCollectionSelector();
        RefreshLists();
        RestoreOpenRequestTabs();
        if (_workspace.OpenRequestTabIds.Count == 0)
        {
            var firstNode = _currentCollection != null
                ? RequestHelpers.GetAllRequestNodes(_currentCollection.Nodes).FirstOrDefault()
                : null;
            if (firstNode?.Request != null)
                SelectRequestInTree(firstNode.Request.Id);
        }
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
    }

    private void RefreshTree()
    {
        RequestTreeView.RootNodes.Clear();
        if (_currentCollection == null)
            return;

        string filter = RequestSearchBox?.Text.Trim() ?? "";
        var root = BuildRootTree(filter);
        RequestTreeView.RootNodes.Add(root);

        // Re-select the current request in the tree
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
    }

    private TreeViewNode BuildRootTree(string filter)
    {
        var childItems = new List<TreeViewNode>();
        foreach (var node in _currentCollection!.Nodes)
        {
            var childItem = BuildTreeItem(node, filter);
            if (childItem != null)
                childItems.Add(childItem);
        }

        // Use the actual collection Nodes list so context-menu mutations propagate
        var rootCollectionNode = new CollectionNode
        {
            Name = _currentCollection.Name,
            IsFolder = true,
            Children = _currentCollection.Nodes
        };

        var rootNode = new TreeViewNode
        {
            Content = rootCollectionNode,
            IsExpanded = true
        };
        foreach (var child in childItems)
            rootNode.Children.Add(child);

        return rootNode;
    }

    private TreeViewNode? BuildTreeItem(CollectionNode node, string filter)
    {
        if (node.IsFolder)
        {
            var childItems = new List<TreeViewNode>();
            foreach (var child in node.Children)
            {
                var childItem = BuildTreeItem(child, filter);
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
                IsExpanded = hasFilter || childItems.Count <= 20
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

    private static bool MatchesNodeFilter(CollectionNode node, string filter)
    {
        if (node.Request == null)
            return false;
        var req = node.Request;
        return Contains(req.Name, filter) ||
               Contains(req.Method, filter) ||
               Contains(req.Url, filter) ||
               Contains(req.GrpcMethod, filter);
    }

    private void RequestTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_isUpdatingTabs)
            return;

        if (args.InvokedItem is not TreeViewNode tvNode || tvNode.Content is not CollectionNode node)
            return;

        _selectedNode = node;

        if (!node.IsFolder && node.Request != null)
            OpenRequestTab(node.Request.Id, true);
    }

    private void SelectRequestInTree(string requestId)
    {
        _isUpdatingTabs = true;
        if (RequestTreeView.RootNodes.Count > 0)
        {
            var root = RequestTreeView.RootNodes[0];
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


    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListView.SelectedItem is not RequestHistoryEntry entry)
            return;

        ResponseSummaryText.Text = $"历史：{entry.DisplayText}";
        SetResponseMetrics(entry.StatusCode?.ToString() ?? (entry.IsSuccess ? "OK" : "ERR"), entry.ElapsedMilliseconds, null, entry.IsSuccess);
        ResponseHeadersBox.Text = entry.ResponseHeaders;
        string body = JsonFormatService.TryFormat(entry.ResponseBody, out string formatted)
            ? formatted
            : entry.ResponseBody;
        DisplayResponseBody(body);
    }

    private void LoadEditor(ApiRequest request)
    {
        _isLoadingEditor = true;
        NameBox.Text = request.Name;
        UrlBox.Text = request.Url;
        GrpcMethodBox.Text = request.GrpcMethod;
        GrpcTlsCheckBox.IsChecked = request.GrpcUseTls;
        HeadersTable.SetItems(request.Headers);
        QueryTable.SetItems(request.Query);
        FormDataTable.SetItems(request.FormData);
        UrlEncodedTable.SetItems(request.UrlEncodedData);
        BodyBox.Text = request.Body;
        BinaryFilePathText.Text = request.BinaryFilePath ?? "";
        SelectBodyType(request.BodyType);
        SelectType(request.Type);
        SelectMethod(request.Method);
        UpdateTypeVisibility(request.Type);
        UpdateBodyVisibility();
        _isLoadingEditor = false;
    }

    private void LoadRequestById(string requestId)
    {
        var match = FindRequest(requestId);
        if (match.Request == null)
            return;

        _currentCollection = match.Collection;
        _currentRequest = match.Request;
        _workspace.ActiveRequestTabId = requestId;
        RefreshCollectionSelector();
        RefreshLists();
        LoadEditor(match.Request);
        SelectRequestInList(requestId);
    }

    private void ClearEditor()
    {
        _isLoadingEditor = true;
        NameBox.Text = "";
        UrlBox.Text = "";
        GrpcMethodBox.Text = "";
        GrpcTlsCheckBox.IsChecked = false;
        HeadersTable.SetItems(Array.Empty<KeyValuePairItem>());
        QueryTable.SetItems(Array.Empty<KeyValuePairItem>());
        FormDataTable.SetItems(Array.Empty<KeyValuePairItem>());
        UrlEncodedTable.SetItems(Array.Empty<KeyValuePairItem>());
        BodyBox.Text = "";
        BinaryFilePathText.Text = "";
        SelectBodyType(ApiBodyType.None);
        SelectType(ApiRequestType.Http);
        SelectMethod("GET");
        UpdateTypeVisibility(ApiRequestType.Http);
        UpdateBodyVisibility();
        _isLoadingEditor = false;
    }

    private void ApplyEditor()
    {
        if (_isLoadingEditor || _currentRequest == null)
            return;

        _currentRequest.Name = NameBox.Text.Trim();
        _currentRequest.Url = UrlBox.Text.Trim();
        _currentRequest.GrpcMethod = GrpcMethodBox.Text.Trim();
        _currentRequest.GrpcUseTls = GrpcTlsCheckBox.IsChecked == true;
        _currentRequest.Body = BodyBox.Text;
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
        await _storage.SaveAsync(_workspace);
        ResponseSummaryText.Text = $"已创建集合：{collection.Name}";
    }

    private async void DeleteCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null)
            return;

        string deletedName = _currentCollection.Name;
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
        await _storage.SaveAsync(_workspace);
        ResponseSummaryText.Text = $"已删除集合：{deletedName}";
    }

    private async void AddRequestButton_Click(object sender, RoutedEventArgs e)
    {
        _currentCollection ??= _workspace.Collections.FirstOrDefault();
        if (_currentCollection == null)
        {
            _currentCollection = new ApiCollection { Name = "默认集合" };
            _workspace.Collections.Add(_currentCollection);
        }

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

        // Add to selected folder or root
        var targetFolder = GetTargetFolder();
        var targetList = targetFolder != null ? targetFolder.Children : _currentCollection.Nodes;
        targetList.Add(requestNode);

        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshCollectionSelector();
        RefreshLists();
        SelectRequestInTree(request.Id);
        OpenRequestTab(request.Id, true);
    }

    private CollectionNode? GetTargetFolder()
    {
        if (_currentCollection == null)
            return null;
        if (_selectedNode != null && _selectedNode.IsFolder)
            return _selectedNode;
        return null;
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
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        ResponseSummaryText.Text = $"已创建文件夹：{folderName}";
    }

    private async void DeleteRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _currentRequest == null)
            return;

        string deletedName = _currentRequest.Name;
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

        await _storage.SaveAsync(_workspace);
        RefreshLists();
        RefreshOpenRequestTabs();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        else
            ClearEditor();
        ResponseSummaryText.Text = $"已删除请求：{deletedName}";
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
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        if (_currentRequest != null)
            OpenRequestTab(_currentRequest.Id, true);
        ResponseSummaryText.Text = $"已复制请求：{clonedNode.Name}";
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
            // Convert flat requests to nodes
            foreach (var req in collection.Requests)
            {
                collection.Nodes.Add(new CollectionNode
                {
                    Name = req.Name,
                    IsFolder = false,
                    Request = req
                });
            }
            collection.Requests.Clear();

            _workspace.Collections.Add(collection);
            _currentCollection = collection;
            await _storage.SaveAsync(_workspace);
            RefreshCollectionSelector();
            RefreshLists();

            var firstRequestNode = RequestHelpers.GetAllRequestNodes(collection.Nodes).FirstOrDefault();
            if (firstRequestNode?.Request != null)
                SelectRequestInTree(firstRequestNode.Request.Id);

            int requestCount = RequestHelpers.GetAllRequestNodes(collection.Nodes).Count();
            ResponseSummaryText.Text = $"已导入 {collection.Name}，共 {requestCount} 个请求";
        }
        catch (Exception ex)
        {
            ResponseSummaryText.Text = "导入失败";
            DisplayResponseBody(ex.ToString());
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        ApplyEnvironmentEditor();
        await _storage.SaveAsync(_workspace);
        RefreshCollectionSelector();
        RefreshLists();
        RefreshOpenRequestTabs();
        ResponseSummaryText.Text = "已保存";
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest == null)
            return;

        ApplyEditor();
        ApplyEnvironmentEditor();
        await _storage.SaveAsync(_workspace);
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
        SendButton.IsEnabled = false;
        CancelSendButton.Visibility = Visibility.Visible;
        ResponseSummaryText.Text = "发送中...";
        SetResponseMetrics(null, null, null, false);
        RichTextHelper.ApplyPlainText(ResponseBodyRichTextBlock, "");
        ResponseHeadersBox.Text = "";

        try
        {
            ApiRequest resolvedRequest = EnvironmentVariableResolver.Resolve(_currentRequest, _workspace.EnvironmentVariables);
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
                Method = _currentRequest.Type == ApiRequestType.Grpc ? "POST" : _currentRequest.Method,
                Url = _currentRequest.Type == ApiRequestType.Grpc
                    ? $"{resolvedRequest.Url}/{resolvedRequest.GrpcMethod}"
                    : RequestHelpers.BuildUrl(resolvedRequest),
                StatusCode = response.StatusCode,
                ElapsedMilliseconds = response.ElapsedMilliseconds,
                IsSuccess = response.IsSuccess,
                RequestBody = resolvedRequest.Body,
                ResponseHeaders = response.Headers,
                ResponseBody = response.Body
            });

            if (_workspace.History.Count > 500)
                _workspace.History = _workspace.History.OrderByDescending(x => x.Timestamp).Take(500).ToList();

            await _storage.SaveAsync(_workspace);
            RefreshLists();
            RefreshOpenRequestTabs();
        }
        finally
        {
            _sendCancellation.Dispose();
            _sendCancellation = null;
            SendButton.IsEnabled = true;
            CancelSendButton.Visibility = Visibility.Collapsed;
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
            UpdateTypeVisibility(_currentRequest.Type);
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

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => ApplyEditor();

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

    private void CancelSendButton_Click(object sender, RoutedEventArgs e)
    {
        _sendCancellation?.Cancel();
        ResponseSummaryText.Text = "正在取消...";
    }

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
        await _storage.SaveAsync(_workspace);
    }

    private void FormatRequestJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (JsonFormatService.TryFormat(BodyBox.Text, out string formatted))
        {
            BodyBox.Text = formatted;
            ApplyEditor();
            ResponseSummaryText.Text = "请求 Body JSON 已格式化";
        }
        else
        {
            ResponseSummaryText.Text = "请求 Body 不是有效 JSON";
        }
    }

    private void FormatResponseJsonButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-read current displayed text from the RichTextBlock
        string currentText = GetResponseRichText();
        if (JsonFormatService.TryFormat(currentText, out string formatted))
        {
            DisplayResponseBody(formatted);
            ResponseSummaryText.Text = "响应 Body JSON 已格式化";
        }
        else
        {
            ResponseSummaryText.Text = "响应 Body 不是有效 JSON";
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

        BodyBox.Visibility = isRaw ? Visibility.Visible : Visibility.Collapsed;
        FormDataTable.Visibility = isFormData ? Visibility.Visible : Visibility.Collapsed;
        UrlEncodedTable.Visibility = isUrlEncoded ? Visibility.Visible : Visibility.Collapsed;
        BinaryInfoPanel.Visibility = isBinary ? Visibility.Visible : Visibility.Collapsed;
        SelectFileButton.Visibility = isBinary ? Visibility.Visible : Visibility.Collapsed;
        BinaryFilePathText.Visibility = (isBinary && !string.IsNullOrWhiteSpace(BinaryFilePathText.Text)) ? Visibility.Visible : Visibility.Collapsed;
        RawFormatComboBox.Visibility = isRaw ? Visibility.Visible : Visibility.Collapsed;
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
        if (_isLoadingEditor)
            return;

        _workspace.EnvironmentVariables = EnvironmentTable.GetItems();
    }

    private void EnvironmentTable_ItemsChanged(object? sender, EventArgs e)
    {
        if (!_isLoadingEditor)
            _workspace.EnvironmentVariables = EnvironmentTable.GetItems();
    }

    /// <summary>Returns current environment variable names for autocomplete.</summary>
    private List<string> GetEnvironmentVariableNames()
    {
        return _workspace.EnvironmentVariables
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => x.Key.Trim())
            .Distinct()
            .ToList();
    }

    private void UpdateTypeVisibility(ApiRequestType type)
    {
        bool isGrpc = type == ApiRequestType.Grpc;
        GrpcMethodBox.Visibility = isGrpc ? Visibility.Visible : Visibility.Collapsed;
        GrpcTlsCheckBox.Visibility = isGrpc ? Visibility.Visible : Visibility.Collapsed;
        MethodComboBox.IsEnabled = type == ApiRequestType.Http;

        if (type == ApiRequestType.WebSocket)
        {
            SelectMethod("CONNECT");
            if (_currentRequest != null)
                _currentRequest.Method = "CONNECT";
        }
        else if (type == ApiRequestType.Grpc)
        {
            SelectMethod("POST");
            if (_currentRequest != null)
                _currentRequest.Method = "POST";
        }
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
                actionPanel.Visibility = Visibility.Visible;
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
        await AddRequestToNode(node);
    }

    private void NodeMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionNode node)
            return;

        _contextMenuNode = node;
        _selectedNode = node;

        var flyout = (MenuFlyout)Resources["TreeNodeFlyout"];
        flyout.ShowAt(btn);
    }

    private async Task AddRequestToNode(CollectionNode node)
    {
        if (_currentCollection == null)
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

        var targetList = node.IsFolder ? node.Children : _currentCollection.Nodes;
        targetList.Add(requestNode);

        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        SelectRequestInTree(request.Id);
        OpenRequestTab(request.Id, true);
        ResponseSummaryText.Text = $"已创建请求：{request.Name}";
    }

    private void RequestTreeView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element)
            return;

        // Walk up the visual tree to find the TreeViewItem
        var treeViewItem = FindAncestor<TreeViewItem>(element);
        if (treeViewItem?.Content is not CollectionNode collectionNode)
            return;

        _contextMenuNode = collectionNode;
        _selectedNode = collectionNode;

        var flyout = (MenuFlyout)Resources["TreeNodeFlyout"];
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

    private async void ContextAddRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _contextMenuNode == null)
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

        var targetList = _contextMenuNode.IsFolder ? _contextMenuNode.Children : _currentCollection.Nodes;
        targetList.Add(requestNode);

        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        SelectRequestInTree(request.Id);
        OpenRequestTab(request.Id, true);
        ResponseSummaryText.Text = $"已创建请求：{request.Name}";
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

        await _storage.SaveAsync(_workspace);
        RefreshLists();
        ResponseSummaryText.Text = $"已创建文件夹：{folderName}";
    }

    private async void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuNode == null)
            return;

        string title = _contextMenuNode.IsFolder ? "重命名文件夹" : "重命名请求";
        string newName = await ShowInputDialogAsync(title, "新名称", _contextMenuNode.Name);
        if (string.IsNullOrWhiteSpace(newName))
            return;

        _contextMenuNode.Name = newName;
        if (_contextMenuNode.Request != null)
            _contextMenuNode.Request.Name = newName;

        await _storage.SaveAsync(_workspace);
        RefreshLists();
        RefreshOpenRequestTabs();
        ResponseSummaryText.Text = $"已重命名为：{newName}";
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
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        if (_currentRequest != null)
            SelectRequestInTree(_currentRequest.Id);
        if (_currentRequest != null)
            OpenRequestTab(_currentRequest.Id, true);
        ResponseSummaryText.Text = $"已复制请求：{clonedNode.Name}";
    }

    private async void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _contextMenuNode == null)
            return;

        if (_contextMenuNode.IsFolder)
        {
            // Delete folder and all its contents
            var parentList = FindNodeParentList(_currentCollection.Nodes, _contextMenuNode);
            if (parentList != null)
                parentList.Remove(_contextMenuNode);
            else
                _currentCollection.Nodes.Remove(_contextMenuNode);

            // Remove any open tabs for requests inside the deleted folder
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

            await _storage.SaveAsync(_workspace);
            RefreshLists();
            RefreshOpenRequestTabs();
            ResponseSummaryText.Text = $"已删除文件夹：{_contextMenuNode.Name}";
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

            await _storage.SaveAsync(_workspace);
            RefreshLists();
            RefreshOpenRequestTabs();
            ResponseSummaryText.Text = $"已删除请求：{_contextMenuNode.Name}";
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
            _workspace.OpenRequestTabIds.Remove(id);

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
            _workspace.OpenRequestTabIds.Remove(id);

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
            _workspace.OpenRequestTabIds.Remove(id);

        if (toRemove.Contains(_workspace.ActiveRequestTabId))
            _workspace.ActiveRequestTabId = _rightClickTabId;

        RefreshOpenRequestTabs();
        if (!string.IsNullOrWhiteSpace(_workspace.ActiveRequestTabId))
            LoadRequestById(_workspace.ActiveRequestTabId);
        else
            LoadRequestById(_rightClickTabId);
        _ = PersistWorkspaceAsync();
    }

    private void CloseTabById(string requestId)
    {
        int index = _workspace.OpenRequestTabIds.IndexOf(requestId);
        _workspace.OpenRequestTabIds.Remove(requestId);

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

    private async void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        _currentCollection ??= _workspace.Collections.FirstOrDefault();
        if (_currentCollection == null)
        {
            _currentCollection = new ApiCollection { Name = "默认集合" };
            _workspace.Collections.Add(_currentCollection);
        }

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

        var targetFolder = GetTargetFolder();
        var targetList = targetFolder != null ? targetFolder.Children : _currentCollection.Nodes;
        targetList.Add(requestNode);

        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshCollectionSelector();
        RefreshLists();
        SelectRequestInTree(request.Id);
        OpenRequestTab(request.Id, true);
    }

    /// <summary>
    /// Show a simple input dialog to get user text input.
    /// </summary>
    private async Task<string> ShowInputDialogAsync(string title, string placeholder, string defaultValue)
    {
        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            Text = defaultValue,
            MinWidth = 300
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
