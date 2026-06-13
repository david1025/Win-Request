using System;
using System.Text.Json;

namespace WinRequest.Services;

public static class JsonFormatService
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true
    };

    public static bool TryFormat(string input, out string formatted)
    {
        formatted = input;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            formatted = JsonSerializer.Serialize(document.RootElement, PrettyOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
