using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SBModManager.Services;

public sealed class GitHubReleaseInfo
{
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public List<GitHubAsset> Assets { get; init; } = new();
}

public sealed class GitHubAsset
{
    public string Name { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>Minimal GitHub Releases client used for the UE4SS installer.</summary>
public static class GitHubService
{
    private static readonly HttpClient Http = CreateClient(allowRedirect: true);
    private static readonly HttpClient NoRedirectHttp = CreateClient(allowRedirect: false);

    private static HttpClient CreateClient(bool allowRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirect,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SBModManager/1.5 (+https://github.com/xiangfa-test/Stellar-Blade-Mod-Manager)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Resolve the latest release without the GitHub REST API (anonymous API is limited to
    /// 60 requests/hour and returns 403 when exhausted). Uses the releases/latest redirect
    /// plus the expanded-assets fragment, then falls back to the API if the HTML changes.
    /// </summary>
    public static async Task<GitHubReleaseInfo> GetLatestReleaseAsync(string repository, CancellationToken ct = default)
    {
        try
        {
            var fromHtml = await GetLatestReleaseFromHtmlAsync(repository, ct).ConfigureAwait(false);
            if (fromHtml.Assets.Count > 0)
            {
                return fromHtml;
            }
            Log.Warn("GitHub 页面解析未得到资源，回退 API: " + repository);
        }
        catch (Exception ex)
        {
            Log.Warn("GitHub 页面解析失败，回退 API: " + ex.Message);
        }

        return await GetLatestReleaseFromApiAsync(repository, ct).ConfigureAwait(false);
    }

    private static async Task<GitHubReleaseInfo> GetLatestReleaseFromHtmlAsync(string repository, CancellationToken ct)
    {
        var latestUrl = $"https://github.com/{repository}/releases/latest";
        using var latestResponse = await NoRedirectHttp.GetAsync(latestUrl, ct).ConfigureAwait(false);

        string? tag = null;
        if (latestResponse.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var location = latestResponse.Headers.Location?.ToString() ?? "";
            const string marker = "/releases/tag/";
            var index = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                tag = Uri.UnescapeDataString(location[(index + marker.Length)..].Trim('/'));
            }
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException("GitHub 没有返回 releases/latest 重定向。");
        }

        var expandedUrl = $"https://github.com/{repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        var html = await Http.GetStringAsync(expandedUrl, ct).ConfigureAwait(false);

        var assets = new List<GitHubAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, "href=\"(?<path>/[^\"]+/releases/download/[^\"]+)\""))
        {
            var path = match.Groups["path"].Value;
            var fileName = Uri.UnescapeDataString(path.Split('/').Last());
            if (string.IsNullOrWhiteSpace(fileName) || !seen.Add(fileName)) continue;

            assets.Add(new GitHubAsset
            {
                Name = fileName,
                DownloadUrl = "https://github.com" + path,
                Size = 0,
            });
        }

        return new GitHubReleaseInfo
        {
            TagName = tag,
            Name = tag,
            HtmlUrl = $"https://github.com/{repository}/releases/tag/{tag}",
            Assets = assets,
        };
    }

    private static async Task<GitHubReleaseInfo> GetLatestReleaseFromApiAsync(string repository, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode == HttpStatusCode.Forbidden
                ? "（GitHub 匿名 API 配额可能已用完，请稍后再试，或使用「从本地 zip 安装」）"
                : "";
            throw new InvalidOperationException($"GitHub API {(int)response.StatusCode}: {Truncate(body, 200)} {hint}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsElement))
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var download = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(download)) continue;
                assets.Add(new GitHubAsset
                {
                    Name = name,
                    DownloadUrl = download,
                    Size = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                });
            }
        }

        return new GitHubReleaseInfo
        {
            TagName = GetString(root, "tag_name"),
            Name = GetString(root, "name"),
            HtmlUrl = GetString(root, "html_url"),
            Assets = assets,
        };
    }

    /// <summary>Pick the normal UE4SS zip (avoid the zDEV / zCustomGameConfigs / zMapGen helper archives).</summary>
    public static GitHubAsset? PickUe4ssAsset(GitHubReleaseInfo release)
    {
        static int Score(string name)
        {
            var lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) return -1;
            if (lower.Contains("zdev") || lower.Contains("zcustomgameconfigs") || lower.Contains("zmapgen")) return -1;
            if (lower.StartsWith("ue4ss")) return 100;
            if (lower.Contains("ue4ss")) return 50;
            return 10;
        }

        return release.Assets
            .Select(a => (asset: a, score: Score(a.Name)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.asset)
            .FirstOrDefault();
    }

    /// <summary>Download with resume + retries (delegates to <see cref="ResumableDownloader"/>).</summary>
    public static Task DownloadAsync(string url, string targetPath, IProgress<double>? progress = null,
        CancellationToken ct = default)
        => ResumableDownloader.DownloadAsync(url, targetPath, progress, ct);

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
