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
            ApiRequestType.Grpc => ExecuteGrpcAsync(request, cancellationToken),
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
                    formContent.Add(new StringContent(field.Value ?? ""), field.Key);
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
                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
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
            foreach (var header in request.Headers.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
                socket.Options.SetRequestHeader(header.Key, header.Value);

            await socket.ConnectAsync(new Uri(RequestHelpers.BuildUrl(request)), cancellationToken);

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

    private static async Task<ApiResponse> ExecuteGrpcAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string grpcurl = OperatingSystem.IsWindows() ? "grpcurl.exe" : "grpcurl";
        try
        {
            string tlsFlag = request.GrpcUseTls ? "" : "-plaintext";
            string headers = string.Join(" ", request.Headers
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => $"-H {Quote($"{x.Key}: {x.Value}")}"));
            string data = string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body;
            string args = $"{tlsFlag} {headers} -d {Quote(data)} {Quote(request.Url)} {Quote(request.GrpcMethod)}";

            var startInfo = new ProcessStartInfo
            {
                FileName = grpcurl,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("无法启动 grpcurl。");

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            stopwatch.Stop();

            return new ApiResponse
            {
                StatusText = process.ExitCode == 0 ? "gRPC OK" : $"gRPC Exit {process.ExitCode}",
                Headers = $"Command: grpcurl {args}",
                Body = string.IsNullOrWhiteSpace(error) ? output : $"{output}{Environment.NewLine}{error}",
                BodyBytes = Encoding.UTF8.GetByteCount(string.IsNullOrWhiteSpace(error) ? output : $"{output}{Environment.NewLine}{error}"),
                Error = process.ExitCode == 0 ? "" : error.Trim(),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                IsSuccess = process.ExitCode == 0
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                StatusText = "gRPC Error",
                Body = "gRPC 请求通过 grpcurl 执行。请确认已安装 grpcurl，并且目标服务支持你填写的 service/method 与 JSON 请求体。" +
                       Environment.NewLine + Environment.NewLine + ex,
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

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
