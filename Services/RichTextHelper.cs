using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinRequest.Services;

public static class RichTextHelper
{
    public static void ApplyJsonHighlighting(RichTextBlock richTextBlock, string json)
    {
        richTextBlock.Blocks.Clear();

        if (string.IsNullOrEmpty(json))
            return;

        bool isDark = IsDarkTheme(richTextBlock);
        var tokens = JsonHighlightService.Tokenize(json);
        var paragraph = new Paragraph();

        foreach (var token in tokens)
        {
            var run = new Run { Text = token.Text };
            if (token.Kind != JsonTokenKind.Whitespace)
            {
                run.Foreground = new SolidColorBrush(JsonHighlightService.GetColor(token.Kind, isDark));
            }
            paragraph.Inlines.Add(run);
        }

        richTextBlock.Blocks.Add(paragraph);
    }

    public static void ApplyXmlHighlighting(RichTextBlock richTextBlock, string xml)
    {
        richTextBlock.Blocks.Clear();
        if (string.IsNullOrEmpty(xml))
            return;

        bool isDark = IsDarkTheme(richTextBlock);
        var tokens = XmlHighlightService.TokenizeDetailed(xml);
        var paragraph = new Paragraph();

        foreach (var token in tokens)
        {
            var run = new Run { Text = token.Text };
            if (token.Kind != XmlTokenKind.Whitespace)
                run.Foreground = new SolidColorBrush(XmlHighlightService.GetColor(token.Kind, isDark));
            paragraph.Inlines.Add(run);
        }

        richTextBlock.Blocks.Add(paragraph);
    }

    public static void ApplyPlainText(RichTextBlock richTextBlock, string text)
    {
        richTextBlock.Blocks.Clear();
        if (string.IsNullOrEmpty(text))
            return;

        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text });
        richTextBlock.Blocks.Add(paragraph);
    }

    private static bool IsDarkTheme(FrameworkElement element)
    {
        return element.ActualTheme == ElementTheme.Dark ||
               (element.ActualTheme == ElementTheme.Default &&
                Application.Current.RequestedTheme == ApplicationTheme.Dark);
    }
}
