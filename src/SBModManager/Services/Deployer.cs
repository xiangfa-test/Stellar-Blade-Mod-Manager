using System.Runtime.InteropServices;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class ExternalMod
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public long SizeBytes { get; init; }
}

public sealed class DeployResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<string> DeployedFiles { get; init; } = new();
    public List<ModConflict> Conflicts { get; init; } = new();
}

public static class Deployer
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static string PaksModsDir(string gameDir)
        => Path.Combine(gameDir, "SB", "Content", "Paks", "~mods");

    public static string BinariesDir(string gameDir)
        => Path.Combine(gameDir, "SB", "Binaries", "Win64");

    public static string Ue4ssModsDir(string gameDir)
        => Path.Combine(BinariesDir(gameDir), "ue4ss", "Mods");

    /// <summary>Compute every file this mod wants to place inside the game folder.</summary>
    public static List<(string Source, string Target)> ComputeTargets(ModRecord mod, string gameDir)
    {
        var list = new List<(string, string)>();
        var modRoot = mod.ModDir;

        foreach (var rel in mod.Files)
        {
            var src = Path.Combine(modRoot, rel);
            string targetRel;
            switch (mod.Type)
            {
                case ModType.Pak:
                    targetRel = Path.Combine("SB", "Content", "Paks", "~mods",
                        PakFileName(mod, Path.GetFileName(rel)));
                    break;
                case ModType.Loose:
                    targetRel = Path.Combine("SB", "Content", "Paks", "~mods", mod.FolderName, rel);
                    break;
                case ModType.Cns:
                    targetRel = Path.Combine("SB", "Content", "Paks", "~mods", "CustomNanosuitSystem", StripCnsPrefix(rel));
                    break;
                case ModType.Ue4ss:
                    targetRel = Path.Combine("SB", "Binaries", "Win64", "ue4ss", "Mods", mod.FolderName, rel);
                    break;
                case ModType.Ue4ssRuntime:
                    targetRel = Path.Combine("SB", "Binaries", "Win64", StripRuntimePrefix(rel));
                    break;
                case ModType.GameRoot:
                    targetRel = rel;
                    break;
                case ModType.Video:
                    targetRel = rel; // already normalized to SB\Content\Movies\...
                    break;
                case ModType.LogicMod:
                    targetRel = rel; // already normalized to SB\Content\Paks\LogicMods\...
                    break;
                case ModType.IniTweak:
                    targetRel = Path.Combine("..", "..", "..", "..", "..", "..", "SB", "Saved", "Config", "WindowsNoEditor",
                        Path.GetFileName(rel));
                    // special-cased below: ini tweaks live in %LOCALAPPDATA%
                    list.Add((src, Path.Combine(AppPaths.GameConfigDir, Path.GetFileName(rel))));
                    continue;
                default:
                    targetRel = Path.Combine("SB", "Content", "Paks", "~mods", mod.FolderName, rel);
                    break;
            }
            list.Add((src, Path.Combine(gameDir, targetRel)));
        }
        return list;
    }

    /// <summary>
    /// Prefix a pak basename with the mod's load-order number (0001_...) so UE mounts it in the
    /// expected order; .pak / .ucas / .utoc must keep the same basename.
    /// </summary>
    public static string PakFileName(ModRecord mod, string fileName)
    {
        if (!State.Config.PakOrderPrefixEnabled) return fileName;
        var priority = Math.Max(0, mod.Priority);
        return priority.ToString("D4") + "_" + fileName;
    }

    private static string StripCnsPrefix(string rel)
    {
        foreach (var prefix in new[] { @"CustomNanosuitSystem\", @"~mods\CustomNanosuitSystem\" })
        {
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return rel[prefix.Length..];
            }
        }
        return rel;
    }

    private static string StripRuntimePrefix(string rel)
    {
        foreach (var prefix in new[] { @"SB\Binaries\Win64\", @"Binaries\Win64\", @"Win64\" })
        {
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return rel[prefix.Length..];
            }
        }
        return rel;
    }

    public static bool IsFileDeployedBy(string targetPath)
        => State.Deployed.Mods.Values.Any(d => d.Files.Contains(targetPath, StringComparer.OrdinalIgnoreCase));

    public static string? DeployingModOf(string targetPath)
        => State.Deployed.Mods.FirstOrDefault(
            kv => kv.Value.Files.Contains(targetPath, StringComparer.OrdinalIgnoreCase)).Key;

    /// <summary>Serializes all file operations on the game folder (deploy/undeploy/redeploy).</summary>
    private static readonly object FileLock = new();

    /// <summary>State used by the deployer; replaceable for tests.</summary>
    public static IAppState State { get; set; } = AppState.Instance;

    public static DeployResult Deploy(ModRecord mod, string gameDir, bool force, IProgress<string>? progress = null)
    {
        lock (FileLock)
        {
            return DeployCore(mod, gameDir, force, progress);
        }
    }

    private static DeployResult DeployCore(ModRecord mod, string gameDir, bool force, IProgress<string>? progress)
    {
        var targets = ComputeTargets(mod, gameDir);
        if (targets.Count == 0)
        {
            return new DeployResult { Success = false, Error = "这个模组没有可部署的文件。" };
        }

        var state = State.Deployed;
        var conflicts = new List<ModConflict>();
        foreach (var (_, target) in targets)
        {
            if (!File.Exists(target)) continue;
            var owner = DeployingModOf(target);
            if (owner == mod.Id) continue; // already ours
            var other = owner is null ? "外部文件" : (State.Library.FirstOrDefault(m => m.Id == owner)?.Name ?? owner);
            if (!force)
            {
                conflicts.Add(new ModConflict
                {
                    TargetPath = target,
                    ModName = mod.Name,
                    OtherModName = other,
                });
            }
        }
        if (conflicts.Count > 0)
        {
            return new DeployResult { Success = false, Conflicts = conflicts, Error = "存在文件冲突。" };
        }

        var deployed = new List<string>();
        var backups = state.Mods.TryGetValue(mod.Id, out var existingDeployment)
            ? new Dictionary<string, string>(existingDeployment.Backups, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displaced = existingDeployment is not null
            ? new Dictionary<string, string>(existingDeployment.Displaced, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displacedSources = existingDeployment is not null
            ? new Dictionary<string, string>(existingDeployment.DisplacedSources, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = existingDeployment is not null
            ? new Dictionary<string, string>(existingDeployment.Sources, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var useHardLink = State.Config.UseHardLinks && SameVolume(mod.ModDir, gameDir);
            DeployJournal.Begin(mod, gameDir, targets.Select(t => t.Target), backups);
            foreach (var (src, target) in targets)
            {
                if (!File.Exists(src))
                {
                    Log.Warn($"模组文件缺失，跳过: {src}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var previousOwner = DeployingModOf(target);
                    if (force && previousOwner is not null && previousOwner != mod.Id
                        && state.Mods.TryGetValue(previousOwner, out var previousDeployment))
                    {
                        previousDeployment.Files.RemoveAll(f =>
                            string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
                        displaced[target] = previousOwner;
                        string? previousSource = null;
                        if (previousDeployment.Sources.TryGetValue(target, out var recordedSource))
                        {
                            previousSource = recordedSource;
                        }
                        if (string.IsNullOrEmpty(previousSource))
                        {
                            var previousRecord = State.Library.FirstOrDefault(m => m.Id == previousOwner);
                            if (previousRecord is not null)
                            {
                                previousSource = ComputeTargets(previousRecord, gameDir)
                                    .FirstOrDefault(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase))
                                    .Source;
                            }
                        }
                        if (!string.IsNullOrEmpty(previousSource))
                        {
                            displacedSources[target] = previousSource;
                        }
                    }

                    // Untracked file = game / Vortex / manual file: keep a copy so undeploy can restore it.
                    if (previousOwner is null && !backups.ContainsKey(target))
                    {
                        var backupPath = BackupFile(target, gameDir, mod.Id);
                        if (backupPath is not null)
                        {
                            backups[target] = backupPath;
                        }
                    }

                    File.Delete(target);
                }
                if (useHardLink)
                {
                    if (!CreateHardLinkW(target, src, IntPtr.Zero))
                    {
                        File.Copy(src, target, overwrite: true);
                    }
                }
                else
                {
                    File.Copy(src, target, overwrite: true);
                }
                deployed.Add(target);
                sources[target] = src;
                progress?.Report($"部署 {targets.Count}/{targets.Count}... {Path.GetFileName(target)}");
            }

            state.Mods[mod.Id] = new DeployedMod
            {
                Files = deployed,
                DeployedAt = DateTimeOffset.Now,
                HardLinked = useHardLink,
                Backups = backups,
                Displaced = displaced,
                DisplacedSources = displacedSources,
                Sources = sources,
            };
            State.SaveDeployed();
            DeployJournal.Complete();
            Ue4ssConfig.Apply(mod, gameDir, enabled: true);
            Views.ModsPage.InvalidateConflictCache();
            Log.Info($"已启用模组 {mod.Name}，部署 {deployed.Count} 个文件 (hardlink={useHardLink})");
            return new DeployResult { Success = true, DeployedFiles = deployed };
        }
        catch (Exception ex)
        {
            Log.Error("启用模组失败: " + mod.Name, ex);
            // roll back partial deployment
            foreach (var f in deployed)
            {
                TryDelete(f);
            }
            // put back game files that were already replaced during this attempt
            foreach (var (target, backup) in backups)
            {
                try
                {
                    if (!File.Exists(backup)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, overwrite: true);
                    File.Delete(backup);
                }
                catch (Exception restoreError)
                {
                    Log.Warn($"回滚备份失败 {target}: {restoreError.Message}");
                }
            }
            CleanupBackupDir(mod.Id);
            DeployJournal.Complete();
            return new DeployResult { Success = false, Error = ex.Message };
        }
    }

    public static void Undeploy(string modId)
    {
        lock (FileLock)
        {
            UndeployCore(modId);
        }
    }

    private static void UndeployCore(string modId)
    {
        var state = State.Deployed;
        if (!state.Mods.Remove(modId, out var deployed)) return;

        if (State.GameDir is { } gameDir)
        {
            var record = State.Library.FirstOrDefault(m => m.Id == modId);
            if (record is not null)
            {
                Ue4ssConfig.Apply(record, gameDir, enabled: false);
            }
            else
            {
                // Library entry already gone: infer the UE4SS folder names from deployed paths.
                Ue4ssConfig.ApplyFromPaths(deployed.Files, gameDir, enabled: false);
            }
        }

        foreach (var file in deployed.Files)
        {
            TryDelete(file);
        }
        RestoreBackups(modId, deployed, State.GameDir);
        RestoreDisplaced(deployed);
        State.SaveDeployed();
        Views.ModsPage.InvalidateConflictCache();
        Log.Info($"已停用模组 {modId}，移除 {deployed.Files.Count} 个文件");
    }

    /// <summary>Copy an overwritten game file into the backup store before replacing it.</summary>
    private static string? BackupFile(string target, string gameDir, string modId)
    {
        try
        {
            var rel = Path.GetRelativePath(gameDir, target);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                rel = Path.GetFileName(target);
            }

            var backup = Path.Combine(AppPaths.BackupsRoot, modId, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, overwrite: true);
            Log.Info($"已备份游戏原文件: {target}");
            return backup;
        }
        catch (Exception ex)
        {
            Log.Warn($"备份游戏文件失败 {target}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Put back files the mod replaced, unless another mod now owns that path.</summary>
    private static void RestoreBackups(string modId, DeployedMod deployed, string? gameDir)
    {
        if (deployed.Backups.Count == 0) return;
        foreach (var (target, backup) in deployed.Backups)
        {
            try
            {
                if (!File.Exists(backup)) continue;
                var owner = DeployingModOf(target);
                if (owner is not null && owner != modId)
                {
                    Log.Warn($"跳过恢复 {target}: 已被其它模组接管");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) File.Delete(target);
                File.Copy(backup, target, overwrite: true);
                File.Delete(backup);
                Log.Info($"已恢复游戏原文件: {target}");
            }
            catch (Exception ex)
            {
                Log.Warn($"恢复游戏文件失败 {target}: {ex.Message}");
            }
        }

        CleanupBackupDir(modId);
    }

    /// <summary>When this mod took over another mod's file, give it back if that mod is still enabled.</summary>
    private static void RestoreDisplaced(DeployedMod deployed)
    {
        if (deployed.Displaced.Count == 0) return;
        var state = AppState.Instance;
        if (state.GameDir is not { } gameDir) return;

        foreach (var (target, ownerId) in deployed.Displaced)
        {
            try
            {
                if (!state.IsEnabled(ownerId)) continue;
                if (DeployingModOf(target) is not null) continue;

                var owner = state.Library.FirstOrDefault(m => m.Id == ownerId);
                var source = owner is not null
                    ? ComputeTargets(owner, gameDir)
                        .FirstOrDefault(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase)).Source
                    : null;
                if (string.IsNullOrEmpty(source) &&
                    deployed.DisplacedSources.TryGetValue(target, out var storedSource))
                {
                    source = storedSource;
                }
                if (string.IsNullOrEmpty(source) &&
                    state.Deployed.Mods.TryGetValue(ownerId, out var ownerDeploymentNow) &&
                    ownerDeploymentNow.Sources.TryGetValue(target, out var deployedSource))
                {
                    source = deployedSource;
                }
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                if (state.Deployed.Mods.TryGetValue(ownerId, out var ownerDeployment) &&
                    !ownerDeployment.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    ownerDeployment.Files.Add(target);
                }
                Log.Info($"已恢复被覆盖的模组文件: {owner?.Name ?? ownerId} -> {target}");
            }
            catch (Exception ex)
            {
                Log.Warn($"恢复被覆盖文件失败 {target}: {ex.Message}");
            }
        }
    }

    private static void CleanupBackupDir(string modId)
    {
        try
        {
            var dir = Path.Combine(AppPaths.BackupsRoot, modId);
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(p => p.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理备份目录失败: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            var attrs = File.GetAttributes(file);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            File.Delete(file);
            var dir = Path.GetDirectoryName(file);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除文件失败 {file}: {ex.Message}");
        }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Find files inside ~mods that this manager did not deploy.</summary>
    public static List<ExternalMod> ScanExternal(string gameDir)
    {
        var result = new List<ExternalMod>();
        var modsDir = PaksModsDir(gameDir);
        if (!Directory.Exists(modsDir)) return result;

        var managed = State.Deployed.Mods.Values
            .SelectMany(d => d.Files)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, (List<string> files, long size, string type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories))
        {
            if (managed.Contains(file)) continue;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            string key;
            string type;
            if (ext is ".pak" or ".ucas" or ".utoc")
            {
                key = "PAK: " + Path.GetFileNameWithoutExtension(file);
                type = "外部 PAK";
            }
            else
            {
                var rel = Path.GetRelativePath(modsDir, file);
                key = "文件夹: " + rel.Split('\\')[0];
                type = "外部资源";
            }
            var size = new FileInfo(file).Length;
            if (!groups.TryGetValue(key, out var g))
            {
                g = (new List<string>(), 0, type);
            }
            g.files.Add(file);
            groups[key] = (g.files, g.size + size, g.type);
        }

        foreach (var (key, value) in groups)
        {
            result.Add(new ExternalMod
            {
                Name = key[(key.IndexOf(':') + 1)..].Trim(),
                Type = value.type,
                Files = value.files,
                SizeBytes = value.size,
            });
        }
        return result.OrderBy(e => e.Name).ToList();
    }

    public static void DeleteExternal(ExternalMod external)
    {
        foreach (var f in external.Files)
        {
            TryDelete(f);
        }
        Log.Info($"已删除外部文件组: {external.Name}");
    }

    /// <summary>Build a readable conflict graph across currently enabled mods.</summary>
    public static ConflictReport BuildConflictReport(string gameDir)
    {
        var state = AppState.Instance;
        var enabled = state.Library.Where(m => state.IsEnabled(m.Id)).ToList();
        var byFile = new Dictionary<string, List<ModRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabled)
        {
            try
            {
                foreach (var (_, target) in ComputeTargets(mod, gameDir))
                {
                    if (!byFile.TryGetValue(target, out var list))
                    {
                        list = new List<ModRecord>();
                        byFile[target] = list;
                    }
                    list.Add(mod);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("冲突分析失败: " + mod.Name + " " + ex.Message);
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var chains = new List<ConflictChain>();
        foreach (var (file, mods) in byFile)
        {
            if (mods.Count < 2) continue;
            var ordered = mods.OrderBy(m => m.Priority).ThenBy(m => m.Name).ToList();
            chains.Add(new ConflictChain
            {
                TargetPath = file,
                Mods = ordered.Select(m => m.Name).ToList(),
            });
            foreach (var mod in ordered)
            {
                counts.TryGetValue(mod.Id, out var c);
                counts[mod.Id] = c + 1;
            }
        }

        var threshold = Math.Max(2, State.Config.HighConflictThreshold);
        var stats = counts
            .Select(kv => new ModConflictStat
            {
                ModId = kv.Key,
                ModName = state.Library.FirstOrDefault(m => m.Id == kv.Key)?.Name ?? kv.Key,
                ConflictCount = kv.Value,
                IsHigh = kv.Value >= threshold,
            })
            .OrderByDescending(s => s.ConflictCount)
            .ToList();

        return new ConflictReport
        {
            DeployedCount = state.Deployed.Mods.Count,
            Chains = chains.OrderByDescending(c => c.Mods.Count).ThenBy(c => c.TargetPath).ToList(),
            HighConflictMods = stats,
        };
    }

    /// <summary>Undeploy and re-deploy every enabled mod in load-order priority.</summary>
    public static (int Deployed, int Failed) RedeployAll(string gameDir)
    {
        lock (FileLock)
        {
            return RedeployAllCore(gameDir);
        }
    }

    private static (int Deployed, int Failed) RedeployAllCore(string gameDir)
    {
        var state = AppState.Instance;
        var enabled = state.Library.Where(m => state.IsEnabled(m.Id))
            .OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var mod in enabled)
        {
            Undeploy(mod.Id);
        }

        var ok = 0;
        var failed = 0;
        foreach (var mod in enabled)
        {
            var result = Deploy(mod, gameDir, force: true);
            if (result.Success) ok++;
            else failed++;
        }
        Views.ModsPage.InvalidateConflictCache();
        Log.Info($"按加载顺序重建部署: 成功 {ok}，失败 {failed}");
        return (ok, failed);
    }

    public static bool IsUe4ssInstalled(string gameDir)
    {
        var binaries = BinariesDir(gameDir);
        var proxy = new[] { "dwmapi.dll", "xinput1_3.dll", "version.dll" }
            .Any(p => File.Exists(Path.Combine(binaries, p)));
        return proxy && Directory.Exists(Path.Combine(binaries, "ue4ss"));
    }
}
