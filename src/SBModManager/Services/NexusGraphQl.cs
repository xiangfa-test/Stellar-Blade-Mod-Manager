using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SBModManager.Services;

/// <summary>
/// Nexus public GraphQL API. Used where the REST API has no equivalent: tag filtering and
/// all-time ranking (most downloaded / most endorsed).
/// </summary>
public static class NexusGraphQl
{
    private const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private const string SearchQuery = """
        query($filter: ModsFilter, $sort: [ModsSort!], $count: Int) {
          mods(filter: $filter, sort: $sort, count: $count) {
            nodes {
              modId
              name
              summary
              version
              author
              category
              thumbnailUrl
              downloads
              endorsements
              updatedAt
              createdAt
              adultContent
              tags { name }
            }
          }
        }
        """;

    public static async Task<List<NexusModSummary>> SearchAsync(string domain, string? tag, string? text,
        string sortField, int count, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object>
        {
            ["gameDomainName"] = new { value = domain },
        };
        if (!string.IsNullOrWhiteSpace(tag))
        {
            filter["tag"] = new { value = tag };
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            filter["name"] = new { value = text, op = "WILDCARD" };
        }

        var sortKey = sortField switch
        {
            "downloads" => "downloads",
            "endorsements" => "endorsements",
            "createdAt" => "createdAt",
            _ => "updatedAt",
        };

        var variables = new Dictionary<string, object?>
        {
            ["filter"] = filter,
            ["sort"] = new[] { new Dictionary<string, object> { [sortKey] = new { direction = "DESC" } } },
            ["count"] = Math.Clamp(count, 1, 100),
        };

        var root = await PostAsync(new { query = SearchQuery, variables }, ct).ConfigureAwait(false);
        return ParseSearchResponse(root);
    }

    /// <summary>Parses a GraphQL response; malformed payloads yield an empty list instead of throwing.</summary>
    internal static List<NexusModSummary> ParseSearchResponse(JsonElement root)
    {
        var result = new List<NexusModSummary>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mods", out var mods) ||
            mods.ValueKind != JsonValueKind.Object ||
            !mods.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            result.Add(new NexusModSummary
            {
                ModId = GetInt(node, "modId"),
                Name = GetString(node, "name"),
                Summary = GetString(node, "summary"),
                Author = GetString(node, "author"),
                Version = GetString(node, "version"),
                PictureUrl = GetString(node, "thumbnailUrl"),
                Category = GetString(node, "category"),
                UpdatedTimestamp = ParseTimestamp(GetString(node, "updatedAt")),
                Downloads = GetLong(node, "downloads"),
                Endorsements = GetLong(node, "endorsements"),
                Adult = node.TryGetProperty("adultContent", out var adult) && adult.ValueKind == JsonValueKind.True,
                Tags = node.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(t => GetString(t, "name")).Where(n => n.Length > 0).ToList()
                    : new List<string>(),
            });
        }

        return result;
    }

    internal static List<NexusModSummary> ParseSearchJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseSearchResponse(doc.RootElement);
        }
        catch (JsonException)
        {
            return new List<NexusModSummary>();
        }
    }

    private const string FilesQuery = """
        query($modId: ID!, $gameId: ID!) {
          modFiles(modId: $modId, gameId: $gameId) {
            fileId
            name
            version
            category
            date
            size
            primary
          }
        }
        """;

    private const string GameQuery = """
        query($domain: String!) {
          game(domainName: $domain) { id }
        }
        """;

    /// <summary>File list for a mod; works anonymously through the public GraphQL endpoint.</summary>
    public static async Task<List<NexusFileInfo>> GetModFilesAsync(string domain, int modId,
        CancellationToken ct = default)
    {
        var result = new List<NexusFileInfo>();
        try
        {
            var gameRoot = await PostAsync(new
            {
                query = GameQuery,
                variables = new Dictionary<string, object> { ["domain"] = domain },
            }, ct).ConfigureAwait(false);

            if (!gameRoot.TryGetProperty("data", out var gameData) ||
                gameData.ValueKind != JsonValueKind.Object ||
                !gameData.TryGetProperty("game", out var game) ||
                game.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            var gameId = GetIdString(game, "id");
            if (string.IsNullOrWhiteSpace(gameId))
            {
                Log.Warn("读取游戏 ID 失败，无法获取文件列表");
                return result;
            }

            var root = await PostAsync(new
            {
                query = FilesQuery,
                variables = new Dictionary<string, object>
                {
                    ["modId"] = modId.ToString(),
                    ["gameId"] = gameId,
                },
            }, ct).ConfigureAwait(false);

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("modFiles", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                result.Add(new NexusFileInfo
                {
                    FileId = GetInt(file, "fileId"),
                    Name = GetString(file, "name"),
                    Version = GetString(file, "version"),
                    Category = GetString(file, "category"),
                    UploadedTimestamp = GetLong(file, "date"),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取模组文件列表失败: " + ex.Message);
        }

        return result;
    }

    /// <summary>Tags actually used by the most downloaded mods of this game.</summary>
    public static async Task<List<string>> GetUsedTagsAsync(string domain, CancellationToken ct = default)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mods = await SearchAsync(domain, tag: null, text: null, sortField: "downloads", count: 100, ct)
                .ConfigureAwait(false);
            foreach (var mod in mods)
            {
                foreach (var tag in mod.Tags) tags.Add(tag);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取标签列表失败: " + ex.Message);
        }

        return tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<JsonElement> PostAsync(object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("SBModManager/1.13");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus GraphQL {(int)response.StatusCode}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            throw new InvalidOperationException("Nexus GraphQL: " + Trim(errors.ToString()));
        }
        return doc.RootElement.Clone();
    }

    private static long ParseTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUnixTimeSeconds() : 0;

    /// <summary>ID fields come back as either strings or numbers depending on the query.</summary>
    private static string GetIdString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number.ToString() : value.GetRawText(),
            _ => "",
        };
    }

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
