using System;
using System.Collections.Generic;
using System.Linq;

namespace WinRequest.Models;

public enum ApiRequestType
{
    Http,
    Grpc,
    WebSocket
}

public enum ApiBodyType
{
    None,
    Raw,
    Json,
    Xml,
    FormData,
    XWwwFormUrlencoded,
    Binary
}

public sealed class ApiWorkspace
{
    public List<ApiCollection> Collections { get; set; } = new();
    public List<RequestHistoryEntry> History { get; set; } = new();
    public List<KeyValuePairItem> EnvironmentVariables { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<string> OpenRequestTabIds { get; set; } = new();
    public string ActiveRequestTabId { get; set; } = "";
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string FontFamily { get; set; } = "Consolas";
    public double TextSize { get; set; } = 13;
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepository { get; set; } = "";
}

public sealed class ApiCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新建集合";
    public List<ApiRequest> Requests { get; set; } = new();
    public List<CollectionNode> Nodes { get; set; } = new();

    /// <summary>
    /// Migrates legacy flat Requests list into the Nodes tree structure.
    /// Call after deserialization to ensure backward compatibility.
    /// </summary>
    public void EnsureNodes()
    {
        if (Nodes.Count == 0 && Requests.Count > 0)
        {
            foreach (var req in Requests)
            {
                Nodes.Add(new CollectionNode
                {
                    Name = req.Name,
                    IsFolder = false,
                    Request = req
                });
            }
            Requests.Clear();
        }
    }
}

/// <summary>
/// Represents a node in the collection tree — either a folder or a request.
/// </summary>
public sealed class CollectionNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; } = true;
    public List<CollectionNode> Children { get; set; } = new();
    public ApiRequest? Request { get; set; }
}

public sealed class ApiRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新建请求";
    public ApiRequestType Type { get; set; } = ApiRequestType.Http;
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<KeyValuePairItem> Headers { get; set; } = new();
    public List<KeyValuePairItem> Query { get; set; } = new();
    public List<KeyValuePairItem> FormData { get; set; } = new();
    public List<KeyValuePairItem> UrlEncodedData { get; set; } = new();
    public string Body { get; set; } = "";
    public ApiBodyType BodyType { get; set; } = ApiBodyType.None;
    public string GrpcMethod { get; set; } = "";
    public bool GrpcUseTls { get; set; }
    public string BinaryFilePath { get; set; } = "";
}

public sealed class KeyValuePairItem
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class RequestHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string RequestName { get; set; } = "";
    public ApiRequestType Type { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int? StatusCode { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public bool IsSuccess { get; set; }
    public string RequestBody { get; set; } = "";
    public string ResponseHeaders { get; set; } = "";
    public string ResponseBody { get; set; } = "";

    public string DisplayText
    {
        get
        {
            string status = StatusCode.HasValue ? StatusCode.Value.ToString() : (IsSuccess ? "OK" : "ERR");
            return $"{Timestamp:HH:mm:ss}  {Type}  {Method}  {status}  {Url}";
        }
    }
}

public sealed class ApiResponse
{
    public int? StatusCode { get; set; }
    public string StatusText { get; set; } = "";
    public string Headers { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Body { get; set; } = "";
    public long BodyBytes { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public bool IsSuccess { get; set; }
    public string Error { get; set; } = "";

    public string Summary => Error.Length > 0
        ? $"失败 · {ElapsedMilliseconds} ms · {Error}"
        : $"{StatusText} · {ElapsedMilliseconds} ms · {FormatBytes(BodyBytes)}{FormatContentType(ContentType)}";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string FormatContentType(string contentType)
    {
        return string.IsNullOrWhiteSpace(contentType) ? "" : $" · {contentType}";
    }
}

public sealed class RequestListItem
{
    public string CollectionId { get; init; } = "";
    public string CollectionName { get; init; } = "";
    public ApiRequest Request { get; init; } = new();
    public string DisplayText => $"{CollectionName} / {Request.Type}  {Request.Method}  {Request.Name}";
}

public static class RequestHelpers
{
    public static string BuildUrl(ApiRequest request)
    {
        string url = request.Url.Trim();
        var query = request.Query
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? "")}")
            .ToList();

        if (query.Count == 0)
            return url;

        return url.Contains('?') ? $"{url}&{string.Join('&', query)}" : $"{url}?{string.Join('&', query)}";
    }

    public static ApiRequest Clone(ApiRequest source)
    {
        return new ApiRequest
        {
            Id = source.Id,
            Name = source.Name,
            Type = source.Type,
            Method = source.Method,
            Url = source.Url,
            Headers = source.Headers.Select(ClonePair).ToList(),
            Query = source.Query.Select(ClonePair).ToList(),
            FormData = source.FormData.Select(ClonePair).ToList(),
            UrlEncodedData = source.UrlEncodedData.Select(ClonePair).ToList(),
            Body = source.Body,
            BodyType = source.BodyType,
            GrpcMethod = source.GrpcMethod,
            GrpcUseTls = source.GrpcUseTls,
            BinaryFilePath = source.BinaryFilePath
        };
    }

    private static KeyValuePairItem ClonePair(KeyValuePairItem source)
    {
        return new KeyValuePairItem
        {
            Key = source.Key,
            Value = source.Value,
            Description = source.Description,
            Enabled = source.Enabled
        };
    }

    /// <summary>
    /// Deep-clone a CollectionNode tree.
    /// </summary>
    public static CollectionNode CloneNode(CollectionNode source)
    {
        var clone = new CollectionNode
        {
            Id = Guid.NewGuid().ToString(),
            Name = source.Name,
            IsFolder = source.IsFolder,
            Request = source.Request != null ? Clone(source.Request) : null
        };
        if (source.Request != null)
            clone.Request!.Id = Guid.NewGuid().ToString();
        foreach (var child in source.Children)
            clone.Children.Add(CloneNode(child));
        return clone;
    }

    /// <summary>
    /// Find a request node by ID in a list of nodes (recursive).
    /// </summary>
    public static CollectionNode? FindNodeById(List<CollectionNode> nodes, string requestId)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && node.Request?.Id == requestId)
                return node;
            var found = FindNodeById(node.Children, requestId);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Find the parent list that contains the node with the given request ID.
    /// </summary>
    public static List<CollectionNode>? FindParentList(List<CollectionNode> nodes, string requestId)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && node.Request?.Id == requestId)
                return nodes;
            var found = FindParentList(node.Children, requestId);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Find a folder node by its node ID (recursive).
    /// </summary>
    public static CollectionNode? FindFolderById(List<CollectionNode> nodes, string nodeId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == nodeId)
                return node;
            var found = FindFolderById(node.Children, nodeId);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Collect all request nodes from a tree (flattened).
    /// </summary>
    public static IEnumerable<CollectionNode> GetAllRequestNodes(List<CollectionNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && node.Request != null)
                yield return node;
            foreach (var child in GetAllRequestNodes(node.Children))
                yield return child;
        }
    }

    /// <summary>
    /// Returns the ordered list of node names from root to the node (inclusive) that contains the given requestId.
    /// For a request node the last entry is the request node itself; folders precede it.
    /// Returns null if not found.
    /// </summary>
    public static List<string>? GetNodePath(List<CollectionNode> nodes, string requestId)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && node.Request?.Id == requestId)
                return new List<string> { node.Name };
            if (node.IsFolder)
            {
                var subPath = GetNodePath(node.Children, requestId);
                if (subPath != null)
                {
                    subPath.Insert(0, node.Name);
                    return subPath;
                }
            }
        }
        return null;
    }
}
