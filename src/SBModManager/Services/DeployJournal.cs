using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Journal for in-flight deployments. A crash between "copying files" and "saving deployed.json"
/// would otherwise leave files the manager no longer knows about. On startup the journal is
/// replayed: targets that are still there get adopted into the deployment record, so disabling
/// the mod later removes them instead of leaving orphans.
/// </summary>
public static class DeployJournal
{
    public sealed class Entry
    {
        /// <summary>"deploy" or "undeploy". Older journals without it were deploy-only.</summary>
        public string Kind { get; set; } = "deploy";

        public string ModId { get; set; } = "";
        public string ModName { get; set; } = "";
        public string GameDir { get; set; } = "";
        public List<string> Targets { get; set; } = new();
        public Dictionary<string, string> Backups { get; set; } = new();

        /// <summary>Target path -> mod id whose file this one took over (undeploy only).</summary>
        public Dictionary<string, string> Displaced { get; set; } = new();

        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    }

    private static string JournalPath => Path.Combine(AppPaths.Root, "deploy-journal.json");

    public static void Begin(ModRecord mod, string gameDir, IEnumerable<string> targets,
        IReadOnlyDictionary<string, string> backups)
    {
        try
        {
            var entry = new Entry
            {
                ModId = mod.Id,
                ModName = mod.Name,
                GameDir = gameDir,
                Targets = targets.ToList(),
                Backups = backups.ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            Directory.CreateDirectory(AppPaths.Root);
            FileSafe.WriteAllText(JournalPath, JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("写部署日志失败: " + ex.Message);
        }
    }

    /// <summary>
    /// Record an in-flight undeploy: which files this mod is about to remove and which originals
    /// it must put back. Undeploy used to be unjournaled, so a crash mid-way left the game with
    /// the mod files deleted and the original files still stranded in the backup store.
    /// </summary>
    public static void BeginUndeploy(ModRecord? mod, string modId, string gameDir, DeployedMod deployed)
    {
        try
        {
            var entry = new Entry
            {
                Kind = "undeploy",
                ModId = mod?.Id ?? modId,
                ModName = mod?.Name ?? modId,
                GameDir = gameDir,
                Targets = deployed.Files.ToList(),
                Backups = new Dictionary<string, string>(deployed.Backups),
                Displaced = new Dictionary<string, string>(deployed.Displaced),
            };
            Directory.CreateDirectory(AppPaths.Root);
            FileSafe.WriteAllText(JournalPath, JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("写部署日志失败: " + ex.Message);
        }
    }

    public static void Complete()
    {
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
        }
        catch (Exception ex)
        {
            Log.Warn("清除部署日志失败: " + ex.Message);
        }
    }

    /// <summary>Recover a half-finished deployment left behind by a crash.</summary>
    public static void Recover()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(JournalPath));
            if (entry is null || string.IsNullOrWhiteSpace(entry.ModId))
            {
                Complete();
                return;
            }

            if (string.Equals(entry.Kind, "undeploy", StringComparison.OrdinalIgnoreCase))
            {
                RecoverUndeploy(entry);
                Complete();
                return;
            }

            var state = AppServices.State;
            var existing = entry.Targets.Where(File.Exists).ToList();
            var missing = entry.Targets.Count - existing.Count;

            if (existing.Count > 0 && state.Library.Any(m => m.Id == entry.ModId))
            {
                if (!state.Deployed.Mods.TryGetValue(entry.ModId, out var deployed))
                {
                    deployed = new DeployedMod();
                    state.Deployed.Mods[entry.ModId] = deployed;
                }
                foreach (var target in existing)
                {
                    if (!deployed.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
                    {
                        deployed.Files.Add(target);
                    }
                }
                foreach (var (target, backup) in entry.Backups)
                {
                    deployed.Backups.TryAdd(target, backup);
                }
                state.SaveDeployed();
                Log.Warn($"检测到未完成的部署，已接管 {existing.Count} 个文件（{missing} 个未写入）: {entry.ModName}");
            }
            else if (existing.Count > 0)
            {
                // The mod record is gone; remove the orphaned files.
                foreach (var target in existing)
                {
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"清理残留文件失败 {target}: {ex.Message}");
                    }
                }
                Log.Warn($"检测到无主的部署残留，已清理 {existing.Count} 个文件: {entry.ModName}");
            }

            Complete();
        }
        catch (Exception ex)
        {
            Log.Warn("恢复部署日志失败: " + ex.Message);
            Complete();
        }
    }

    /// <summary>
    /// Finish an interrupted undeploy: delete the mod files that are still there, put the backed-up
    /// originals back, then apply the state-file update that never got written.
    /// </summary>
    private static void RecoverUndeploy(Entry entry)
    {
        var state = AppServices.State;
        var removed = 0;
        foreach (var target in entry.Targets ?? new List<string>())
        {
            try
            {
                if (!File.Exists(target) || IsOwnedByOther(target, entry.ModId)) continue;
                File.Delete(target);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warn($"清理残留文件失败 {target}: {ex.Message}");
            }
        }

        var restored = 0;
        foreach (var (target, backup) in entry.Backups ?? new Dictionary<string, string>())
        {
            try
            {
                if (!File.Exists(backup) || IsOwnedByOther(target, entry.ModId)) continue;
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(backup, target, overwrite: true);
                File.Delete(backup);
                restored++;
            }
            catch (Exception ex)
            {
                Log.Warn($"恢复游戏原文件失败 {target}: {ex.Message}");
            }
        }

        // deployed.json still lists this mod because SaveDeployed never ran: finish that too, and
        // give files back to the mod that owned them before this one took over.
        state.Deployed.Mods.Remove(entry.ModId);
        foreach (var (target, ownerId) in entry.Displaced ?? new Dictionary<string, string>())
        {
            if (state.Deployed.Mods.TryGetValue(ownerId, out var owner) && File.Exists(target) &&
                !owner.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                owner.Files.Add(target);
            }
        }
        state.SaveDeployed();

        Log.Warn($"检测到未完成的停用，已清理 {removed} 个文件、恢复 {restored} 个原文件: {entry.ModName}");
    }

    /// <summary>True when a mod other than <paramref name="modId"/> currently owns the target path.</summary>
    private static bool IsOwnedByOther(string target, string modId)
        => AppServices.State.Deployed.Mods.Any(kv =>
            !string.Equals(kv.Key, modId, StringComparison.OrdinalIgnoreCase) &&
            kv.Value.Files.Contains(target, StringComparer.OrdinalIgnoreCase));
}
