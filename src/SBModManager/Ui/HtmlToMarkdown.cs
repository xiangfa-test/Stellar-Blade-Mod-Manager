using System.Net;
using System.Text.RegularExpressions;

namespace SBModManager.Ui;

/// <summary>Converts the HTML returned by the Nexus mod API into simple Markdown.</summary>
public static class HtmlToMarkdown
{
    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = html;
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*p\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*p[^>]*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*h([1-4])[^>]*>", m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*h[1-4]\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*li\s*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(strong|b)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*(strong|b)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(em|i)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*(em|i)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(code|pre)[^>]*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*(code|pre)\s*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<a[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
            m => $"[{StripTags(m.Groups[2].Value)}]({m.Groups[1].Value})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<img[^>]*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string StripTags(string value) => Regex.Replace(value, @"<[^>]+>", "").Trim();
}
