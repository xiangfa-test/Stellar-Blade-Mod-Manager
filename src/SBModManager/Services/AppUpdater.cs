using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace SBModManager.Services;

public sealed class AppUpdateInfo
{
    public string Tag { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public GitHubAsset? Package { get; init; }
    public GitHubAsset? Checksums { get; init; }
    public bool IsNewer { get; init; }
}

/// <summary>
/// In-app update for the manager itself: checks GitHub releases, downloads the win-x64 zip,
/// verifies its SHA256 and replaces the application files after the process exits.
/// </summary>
public static class AppUpdater
{
    public const string Repository = "xiangfa-test/Stellar-Blade-Mod-Manager";

    /// <summary>Skip the automatic startup check in headless/test modes.</summary>
    public static bool StartupCheckAllowed()
    {
        if (Program.SelfTestPath is not null || Program.ScreenshotPath is not null ||
            Program.SelfUpdateRequested)
        {
            return false;
        }
        return Environment.GetEnvironmentVariable("SBMM_SKIP_UPDATE_CHECK") is null or "";
    }

    /// <summary>Remove leftover download/staging folders from earlier update attempts.</summary>
    public static void CleanupTemp()
    {
        try
        {
            var root = AppPaths.TempRoot;
            if (!Directory.Exists(root)) return;

            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!(name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("import-", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("fomod-", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("collection-", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn("清理更新缓存失败 " + dir + ": " + ex.Message);
                }
            }

            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("collection-", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("清理更新缓存失败 " + file + ": " + ex.Message);
                    }
                }
            }

            if (removed > 0) Log.Info($"已清理 {removed} 个更新缓存项");
        }
        catch (Exception ex)
        {
            Log.Warn("清理更新缓存失败: " + ex.Message);
        }
    }

    public static string CurrentVersion
    {
        get
        {
            // Test hook: pretend to be an older build so the update path can be exercised.
            if (Environment.GetEnvironmentVariable("SBMM_TEST_CURRENT_VERSION") is { Length: > 0 } fake)
            {
                return fake;
            }

            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }
            var version = assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var release = await GitHubService.GetLatestReleaseAsync(Repository, ct).ConfigureAwait(false);
        var version = release.TagName.TrimStart('v', 'V');
        var package = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("SBModManager-v", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        var checksums = release.Assets.FirstOrDefault(a =>
            a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        return new AppUpdateInfo
        {
            Tag = release.TagName,
            Version = version,
            ReleaseUrl = release.HtmlUrl,
            Package = package,
            Checksums = checksums,
            IsNewer = CompareVersions(version, CurrentVersion) > 0,
        };
    }

    /// <summary>Download, verify and unpack into a staging folder. Returns the folder that holds SBModManager/.</summary>
    public static async Task<string> PrepareAsync(AppUpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (info.Package is null)
        {
            throw new InvalidOperationException("最新 Release 里没有找到 win-x64 压缩包。");
        }

        var root = Path.Combine(AppPaths.TempRoot, "update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "update.zip");

        await GitHubService.DownloadAsync(info.Package.DownloadUrl, zipPath, progress, ct).ConfigureAwait(false);

        if (info.Checksums is not null)
        {
            var sumsPath = Path.Combine(root, "SHA256SUMS.txt");
            await GitHubService.DownloadAsync(info.Checksums.DownloadUrl, sumsPath, null, ct).ConfigureAwait(false);
            var expected = ParseChecksum(await File.ReadAllTextAsync(sumsPath, ct).ConfigureAwait(false), info.Package.Name);
            if (expected is null)
            {
                throw new InvalidOperationException("SHA256SUMS.txt 里没有这个压缩包的校验值。");
            }

            var actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"下载的安装包校验失败（期望 {expected[..12]}…，实际 {actual[..12]}…）。");
            }
            Log.Info("更新包 SHA256 校验通过: " + actual);
        }

        var stage = Path.Combine(root, "stage");
        ZipFile.ExtractToDirectory(zipPath, stage);
        var exe = Path.Combine(stage, "SBModManager", "SBModManager.exe");
        if (!File.Exists(exe))
        {
            // Some zips may not have the top-level folder.
            exe = Path.Combine(stage, "SBModManager.exe");
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException("更新包里没有找到 SBModManager.exe。");
            }
            return stage;
        }

        return Path.Combine(stage, "SBModManager");
    }

    /// <summary>Start a helper that waits for this process to exit, copies the files and restarts the app.</summary>
    public static void StartUpdater(string stagedAppDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exePath = Path.Combine(appDir, "SBModManager.exe");
        var scriptPath = Path.Combine(AppPaths.TempRoot, "sbmm-apply-update-" + Guid.NewGuid().ToString("N")[..8] + ".ps1");

        var script = """
$ErrorActionPreference = 'SilentlyContinue'
$parent = __PID__
$source = '__SOURCE__'
$target = '__TARGET__'
$exe = '__EXE__'

$deadline = (Get-Date).AddSeconds(120)
while ((Get-Process -Id $parent -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Milliseconds 800

$ok = $false
for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
    try {
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force -ErrorAction Stop
        $ok = $true
    } catch {
        Start-Sleep -Milliseconds 500
    }
}

if ($ok) {
    Start-Process -FilePath $exe
    Remove-Item -Path (Split-Path (Split-Path $source -Parent) -Parent) -Recurse -Force
} else {
    Add-Content -Path (Join-Path $target 'update-error.log') -Value ("update failed at " + (Get-Date))
}
Remove-Item -Path $PSCommandPath -Force
"""
            .Replace("__PID__", Environment.ProcessId.ToString())
            .Replace("__SOURCE__", stagedAppDir.Replace("'", "''"))
            .Replace("__TARGET__", appDir.Replace("'", "''"))
            .Replace("__EXE__", exePath.Replace("'", "''"));

        File.WriteAllText(scriptPath, script);
        Log.Info("启动更新脚本: " + scriptPath);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
    }

    public static int CompareVersions(string left, string right)
    {
        static int[] Parts(string value) => value
            .Split('.', '-', '+')
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToArray();

        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }
        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
