using System.Diagnostics;
using System.IO.Compression;

namespace SBModManager.Services;

public static class ArchiveService
{
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
    public static readonly string[] PakExtensions = { ".pak", ".ucas", ".utoc" };

    public static bool IsArchive(string path)
        => ArchiveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsPakPart(string path)
        => PakExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string? Find7Zip()
    {
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            @"C:\Program Files\Bandizip\bz.exe",
            @"C:\Program Files (x86)\Bandizip\bz.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\7-Zip\7z.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // fall back to PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim('"'), "7z.exe");
                if (File.Exists(exe)) return exe;
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }
        return null;
    }

    /// <summary>Extract an archive into destDir. Returns an error message or null.</summary>
    public static string? Extract(string archive, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(archive).ToLowerInvariant();
        var sevenZip = Find7Zip();

        if (ext == ".zip")
        {
            try
            {
                ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"内置 ZIP 解压失败，尝试外部工具: {ex.Message}");
                if (sevenZip is null) return "ZIP 解压失败：" + ex.Message;
            }
        }

        if (sevenZip is null)
        {
            return $"需要 7-Zip 才能解压 {ext} 文件。请安装 7-Zip 后重试。";
        }

        try
        {
            var isBandizip = Path.GetFileName(sevenZip).Equals("bz.exe", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(archive) ?? destDir,
            };
            if (isBandizip)
            {
                psi.ArgumentList.Add("x");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-o:" + destDir);
                psi.ArgumentList.Add(archive);
            }
            else
            {
                psi.ArgumentList.Add("x");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-o" + destDir);
                psi.ArgumentList.Add(archive);
            }

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动解压程序。");
            p.WaitForExit(120_000);
            if (p.ExitCode != 0)
            {
                return $"解压失败（退出码 {p.ExitCode}）。";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "解压失败：" + ex.Message;
        }
    }
}
