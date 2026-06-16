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
        var folderMap = new Dictionary<string, CollectionNode>();
        var untaggedNodes = new List<CollectionNode>();

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

                    AddParameters(request, path.Value, root);
                    AddParameters(request, operation.Value, root);
                    AddJsonBody(request, operation.Value, root);

                    var node = new CollectionNode
                    {
                        Name = request.Name,
                        IsFolder = false,
                        Request = request
                    };

                    string? tag = GetFirstTag(operation.Value);
                    if (tag != null)
                    {
                        if (!folderMap.TryGetValue(tag, out CollectionNode? folder))
                        {
                            folder = new CollectionNode { Name = tag, IsFolder = true };
                            folderMap[tag] = folder;
                        }
                        folder.Children.Add(node);
                    }
                    else
                    {
                        untaggedNodes.Add(node);
                    }
                }
            }
        }

        foreach (var folder in folderMap.Values)
            collection.Nodes.Add(folder);
        foreach (var node in untaggedNodes)
            collection.Nodes.Add(node);

        if (collection.Nodes.Count == 0)
        {
            collection.Nodes.Add(new CollectionNode
            {
                Name = "导入的空 OpenAPI 文档",
                IsFolder = false,
                Request = new ApiRequest
                {
                    Name = "导入的空 OpenAPI 文档",
                    Method = "GET",
                    Url = baseUrl,
                    Type = ApiRequestType.Http
                }
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

    private static void AddParameters(ApiRequest request, JsonElement operation, JsonElement root)
    {
        if (!operation.TryGetProperty("parameters", out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            JsonElement effective = ResolveRefElement(parameter, root);

            string name = ReadString(effective, "name");
            string location = ReadString(effective, "in");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (effective.TryGetProperty("schema", out JsonElement paramSchema))
            {
                JsonElement resolvedSchema = ResolveRefElement(paramSchema, root);

                if (string.Equals(location, "query", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsObjectSchema(resolvedSchema))
                    {
                        ExpandObjectToQuery(request, resolvedSchema, root);
                    }
                    else
                    {
                        request.Query.Add(new KeyValuePairItem { Key = name, Value = "" });
                    }
                }
                else if (string.Equals(location, "header", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Add(new KeyValuePairItem { Key = name, Value = "" });
                }
            }
            else
            {
                if (string.Equals(location, "query", StringComparison.OrdinalIgnoreCase))
                    request.Query.Add(new KeyValuePairItem { Key = name, Value = "" });
                else if (string.Equals(location, "header", StringComparison.OrdinalIgnoreCase))
                    request.Headers.Add(new KeyValuePairItem { Key = name, Value = "" });
            }
        }
    }

    private static bool IsObjectSchema(JsonElement schema)
    {
        string type = ReadString(schema, "type");
        if (type == "object")
            return true;
        if (schema.TryGetProperty("properties", out _))
            return true;
        if (schema.TryGetProperty("allOf", out _))
            return true;
        return false;
    }

    private static void ExpandObjectToQuery(ApiRequest request, JsonElement schema, JsonElement root)
    {
        var visited = new HashSet<string>();
        ExpandObjectToQueryInternal(request, schema, root, visited);
    }

    private static void ExpandObjectToQueryInternal(ApiRequest request, JsonElement schema, JsonElement root, HashSet<string> visited)
    {
        if (schema.TryGetProperty("allOf", out JsonElement allOf) &&
            allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sub in allOf.EnumerateArray())
            {
                JsonElement effective = sub;
                if (sub.TryGetProperty("$ref", out JsonElement subRef) &&
                    subRef.ValueKind == JsonValueKind.String)
                {
                    string refPath = subRef.GetString() ?? "";
                    if (!visited.Contains(refPath))
                    {
                        var resolved = ResolveRef(root, refPath);
                        if (resolved != null)
                        {
                            visited.Add(refPath);
                            effective = resolved.Value;
                        }
                    }
                }
                ExpandObjectToQueryInternal(request, effective, root, visited);
            }
            return;
        }

        if (schema.TryGetProperty("properties", out JsonElement props))
        {
            foreach (JsonProperty prop in props.EnumerateObject())
            {
                string propType = ReadString(prop.Value, "type");
                if (propType == "array" || propType == "object")
                    continue;
                request.Query.Add(new KeyValuePairItem { Key = prop.Name, Value = "" });
            }
        }
    }

    private static void AddJsonBody(ApiRequest request, JsonElement operation, JsonElement root)
    {
        if (!operation.TryGetProperty("requestBody", out JsonElement body) ||
            !body.TryGetProperty("content", out JsonElement content))
            return;

        foreach (JsonProperty mediaType in content.EnumerateObject())
        {
            request.Headers.Add(new KeyValuePairItem { Key = "Content-Type", Value = mediaType.Name });

            if (mediaType.Name.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                if (mediaType.Value.TryGetProperty("schema", out JsonElement schema))
                {
                    var visited = new HashSet<string>();
                    request.Body = GenerateExampleJson(schema, root, visited, 0, 0);
                }
                else
                {
                    request.Body = "{\n  \n}";
                }
            }
            else
            {
                request.Body = "";
            }
            return;
        }
    }

    private static JsonElement? ResolveRef(JsonElement root, string refPath)
    {
        if (string.IsNullOrEmpty(refPath) || !refPath.StartsWith("#/"))
            return null;

        string[] parts = refPath[2..].Split('/');
        JsonElement current = root;
        foreach (string part in parts)
        {
            if (!current.TryGetProperty(part, out current))
                return null;
        }
        return current;
    }

    private static JsonElement ResolveRefElement(JsonElement element, JsonElement root)
    {
        if (element.TryGetProperty("$ref", out JsonElement refValue) &&
            refValue.ValueKind == JsonValueKind.String)
        {
            string refPath = refValue.GetString() ?? "";
            var resolved = ResolveRef(root, refPath);
            if (resolved != null)
                return resolved.Value;
        }
        return element;
    }

    private static string GenerateExampleJson(JsonElement schema, JsonElement root, HashSet<string> visited, int depth, int indentLevel)
    {
        if (depth > 10)
            return "{}";

        if (schema.TryGetProperty("$ref", out JsonElement refElement) &&
            refElement.ValueKind == JsonValueKind.String)
        {
            string refPath = refElement.GetString() ?? "";
            if (visited.Contains(refPath))
                return "{}";
            var resolved = ResolveRef(root, refPath);
            if (resolved == null)
                return "{}";
            visited.Add(refPath);
            return GenerateExampleJson(resolved.Value, root, visited, depth, indentLevel);
        }

        if (schema.TryGetProperty("allOf", out JsonElement allOf) &&
            allOf.ValueKind == JsonValueKind.Array)
        {
            var mergedProps = new List<(string name, JsonElement schema)>();
            foreach (JsonElement sub in allOf.EnumerateArray())
            {
                JsonElement effective = sub;
                if (sub.TryGetProperty("$ref", out JsonElement subRef) &&
                    subRef.ValueKind == JsonValueKind.String)
                {
                    string refPath = subRef.GetString() ?? "";
                    if (!visited.Contains(refPath))
                    {
                        var resolved = ResolveRef(root, refPath);
                        if (resolved != null)
                        {
                            visited.Add(refPath);
                            effective = resolved.Value;
                        }
                    }
                }
                if (effective.TryGetProperty("properties", out JsonElement props))
                {
                    foreach (JsonProperty prop in props.EnumerateObject())
                        mergedProps.Add((prop.Name, prop.Value));
                }
            }
            return BuildObjectJson(mergedProps, root, visited, depth, indentLevel);
        }

        if (schema.TryGetProperty("oneOf", out JsonElement oneOf) &&
            oneOf.ValueKind == JsonValueKind.Array && oneOf.GetArrayLength() > 0)
        {
            return GenerateExampleJson(oneOf[0], root, visited, depth + 1, indentLevel);
        }

        if (schema.TryGetProperty("anyOf", out JsonElement anyOf) &&
            anyOf.ValueKind == JsonValueKind.Array && anyOf.GetArrayLength() > 0)
        {
            return GenerateExampleJson(anyOf[0], root, visited, depth + 1, indentLevel);
        }

        string type = ReadString(schema, "type");

        if (type == "array")
        {
            if (schema.TryGetProperty("items", out JsonElement items))
            {
                string itemJson = GenerateExampleJson(items, root, visited, depth + 1, indentLevel + 1);
                return $"[{itemJson}]";
            }
            return "[]";
        }

        if (type == "object" || schema.TryGetProperty("properties", out _))
        {
            var propsList = new List<(string name, JsonElement schema)>();
            if (schema.TryGetProperty("properties", out JsonElement props))
            {
                foreach (JsonProperty prop in props.EnumerateObject())
                    propsList.Add((prop.Name, prop.Value));
            }
            return BuildObjectJson(propsList, root, visited, depth, indentLevel);
        }

        return type switch
        {
            "string" => "\"\"",
            "integer" => "0",
            "number" => "0",
            "boolean" => "false",
            _ => "\"\""
        };
    }

    private static string BuildObjectJson(
        List<(string name, JsonElement schema)> props,
        JsonElement root,
        HashSet<string> visited,
        int depth,
        int indentLevel)
    {
        if (props.Count == 0)
            return "{}";

        string inner = new string(' ', (indentLevel + 1) * 2);
        string outer = new string(' ', indentLevel * 2);
        var parts = new List<string>();
        foreach (var (name, propSchema) in props)
        {
            string value = GenerateExampleJson(propSchema, root, visited, depth + 1, indentLevel + 1);
            parts.Add($"{inner}\"{name}\": {value}");
        }
        return $"{{\n{string.Join(",\n", parts)}\n{outer}}}";
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

    private static string? GetFirstTag(JsonElement operation)
    {
        if (!operation.TryGetProperty("tags", out JsonElement tags) ||
            tags.ValueKind != JsonValueKind.Array ||
            tags.GetArrayLength() == 0)
            return null;

        string tag = tags[0].ValueKind == JsonValueKind.String ? tags[0].GetString() ?? "" : "";
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
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
