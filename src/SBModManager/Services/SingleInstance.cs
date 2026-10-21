using System.Text.Json;

namespace SBModManager.Services;

public sealed record InstanceRequest(string Kind, string Payload);

/// <summary>
/// Single-instance guard + a small file-based request queue so that a second launch
/// (for example an nxm:// link while the app is already open) is forwarded to the
/// running instance instead of opening another window.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;

    /// <summary>
    /// Places the request is written to. The running instance may be elevated while the second
    /// launch is not, and the two can resolve different data directories, so we fan the request
    /// out to every location a user process can normally write.
    /// </summary>
    private static IEnumerable<string> WriteDirs
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>
            {
                Path.Combine(AppPaths.Root, "requests"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SBModManager", "requests"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "SBModManager", "requests"),
                Path.Combine(Path.GetTempPath(), "SBModManager-requests"),
            };
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Places the running instance scans; includes the portable dir as a fallback.</summary>
    private static IEnumerable<string> ReadDirs
    {
        get
        {
            foreach (var dir in WriteDirs)
            {
                yield return dir;
            }
            yield return Path.Combine(AppContext.BaseDirectory, "SBMM-Data", "requests");
        }
    }

    public static bool IsFirstInstance { get; private set; }

    /// <summary>Try to become the primary instance. When another instance is alive, this is false.</summary>
    public static void Acquire(bool waitForElevatedHandoff, string[] args, string? nxmUrl)
    {
        var name = $"Local\\SBModManager-{Environment.UserName}";
        var attempts = waitForElevatedHandoff ? 12 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, name, out var created);
                if (created)
                {
                    _mutex = mutex; // keep a root so it is not finalized/collected
                    IsFirstInstance = true;
                    CleanupOldRequests();
                    return;
                }
                mutex.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // Created by an elevated instance: still try to open it for signalling.
                try
                {
                    using var existing = Mutex.OpenExisting(name);
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // ignore and retry
            }

            if (attempt + 1 < attempts)
            {
                Thread.Sleep(250);
            }
        }

        IsFirstInstance = false;
        Enqueue(nxmUrl is null ? new InstanceRequest("show", "") : new InstanceRequest("nxm", nxmUrl));
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    public static void Enqueue(InstanceRequest request)
    {
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.{request.Kind}";
        var written = 0;
        foreach (var dir in WriteDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), request.Payload ?? "");
                written++;
            }
            catch
            {
                // try the next writable location
            }
        }

        if (written > 0)
        {
            Log.Info($"已把启动请求转发给现有实例: {request.Kind}（写入 {written} 个队列目录）");
        }
        else
        {
            Log.Warn("转发启动请求失败：没有可写的队列目录");
        }
    }

    /// <summary>Called by the running instance (UI thread) to pick up forwarded requests.</summary>
    public static List<InstanceRequest> Drain()
    {
        var result = new List<InstanceRequest>();
        var seenPayloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in ReadDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var payload = File.ReadAllText(file);
                        var kind = Path.GetExtension(file).TrimStart('.');
                        // The same request is written to every writable queue dir; only handle once.
                        var dedupe = kind + "|" + payload;
                        if (seenPayloads.Add(dedupe))
                        {
                            result.Add(new InstanceRequest(kind, payload));
                        }
                        File.Delete(file);
                    }
                    catch
                    {
                        // leave the file for the next tick
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取转发请求失败: " + ex.Message);
            }
        }
        if (result.Count > 0)
        {
            Log.Info($"收到 {result.Count} 个转发请求: " + string.Join(", ", result.Select(r => r.Kind)));
        }
        return result;
    }

    private static void CleanupOldRequests()
    {
        foreach (var dir in ReadDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10))
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
