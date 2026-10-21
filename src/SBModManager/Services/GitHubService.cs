using System.Net.Http;
using System.Text.Json;

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

/// <summary>Minimal GitHub Releases client used for the UE4SS one-click installer.</summary>
public static class GitHubService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SBModManager/1.1 (+https://github.com/xiangfa-test/Stellar-Blade-Mod-Manager)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<GitHubReleaseInfo> GetLatestReleaseAsync(string repository, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub API {(int)response.StatusCode}: {Truncate(body, 200)}");
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

    public static async Task DownloadAsync(string url, string targetPath, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(targetPath);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                progress?.Report(Math.Clamp(done * 100.0 / total, 0, 100));
            }
        }
        progress?.Report(100);
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
