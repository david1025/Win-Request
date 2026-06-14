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
            (JsonTokenKind.PropertyName, true)  => Color.FromArgb(255, 156, 220, 254),
            (JsonTokenKind.PropertyName, false) => Color.FromArgb(255, 0, 100, 200),
            (JsonTokenKind.StringValue, true)   => Color.FromArgb(255, 206, 145, 120),
            (JsonTokenKind.StringValue, false)  => Color.FromArgb(255, 163, 21, 21),
            (JsonTokenKind.NumberValue, true)   => Color.FromArgb(255, 181, 206, 168),
            (JsonTokenKind.NumberValue, false)  => Color.FromArgb(255, 9, 134, 134),
            (JsonTokenKind.BooleanValue, true)  => Color.FromArgb(255, 86, 156, 214),
            (JsonTokenKind.BooleanValue, false) => Color.FromArgb(255, 0, 0, 255),
            (JsonTokenKind.NullValue, true)     => Color.FromArgb(255, 86, 156, 214),
            (JsonTokenKind.NullValue, false)    => Color.FromArgb(255, 128, 0, 128),
            (JsonTokenKind.Punctuation, true)   => Color.FromArgb(255, 212, 212, 212),
            (JsonTokenKind.Punctuation, false)  => Color.FromArgb(255, 128, 128, 128),
            _                                   => Color.FromArgb(255, 212, 212, 212),
        };
    }
}
