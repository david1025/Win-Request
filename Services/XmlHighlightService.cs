using System;
using System.Collections.Generic;
using Windows.UI;

namespace WinRequest.Services;

public enum XmlTokenKind
{
    Punctuation,
    TagName,
    AttributeName,
    AttributeValue,
    TextContent,
    Comment,
    CData,
    Whitespace
}

public readonly record struct XmlToken(XmlTokenKind Kind, string Text);

public static class XmlHighlightService
{
    public static List<XmlToken> Tokenize(string xml)
    {
        var tokens = new List<XmlToken>();
        if (string.IsNullOrEmpty(xml))
            return tokens;

        int i = 0;
        while (i < xml.Length)
        {
            if (xml[i] == '<')
            {
                if (i + 3 < xml.Length && xml[i..].StartsWith("<!--"))
                {
                    tokens.Add(ReadComment(xml, ref i));
                    continue;
                }
                if (i + 8 < xml.Length && xml[i..].StartsWith("<![CDATA["))
                {
                    tokens.Add(ReadCData(xml, ref i));
                    continue;
                }
                tokens.Add(ReadTag(xml, ref i));
                continue;
            }

            // Text content
            int start = i;
            while (i < xml.Length && xml[i] != '<')
                i++;
            if (i > start)
                tokens.Add(new XmlToken(XmlTokenKind.TextContent, xml[start..i]));
        }
        return tokens;
    }

    private static XmlToken ReadComment(string xml, ref int i)
    {
        int start = i;
        i += 4; // skip <!--
        while (i + 2 < xml.Length && !xml[i..].StartsWith("-->"))
            i++;
        if (i + 2 < xml.Length) i += 3; // skip -->
        return new XmlToken(XmlTokenKind.Comment, xml[start..i]);
    }

    private static XmlToken ReadCData(string xml, ref int i)
    {
        int start = i;
        i += 9; // skip <![CDATA[
        while (i + 2 < xml.Length && !xml[i..].StartsWith("]]>"))
            i++;
        if (i + 2 < xml.Length) i += 3; // skip ]]>
        return new XmlToken(XmlTokenKind.CData, xml[start..i]);
    }

    private static void AddToken(List<XmlToken> tokens, XmlTokenKind kind, string text)
    {
        if (!string.IsNullOrEmpty(text))
            tokens.Add(new XmlToken(kind, text));
    }

    private static XmlToken ReadTag(string xml, ref int i)
    {
        // We'll return a single punctuation token for the whole tag content
        // but we need to parse it to identify parts. Instead, we'll build sub-tokens.
        // For simplicity, we'll handle it inline and return a dummy; actual tokens are added to a list passed by ref.
        // Actually, we need to return one token here, so let's just mark the entire tag as TagName for now.
        // Better approach: read the tag and return multiple tokens.
        // We'll use a different strategy: tokenize the tag manually.
        int start = i;
        i++; // skip <

        // Determine tag type: closing tag </...>, processing instruction <?...?>, or opening tag <...>
        bool isClosing = i < xml.Length && xml[i] == '/';
        if (isClosing) i++;

        bool isProcessing = i < xml.Length && xml[i] == '?';
        if (isProcessing) i++;

        // Read tag name
        int nameStart = i;
        while (i < xml.Length && !char.IsWhiteSpace(xml[i]) && xml[i] != '>' && xml[i] != '/' && xml[i] != '?')
            i++;
        string tagName = xml[nameStart..i];

        // Find end of tag
        while (i < xml.Length && xml[i] != '>')
            i++;
        if (i < xml.Length) i++; // skip >

        return new XmlToken(XmlTokenKind.TagName, xml[start..i]);
    }

    /// <summary>
    /// Full tokenization that splits tags into individual parts.
    /// </summary>
    public static List<XmlToken> TokenizeDetailed(string xml)
    {
        var tokens = new List<XmlToken>();
        if (string.IsNullOrEmpty(xml))
            return tokens;

        int i = 0;
        while (i < xml.Length)
        {
            char c = xml[i];

            // Whitespace outside tags
            if (char.IsWhiteSpace(c) && (i == 0 || xml[i - 1] == '>' || !IsInsideTagContext(xml, i)))
            {
                // Check if this whitespace is between tags (text content) or inside a tag
                int start = i;
                while (i < xml.Length && char.IsWhiteSpace(xml[i]))
                    i++;
                if (i < xml.Length && xml[i] != '<')
                {
                    // This is text content whitespace
                    tokens.Add(new XmlToken(XmlTokenKind.Whitespace, xml[start..i]));
                }
                else if (i == xml.Length)
                {
                    tokens.Add(new XmlToken(XmlTokenKind.Whitespace, xml[start..i]));
                }
                else
                {
                    tokens.Add(new XmlToken(XmlTokenKind.Whitespace, xml[start..i]));
                }
                continue;
            }

            if (c == '<')
            {
                // Comment
                if (i + 3 < xml.Length && xml.Substring(i, 4) == "<!--")
                {
                    tokens.Add(ReadComment(xml, ref i));
                    continue;
                }
                // CDATA
                if (i + 8 < xml.Length && xml.Substring(i, 9) == "<![CDATA[")
                {
                    tokens.Add(ReadCData(xml, ref i));
                    continue;
                }

                // Regular tag
                TokenizeTag(xml, ref i, tokens);
                continue;
            }

            // Text content
            int textStart = i;
            while (i < xml.Length && xml[i] != '<')
                i++;
            if (i > textStart)
                tokens.Add(new XmlToken(XmlTokenKind.TextContent, xml[textStart..i]));
        }
        return tokens;
    }

