using System.Diagnostics;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace SBModManager.Services;

public static class ArchiveService
{
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
    public static readonly string[] PakExtensions = { ".pak", ".ucas", ".utoc" };

    /// <summary>A mod archive with more entries than this is treated as hostile, not as a mod.</summary>
    public const int MaxEntries = 100_000;

    /// <summary>Upper bound on the total uncompressed size we are willing to write.</summary>
    public const long MaxTotalBytes = 32L * 1024 * 1024 * 1024;

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
        var errors = new List<string>();

        // Fast path: the framework's own ZIP implementation.
        if (ext == ".zip")
        {
            var limitError = EnsureZipWithinLimits(archive);
            if (limitError is not null)
            {
                Log.Warn($"拒绝解压 {Path.GetFileName(archive)}: {limitError}");
                return limitError;
            }

            try
            {
                ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true);
                return null;
            }
            catch (Exception ex)
            {
                errors.Add("ZIP 内置解压: " + ex.Message);
                Log.Warn("ZIP 内置解压失败，尝试内置 7z 引擎: " + ex.Message);
            }
        }

        // Built-in 7z/RAR/ZIP engine (SharpCompress): no external 7-Zip installation needed.
        var builtinError = ExtractBuiltin(archive, destDir);
        if (builtinError is null)
        {
            Log.Info($"内置解压完成: {Path.GetFileName(archive)}");
            return null;
        }
        errors.Add("内置 7z 引擎: " + builtinError);
        Log.Warn("内置 7z 引擎解压失败，尝试外部工具: " + builtinError);

        var sevenZip = Find7Zip();
        if (sevenZip is null)
        {
            return $"无法解压 {ext}：{string.Join("；", errors)}\n" +
                   "可以尝试把模组重新打包为 .zip，或安装 7-Zip 后重试。";
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
            if (!p.WaitForExit(120_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }
                return "解压超时（超过 120 秒），已终止解压程序。";
            }
            if (p.ExitCode != 0)
            {
                return $"解压失败（7-Zip 退出码 {p.ExitCode}）：{string.Join("；", errors)}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "解压失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Extract with the bundled SharpCompress engine (7z, rar, zip and others).
    /// Tries random-access archives first (required for 7z), then the streaming reader,
    /// and rejects path traversal in both cases.
    /// </summary>
    private static string? ExtractBuiltin(string archive, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar))
        {
            destFull += Path.DirectorySeparatorChar;
        }

        var archiveError = TryExtractArchive(archive, destFull);
        if (archiveError is null)
        {
            Log.Info($"内置 7z 引擎解压完成: {Path.GetFileName(archive)}");
            return null;
        }

        var readerError = TryExtractReader(archive, destFull);
        if (readerError is null)
        {
            Log.Info($"内置流式解压完成: {Path.GetFileName(archive)}");
            return null;
        }

        return archiveError + "；" + readerError;
    }

    private static string? TryExtractArchive(string archive, string destFull)
    {
        try
        {
            using var stream = File.OpenRead(archive);
            using var archiveObj = ArchiveFactory.OpenArchive(stream, new ReaderOptions());
            var count = 0;
            foreach (var entry in archiveObj.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

                var target = CombineSafe(destFull, entry.Key);
                if (target is null) return "压缩包包含非法路径: " + entry.Key;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = entry.OpenEntryStream())
                using (var output = File.Create(target))
                {
                    CopyLimited(input, output);
                }
                count++;
                if (count > MaxEntries) return $"压缩包条目过多（超过 {MaxEntries}），已取消解压。";
            }

            return count == 0 ? "压缩包内没有文件。" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? TryExtractReader(string archive, string destFull)
    {
        try
        {
            using var stream = File.OpenRead(archive);
            using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
            var count = 0;
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

                var target = CombineSafe(destFull, entry.Key);
                if (target is null) return "压缩包包含非法路径: " + entry.Key;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = reader.OpenEntryStream())
                using (var output = File.Create(target))
                {
                    CopyLimited(input, output);
                }
                count++;
                if (count > MaxEntries) return $"压缩包条目过多（超过 {MaxEntries}），已取消解压。";
            }

            return count == 0 ? "压缩包内没有文件。" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }


    /// <summary>
    /// Cheap pre-flight for ZIP files: reject archives whose entry count or total uncompressed
    /// size suggests a decompression bomb. Returns an error message, or null when it looks sane.
    /// </summary>
    public static string? EnsureZipWithinLimits(string archive)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var count = 0;
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                count++;
                total += entry.Length;
                if (count > MaxEntries) return $"压缩包条目过多（超过 {MaxEntries}），已取消解压。";
                if (total > MaxTotalBytes) return "压缩包解压后体积过大，已取消解压。";
            }
        }
        catch (Exception ex)
        {
            // A zip we cannot even enumerate is handled by the extractor below; do not block it.
            Log.Warn("检查压缩包体积失败（继续尝试解压）: " + ex.Message);
        }
        return null;
    }

    /// <summary>Copy while counting, so a bomb cannot fill the disk before we notice.</summary>
    private static void CopyLimited(Stream input, Stream output)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxTotalBytes)
            {
                throw new InvalidOperationException("压缩包解压后体积过大，已取消解压。");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static string? CombineSafe(string destFullWithSeparator, string entryKey)
    {
        var rel = entryKey
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (rel.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(destFullWithSeparator, rel));
        return full.StartsWith(destFullWithSeparator, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
