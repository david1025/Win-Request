using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinRequest.Models;

namespace WinRequest.Services;

public sealed class RequestExecutionService
{
    private readonly HttpClient _httpClient = new();

    public Task<ApiResponse> ExecuteAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        return request.Type switch
        {
            ApiRequestType.Http => ExecuteHttpAsync(request, cancellationToken),
            ApiRequestType.WebSocket => ExecuteWebSocketAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported request type: {request.Type}")
        };
    }

    private async Task<ApiResponse> ExecuteHttpAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), RequestHelpers.BuildUrl(request));

            if (!string.IsNullOrWhiteSpace(request.Body) && request.Method is not "GET" and not "HEAD"
                && request.BodyType is not ApiBodyType.FormData
                and not ApiBodyType.XWwwFormUrlencoded
                and not ApiBodyType.Binary)
                message.Content = new StringContent(request.Body, Encoding.UTF8, GuessContentType(request));

            if (request.BodyType == ApiBodyType.FormData && request.Method is not "GET" and not "HEAD")
            {
                var formContent = new MultipartFormDataContent();
                foreach (var field in request.FormData.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
                {
                    if (field.ValueKind == KeyValueValueKind.File)
                    {
                        string filePath = field.Value ?? "";
                        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                            continue;

                        byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken);
                        string fileName = System.IO.Path.GetFileName(filePath);
                        formContent.Add(new ByteArrayContent(fileBytes), field.Key, fileName);
                    }
                    else
                    {
                        formContent.Add(new StringContent(field.Value ?? ""), field.Key);
                    }
                }
                message.Content = formContent;
            }

            if (request.BodyType == ApiBodyType.XWwwFormUrlencoded && request.Method is not "GET" and not "HEAD")
            {
                var pairs = request.UrlEncodedData
                    .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
                    .Select(x => new KeyValuePair<string, string>(x.Key, x.Value ?? ""));
                message.Content = new FormUrlEncodedContent(pairs);
            }

            if (request.BodyType == ApiBodyType.Binary && request.Method is not "GET" and not "HEAD"
                && !string.IsNullOrWhiteSpace(request.BinaryFilePath) && System.IO.File.Exists(request.BinaryFilePath))
            {
                byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(request.BinaryFilePath, cancellationToken);
                string fileName = System.IO.Path.GetFileName(request.BinaryFilePath);
                message.Content = new ByteArrayContent(fileBytes);
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                message.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { FileName = fileName };
            }

            foreach (var header in request.Headers.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
            {
                string headerValue = ResolveHeaderValue(header, message.RequestUri);
                if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (!message.Headers.TryAddWithoutValidation("Host", headerValue))
                        message.Content?.Headers.TryAddWithoutValidation("Host", headerValue);
                    continue;
                }

                if (!message.Headers.TryAddWithoutValidation(header.Key, headerValue))
                    message.Content?.Headers.TryAddWithoutValidation(header.Key, headerValue);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken);
            byte[] bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string body = DecodeBody(bodyBytes, response.Content.Headers.ContentType?.CharSet);
            stopwatch.Stop();

            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(x => $"{x.Key}: {string.Join(", ", x.Value)}");

            return new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                StatusText = $"{(int)response.StatusCode} {response.ReasonPhrase}",
                Headers = string.Join(Environment.NewLine, headers),
                ContentType = response.Content.Headers.ContentType?.MediaType ?? "",
                Body = body,
                BodyBytes = bodyBytes.LongLength,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                IsSuccess = response.IsSuccessStatusCode
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                StatusText = "HTTP Error",
                Body = ex.ToString(),
                Error = ex.Message,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static async Task<ApiResponse> ExecuteWebSocketAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var socket = new ClientWebSocket();
            var uri = new Uri(RequestHelpers.BuildUrl(request));
            foreach (var header in request.Headers.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
                socket.Options.SetRequestHeader(header.Key, ResolveHeaderValue(header, uri));

            await socket.ConnectAsync(uri, cancellationToken);

            if (!string.IsNullOrEmpty(request.Body))
            {
                byte[] outbound = Encoding.UTF8.GetBytes(request.Body);
                await socket.SendAsync(outbound, WebSocketMessageType.Text, true, cancellationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var buffer = new byte[1024 * 128];
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
            string body = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Win Request done", CancellationToken.None);

            stopwatch.Stop();
            return new ApiResponse
            {
                StatusText = $"WebSocket {socket.State}",
                Headers = $"MessageType: {result.MessageType}{Environment.NewLine}EndOfMessage: {result.EndOfMessage}",
                Body = body,
                BodyBytes = Encoding.UTF8.GetByteCount(body),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                StatusText = "WebSocket Error",
                Body = ex.ToString(),
                Error = ex.Message,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static string GuessContentType(ApiRequest request)
    {
        // Check explicit Content-Type header first
        var contentTypeHeader = request.Headers.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
        if (contentTypeHeader != null && !string.IsNullOrWhiteSpace(contentTypeHeader.Value))
            return contentTypeHeader.Value;

        // Use BodyType to determine content type
        return request.BodyType switch
        {
            ApiBodyType.Json => "application/json",
            ApiBodyType.Xml => "application/xml",
            ApiBodyType.FormData => "multipart/form-data",
            ApiBodyType.XWwwFormUrlencoded => "application/x-www-form-urlencoded",
            ApiBodyType.Raw => "text/plain",
            ApiBodyType.Binary => "application/octet-stream",
            _ => GuessContentTypeFromBody(request.Body)
        };
    }

    private static string GuessContentTypeFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "text/plain";
        string trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "application/json" : "text/plain";
    }

    private static string DecodeBody(byte[] body, string? charset)
    {
        if (body.Length == 0)
            return "";

        try
        {
            Encoding encoding = string.IsNullOrWhiteSpace(charset)
                ? Encoding.UTF8
                : Encoding.GetEncoding(charset);
            return encoding.GetString(body);
        }
        catch
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    private static string ResolveHeaderValue(KeyValuePairItem header, Uri? requestUri)
    {
        if (!header.IsAutoGenerated)
            return header.Value ?? "";

        return header.Key.Trim().ToUpperInvariant() switch
        {
            "WINREQUEST-TOKEN" => Guid.NewGuid().ToString("N"),
            "HOST" => requestUri?.Authority ?? "",
            _ => header.Value ?? ""
        };
    }
}
