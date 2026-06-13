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
    public string Theme { get; set; } = "System";
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
    public string Body { get; set; } = "";
    public string GrpcMethod { get; set; } = "";
    public bool GrpcUseTls { get; set; }
}

public sealed class KeyValuePairItem
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
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
            Headers = source.Headers.Select(x => new KeyValuePairItem { Key = x.Key, Value = x.Value, Enabled = x.Enabled }).ToList(),
            Query = source.Query.Select(x => new KeyValuePairItem { Key = x.Key, Value = x.Value, Enabled = x.Enabled }).ToList(),
            Body = source.Body,
            GrpcMethod = source.GrpcMethod,
            GrpcUseTls = source.GrpcUseTls
        };
    }
}
