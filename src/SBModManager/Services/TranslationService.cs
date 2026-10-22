using System.Net.Http;
using System.Text.Json;

namespace SBModManager.Services;

/// <summary>
/// Free machine translation via MyMemory (no API key, rate limited). Text is split into
/// sentence-sized chunks because the endpoint rejects long queries.
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string?> TranslateAsync(string text, string from = "en", string to = "zh-CN",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var chunks = Split(text);
        var results = new List<string>();
        foreach (var chunk in chunks)
        {
            var translated = await TranslateChunkAsync(chunk, from, to, ct).ConfigureAwait(false);
            if (translated is null)
            {
                return null;
            }
            results.Add(translated);
        }

        return string.Join("\n", results);
    }

    private static async Task<string?> TranslateChunkAsync(string text, string from, string to,
        CancellationToken ct)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                      $"&langpair={Uri.EscapeDataString(from)}|{Uri.EscapeDataString(to)}";
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translated) ||
                translated.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = translated.GetString() ?? "";
            if (value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }
        catch (Exception ex)
        {
            Log.Warn("翻译失败: " + ex.Message);
            return null;
        }
    }

    private static List<string> Split(string text)
    {
        const int max = 450;
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var sentence in text.Split('\n'))
        {
            if (sentence.Length > max)
            {
                foreach (var part in Enumerate(sentence, max))
                {
                    chunks.Add(part);
                }
                continue;
            }

            if (current.Length + sentence.Length + 1 > max && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.AppendLine(sentence);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static IEnumerable<string> Enumerate(string text, int max)
    {
        for (var i = 0; i < text.Length; i += max)
        {
            yield return text.Substring(i, Math.Min(max, text.Length - i));
        }
    }
}
