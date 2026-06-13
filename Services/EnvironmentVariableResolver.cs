using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinRequest.Models;

namespace WinRequest.Services;

public static partial class EnvironmentVariableResolver
{
    public static ApiRequest Resolve(ApiRequest request, IEnumerable<KeyValuePairItem> variables)
    {
        var clone = RequestHelpers.Clone(request);
        var map = variables
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key.Trim())
            .ToDictionary(x => x.Key, x => x.Last().Value ?? "");

        clone.Name = ReplaceTokens(clone.Name, map);
        clone.Url = ReplaceTokens(clone.Url, map);
        clone.Body = ReplaceTokens(clone.Body, map);
        clone.GrpcMethod = ReplaceTokens(clone.GrpcMethod, map);
        clone.Headers = clone.Headers
            .Select(x => new KeyValuePairItem
            {
                Key = ReplaceTokens(x.Key, map),
                Value = ReplaceTokens(x.Value, map),
                Enabled = x.Enabled
            })
            .ToList();
        clone.Query = clone.Query
            .Select(x => new KeyValuePairItem
            {
                Key = ReplaceTokens(x.Key, map),
                Value = ReplaceTokens(x.Value, map),
                Enabled = x.Enabled
            })
            .ToList();

        return clone;
    }

    private static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return VariablePattern().Replace(value, match =>
        {
            string key = match.Groups[1].Value.Trim();
            return variables.TryGetValue(key, out string? replacement) ? replacement : match.Value;
        });
    }

    [GeneratedRegex("\\{\\{\\s*([^{}]+?)\\s*\\}\\}")]
    private static partial Regex VariablePattern();
}
