using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class ProfileEntry
{
    public string ModId { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; }
}

public sealed class ModProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<ProfileEntry> Entries { get; set; } = new();
}

/// <summary>Named snapshots of "which mods are enabled + their load order".</summary>
public static class ProfileStore
{

    /// <summary>State used by this service; replaceable for tests.</summary>
    public static IAppState State { get; set; } = AppState.Instance;

    private static string DirectoryPath => Path.Combine(AppPaths.Root, "profiles");

    public static List<ModProfile> LoadAll()
    {
        var result = new List<ModProfile>();
        try
        {
            if (!Directory.Exists(DirectoryPath)) return result;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<ModProfile>(File.ReadAllText(file));
                    if (profile is not null && !string.IsNullOrWhiteSpace(profile.Name)) result.Add(profile);
                }
                catch (Exception ex)
                {
                    Log.Warn($"读取方案失败 {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取方案目录失败: " + ex.Message);
        }
        return result.OrderByDescending(p => p.CreatedAt).ToList();
    }

    public static void Save(ModProfile profile)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, profile.Id + ".json"),
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string id)
    {
        try
        {
            var path = Path.Combine(DirectoryPath, id + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("删除方案失败: " + ex.Message);
        }
    }

    public static ModProfile Capture(string name)
    {
        var state = State;
        return new ModProfile
        {
            Name = name,
            Entries = state.Library.Select(m => new ProfileEntry
            {
                ModId = m.Id,
                Priority = m.Priority,
                Enabled = state.IsEnabled(m.Id),
            }).ToList(),
        };
    }

    /// <summary>Switch the library to the profile: disable others first, then deploy in priority order.</summary>
    public static (int Enabled, int Failed) Apply(ModProfile profile, IProgress<string>? progress = null)
    {
        var state = State;
        if (state.GameDir is not { } gameDir)
        {
            return (-1, 0);
        }

        var entries = profile.Entries.ToDictionary(e => e.ModId, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in state.Library.ToList())
        {
            if (state.IsEnabled(mod.Id) && (!entries.TryGetValue(mod.Id, out var entry) || !entry.Enabled))
            {
                Deployer.Undeploy(mod.Id);
            }
        }

        foreach (var mod in state.Library)
        {
            if (!entries.TryGetValue(mod.Id, out var entry)) continue;
            if (mod.Priority != entry.Priority)
            {
                mod.Priority = entry.Priority;
                ModStore.Save(mod);
            }
        }
        state.ReloadLibrary();

        var enabled = 0;
        var failed = 0;
        foreach (var entry in profile.Entries.Where(e => e.Enabled).OrderBy(e => e.Priority))
        {
            var mod = state.Library.FirstOrDefault(m => m.Id == entry.ModId);
            if (mod is null) continue;
            progress?.Report("部署 " + mod.Name);
            if (state.IsEnabled(mod.Id)) { enabled++; continue; }
            var result = Deployer.Deploy(mod, gameDir, force: true);
            if (result.Success) enabled++;
            else failed++;
        }

        state.ReloadLibrary();
        return (enabled, failed);
    }
}
