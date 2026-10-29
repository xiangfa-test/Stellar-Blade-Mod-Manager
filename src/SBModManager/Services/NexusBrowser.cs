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
    public List<string> Tags { get; init; } = new();

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

}
