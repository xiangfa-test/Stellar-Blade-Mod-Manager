using System.Text;

namespace SBModManager.Services;

public static class AppPaths
{
    private static readonly List<string> ResolutionLog = new();

    /// <summary>
    /// Data root. Order: --data-dir / SBMM_DATA_DIR, %LOCALAPPDATA%\SBModManager,
    /// &lt;exe dir&gt;\SBMM-Data (portable), %TEMP%\SBModManager.
    /// The first candidate that passes a real write test wins, so a locked-down or
    /// security-software-protected profile does not prevent the app from starting.
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    public static string ModsRoot => Path.Combine(Root, "mods");
    public static string BackupsRoot => Path.Combine(Root, "backups");
    public static string LogsRoot => Path.Combine(Root, "logs");
    public static string TempRoot => Path.Combine(Root, "tmp");
    public static string StateFile => Path.Combine(Root, "deployed.json");
    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string GameConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SB", "Saved", "Config", "WindowsNoEditor");

    public static string ModDir(string id) => Path.Combine(ModsRoot, id);

    public static IReadOnlyList<string> RootResolutionLog => ResolutionLog;

    private static string ResolveRoot()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Program.DataDirOverride))
        {
            candidates.Add(Program.DataDirOverride!);
        }
        var env = Environment.GetEnvironmentVariable("SBMM_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env) && !candidates.Contains(env, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(env);
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SBModManager"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SBModManager"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "SBMM-Data"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "SBModManager"));

        foreach (var candidate in candidates)
        {
            if (TryPrepare(candidate))
            {
                ResolutionLog.Add(candidate);
                return candidate;
            }
            ResolutionLog.Add(candidate + " (denied)");
        }

        throw new IOException(
            "无法创建数据目录，已尝试:\n" + string.Join("\n", ResolutionLog));
    }

    /// <summary>Verify a directory is truly writable (creating subdirectories and files).</summary>
    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probeDir = Path.Combine(dir, ".probe-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(probeDir);
            var probeFile = Path.Combine(probeDir, "probe.txt");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            Directory.Delete(probeDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModsRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TempRoot);
    }
}

public static class Log
{
    private static readonly object Sync = new();

    /// <summary>Rotate once the current day file passes this size; one previous file is kept.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>Checking the size on every line would be wasteful; sample it instead.</summary>
    private const int WritesPerSizeCheck = 200;

    private static int _writesSinceCheck;

    /// <summary>Computed per write so a session that crosses midnight keeps logging to today.</summary>
    private static string FilePath => Path.Combine(
        AppPaths.LogsRoot, $"sbmm-{DateTime.Now:yyyyMMdd}.log");

    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsRoot);
                var path = FilePath;
                if (++_writesSinceCheck >= WritesPerSizeCheck)
                {
                    _writesSinceCheck = 0;
                    RotateIfTooLarge(path);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // logging must never throw
            }
        }

        // A subscriber must never break the caller that just logged something.
        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Move the log aside when it grows past the cap, so a long session stays bounded.</summary>
    private static void RotateIfTooLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var backup = path + ".1";
            File.Delete(backup);
            File.Move(path, backup);
        }
        catch
        {
            // rotation is best effort
        }
    }

    public static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogsRoot);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogsRoot)
        {
            UseShellExecute = true,
        });
    }
}
