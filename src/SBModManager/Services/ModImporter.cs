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

            // Some packs (Custom Nanosuit System, UE4SS bundles) ship an SB/ tree that has to be
            // merged into the game root. Detect that before stripping a common top-level folder.
            var overlayFromRaw = IsGameRootOverlay(relFiles);

            // Strip a single common top-level directory ("MyMod/..." -> "...").
            var rootParts = relFiles.Select(r => r.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? commonRoot = null;
            if (!overlayFromRaw && rootParts.Count == 1 && files.Count >= 1 && relFiles.Any(r => r.Contains('\\')))
            {
                var first = rootParts[0];
                var firstIsDir = Directory.Exists(Path.Combine(srcDir, first));
                if (firstIsDir && relFiles.Count(r => r.StartsWith(first + "\\", StringComparison.OrdinalIgnoreCase)) == relFiles.Count)
                {
                    commonRoot = first;
                }
            }

            string Rel(string rel) => commonRoot is null ? rel : rel[(commonRoot.Length + 1)..];

            var stripped = overlayFromRaw ? relFiles : relFiles.Select(Rel).ToList();
            var overlay = overlayFromRaw || IsGameRootOverlay(stripped);
            if (overlayFromRaw)
            {
                commonRoot = null; // files are already SB-relative
            }

            // Source path on disk vs. path recorded in the mod (the latter gets an SB\ prefix
            // for game-root packs so the deployer can map it back to the game folder).
            var pairs = overlay
                ? stripped.Where(IsGameTreeFile)
                    .Select(f => (Source: f,
                        Record: f.StartsWith("SB\\", StringComparison.OrdinalIgnoreCase) ? f : "SB\\" + f))
                    .ToList()
                : stripped.Select(f => (Source: f, Record: f)).ToList();

            var recordFiles = pairs.Select(p => p.Record).ToList();
            var type = overlay ? ModType.GameRoot : DetectType(recordFiles);

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
                Files = recordFiles,
            };

            var dest = AppPaths.ModDir(record.Id);
            Directory.CreateDirectory(dest);
            foreach (var (sourceRel, recordRel) in pairs)
            {
                var from = Path.Combine(srcDir, commonRoot is null ? sourceRel : Path.Combine(commonRoot, sourceRel));
                var to = Path.Combine(dest, recordRel);
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

            CnsAnalyzer.Analyze(record);

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

    /// <summary>
    /// Packs recorded without the SB\ prefix (archive starts at Binaries/ or Content/) were
    /// previously deployed below ~mods. Move them to GameRoot and add the SB\ prefix.
    /// </summary>
    public static int MigrateGameRootOverlaysV2()
    {
        var state = AppState.Instance;
        var migrated = 0;

        foreach (var record in state.Library.ToList())
        {
            if (record.Type is ModType.GameRoot) continue;
            if (record.Type is not (ModType.Pak or ModType.Loose or ModType.Cns)) continue;
            if (record.Files.Count == 0) continue;
            if (record.Files.Any(f => f.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase))) continue;

            var gameTree = record.Files.All(f =>
                f.StartsWith(@"Binaries\", StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(@"Content\", StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(@"Engine\", StringComparison.OrdinalIgnoreCase));
            if (!gameTree) continue;

            var wasEnabled = state.IsEnabled(record.Id);
            if (wasEnabled)
            {
                Deployer.Undeploy(record.Id);
            }

            record.Files = AddSbPrefixAndMoveFiles(record);
            record.Type = ModType.GameRoot;
            ModStore.Save(record);

            if (wasEnabled && state.GameDir is { } gameDir)
            {
                Deployer.Deploy(record, gameDir, force: true);
            }

            migrated++;
        }

        if (migrated > 0)
        {
            state.ReloadLibrary();
            Log.Info($"已把 {migrated} 个无 SB 前缀的包迁移为「游戏根目录」类型");
        }
        return migrated;
    }

    /// <summary>
    /// Add the SB\ prefix to recorded paths and move the matching files in the mod library.
    /// </summary>
    private static List<string> AddSbPrefixAndMoveFiles(ModRecord record)
    {
        var modDir = record.ModDir;
        var newFiles = new List<string>(record.Files.Count);
        foreach (var rel in record.Files)
        {
            var newRel = rel.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase) ? rel : @"SB\" + rel;
            var src = Path.Combine(modDir, rel);
            var dst = Path.Combine(modDir, newRel);
            if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) && File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst, overwrite: true);
            }
            newFiles.Add(newRel);
        }
        return newFiles;
    }

    /// <summary>True when every file is either under SB\ or a harmless documentation file.</summary>
    /// <summary>
    /// Older versions stripped the SB\ prefix from CNS/UE4SS packs and deployed them below ~mods.
    /// Move those records to the GameRoot type so their files merge into the game root again.
    /// </summary>
    public static int MigrateGameRootOverlays()
    {
        var state = AppState.Instance;
        var migrated = 0;

        foreach (var record in state.Library.ToList())
        {
            if (record.Type is ModType.GameRoot or ModType.Cns) continue;
            if (record.Type is not (ModType.Pak or ModType.Loose)) continue;
            if (record.Files.Count == 0) continue;

            var looksOverlay = record.Files.All(f =>
                f.StartsWith(@"Content\Paks\~mods\CustomNanosuitSystem\", StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(@"Content\Paks\LogicMods\", StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(@"Binaries\Win64\ue4ss\", StringComparison.OrdinalIgnoreCase));
            if (!looksOverlay) continue;

            var wasEnabled = state.IsEnabled(record.Id);
            if (wasEnabled)
            {
                Deployer.Undeploy(record.Id);
            }

            record.Files = record.Files
                .Select(f => f.StartsWith(@"SB\", StringComparison.OrdinalIgnoreCase) ? f : @"SB\" + f)
                .ToList();
            record.Type = ModType.GameRoot;
            ModStore.Save(record);

            if (wasEnabled && state.GameDir is { } gameDir)
            {
                Deployer.Deploy(record, gameDir, force: false);
            }

            migrated++;
        }

        if (migrated > 0)
        {
            state.ReloadLibrary();
            Log.Info($"已把 {migrated} 个 SB 根目录包迁移为「游戏根目录」类型");
        }
        return migrated;
    }

    public static bool IsGameRootOverlay(IReadOnlyList<string> relFiles)
    {
        if (relFiles.Count == 0) return false;
        if (!relFiles.Any(IsGameTreeFile)) return false;
        return relFiles.Where(r => !IsGameTreeFile(r)).All(IsDocumentationFile);
    }

    /// <summary>True when the path is relative to the game root (with or without the SB\ prefix).</summary>
    private static bool IsGameTreeFile(string rel)
        => rel.StartsWith("SB\\", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("Binaries\\", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("Content\\", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("Engine\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentationFile(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".url" or ".rtf" or ".html";
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
