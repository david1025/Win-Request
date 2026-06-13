using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinRequest.Services;

public sealed class GitHubUpdateService
{
    private readonly HttpClient _client = new();

    public GitHubUpdateService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("WinRequest/1.0");
    }

    public async Task<GitHubReleaseInfo> CheckLatestReleaseAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
            return new GitHubReleaseInfo { IsSuccess = false, Message = "请先填写 GitHub Owner 和 Repository。" };

        string url = $"https://api.github.com/repos/{owner.Trim()}/{repository.Trim()}/releases/latest";
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubReleaseInfo
                {
                    IsSuccess = false,
                    Message = $"GitHub 返回 {(int)response.StatusCode}: {response.ReasonPhrase}",
                    RawBody = body
                };
            }

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            return new GitHubReleaseInfo
            {
                IsSuccess = true,
                TagName = ReadString(root, "tag_name"),
                Name = ReadString(root, "name"),
                HtmlUrl = ReadString(root, "html_url"),
                PublishedAt = ReadString(root, "published_at"),
                Message = "已获取 GitHub 最新 Release。",
                RawBody = body
            };
        }
        catch (Exception ex)
        {
            return new GitHubReleaseInfo
            {
                IsSuccess = false,
                Message = ex.Message,
                RawBody = ex.ToString()
            };
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }
}

public sealed class GitHubReleaseInfo
{
    public bool IsSuccess { get; set; }
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string PublishedAt { get; set; } = "";
    public string Message { get; set; } = "";
    public string RawBody { get; set; } = "";
}
