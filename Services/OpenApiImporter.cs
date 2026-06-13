using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WinRequest.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WinRequest.Services;

public sealed class OpenApiImporter
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace"
    };

    public async Task<ApiCollection> ImportAsync(string filePath)
    {
        string text = await File.ReadAllTextAsync(filePath);
        using JsonDocument document = ParseDocument(text, filePath);
        JsonElement root = document.RootElement;

        string title = ReadString(root, "info", "title");
        var collection = new ApiCollection
        {
            Name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title
        };

        string baseUrl = ReadBaseUrl(root);
        if (root.TryGetProperty("paths", out JsonElement paths))
        {
            foreach (JsonProperty path in paths.EnumerateObject())
            {
                foreach (JsonProperty operation in path.Value.EnumerateObject())
                {
                    if (!HttpMethods.Contains(operation.Name))
                        continue;

                    string method = operation.Name.ToUpperInvariant();
                    string convertedPath = ConvertPathVariables(path.Name);
                    string summary = ReadString(operation.Value, "summary");
                    string operationId = ReadString(operation.Value, "operationId");
                    var request = new ApiRequest
                    {
                        Type = ApiRequestType.Http,
                        Method = method,
                        Name = FirstNonEmpty(summary, operationId, $"{method} {path.Name}"),
                        Url = CombineUrl(baseUrl, convertedPath)
                    };

                    AddParameters(request, path.Value);
                    AddParameters(request, operation.Value);
                    AddJsonBodyPlaceholder(request, operation.Value);
                    collection.Requests.Add(request);
                }
            }
        }

        if (collection.Requests.Count == 0)
        {
            collection.Requests.Add(new ApiRequest
            {
                Name = "导入的空 OpenAPI 文档",
                Method = "GET",
                Url = baseUrl,
                Type = ApiRequestType.Http
            });
        }

        return collection;
    }

    private static JsonDocument ParseDocument(string text, string filePath)
    {
        if (Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            text.TrimStart().StartsWith('{'))
        {
            return JsonDocument.Parse(text);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        object? yaml = deserializer.Deserialize(new StringReader(text));
        var serializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();
        return JsonDocument.Parse(serializer.Serialize(yaml));
    }

    private static void AddParameters(ApiRequest request, JsonElement operation)
    {
        if (!operation.TryGetProperty("parameters", out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            string name = ReadString(parameter, "name");
            string location = ReadString(parameter, "in");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (string.Equals(location, "query", StringComparison.OrdinalIgnoreCase))
                request.Query.Add(new KeyValuePairItem { Key = name, Value = "" });
            else if (string.Equals(location, "header", StringComparison.OrdinalIgnoreCase))
                request.Headers.Add(new KeyValuePairItem { Key = name, Value = "" });
        }
    }

    private static void AddJsonBodyPlaceholder(ApiRequest request, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out JsonElement body) ||
            !body.TryGetProperty("content", out JsonElement content))
            return;

        foreach (JsonProperty mediaType in content.EnumerateObject())
        {
            request.Headers.Add(new KeyValuePairItem { Key = "Content-Type", Value = mediaType.Name });
            request.Body = mediaType.Name.Contains("json", StringComparison.OrdinalIgnoreCase) ? "{\n  \n}" : "";
            return;
        }
    }

    private static string ReadBaseUrl(JsonElement root)
    {
        if (root.TryGetProperty("servers", out JsonElement servers) &&
            servers.ValueKind == JsonValueKind.Array &&
            servers.GetArrayLength() > 0)
        {
            string url = ReadString(servers[0], "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        string host = ReadString(root, "host");
        if (string.IsNullOrWhiteSpace(host))
            return "";

        string basePath = ReadString(root, "basePath");
        string scheme = "https";
        if (root.TryGetProperty("schemes", out JsonElement schemes) &&
            schemes.ValueKind == JsonValueKind.Array &&
            schemes.GetArrayLength() > 0)
            scheme = schemes[0].GetString() ?? scheme;
        return $"{scheme}://{host}{basePath}";
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return path;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private static string ConvertPathVariables(string path)
    {
        return System.Text.RegularExpressions.Regex.Replace(path, "\\{([^{}]+)\\}", "{{$1}}");
    }

    private static string ReadString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string part in path)
        {
            if (!current.TryGetProperty(part, out current))
                return "";
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }
}
