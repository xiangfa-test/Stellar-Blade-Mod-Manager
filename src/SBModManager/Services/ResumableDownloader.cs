using System.Net;
using System.Net.Http;

namespace SBModManager.Services;

/// <summary>
/// Resumable download: keeps a .part file next to the target and continues with a Range request.
/// Falls back to a full download when the server ignores ranges.
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

        long existing = 0;
        if (File.Exists(partPath))
        {
            existing = new FileInfo(partPath).Length;
        }
        else if (File.Exists(targetPath))
        {
            // A previous run completed; nothing to do.
            progress?.Report(100);
            return;
        }

        long total = -1;
        var resumed = false;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                }

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    resumed = true;
                }
                else if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored the range: restart from scratch.
                    Log.Warn("服务器不支持断点续传，重新下载: " + Path.GetFileName(targetPath));
                    existing = 0;
                    resumed = false;
                    TryDelete(partPath);
                }

                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength is { } length
                    ? length + (resumed ? existing : 0)
                    : -1;

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
                progress?.Report(100);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the .part file so the next attempt can resume.
                if (File.Exists(partPath)) existing = new FileInfo(partPath).Length;
                if (attempt >= 3)
                {
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
