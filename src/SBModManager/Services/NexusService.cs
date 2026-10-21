using System.Net.Http;
using System.Text;
using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class NexusFileInfo
{
    public int FileId { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Category { get; init; } = "";
    public long UploadedTimestamp { get; init; }
}

public sealed class NexusCollectionMod
{
    public int ModId { get; init; }
    public int FileId { get; init; }
    public string ModName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Size in kilobytes, as returned by the collections GraphQL API.</summary>
    public long SizeKb { get; init; }
    public bool Optional { get; init; }
    public string PageUrl { get; init; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(ModName) ? FileName : ModName;
    public string SizeText => SizeKb <= 0 ? "" : SizeKb >= 1024 ? $"{SizeKb / 1024.0:0.#} MB" : $"{SizeKb} KB";
}

public sealed class NexusCollection
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string GameDomain { get; init; } = "";
    public string Summary { get; init; } = "";
    public int RevisionNumber { get; init; }
    public int ModCount { get; init; }
    public long TotalSize { get; init; }
    public string PageUrl { get; init; } = "";
    public List<NexusCollectionMod> Mods { get; init; } = new();
    /// <summary>Published revision numbers, newest first.</summary>
    public List<int> Revisions { get; init; } = new();
    public int OptionalCount => Mods.Count(m => m.Optional);
    public string SizeText => TotalSize <= 0 ? "未知" : $"{TotalSize / (1024.0 * 1024 * 1024):0.##} GB";
}

/// <summary>Nexus Mods API helpers: update checks, nxm:// download links and collections.</summary>
public static class NexusService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private const string CollectionQuery = """
        query($slug: String!, $revision: Int) {
          collection(slug: $slug, viewAdultContent: true) {
            id name slug summary
            game { domainName }
            user { name }
            latestPublishedRevision { revisionNumber }
            revisions { revisionNumber }
          }
          collectionRevision(slug: $slug, revision: $revision, viewAdultContent: true) {
            id revisionNumber modCount totalSize
            modFiles { optional file { modId fileId name version size mod { name } } }
          }
        }
        """;

    /// <summary>
    /// Fetch a Nexus collection (metadata + the exact mod files it installs) from the public
    /// GraphQL endpoint. Anonymous access works; an API key is only needed to download files.
    /// Accepts a full collection URL or just the slug.
    /// </summary>
    public static async Task<NexusCollection> GetCollectionAsync(string input, string? apiKey = null,
        int? revision = null, CancellationToken ct = default)
    {
        var (slug, gameFromUrl) = ParseCollectionRef(input);

        var payload = JsonSerializer.Serialize(new
        {
            query = CollectionQuery,
            variables = new { slug, revision },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql");
        request.Headers.UserAgent.ParseAdd("SBModManager/1.3");
        request.Headers.TryAddWithoutValidation("Application-Name", "SBModManager");
        request.Headers.TryAddWithoutValidation("Application-Version", "1.3.0");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
        }
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Nexus GraphQL {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException("Nexus GraphQL 错误: " + Truncate(errors.ToString(), 300));
        }

        var data = doc.RootElement.GetProperty("data");
        if (!data.TryGetProperty("collection", out var coll) || coll.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"找不到合集「{slug}」，请检查链接。");
        }
        if (!data.TryGetProperty("collectionRevision", out var rev) || rev.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"合集「{slug}」没有已发布的版本。");
        }

        var game = coll.TryGetProperty("game", out var g) && g.TryGetProperty("domainName", out var dn)
            ? dn.GetString() ?? gameFromUrl
            : gameFromUrl;
        var author = coll.TryGetProperty("user", out var u) && u.TryGetProperty("name", out var un)
            ? un.GetString() ?? ""
            : "";
        var summary = coll.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
        var name = coll.TryGetProperty("name", out var nm) ? nm.GetString() ?? slug : slug;

        var revisions = new List<int>();
        if (coll.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in revs.EnumerateArray())
            {
                var number = JsonInt(item, "revisionNumber");
                if (number > 0) revisions.Add(number);
            }
        }
        revisions.Sort((a, b) => b.CompareTo(a));

        var mods = new List<NexusCollectionMod>();
        if (rev.TryGetProperty("modFiles", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.EnumerateArray())
            {
                if (!item.TryGetProperty("file", out var file)) continue;
                var modId = JsonInt(file, "modId");
                var fileId = JsonInt(file, "fileId");
                if (modId <= 0 || fileId <= 0) continue;

                var modName = "";
                if (file.TryGetProperty("mod", out var mod) && mod.TryGetProperty("name", out var mn))
                {
                    modName = mn.GetString() ?? "";
                }
                var fileName = file.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                mods.Add(new NexusCollectionMod
                {
                    ModId = modId,
                    FileId = fileId,
                    ModName = string.IsNullOrWhiteSpace(modName) ? fileName : modName,
                    FileName = fileName,
                    Version = file.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    SizeKb = JsonLong(file, "size"),
                    Optional = item.TryGetProperty("optional", out var op) && op.GetBoolean(),
                    PageUrl = $"https://www.nexusmods.com/{game}/mods/{modId}",
                });
            }
        }

        return new NexusCollection
        {
            Slug = slug,
            Name = name,
            Author = author,
            GameDomain = string.IsNullOrWhiteSpace(game) ? "stellarblade" : game,
            Summary = summary,
            RevisionNumber = JsonInt(rev, "revisionNumber"),
            ModCount = JsonInt(rev, "modCount") is var count && count > 0 ? count : mods.Count,
            TotalSize = JsonLong(rev, "totalSize"),
            PageUrl = $"https://www.nexusmods.com/{game}/collections/{slug}",
            Mods = mods,
            Revisions = revisions,
        };
    }

    /// <summary>GraphQL returns some numeric fields as strings; accept both.</summary>
    private static long JsonLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)) return parsed;
        return 0;
    }

    private static int JsonInt(JsonElement parent, string name)
    {
        var value = JsonLong(parent, name);
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    private static (string Slug, string Game) ParseCollectionRef(string input)
    {
        var text = (input ?? "").Trim();
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"nexusmods\.com/games/(?<game>[A-Za-z0-9_-]+)/collections/(?<slug>[A-Za-z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups["slug"].Value.ToLowerInvariant(), match.Groups["game"].Value.ToLowerInvariant());
        }

        var slugMatch = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Za-z0-9]+)$");
        if (!slugMatch.Success)
        {
            throw new InvalidOperationException("不是有效的 Nexus 合集链接或 slug。");
        }
        return (slugMatch.Groups[1].Value.ToLowerInvariant(), "stellarblade");
    }

    /// <summary>Published mod name/version, used so Nexus downloads keep the page title.</summary>
    public static async Task<(string Name, string Version)?> GetModInfoAsync(string domain, int modId,
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || modId <= 0) return null;
        try
        {
            var url = $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(domain)}/mods/{modId}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            request.Headers.UserAgent.ParseAdd("SBModManager/1.9");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(body);
            var name = GetString(doc.RootElement, "name");
            var version = GetString(doc.RootElement, "version");
            return string.IsNullOrWhiteSpace(name) ? null : (name, version);
        }
        catch (Exception ex)
        {
            Log.Warn("获取 Nexus 模组信息失败: " + ex.Message);
            return null;
        }
    }

    public static async Task<List<NexusFileInfo>> GetFilesAsync(string domain, int modId, string apiKey,
        CancellationToken ct = default)
    {
        var url = $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(domain)}/mods/{modId}/files.json?category=main";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.UserAgent.ParseAdd("SBModManager/1.1");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus API {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        using var doc = JsonDocument.Parse(body);
        var result = new List<NexusFileInfo>();
        if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                result.Add(new NexusFileInfo
                {
                    FileId = file.TryGetProperty("file_id", out var id) ? id.GetInt32() : 0,
                    Name = GetString(file, "name"),
                    Version = GetString(file, "version"),
                    Category = GetString(file, "category_name"),
                    UploadedTimestamp = file.TryGetProperty("uploaded_timestamp", out var ts) ? ts.GetInt64() : 0,
                });
            }
        }
        return result;
    }

    public static async Task<UpdateInfo> CheckAsync(ModRecord mod, string apiKey, string domain,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new UpdateInfo
            {
                ModId = mod.Id,
                ModName = mod.Name,
                CurrentVersion = mod.Version,
                Error = "未配置 Nexus API Key",
            };
        }
        if (mod.NexusModId is null or <= 0)
        {
            return new UpdateInfo
            {
                ModId = mod.Id,
                ModName = mod.Name,
                CurrentVersion = mod.Version,
                Error = "该模组没有关联 Nexus 模组 ID",
            };
        }

        try
        {
            var files = await GetFilesAsync(domain, mod.NexusModId.Value, apiKey, ct).ConfigureAwait(false);
            if (files.Count == 0)
            {
                return new UpdateInfo
                {
                    ModId = mod.Id,
                    ModName = mod.Name,
                    CurrentVersion = mod.Version,
                    Error = "Nexus 上没有可用的主文件",
                };
            }

            var latest = files.OrderByDescending(f => f.UploadedTimestamp).First();
            var hasUpdate = latest.FileId != mod.NexusFileId &&
                            (string.IsNullOrWhiteSpace(mod.Version) || !VersionsEqual(mod.Version, latest.Version));

            mod.LatestVersion = latest.Version;
            mod.UpdateAvailable = hasUpdate;
            mod.LastCheckedAt = DateTimeOffset.Now;
            ModStore.Save(mod);

            return new UpdateInfo
            {
                ModId = mod.Id,
                ModName = mod.Name,
                CurrentVersion = mod.Version,
                LatestVersion = latest.Version,
                LatestFileId = latest.FileId,
                HasUpdate = hasUpdate,
                PageUrl = $"https://www.nexusmods.com/{domain}/mods/{mod.NexusModId}",
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"检查更新失败 {mod.Name}: {ex.Message}");
            return new UpdateInfo
            {
                ModId = mod.Id,
                ModName = mod.Name,
                CurrentVersion = mod.Version,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Resolve an nxm file download link.
    /// For free accounts this only works when the nxm:// link carries the one-time
    /// key + expires that Nexus generates for "Download with Manager" buttons;
    /// premium accounts can also request a keyless link.
    /// </summary>
    public static async Task<string?> GetDownloadLinkAsync(string domain, int modId, int fileId, string? apiKey,
        string? key = null, string? expires = null, CancellationToken ct = default)
    {
        var url = BuildDownloadLinkUrl(domain, modId, fileId, key, expires);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
        }
        request.Headers.UserAgent.ParseAdd("SBModManager/1.2");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus API {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var uri = GetString(item, "URI");
                if (!string.IsNullOrWhiteSpace(uri)) return uri;
            }
        }
        return null;
    }

    /// <summary>
    /// Build the download_link URL. Nexus uses the key+expires from the nxm:// URL to
    /// authorise a single file for free accounts (same mechanism as Vortex).
    /// </summary>
    internal static string BuildDownloadLinkUrl(string domain, int modId, int fileId,
        string? key = null, string? expires = null)
    {
        var url = $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(domain)}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(expires))
        {
            url += $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        }
        return url;
    }

    private static bool VersionsEqual(string a, string b)
    {
        static string Normalize(string v) => v.Trim().TrimStart('v', 'V').Trim();
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
