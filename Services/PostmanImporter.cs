using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WinRequest.Models;

namespace WinRequest.Services;

public sealed class PostmanImporter
{
    public async Task<ApiCollection> ImportAsync(string filePath)
    {
        string text = await File.ReadAllTextAsync(filePath);
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        string collectionName = ReadString(root, "info", "name");
        if (string.IsNullOrWhiteSpace(collectionName))
            collectionName = Path.GetFileNameWithoutExtension(filePath);

        var collection = new ApiCollection
        {
            Name = collectionName
        };

        if (root.TryGetProperty("item", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                var node = ConvertItem(item);
                if (node != null)
                    collection.Nodes.Add(node);
            }
        }

        if (root.TryGetProperty("variable", out JsonElement variables) &&
            variables.ValueKind == JsonValueKind.Array)
        {
            var envProfile = collection.ImportedEnvironment ?? new EnvironmentProfile
            {
                Name = $"{collectionName} Variables"
            };

            foreach (JsonElement v in variables.EnumerateArray())
            {
                string key = ReadString(v, "key");
                string value = ReadString(v, "value");
                if (!string.IsNullOrEmpty(key))
                {
                    envProfile.Variables.Add(new KeyValuePairItem
                    {
                        Key = key,
                        Value = value,
                        Enabled = true
                    });
                }
            }

            if (envProfile.Variables.Count > 0)
                collection.ImportedEnvironment = envProfile;
        }

        if (collection.Nodes.Count == 0)
        {
            collection.Nodes.Add(new CollectionNode
            {
                Name = "导入的空 Postman 集合",
                IsFolder = false,
                Request = new ApiRequest
                {
                    Name = "导入的空 Postman 集合",
                    Method = "GET",
                    Url = "",
                    Type = ApiRequestType.Http
                }
            });
        }

        return collection;
    }

    private CollectionNode? ConvertItem(JsonElement item)
    {
        string name = ReadString(item, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = "Untitled";

        if (item.TryGetProperty("item", out JsonElement children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            var folderNode = new CollectionNode
            {
                Name = name,
                IsFolder = true
            };

            foreach (JsonElement child in children.EnumerateArray())
            {
                var childNode = ConvertItem(child);
                if (childNode != null)
                    folderNode.Children.Add(childNode);
            }

            return folderNode;
        }

        if (item.TryGetProperty("request", out JsonElement requestElement))
        {
            var request = ConvertRequest(requestElement, name);
            return new CollectionNode
            {
                Name = name,
                IsFolder = false,
                Request = request
            };
        }

        return null;
    }

    private ApiRequest ConvertRequest(JsonElement requestElement, string name)
    {
        var request = new ApiRequest
        {
            Name = name,
            Type = ApiRequestType.Http,
            Method = ReadString(requestElement, "method"),
            Url = ""
        };

        if (string.IsNullOrWhiteSpace(request.Method))
            request.Method = "GET";

        ConvertUrl(requestElement, request);
        ConvertHeaders(requestElement, request);
        ConvertBody(requestElement, request);
        ConvertQueryParams(requestElement, request);

        return request;
    }

    private void ConvertUrl(JsonElement requestElement, ApiRequest request)
    {
        if (!requestElement.TryGetProperty("url", out JsonElement urlElement))
            return;

        if (urlElement.ValueKind == JsonValueKind.String)
        {
            request.Url = urlElement.GetString() ?? "";
            return;
        }

        string raw = ReadString(urlElement, "raw");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            request.Url = raw;
            return;
        }

        string protocol = ReadString(urlElement, "protocol");
        string host = "";
        if (urlElement.TryGetProperty("host", out JsonElement hostElement) &&
            hostElement.ValueKind == JsonValueKind.Array)
        {
            host = string.Join(".", hostElement.EnumerateArray()
                .Select(h => h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : h.GetRawText()));
        }

        string port = ReadString(urlElement, "port");
        string path = "";
        if (urlElement.TryGetProperty("path", out JsonElement pathElement) &&
            pathElement.ValueKind == JsonValueKind.Array)
        {
            path = "/" + string.Join("/", pathElement.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.GetRawText()));
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            string prefix = string.IsNullOrWhiteSpace(protocol) ? "http://" : $"{protocol}://";
            request.Url = $"{prefix}{host}{(string.IsNullOrWhiteSpace(port) ? "" : $":{port}")}{path}";
        }
    }

