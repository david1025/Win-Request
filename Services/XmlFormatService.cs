using System;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinRequest.Services;

public static class XmlFormatService
{
    public static bool TryFormat(string input, out string formatted)
    {
        formatted = input;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            var doc = XDocument.Parse(input, LoadOptions.PreserveWhitespace);
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = doc.Declaration == null,
                Encoding = Encoding.UTF8
            };
            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.WriteTo(writer);
            }
            formatted = sb.ToString();
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
