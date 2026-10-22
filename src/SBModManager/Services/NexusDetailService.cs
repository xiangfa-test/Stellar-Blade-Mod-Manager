using System.Net.Http;
using System.Text.Json;

namespace SBModManager.Services;

public sealed class NexusModDetail
{
    public int ModId { get; init; }
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string PictureUrl { get; init; } = "";
    public string Category { get; init; } = "";
    public long UpdatedTimestamp { get; init; }
    public long Downloads { get; init; }
    public long Endorsements { get; init; }
    public bool Adult { get; init; }
    public List<NexusFileInfo> Files { get; init; } = new();

    public string MetaText()
        => string.Join("  ·  ", new[]
        {
            string.IsNullOrWhiteSpace(Author) ? "" : "作者 " + Author,
            string.IsNullOrWhiteSpace(Version) ? "" : "v" + Version.TrimStart('v', 'V'),
            Downloads > 0 ? $"{Downloads:N0} 下载" : "",
            Endorsements > 0 ? $"{Endorsements:N0} 点赞" : "",
            UpdatedTimestamp > 0
                ? "更新于 " + DateTimeOffset.FromUnixTimeSeconds(UpdatedTimestamp).LocalDateTime.ToString("yyyy-MM-dd")
                : "",
        }.Where(s => !string.IsNullOrEmpty(s)));
}

public static class NexusDetailService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<NexusModDetail?> GetAsync(string domain, int modId, string apiKey,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(domain)}/mods/{modId}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            request.Headers.UserAgent.ParseAdd("SBModManager/1.11");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var files = new List<NexusFileInfo>();
            if (root.TryGetProperty("files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in filesNode.EnumerateArray())
                {
                    files.Add(new NexusFileInfo
                    {
                        FileId = GetInt(file, "file_id"),
                        Name = GetString(file, "name"),
                        Version = GetString(file, "version"),
                        Category = GetString(file, "category_name"),
                        UploadedTimestamp = GetLong(file, "uploaded_timestamp"),
                    });
                }
            }

            return new NexusModDetail
            {
                ModId = GetInt(root, "mod_id") is var id && id > 0 ? id : modId,
                Name = GetString(root, "name"),
                Summary = GetString(root, "summary"),
                Description = GetString(root, "description"),
                Author = GetString(root, "author"),
                Version = GetString(root, "version"),
                PictureUrl = GetString(root, "picture_url"),
                Category = GetString(root, "category_name"),
                UpdatedTimestamp = GetLong(root, "updated_timestamp"),
                Downloads = GetLong(root, "mod_downloads"),
                Endorsements = GetLong(root, "endorsement_count"),
                Adult = root.TryGetProperty("contains_adult_content", out var adult) &&
                        adult.ValueKind == JsonValueKind.True,
                Files = files.OrderByDescending(f => f.UploadedTimestamp).ToList(),
            };
        }
        catch (Exception ex)
        {
            Log.Warn("获取 N 网模组详情失败: " + ex.Message);
            return null;
        }
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
