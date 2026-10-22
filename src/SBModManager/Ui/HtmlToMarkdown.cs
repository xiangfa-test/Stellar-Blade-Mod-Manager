using System.Net;
using System.Text.RegularExpressions;

namespace SBModManager.Ui;

/// <summary>
/// Converts a Nexus mod description to Markdown. Nexus serves a mix of HTML (newer pages) and
/// BBCode (older pages), so both are handled.
/// </summary>
public static class HtmlToMarkdown
{
    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = ConvertHtml(html);
        text = ConvertBbCode(text);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string ConvertHtml(string html)
    {
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
        text = Regex.Replace(text, @"<img[^>]*src\s*=\s*[""']([^""']+)[""'][^>]*>",
            m => "\n![](" + m.Groups[1].Value + ")\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return text;
    }

    private static string ConvertBbCode(string text)
    {
        var value = text;

        // [url=https://...]text[/url] -> [text](https://...)
        value = Regex.Replace(value, @"\[url\s*=\s*[""']?([^\]""']+)[""']?\s*\](.*?)\[/url\]",
            m => $"[{StripBbTags(m.Groups[2].Value).Trim()}]({m.Groups[1].Value.Trim()})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // [url]https://...[/url] -> link with the URL as text
        value = Regex.Replace(value, @"\[url\](.*?)\[/url\]",
            m => $"[{m.Groups[1].Value.Trim()}]({m.Groups[1].Value.Trim()})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // [b]x[/b] -> **x**
        value = Regex.Replace(value, @"\[b\](.*?)\[/b\]", "**$1**",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[i\](.*?)\[/i\]", "*$1*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[u\](.*?)\[/u\]", "$1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[s\](.*?)\[/s\]", "$1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // lists: [list][*]a[*]b[/list]
        value = Regex.Replace(value, @"\[list[^\]]*\]", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\[/list\]", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\[\*\]", "\n- ", RegexOptions.IgnoreCase);

        // quotes / headings / code
        value = Regex.Replace(value, @"\[quote(?:=[^\]]*)?\](.*?)\[/quote\]",
            m => "\n> " + m.Groups[1].Value.Replace("\n", "\n> ") + "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[h(\d)\](.*?)\[/h\1\]",
            m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + m.Groups[2].Value + "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[code\](.*?)\[/code\]", "`$1`",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // images become markdown images
        value = Regex.Replace(value, @"\[img\](.*?)\[/img\]",
            m => "\n![](" + m.Groups[1].Value.Trim() + ")\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"\[/?(size|color|font|center|left|right|spoiler|hr|background)[^\]]*\]", "",
            RegexOptions.IgnoreCase);

        // any remaining known BBCode tag: strip the tag, keep the content
        value = Regex.Replace(value, @"\[/?(b|i|u|s|list|url|quote|code|img)[^\]]*\]", "",
            RegexOptions.IgnoreCase);

        return value;
    }

    private static string StripBbTags(string value) => Regex.Replace(value, @"\[[^\]]*\]", "");

    private static string StripTags(string value) => Regex.Replace(value, @"<[^>]+>", "").Trim();
}
