using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text.RegularExpressions;

namespace SBModManager.Ui;

/// <summary>
/// Tiny Markdown renderer good enough for mod descriptions/notes: headings, bold, italic,
/// inline code, links, bullet/numbered lists and fenced code blocks.
/// </summary>
public static class MarkdownRenderer
{
    public static UIElement Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            panel.Children.Add(new TextBlock { Text = "（没有说明）", Opacity = 0.6 });
            return panel;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var text = string.Join("\n", paragraph).Trim();
            paragraph.Clear();
            if (text.Length == 0) return;
            panel.Children.Add(new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Blocks = { BuildParagraph(text) },
            });
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    inCode = false;
                    continue;
                }

                FlushParagraph();
                inCode = true;
                continue;
            }

            if (inCode)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = raw,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Opacity = 0.9,
                });
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var image = Regex.Match(line, @"^!\[[^\]]*\]\(([^)]+)\)$");
            if (image.Success && Uri.TryCreate(image.Groups[1].Value, UriKind.Absolute, out var imageUri))
            {
                FlushParagraph();
                panel.Children.Add(new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(imageUri),
                    Stretch = Stretch.Uniform,
                    MaxHeight = 420,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 4),
                });
                continue;
            }

            var heading = Regex.Match(line, "^(#{1,4})\\s+(.*)$");
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Value.Length;
                panel.Children.Add(new TextBlock
                {
                    Text = StripInline(heading.Groups[2].Value),
                    FontSize = level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 },
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            var bullet = Regex.Match(line, "^\\s*[-*+]\\s+(.*)$");
            if (bullet.Success)
            {
                FlushParagraph();
                panel.Children.Add(BuildListItem("•", bullet.Groups[1].Value));
                continue;
            }

            var numbered = Regex.Match(line, "^\\s*(\\d+)[.)]\\s+(.*)$");
            if (numbered.Success)
            {
                FlushParagraph();
                panel.Children.Add(BuildListItem(numbered.Groups[1].Value + ".", numbered.Groups[2].Value));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return panel;
    }

    private static UIElement BuildListItem(string marker, string text)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(8, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bullet = new TextBlock { Text = marker, Opacity = 0.8 };
        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var body = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Blocks = { BuildParagraph(text) },
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static Paragraph BuildParagraph(string text)
    {
        var paragraph = new Paragraph();
        var pattern = new Regex(
            "\\*\\*(?<bold>.+?)\\*\\*|__(?<bold2>.+?)__|`(?<code>[^`]+)`|\\*(?<italic>[^*]+)\\*|_(?<italic2>[^_]+)_|\\[(?<linkText>[^\\]]+)\\]\\((?<url>[^)]+)\\)");
        var index = 0;

        foreach (Match match in pattern.Matches(text))
        {
            if (match.Index > index)
            {
                paragraph.Inlines.Add(new Run { Text = text[index..match.Index] });
            }

            if (match.Groups["bold"].Success || match.Groups["bold2"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = match.Groups["bold"].Success ? match.Groups["bold"].Value : match.Groups["bold2"].Value,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }
            else if (match.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = match.Groups["code"].Value,
                    FontFamily = new FontFamily("Consolas"),
                });
            }
            else if (match.Groups["italic"].Success || match.Groups["italic2"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = match.Groups["italic"].Success ? match.Groups["italic"].Value : match.Groups["italic2"].Value,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                });
            }
            else if (match.Groups["linkText"].Success)
            {
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri))
                {
                    hyperlink.NavigateUri = uri;
                }
                hyperlink.Inlines.Add(new Run { Text = match.Groups["linkText"].Value });
                paragraph.Inlines.Add(hyperlink);
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            paragraph.Inlines.Add(new Run { Text = text[index..] });
        }

        return paragraph;
    }

    private static string StripInline(string text)
        => Regex.Replace(text, "[*_`]", "");
}