    private void ConvertHeaders(JsonElement requestElement, ApiRequest request)
    {
        if (!requestElement.TryGetProperty("header", out JsonElement headers) ||
            headers.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement header in headers.EnumerateArray())
        {
            string key = ReadString(header, "key");
            string value = ReadString(header, "value");
            if (string.IsNullOrEmpty(key))
                continue;

            bool disabled = false;
            if (header.TryGetProperty("disabled", out JsonElement disabledElement) &&
                disabledElement.ValueKind == JsonValueKind.True)
                disabled = true;

            request.Headers.Add(new KeyValuePairItem
            {
                Key = key,
                Value = value,
                Enabled = !disabled
            });
        }
    }

    private void ConvertBody(JsonElement requestElement, ApiRequest request)
    {
        if (!requestElement.TryGetProperty("body", out JsonElement bodyElement))
            return;

        string mode = ReadString(bodyElement, "mode");

        switch (mode?.ToLowerInvariant())
        {
            case "raw":
                request.Body = ReadString(bodyElement, "raw");
                request.BodyType = DetectRawBodyType(bodyElement);
                break;

            case "formdata":
                request.BodyType = ApiBodyType.FormData;
                if (bodyElement.TryGetProperty("formdata", out JsonElement formdata) &&
                    formdata.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement field in formdata.EnumerateArray())
                    {
                        string key = ReadString(field, "key");
                        string value = ReadString(field, "value");
                        string type = ReadString(field, "type");
                        bool disabled = false;
                        if (field.TryGetProperty("disabled", out JsonElement d) &&
                            d.ValueKind == JsonValueKind.True)
                            disabled = true;

                        request.FormData.Add(new KeyValuePairItem
                        {
                            Key = key,
                            Value = value,
                            ValueKind = string.Equals(type, "file", StringComparison.OrdinalIgnoreCase)
                                ? KeyValueValueKind.File
                                : KeyValueValueKind.Text,
                            Enabled = !disabled
                        });
                    }
                }
                break;

            case "urlencoded":
                request.BodyType = ApiBodyType.XWwwFormUrlencoded;
                if (bodyElement.TryGetProperty("urlencoded", out JsonElement urlencoded) &&
                    urlencoded.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement field in urlencoded.EnumerateArray())
                    {
                        string key = ReadString(field, "key");
                        string value = ReadString(field, "value");
                        bool disabled = false;
                        if (field.TryGetProperty("disabled", out JsonElement d) &&
                            d.ValueKind == JsonValueKind.True)
                            disabled = true;

                        request.UrlEncodedData.Add(new KeyValuePairItem
                        {
                            Key = key,
                            Value = value,
                            Enabled = !disabled
                        });
                    }
                }
                break;

            case "binary":
                request.BodyType = ApiBodyType.Binary;
                break;

            case "graphql":
                request.BodyType = ApiBodyType.Json;
                if (bodyElement.TryGetProperty("graphql", out JsonElement graphql))
                {
                    string query = ReadString(graphql, "query");
                    string variables = ReadString(graphql, "variables");
                    request.Body = $"{{\"query\":\"{EscapeJsonString(query)}\",\"variables\":{(!string.IsNullOrWhiteSpace(variables) ? variables : "{}")}}}";
                }
                break;

            default:
                request.BodyType = ApiBodyType.None;
                break;
        }
    }

    private static ApiBodyType DetectRawBodyType(JsonElement bodyElement)
    {
        if (bodyElement.TryGetProperty("options", out JsonElement options) &&
            options.TryGetProperty("raw", out JsonElement rawOptions))
        {
            string language = ReadStringStatic(rawOptions, "language");
            return language?.ToLowerInvariant() switch
            {
                "json" => ApiBodyType.Json,
                "xml" => ApiBodyType.Xml,
                "html" => ApiBodyType.Raw,
                "text" => ApiBodyType.Raw,
                "javascript" => ApiBodyType.Raw,
                _ => ApiBodyType.Raw
            };
        }

        return ApiBodyType.Raw;
    }

    private void ConvertQueryParams(JsonElement requestElement, ApiRequest request)
    {
        if (!requestElement.TryGetProperty("url", out JsonElement urlElement) ||
            urlElement.ValueKind != JsonValueKind.Object)
            return;

        if (!urlElement.TryGetProperty("query", out JsonElement queryElement) ||
            queryElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement param in queryElement.EnumerateArray())
        {
            string key = ReadString(param, "key");
            string value = ReadString(param, "value");
            if (string.IsNullOrEmpty(key))
                continue;

            bool disabled = false;
            if (param.TryGetProperty("disabled", out JsonElement d) &&
                d.ValueKind == JsonValueKind.True)
                disabled = true;

            request.Query.Add(new KeyValuePairItem
            {
                Key = key,
                Value = value,
                Enabled = !disabled
            });
        }
    }

    private static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
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

    private static string ReadStringStatic(JsonElement element, params string[] path)
    {
        return ReadString(element, path);
    }
}
