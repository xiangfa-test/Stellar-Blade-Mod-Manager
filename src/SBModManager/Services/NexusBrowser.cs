using System.Net.Http;
using System.Text.Json;

namespace SBModManager.Services;

public sealed class NexusModSummary
{
    public int ModId { get; init; }
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string PictureUrl { get; init; } = "";
    public string Category { get; init; } = "";
    public long UpdatedTimestamp { get; init; }
    public long Downloads { get; init; }
    public long Endorsements { get; init; }
    public bool Adult { get; init; }

    public string PageUrl(string domain) => $"https://www.nexusmods.com/{domain}/mods/{ModId}";
    public string MetaText => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(Author) ? "" : "作者 " + Author,
        string.IsNullOrWhiteSpace(Version) ? "" : "v" + Version.TrimStart('v', 'V'),
        Downloads > 0 ? $"{Downloads:N0} 下载" : "",
        UpdatedTimestamp > 0
            ? "更新于 " + DateTimeOffset.FromUnixTimeSeconds(UpdatedTimestamp).LocalDateTime.ToString("yyyy-MM-dd")
            : "",
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Nexus Mods 站内浏览：列表、搜索、详情、下载导入。</summary>
public static class NexusBrowser
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<List<NexusModSummary>> GetListAsync(string domain, string? apiKey,
        string kind = "latest_updated", string? search = null, int count = 120, CancellationToken ct = default)
    {
        var sort = kind switch
        {
            "latest_added" => "new",
            "trending" => "trending",
            "popular" => "popular",
            _ => "updated",
        };

        // v3 search is public and paginated; pull pages until we have enough (or the term is found).
        var result = new List<NexusModSummary>();
        var pageSize = Math.Clamp(count, 20, 100);
        var pages = string.IsNullOrWhiteSpace(search) ? 1 : 3;

        for (var page = 1; page <= pages; page++)
        {
            var pageResult = await NexusApiV3.SearchAsync(domain, sort, page, pageSize, apiKey, search, ct)
                .ConfigureAwait(false);
            result.AddRange(pageResult.Mods);
            if (result.Count >= count || pageResult.Mods.Count < pageSize) break;
        }

        return result.Take(count).ToList();
    }

    private static async Task<List<NexusModSummary>> TrySearchAsync(string domain, string apiKey,
        string search, int count, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(domain)}/mods/search.json" +
                      $"?terms={Uri.EscapeDataString(search)}&count={Math.Max(20, count)}";
            return await RequestListAsync(url, apiKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Nexus 搜索接口不可用，改用列表过滤: " + ex.Message);
            return new List<NexusModSummary>();
        }
    }

    private static async Task<List<NexusModSummary>> RequestListAsync(string url, string apiKey,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.UserAgent.ParseAdd("SBModManager/1.11");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus API {(int)response.StatusCode}: " +
                (body.Length <= 200 ? body : body[..200]));
        }

        using var doc = JsonDocument.Parse(body);
        var result = new List<NexusModSummary>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            result.Add(new NexusModSummary
            {
                ModId = GetInt(item, "mod_id"),
                Name = GetString(item, "name"),
                Summary = GetString(item, "summary"),
                Author = GetString(item, "author"),
                Version = GetString(item, "version"),
                PictureUrl = GetString(item, "picture_url"),
                Category = GetString(item, "category_name"),
                UpdatedTimestamp = GetLong(item, "updated_timestamp"),
                Downloads = GetLong(item, "mod_downloads"),
                Endorsements = GetLong(item, "endorsement_count"),
                Adult = item.TryGetProperty("contains_adult_content", out var adult) &&
                        adult.ValueKind == JsonValueKind.True,
            });
        }

        return result;
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.TryGetInt32(out var value) ? value : 0;

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var v)) return 0;
        if (v.TryGetInt64(out var value)) return value;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed)) return parsed;
        return 0;
    }
}
