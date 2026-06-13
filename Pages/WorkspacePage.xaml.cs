using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest.Pages;

public sealed partial class WorkspacePage : Page
{
    private readonly WorkspaceStorage _storage = new();
    private readonly RequestExecutionService _executor = new();
    private readonly OpenApiImporter _importer = new();
    private readonly ObservableCollection<ApiCollection> _collectionItems = new();
    private readonly ObservableCollection<RequestListItem> _requestItems = new();
    private readonly ObservableCollection<RequestHistoryEntry> _historyItems = new();
    private ApiWorkspace _workspace = new();
    private ApiCollection? _currentCollection;
    private ApiRequest? _currentRequest;
    private CancellationTokenSource? _sendCancellation;
    private bool _isLoadingEditor;
    private bool _isUpdatingTabs;

    public WorkspacePage()
    {
        InitializeComponent();
        CollectionComboBox.ItemsSource = _collectionItems;
        RequestListView.ItemsSource = _requestItems;
        HistoryListView.ItemsSource = _historyItems;
        Loaded += WorkspacePage_Loaded;
    }

    private async void WorkspacePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspacePage_Loaded;
        _workspace = await _storage.LoadAsync();
        App.Current.ApplySettings(_workspace.Settings);
        ApplyEditorFonts();
        _isLoadingEditor = true;
        EnvironmentBox.Text = SerializePairs(_workspace.EnvironmentVariables, "=");
        _isLoadingEditor = false;
        _currentCollection = _workspace.Collections.FirstOrDefault();
        RefreshCollectionSelector();
        RefreshLists();
        RestoreOpenRequestTabs();
        if (_workspace.OpenRequestTabIds.Count == 0 && _requestItems.Count > 0)
            RequestListView.SelectedIndex = 0;
    }

    private void ApplyEditorFonts()
    {
        AppSettingsApplier.ApplyEditorFont(HeadersBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(QueryBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(BodyBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(EnvironmentBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(ResponseBodyBox, _workspace.Settings);
        AppSettingsApplier.ApplyEditorFont(ResponseHeadersBox, _workspace.Settings);
    }

    private void RefreshCollectionSelector()
    {
        _isLoadingEditor = true;
        string selectedId = _currentCollection?.Id ?? "";
        _collectionItems.Clear();
        foreach (var collection in _workspace.Collections)
            _collectionItems.Add(collection);

        _currentCollection = _workspace.Collections.FirstOrDefault(x => x.Id == selectedId)
            ?? _workspace.Collections.FirstOrDefault();
        CollectionComboBox.SelectedItem = _currentCollection;
        CollectionNameBox.Text = _currentCollection?.Name ?? "";
        _isLoadingEditor = false;
    }

    private void RefreshLists()
    {
        _requestItems.Clear();
        var collections = _currentCollection == null
            ? _workspace.Collections
            : _workspace.Collections.Where(x => x.Id == _currentCollection.Id);

        string filter = RequestSearchBox?.Text.Trim() ?? "";
        foreach (var collection in collections)
        {
            foreach (var request in collection.Requests.Where(x => MatchesRequestFilter(x, collection.Name, filter)))
            {
                _requestItems.Add(new RequestListItem
                {
                    CollectionId = collection.Id,
                    CollectionName = collection.Name,
                    Request = request
                });
            }
        }

        _historyItems.Clear();
        foreach (var item in _workspace.History.OrderByDescending(x => x.Timestamp).Take(200))
            _historyItems.Add(item);
    }

    private void RequestListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTabs)
            return;

        if (RequestListView.SelectedItem is not RequestListItem item)
            return;

        OpenRequestTab(item.Request.Id, true);
    }

    private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListView.SelectedItem is not RequestHistoryEntry entry)
            return;

        ResponseSummaryText.Text = $"历史：{entry.DisplayText}";
        ResponseHeadersBox.Text = entry.ResponseHeaders;
        ResponseBodyBox.Text = JsonFormatService.TryFormat(entry.ResponseBody, out string formatted)
            ? formatted
            : entry.ResponseBody;
    }

    private void LoadEditor(ApiRequest request)
    {
        _isLoadingEditor = true;
        NameBox.Text = request.Name;
        UrlBox.Text = request.Url;
        GrpcMethodBox.Text = request.GrpcMethod;
        GrpcTlsCheckBox.IsChecked = request.GrpcUseTls;
        HeadersBox.Text = SerializePairs(request.Headers, ": ");
        QueryBox.Text = SerializePairs(request.Query, "=");
        BodyBox.Text = request.Body;
        SelectType(request.Type);
        SelectMethod(request.Method);
        UpdateTypeVisibility(request.Type);
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
        HeadersBox.Text = "";
        QueryBox.Text = "";
        BodyBox.Text = "";
        SelectType(ApiRequestType.Http);
        SelectMethod("GET");
        UpdateTypeVisibility(ApiRequestType.Http);
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
        _currentRequest.Headers = ParsePairs(HeadersBox.Text, ':');
        _currentRequest.Query = ParsePairs(QueryBox.Text, '=');

        if (TypeComboBox.SelectedItem is ComboBoxItem typeItem &&
            Enum.TryParse<ApiRequestType>(typeItem.Tag?.ToString(), out var type))
        {
            _currentRequest.Type = type;
        }

        if (MethodComboBox.SelectedItem is ComboBoxItem methodItem)
            _currentRequest.Method = methodItem.Content?.ToString() ?? "GET";
    }

    private void ApplyCollectionEditor()
    {
        if (_isLoadingEditor || _currentCollection == null)
            return;

        string name = CollectionNameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            _currentCollection.Name = name;
    }

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
        var deletedRequestIds = _currentCollection.Requests.Select(x => x.Id).ToHashSet();
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
        _currentCollection.Requests.Add(request);
        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshCollectionSelector();
        RefreshLists();
        RequestListView.SelectedItem = _requestItems.FirstOrDefault(x => x.Request.Id == request.Id);
        OpenRequestTab(request.Id, true);
    }

    private async void DeleteRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _currentRequest == null)
            return;

        string deletedName = _currentRequest.Name;
        string deletedId = _currentRequest.Id;
        _currentCollection.Requests.Remove(_currentRequest);
        _workspace.OpenRequestTabIds.Remove(deletedId);
        if (_workspace.ActiveRequestTabId == deletedId)
            _workspace.ActiveRequestTabId = "";
        _currentRequest = _currentCollection.Requests.FirstOrDefault();
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        RefreshOpenRequestTabs();
        RequestListView.SelectedItem = _currentRequest == null
            ? null
            : _requestItems.FirstOrDefault(x => x.Request.Id == _currentRequest.Id);
        if (_currentRequest == null)
            ClearEditor();
        ResponseSummaryText.Text = $"已删除请求：{deletedName}";
    }

    private async void DuplicateRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCollection == null || _currentRequest == null)
            return;

        ApplyEditor();
        var request = RequestHelpers.Clone(_currentRequest);
        request.Id = Guid.NewGuid().ToString();
        request.Name = string.IsNullOrWhiteSpace(request.Name) ? "复制的请求" : $"{request.Name} Copy";
        _currentCollection.Requests.Add(request);
        _currentRequest = request;
        await _storage.SaveAsync(_workspace);
        RefreshLists();
        RequestListView.SelectedItem = _requestItems.FirstOrDefault(x => x.Request.Id == request.Id);
        OpenRequestTab(request.Id, true);
        ResponseSummaryText.Text = $"已复制请求：{request.Name}";
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
            await _storage.SaveAsync(_workspace);
            RefreshCollectionSelector();
            RefreshLists();
            RequestListView.SelectedItem = _requestItems.FirstOrDefault(x => x.CollectionId == collection.Id);
            ResponseSummaryText.Text = $"已导入 {collection.Name}，共 {collection.Requests.Count} 个请求";
        }
        catch (Exception ex)
        {
            ResponseSummaryText.Text = "导入失败";
            ResponseBodyBox.Text = ex.ToString();
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditor();
        ApplyCollectionEditor();
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
        ApplyCollectionEditor();
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
        ResponseBodyBox.Text = "";
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
            ResponseBodyBox.Text = displayBody;
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
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => ApplyEditor();
    private void Editor_CheckChanged(object sender, RoutedEventArgs e) => ApplyEditor();
    private void EnvironmentBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyEnvironmentEditor();
    private void CollectionNameBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCollectionEditor();
    private void RequestSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLists();

    private void CancelSendButton_Click(object sender, RoutedEventArgs e)
    {
        _sendCancellation?.Cancel();
        ResponseSummaryText.Text = "正在取消...";
    }

    private void CollectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingEditor)
            return;

        ApplyCollectionEditor();
        _currentCollection = CollectionComboBox.SelectedItem as ApiCollection;
        _currentRequest = null;
        CollectionNameBox.Text = _currentCollection?.Name ?? "";
        RefreshLists();
        if (_requestItems.Count > 0)
            RequestListView.SelectedIndex = 0;
        else
            ClearEditor();
    }

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
        _isUpdatingTabs = true;
        RequestListView.SelectedItem = _requestItems.FirstOrDefault(x => x.Request.Id == requestId);
        _isUpdatingTabs = false;
    }

    private (ApiCollection? Collection, ApiRequest? Request) FindRequest(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return (null, null);

        foreach (var collection in _workspace.Collections)
        {
            var request = collection.Requests.FirstOrDefault(x => x.Id == requestId);
            if (request != null)
                return (collection, request);
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
        if (JsonFormatService.TryFormat(ResponseBodyBox.Text, out string formatted))
        {
            ResponseBodyBox.Text = formatted;
            ResponseSummaryText.Text = "响应 Body JSON 已格式化";
        }
        else
        {
            ResponseSummaryText.Text = "响应 Body 不是有效 JSON";
        }
    }

    private void ApplyEnvironmentEditor()
    {
        if (_isLoadingEditor)
            return;

        _workspace.EnvironmentVariables = ParsePairs(EnvironmentBox.Text, '=');
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

    private static string SerializePairs(IEnumerable<KeyValuePairItem> pairs, string separator)
    {
        return string.Join(Environment.NewLine, pairs
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => $"{x.Key}{separator}{x.Value}"));
    }

    private static List<KeyValuePairItem> ParsePairs(string text, char separator)
    {
        var result = new List<KeyValuePairItem>();
        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int index = line.IndexOf(separator);
            if (index < 0)
            {
                result.Add(new KeyValuePairItem { Key = line, Value = "" });
                continue;
            }

            result.Add(new KeyValuePairItem
            {
                Key = line[..index].Trim(),
                Value = line[(index + 1)..].Trim()
            });
        }
        return result;
    }

    private static bool MatchesRequestFilter(ApiRequest request, string collectionName, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(collectionName, filter) ||
               Contains(request.Name, filter) ||
               Contains(request.Method, filter) ||
               Contains(request.Url, filter) ||
               Contains(request.GrpcMethod, filter);
    }

    private static bool Contains(string value, string filter)
    {
        return value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
