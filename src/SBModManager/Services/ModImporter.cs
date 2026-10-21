using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

public static class ModImporter
{
    private static readonly Regex VersionPattern = new(
        @"(?i)(?:^|[\s_\-\[\]])(v?\d+(?:\.\d+){1,3}[a-z]?)(?:$|[\s_\-\[\]])", RegexOptions.Compiled);

    /// <summary>Import a packaged mod (.zip/.7z/.rar) or a set of pak files.</summary>
    public static ImportResult ImportArchive(string path)
    {
        var tempRoot = Path.Combine(AppPaths.TempRoot, "import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            var err = ArchiveService.Extract(path, tempRoot);
            if (err is not null) return new ImportResult { Error = err };
            return ImportDirectory(tempRoot, Path.GetFileNameWithoutExtension(path), path, move: false);
        }
        catch (Exception ex)
        {
            Log.Error("导入模组失败: " + path, ex);
            return new ImportResult { Error = ex.Message };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public static ImportResult ImportPakFiles(IReadOnlyList<string> files)
    {
        var tempRoot = Path.Combine(AppPaths.TempRoot, "import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            foreach (var f in files)
            {
                File.Copy(f, Path.Combine(tempRoot, Path.GetFileName(f)), overwrite: true);
            }
            var name = Path.GetFileNameWithoutExtension(files[0]);
            name = StripSuffix(name);
            return ImportDirectory(tempRoot, name, string.Join("; ", files.Select(Path.GetFileName)), move: false);
        }
        catch (Exception ex)
        {
            Log.Error("导入 PAK 失败", ex);
            return new ImportResult { Error = ex.Message };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>Import an already extracted folder (or a user picked folder).</summary>
    public static ImportResult ImportDirectory(string srcDir, string displayName, string sourceName, bool move)
    {
        try
        {
            var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) return new ImportResult { Error = "文件夹/压缩包内没有文件。" };

            var relFiles = files.Select(f => Path.GetRelativePath(srcDir, f).Replace('/', '\\')).ToList();

            // Strip a single common top-level directory ("MyMod/..." -> "...").
            var rootParts = relFiles.Select(r => r.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? commonRoot = null;
            if (rootParts.Count == 1 && files.Count >= 1 && relFiles.Any(r => r.Contains('\\')))
            {
                var first = rootParts[0];
                var firstIsDir = Directory.Exists(Path.Combine(srcDir, first));
                if (firstIsDir && relFiles.Count(r => r.StartsWith(first + "\\", StringComparison.OrdinalIgnoreCase)) == relFiles.Count)
                {
                    commonRoot = first;
                }
            }

            string Rel(string rel) => commonRoot is null ? rel : rel[(commonRoot.Length + 1)..];

            var stripped = relFiles.Select(Rel).ToList();
            var type = DetectType(stripped);

            var name = displayName;
            if (commonRoot is not null && LooksLikeModName(commonRoot))
            {
                name = commonRoot;
            }
            name = CleanName(name);
            var version = VersionPattern.Match(name).Groups[1].Value;
            if (!string.IsNullOrEmpty(version))
            {
                name = name.Replace(version, "").Trim(' ', '_', '-', '[', ']');
            }

            var record = new ModRecord
            {
                Name = string.IsNullOrWhiteSpace(name) ? "未命名模组" : name,
                Version = version,
                Type = type,
                SourceName = sourceName,
                FolderName = Sanitize(name),
                GroupKey = NormalizeGroup(string.IsNullOrWhiteSpace(name) ? "未命名模组" : name),
                Files = stripped,
            };

            var dest = AppPaths.ModDir(record.Id);
            Directory.CreateDirectory(dest);
            foreach (var rel in stripped)
            {
                var from = Path.Combine(srcDir, commonRoot is null ? rel : Path.Combine(commonRoot, rel));
                var to = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (move)
                {
                    File.Move(from, to, overwrite: true);
                }
                else
                {
                    File.Copy(from, to, overwrite: true);
                }
            }

            record.SizeBytes = record.Files.Sum(f =>
            {
                try { return new FileInfo(Path.Combine(dest, f)).Length; }
                catch { return 0L; }
            });

            if (type == ModType.Ue4ss)
            {
                EnsureUe4ssEnabled(dest, record.Files);
            }

            ModStore.Save(record);
            Log.Info($"已导入模组 [{record.Name}] 类型={record.Type} 文件数={record.Files.Count}");
            return new ImportResult { Mod = record };
        }
        catch (Exception ex)
        {
            Log.Error("导入目录失败: " + srcDir, ex);
            return new ImportResult { Error = ex.Message };
        }
    }

    public static ModType DetectType(IReadOnlyList<string> relFiles)
    {
        var lower = relFiles.Select(f => f.ToLowerInvariant().Replace('/', '\\')).ToList();

        if (lower.Any(f => f.EndsWith(".pak") || f.EndsWith(".ucas") || f.EndsWith(".utoc")))
        {
            return ModType.Pak;
        }
        if (lower.Any(f => f.EndsWith(".dekcns.json") || f.EndsWith(".dekani.json")))
        {
            return ModType.Cns;
        }
        if (lower.Any(f => f.EndsWith("dwmapi.dll") || f.EndsWith("xinput1_3.dll") || f.Contains(@"\ue4ss\")))
        {
            return ModType.Ue4ssRuntime;
        }
        if (lower.Any(f => f.EndsWith(@"scripts\main.lua") || f is "enabled.txt" || f.EndsWith(@"\enabled.txt")))
        {
            return ModType.Ue4ss;
        }
        if (lower.All(f => f.EndsWith(".ini")))
        {
            return ModType.IniTweak;
        }
        return ModType.Loose;
    }

    private static void EnsureUe4ssEnabled(string destDir, IEnumerable<string> files)
    {
        // UE4SS requires an enabled.txt next to scripts/ for a mod to load.
        var existing = files.FirstOrDefault(f => f.Equals("enabled.txt", StringComparison.OrdinalIgnoreCase));
        if (existing is null && files.Any(f => f.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)))
        {
            File.WriteAllText(Path.Combine(destDir, "enabled.txt"), "");
        }
    }

    /// <summary>Group key used to treat different installed versions as one mod.</summary>
    public static string NormalizeGroup(string name)
    {
        var s = Regex.Replace(name, @"(?i)[\s_\-.]*(?:v)?\d+(?:\.\d+){1,3}[a-z]?[\s_\-.]*$", " ");
        s = Regex.Replace(s, @"[\s_\-]+", " ").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(s) ? name.Trim().ToLowerInvariant() : s;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Mod";
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    private static string CleanName(string name)
    {
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length > 80 ? name[..80] : name;
    }

    private static bool LooksLikeModName(string s)
    {
        if (s.Length < 3 || s.Length > 80) return false;
        var lower = s.ToLowerInvariant();
        return !lower.Contains("content") && !lower.Equals("sb") && !lower.Equals("binaries")
               && !lower.Equals("files") && !lower.Equals("mod");
    }

    private static string StripSuffix(string name)
    {
        foreach (var suffix in new[] { "_P", "_p", "-P", "_WindowsNoEditor" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }
        return name;
    }

    public static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时目录失败 {dir}: {ex.Message}");
        }
    }
}
