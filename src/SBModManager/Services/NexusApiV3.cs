using System.Net.Http;
using System.Text;
using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>Official Nexus Mods API v3 (https://api.nexusmods.com/v3).</summary>
public static class NexusApiV3
{
    private const string BaseUrl = "https://api.nexusmods.com/v3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public sealed class SearchPage
    {
        public List<NexusModSummary> Mods { get; init; } = new();
        public int TotalCount { get; init; }
    }

    /// <summary>Search is public (no API key needed); a key only filters blocked content.</summary>
    public static async Task<SearchPage> SearchAsync(string domain, string sortField, int page, int pageSize,
        string? apiKey, string? query, CancellationToken ct = default)
    {
        var body = new
        {
            filters = new
            {
                games = new[] { domain },
                include_adult = true,
            },
            sort = new
            {
                field = sortField,
                direction = "desc",
            },
            pagination = new
            {
                page,
                page_size = Math.Clamp(pageSize, 1, 100),
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/mods/search")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        AddHeaders(request, apiKey);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus v3 搜索 {(int)response.StatusCode}: {Trim(text)}");
        }

        using var doc = JsonDocument.Parse(text);
        var result = new SearchPage();
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var total = 0;
        if (doc.RootElement.TryGetProperty("meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("total_count", out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out var totalValue))
        {
            total = totalValue;
        }

        var list = new List<NexusModSummary>();
        foreach (var item in mods.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var summary = GetString(item, "summary");
            if (!string.IsNullOrWhiteSpace(query) &&
                !(GetString(item, "name").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  summary.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new NexusModSummary
            {
                ModId = GetInt(item, "game_scoped_id"),
                Name = GetString(item, "name"),
                Summary = summary,
                Author = item.TryGetProperty("author", out var author) ? GetString(author, "name") : "",
                Version = GetString(item, "version"),
                PictureUrl = GetString(item, "thumbnail_url"),
                Category = item.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Object
                    ? GetString(category, "name")
                    : "",
                UpdatedTimestamp = ParseTimestamp(GetString(item, "updated_at")),
                Downloads = GetLong(item, "downloads"),
                Endorsements = GetLong(item, "endorsements"),
                Adult = item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True,
            });
        }

        return new SearchPage { Mods = list, TotalCount = total };
    }

    /// <summary>Mod detail; description is raw BBCode.</summary>
    public static async Task<NexusModDetail?> GetModAsync(string domain, int modId, string apiKey,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/games/{Uri.EscapeDataString(domain)}/mods/{modId}";
        var (ok, text) = await GetAsync(url, apiKey, ct).ConfigureAwait(false);
        if (!ok) return null;

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var stats = data.TryGetProperty("stats", out var statsNode) ? statsNode : default;
        return new NexusModDetail
        {
            ModId = GetInt(data, "game_scoped_id") is var id && id > 0 ? id : modId,
            Name = GetString(data, "name"),
            Description = GetString(data, "description"),
            Author = GetString(data, "author"),
            Version = GetString(data, "version"),
            Category = data.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Object
                ? GetString(category, "name")
                : "",
            Downloads = stats.ValueKind == JsonValueKind.Object ? GetLong(stats, "downloads") : 0,
            Endorsements = stats.ValueKind == JsonValueKind.Object ? GetLong(stats, "endorsements") : 0,
            Adult = data.TryGetProperty("adult_content", out var adult) && adult.ValueKind == JsonValueKind.True,
            Tags = data.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(t => GetString(t, "name")).Where(n => n.Length > 0).ToList()
                : new List<string>(),
            Files = await GetFileVersionsAsync(domain, modId, apiKey, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Flatten mod files into their downloadable versions (main/update/optional/misc).</summary>
    public static async Task<List<NexusFileInfo>> GetFileVersionsAsync(string domain, int modId, string apiKey,
        CancellationToken ct = default)
    {
        var result = new List<NexusFileInfo>();
        var (ok, text) = await GetAsync($"{BaseUrl}/mods/{modId}/files", apiKey, ct).ConfigureAwait(false);
        if (!ok) return result;

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mod_files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var file in files.EnumerateArray())
        {
            var fileId = GetString(file, "id");
            if (string.IsNullOrWhiteSpace(fileId)) continue;

            var (versionsOk, versionsText) = await GetAsync(
                $"{BaseUrl}/mod-files/{Uri.EscapeDataString(fileId)}/versions", apiKey, ct).ConfigureAwait(false);
            if (!versionsOk) continue;

            using var versionsDoc = JsonDocument.Parse(versionsText);
            if (!versionsDoc.RootElement.TryGetProperty("data", out var versionsData) ||
                versionsData.ValueKind != JsonValueKind.Object ||
                !versionsData.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var version in versions.EnumerateArray())
            {
                if (version.ValueKind != JsonValueKind.Object) continue;
                var versionId = GetInt(version, "game_scoped_id");
                if (versionId <= 0) continue;
                result.Add(new NexusFileInfo
                {
                    FileId = versionId,
                    Name = GetString(version, "name"),
                    Version = GetString(version, "version"),
                    Category = GetString(version, "category"),
                    UploadedTimestamp = ParseTimestamp(GetString(version, "uploaded_at")),
                });
            }
        }

        return result;
    }

    /// <summary>Signed download link for a mod file version (game-scoped version id).</summary>
    public static async Task<string?> GetDownloadLinkAsync(string domain, int versionGameScopedId, string apiKey,
        string? key = null, string? expires = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/games/{Uri.EscapeDataString(domain)}/mod-file-versions/{versionGameScopedId}/download-link";
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(expires))
        {
            url += $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        }

        var (ok, text) = await GetAsync(url, apiKey, ct, applicationName: "SBModManager").ConfigureAwait(false);
        if (!ok) return null;

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            var uri = GetString(link, "url");
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        return null;
    }

    private static async Task<(bool Ok, string Body)> GetAsync(string url, string? apiKey,
        CancellationToken ct, string? applicationName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, apiKey);
        if (!string.IsNullOrWhiteSpace(applicationName))
        {
            request.Headers.TryAddWithoutValidation("Application-Name", applicationName);
        }

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private static void AddHeaders(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
        }
        request.Headers.UserAgent.ParseAdd("SBModManager/1.12");
        request.Headers.Accept.ParseAdd("application/json");
    }

    private static long ParseTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUnixTimeSeconds() : 0;

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var value)) return value;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return 0;
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var value)) return value;
        return 0;
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200];
}
