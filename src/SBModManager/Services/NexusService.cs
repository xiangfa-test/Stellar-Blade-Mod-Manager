using System.Net.Http;
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

/// <summary>Nexus Mods API helpers: update checks and nxm:// download links.</summary>
public static class NexusService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

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
