using System;
using System.Collections.Generic;
using Windows.UI;

namespace WinRequest.Services;

public enum JsonTokenKind
{
    Punctuation,
    PropertyName,
    StringValue,
    NumberValue,
    BooleanValue,
    NullValue,
    Whitespace
}

public readonly record struct JsonToken(JsonTokenKind Kind, string Text);

public static class JsonHighlightService
{
    public static List<JsonToken> Tokenize(string json)
    {
        var tokens = new List<JsonToken>();
        if (string.IsNullOrEmpty(json))
            return tokens;

        int i = 0;
        bool expectProperty = false;

        while (i < json.Length)
        {
            char c = json[i];

            if (char.IsWhiteSpace(c))
            {
                int start = i;
                while (i < json.Length && char.IsWhiteSpace(json[i]))
                    i++;
                tokens.Add(new JsonToken(JsonTokenKind.Whitespace, json[start..i]));
                continue;
            }

            if (c == '{' || c == '}' || c == '[' || c == ']' || c == ':' || c == ',')
            {
                tokens.Add(new JsonToken(JsonTokenKind.Punctuation, c.ToString()));
                expectProperty = c == '{' || c == ',';
                i++;
                continue;
            }

            if (c == '"')
            {
                string str = ReadString(json, ref i);
                if (expectProperty && i < json.Length)
                {
                    // Look ahead for colon to identify property name
                    int peek = i;
                    while (peek < json.Length && char.IsWhiteSpace(json[peek]))
                        peek++;
                    if (peek < json.Length && json[peek] == ':')
                    {
                        tokens.Add(new JsonToken(JsonTokenKind.PropertyName, str));
                        expectProperty = false;
                        continue;
                    }
                }
                tokens.Add(new JsonToken(JsonTokenKind.StringValue, str));
                expectProperty = false;
                continue;
            }

            if (c == '-' || char.IsDigit(c))
            {
                int start = i;
                if (c == '-') i++;
                while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == 'e' || json[i] == 'E' || json[i] == '+' || json[i] == '-'))
                {
                    if ((json[i] == '+' || json[i] == '-') && i > start && json[i - 1] != 'e' && json[i - 1] != 'E')
                        break;
                    i++;
                }
                tokens.Add(new JsonToken(JsonTokenKind.NumberValue, json[start..i]));
                expectProperty = false;
                continue;
            }

            if (TryMatchKeyword(json, ref i, "true") || TryMatchKeyword(json, ref i, "false"))
            {
                tokens.Add(new JsonToken(JsonTokenKind.BooleanValue, json[(i - (json[i - 1] == 'e' ? 4 : 5))..i]));
                expectProperty = false;
                continue;
            }

            if (TryMatchKeyword(json, ref i, "null"))
            {
                tokens.Add(new JsonToken(JsonTokenKind.NullValue, "null"));
                expectProperty = false;
                continue;
            }

            // Unknown character – emit as punctuation
            tokens.Add(new JsonToken(JsonTokenKind.Punctuation, c.ToString()));
            i++;
            expectProperty = false;
        }

        return tokens;
    }

    private static string ReadString(string json, ref int i)
    {
        int start = i;
        i++; // skip opening quote
        while (i < json.Length)
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                i += 2;
                continue;
            }
            if (json[i] == '"')
            {
                i++;
                break;
            }
            i++;
        }
        return json[start..i];
    }

    private static bool TryMatchKeyword(string json, ref int i, string keyword)
    {
        if (i + keyword.Length > json.Length)
            return false;
        for (int j = 0; j < keyword.Length; j++)
        {
            if (json[i + j] != keyword[j])
                return false;
        }
        i += keyword.Length;
        return true;
    }

    public static Color GetColor(JsonTokenKind kind, bool isDark)
    {
        return (kind, isDark) switch
        {
            // Dark theme
            (JsonTokenKind.PropertyName, true)  => Color.FromArgb(255, 255, 167, 100),
            (JsonTokenKind.StringValue, true)   => Color.FromArgb(255, 130, 220, 130),
            (JsonTokenKind.NumberValue, true)   => Color.FromArgb(255, 130, 190, 255),
            (JsonTokenKind.BooleanValue, true)  => Color.FromArgb(255, 200, 130, 255),
            (JsonTokenKind.NullValue, true)     => Color.FromArgb(255, 200, 130, 255),
            (JsonTokenKind.Punctuation, true)   => Color.FromArgb(255, 200, 200, 210),
            // Light theme
            (JsonTokenKind.PropertyName, false) => Color.FromArgb(255, 180, 90, 20),
            (JsonTokenKind.StringValue, false)  => Color.FromArgb(255, 40, 140, 40),
            (JsonTokenKind.NumberValue, false)  => Color.FromArgb(255, 9, 100, 180),
            (JsonTokenKind.BooleanValue, false) => Color.FromArgb(255, 120, 40, 180),
            (JsonTokenKind.NullValue, false)    => Color.FromArgb(255, 120, 40, 180),
            (JsonTokenKind.Punctuation, false)  => Color.FromArgb(255, 80, 80, 90),
            _                                   => Color.FromArgb(255, 80, 80, 90),
        };
    }
}
