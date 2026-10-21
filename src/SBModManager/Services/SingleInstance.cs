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

    /// <summary>Writable places for the handoff queue (data dir first, temp as fallback).</summary>
    private static IEnumerable<string> RequestDirs
    {
        get
        {
            yield return Path.Combine(AppPaths.Root, "requests");
            yield return Path.Combine(Path.GetTempPath(), "SBModManager-requests");
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
        foreach (var dir in RequestDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), request.Payload ?? "");
                Log.Info($"已把启动请求转发给现有实例: {request.Kind}");
                return;
            }
            catch
            {
                // try the next writable location
            }
        }
        Log.Warn("转发启动请求失败：没有可写的队列目录");
    }

    /// <summary>Called by the running instance (UI thread) to pick up forwarded requests.</summary>
    public static List<InstanceRequest> Drain()
    {
        var result = new List<InstanceRequest>();
        foreach (var dir in RequestDirs)
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
                        result.Add(new InstanceRequest(kind, payload));
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
        return result;
    }

    private static void CleanupOldRequests()
    {
        foreach (var dir in RequestDirs)
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
