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
                    targetRel = Path.Combine("SB", "Content", "Paks", "~mods", Path.GetFileName(rel));
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
        => AppState.Instance.Deployed.Mods.Values.Any(d => d.Files.Contains(targetPath, StringComparer.OrdinalIgnoreCase));

    public static string? DeployingModOf(string targetPath)
        => AppState.Instance.Deployed.Mods.FirstOrDefault(
            kv => kv.Value.Files.Contains(targetPath, StringComparer.OrdinalIgnoreCase)).Key;

    public static DeployResult Deploy(ModRecord mod, string gameDir, bool force, IProgress<string>? progress = null)
    {
        var targets = ComputeTargets(mod, gameDir);
        if (targets.Count == 0)
        {
            return new DeployResult { Success = false, Error = "这个模组没有可部署的文件。" };
        }

        var state = AppState.Instance.Deployed;
        var conflicts = new List<ModConflict>();
        foreach (var (_, target) in targets)
        {
            if (!File.Exists(target)) continue;
            var owner = DeployingModOf(target);
            if (owner == mod.Id) continue; // already ours
            var other = owner is null ? "外部文件" : (AppState.Instance.Library.FirstOrDefault(m => m.Id == owner)?.Name ?? owner);
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
        try
        {
            var useHardLink = AppState.Instance.Config.UseHardLinks && SameVolume(mod.ModDir, gameDir);
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
                progress?.Report($"部署 {targets.Count}/{targets.Count}... {Path.GetFileName(target)}");
            }

            state.Mods[mod.Id] = new DeployedMod
            {
                Files = deployed,
                DeployedAt = DateTimeOffset.Now,
                HardLinked = useHardLink,
            };
            AppState.Instance.SaveDeployed();
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
            return new DeployResult { Success = false, Error = ex.Message };
        }
    }

    public static void Undeploy(string modId)
    {
        var state = AppState.Instance.Deployed;
        if (!state.Mods.Remove(modId, out var deployed)) return;
        foreach (var file in deployed.Files)
        {
            TryDelete(file);
        }
        AppState.Instance.SaveDeployed();
        Log.Info($"已停用模组 {modId}，移除 {deployed.Files.Count} 个文件");
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

        var managed = AppState.Instance.Deployed.Mods.Values
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

        var threshold = Math.Max(2, AppState.Instance.Config.HighConflictThreshold);
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
            Chains = chains.OrderByDescending(c => c.Mods.Count).ThenBy(c => c.TargetPath).ToList(),
            HighConflictMods = stats,
        };
    }

    /// <summary>Undeploy and re-deploy every enabled mod in load-order priority.</summary>
    public static (int Deployed, int Failed) RedeployAll(string gameDir)
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
