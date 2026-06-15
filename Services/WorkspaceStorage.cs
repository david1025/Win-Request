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
        var getUsers = new ApiRequest
        {
            Name = "Get Users - API v1",
            Method = "GET",
            Url = "https://api.example.com/v1/users?page=1",
            Type = ApiRequestType.Http,
            Query =
            {
                new KeyValuePairItem { Key = "page", Value = "1", Description = "Page number" },
                new KeyValuePairItem { Key = "limit", Value = "20", Description = "Items per page" }
            }
        };

        var updateProfile = new ApiRequest
        {
            Name = "Update Profile",
            Method = "POST",
            Url = "https://api.example.com/v1/profile",
            Type = ApiRequestType.Http,
            BodyType = ApiBodyType.Json,
            Body = "{\r\n  \"name\": \"Jane Smith\"\r\n}"
        };

        return new ApiWorkspace
        {
            OpenRequestTabIds = { getUsers.Id, updateProfile.Id },
            ActiveRequestTabId = getUsers.Id,
            EnvironmentVariables =
            {
                new KeyValuePairItem { Key = "baseUrl", Value = "https://api.example.com", Description = "Default API host" }
            },
            Collections =
            {
                new ApiCollection
                {
                    Name = "My First API",
                    Nodes =
                    {
                        new CollectionNode
                        {
                            Name = "User API",
                            IsFolder = true,
                            Children =
                            {
                                new CollectionNode
                                {
                                    Name = getUsers.Name,
                                    IsFolder = false,
                                    Request = getUsers
                                },
                                new CollectionNode
                                {
                                    Name = updateProfile.Name,
                                    IsFolder = false,
                                    Request = updateProfile
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}
