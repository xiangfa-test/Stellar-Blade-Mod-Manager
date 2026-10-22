using System.Net;
using System.Net.Http;
using System.Text;

namespace SBModManager.Services;

/// <summary>
/// Resumable download: keeps a .part file next to the target and continues with a Range request.
/// A sidecar .meta records the source URL, so a changed URL (new release) never resumes a stale
/// partial file; 416 responses discard the partial and restart from scratch.
/// </summary>
public static class ResumableDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SBModManager/1.11");
        return client;
    }

    public static async Task DownloadAsync(string url, string targetPath, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";
        var metaPath = targetPath + ".meta";

        // A completed target is reusable only if it came from the same URL.
        if (File.Exists(targetPath) && File.Exists(metaPath) &&
            File.ReadAllText(metaPath, Encoding.UTF8) == url)
        {
            progress?.Report(100);
            return;
        }

        if (File.Exists(targetPath))
        {
            TryDelete(targetPath);
        }

        long existing = 0;
        if (File.Exists(partPath))
        {
            var recordedUrl = File.Exists(metaPath) ? File.ReadAllText(metaPath, Encoding.UTF8) : "";
            if (!string.Equals(recordedUrl, url, StringComparison.Ordinal))
            {
                // Different download target: stale partial bytes are useless.
                TryDelete(partPath);
                TryDelete(metaPath);
            }
            else
            {
                existing = new FileInfo(partPath).Length;
            }
        }

        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, url, Encoding.UTF8);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var resumed = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                }

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
                {
                    // Our partial is longer than the file (or the file changed): start over.
                    Log.Warn("断点续传范围无效（416），重新完整下载: " + Path.GetFileName(targetPath));
                    TryDelete(partPath);
                    existing = 0;
                    continue;
                }

                if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    resumed = true;
                }
                else if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    Log.Warn("服务器不支持断点续传，重新下载: " + Path.GetFileName(targetPath));
                    existing = 0;
                    TryDelete(partPath);
                }

                response.EnsureSuccessStatusCode();

                long total = -1;
                if (response.Content.Headers.ContentRange?.Length is { } rangeTotal)
                {
                    total = rangeTotal;
                }
                else if (response.Content.Headers.ContentLength is { } length)
                {
                    total = length + (resumed ? existing : 0);
                }

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                var done = resumed ? existing : 0;
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

                await output.FlushAsync(ct).ConfigureAwait(false);

                if (total > 0 && done != total)
                {
                    throw new IOException($"下载不完整：{done}/{total} 字节");
                }

                File.Move(partPath, targetPath, overwrite: true);
                File.WriteAllText(metaPath, url, Encoding.UTF8);
                progress?.Report(100);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (File.Exists(partPath)) existing = new FileInfo(partPath).Length;
                if (attempt >= 3)
                {
                    // Never leave a corrupt partial behind for the next run to trip on.
                    if (existing > 0 && ex is HttpRequestException or IOException)
                    {
                        TryDelete(partPath);
                        TryDelete(metaPath);
                    }
                    throw new InvalidOperationException($"下载失败（已重试 3 次）：{ex.Message}", ex);
                }

                Log.Warn($"下载失败（第 {attempt} 次），保留已下载部分并重试: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
