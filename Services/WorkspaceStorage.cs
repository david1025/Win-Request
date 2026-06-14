using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WinRequest.Models;

namespace WinRequest.Services;

public sealed class WorkspaceStorage
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinRequest");

    private static readonly string WorkspaceFile = Path.Combine(AppDataDir, "workspace.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ApiWorkspace> LoadAsync()
    {
        try
        {
            if (!File.Exists(WorkspaceFile))
                return CreateDefaultWorkspace();

            string json = await File.ReadAllTextAsync(WorkspaceFile);
            var workspace = JsonSerializer.Deserialize<ApiWorkspace>(json, JsonOptions) ?? CreateDefaultWorkspace();
            foreach (var collection in workspace.Collections)
                collection.EnsureNodes();
            return workspace;
        }
        catch
        {
            return CreateDefaultWorkspace();
        }
    }

    public async Task SaveAsync(ApiWorkspace workspace)
    {
        Directory.CreateDirectory(AppDataDir);
        string json = JsonSerializer.Serialize(workspace, JsonOptions);
        await File.WriteAllTextAsync(WorkspaceFile, json);
    }

    private static ApiWorkspace CreateDefaultWorkspace()
    {
        return new ApiWorkspace
        {
            EnvironmentVariables =
            {
                new KeyValuePairItem { Key = "baseUrl", Value = "https://httpbin.org" }
            },
            Collections =
            {
                new ApiCollection
                {
                    Name = "默认集合",
                    Nodes =
                    {
                        new CollectionNode
                        {
                            Name = "示例 HTTP",
                            IsFolder = false,
                            Request = new ApiRequest
                            {
                                Name = "示例 HTTP",
                                Method = "GET",
                                Url = "{{baseUrl}}/get",
                                Type = ApiRequestType.Http
                            }
                        },
                        new CollectionNode
                        {
                            Name = "示例 WebSocket",
                            IsFolder = false,
                            Request = new ApiRequest
                            {
                                Name = "示例 WebSocket",
                                Method = "CONNECT",
                                Url = "wss://echo.websocket.events",
                                Type = ApiRequestType.WebSocket,
                                Body = "hello"
                            }
                        },
                        new CollectionNode
                        {
                            Name = "示例 gRPC",
                            IsFolder = false,
                            Request = new ApiRequest
                            {
                                Name = "示例 gRPC",
                                Method = "POST",
                                Url = "localhost:50051",
                                Type = ApiRequestType.Grpc,
                                GrpcMethod = "package.Service/Method",
                                Body = "{}"
                            }
                        }
                    }
                }
            }
        };
    }
}