    private static bool IsInsideTagContext(string xml, int pos)
    {
        // Simple heuristic: find last < or > before pos
        for (int j = pos - 1; j >= 0; j--)
        {
            if (xml[j] == '>') return false;
            if (xml[j] == '<') return true;
        }
        return false;
    }

    private static void TokenizeTag(string xml, ref int i, List<XmlToken> tokens)
    {
        // <
        tokens.Add(new XmlToken(XmlTokenKind.Punctuation, "<"));
        i++;

        // Check for / or ?
        if (i < xml.Length && (xml[i] == '/' || xml[i] == '?'))
        {
            tokens.Add(new XmlToken(XmlTokenKind.Punctuation, xml[i].ToString()));
            i++;
        }

        // Tag name
        int nameStart = i;
        while (i < xml.Length && !char.IsWhiteSpace(xml[i]) && xml[i] != '>' && xml[i] != '/' && xml[i] != '?')
            i++;
        if (i > nameStart)
            tokens.Add(new XmlToken(XmlTokenKind.TagName, xml[nameStart..i]));

        // Attributes and closing
        while (i < xml.Length && xml[i] != '>')
        {
            if (char.IsWhiteSpace(xml[i]))
            {
                int wsStart = i;
                while (i < xml.Length && char.IsWhiteSpace(xml[i]))
                    i++;
                tokens.Add(new XmlToken(XmlTokenKind.Whitespace, xml[wsStart..i]));
                continue;
            }

            if (xml[i] == '/' || xml[i] == '?')
            {
                tokens.Add(new XmlToken(XmlTokenKind.Punctuation, xml[i].ToString()));
                i++;
                continue;
            }

            // Attribute name
            if (char.IsLetterOrDigit(xml[i]) || xml[i] == '_' || xml[i] == ':')
            {
                int attrStart = i;
                while (i < xml.Length && xml[i] != '=' && !char.IsWhiteSpace(xml[i]) && xml[i] != '>' && xml[i] != '/')
                    i++;
                tokens.Add(new XmlToken(XmlTokenKind.AttributeName, xml[attrStart..i]));
                continue;
            }

            if (xml[i] == '=')
            {
                tokens.Add(new XmlToken(XmlTokenKind.Punctuation, "="));
                i++;

                // Attribute value
                if (i < xml.Length && (xml[i] == '"' || xml[i] == '\''))
                {
                    char quote = xml[i];
                    int valStart = i;
                    i++;
                    while (i < xml.Length && xml[i] != quote)
                        i++;
                    if (i < xml.Length) i++; // skip closing quote
                    tokens.Add(new XmlToken(XmlTokenKind.AttributeValue, xml[valStart..i]));
                }
                continue;
            }

            // Unknown
            tokens.Add(new XmlToken(XmlTokenKind.Punctuation, xml[i].ToString()));
            i++;
        }

        // >
        if (i < xml.Length)
        {
            tokens.Add(new XmlToken(XmlTokenKind.Punctuation, ">"));
            i++;
        }
    }

    public static Color GetColor(XmlTokenKind kind, bool isDark)
    {
        return (kind, isDark) switch
        {
            (XmlTokenKind.TagName, true)        => Color.FromArgb(255, 86, 156, 255),
            (XmlTokenKind.AttributeName, true)  => Color.FromArgb(255, 155, 200, 255),
            (XmlTokenKind.AttributeValue, true) => Color.FromArgb(255, 206, 145, 120),
            (XmlTokenKind.TextContent, true)    => Color.FromArgb(255, 212, 212, 212),
            (XmlTokenKind.Comment, true)        => Color.FromArgb(255, 106, 153, 85),
            (XmlTokenKind.CData, true)          => Color.FromArgb(255, 128, 128, 128),
            (XmlTokenKind.Punctuation, true)    => Color.FromArgb(255, 128, 128, 128),

            (XmlTokenKind.TagName, false)       => Color.FromArgb(255, 0, 0, 255),
            (XmlTokenKind.AttributeName, false) => Color.FromArgb(255, 255, 0, 0),
            (XmlTokenKind.AttributeValue, false)=> Color.FromArgb(255, 0, 0, 255),
            (XmlTokenKind.TextContent, false)   => Color.FromArgb(255, 0, 0, 0),
            (XmlTokenKind.Comment, false)       => Color.FromArgb(255, 0, 128, 0),
            (XmlTokenKind.CData, false)         => Color.FromArgb(255, 128, 128, 128),
            (XmlTokenKind.Punctuation, false)   => Color.FromArgb(255, 128, 0, 0),
            _                                   => Color.FromArgb(255, 80, 80, 90),
        };
    }
}
