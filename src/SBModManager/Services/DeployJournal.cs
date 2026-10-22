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
        public string ModId { get; set; } = "";
        public string ModName { get; set; } = "";
        public string GameDir { get; set; } = "";
        public List<string> Targets { get; set; } = new();
        public Dictionary<string, string> Backups { get; set; } = new();
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
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(entry,
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

            var state = AppState.Instance;
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
}
