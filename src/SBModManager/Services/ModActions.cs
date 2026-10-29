using SBModManager.Models;

namespace SBModManager.Services;

public sealed class EnableResult
{
    public bool Success { get; init; }
    public List<ModConflict> Conflicts { get; init; } = new();
    public string? Error { get; init; }
    public List<DependencyIssue> Dependencies { get; init; } = new();
}

/// <summary>
/// Non-UI mod actions: import, enable/disable, delete. The page owns dialogs; this service owns
/// the state transitions so they can be exercised without a window.
/// </summary>
public static class ModActions
{
    private static IAppState State => AppServices.State;

    public static ImportResult Import(string path)
    {
        var result = ModImporter.ImportArchive(path);
        if (result.Mod is not null)
        {
            CollectionInstaller.TagAsLocal(result.Mod);
            State.ReloadLibrary();
        }
        return result;
    }

    public static async Task<ImportResult> ImportAsync(string path,
        Func<FomodConfig, Task<IReadOnlyList<FomodPlugin>?>>? chooser)
    {
        var result = await ModImporter.ImportArchiveAsync(path, chooser).ConfigureAwait(false);
        if (result.Mod is not null)
        {
            CollectionInstaller.TagAsLocal(result.Mod);
            State.ReloadLibrary();
        }
        return result;
    }

    /// <summary>Enable a mod: dependency check, disable sibling versions, deploy.</summary>
    public static EnableResult Enable(ModRecord record, bool force = false)
    {
        var gameDir = State.GameDir;
        if (gameDir is null)
        {
            return new EnableResult { Success = false, Error = "尚未设置游戏目录。" };
        }

        var dependencies = DependencyChecker.Check(record);
        if (dependencies.Any(d => d.Blocking) && !force)
        {
            return new EnableResult { Success = false, Dependencies = dependencies, Error = "缺少必要依赖。" };
        }

        // Versions of the same mod are meant to replace each other, so their files never count as
        // conflicts; conflicts against *other* mods or the game itself block the enable before any
        // sibling is touched. That way a rejected enable never leaves the library with no version.
        var siblings = string.IsNullOrWhiteSpace(record.GroupKey)
            ? new List<ModRecord>()
            : State.Library.Where(m =>
                m.Id != record.Id && State.IsEnabled(m.Id) &&
                string.Equals(m.GroupKey, record.GroupKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!force)
        {
            var conflicts = Deployer.CheckConflicts(record, gameDir, siblings.Select(m => m.Id));
            if (conflicts.Count > 0)
            {
                return new EnableResult { Success = false, Conflicts = conflicts, Error = "存在文件冲突。" };
            }
        }

        var disabledSiblings = new List<ModRecord>();
        foreach (var sibling in siblings)
        {
            Deployer.Undeploy(sibling.Id);
            disabledSiblings.Add(sibling);
        }

        var result = Deployer.Deploy(record, gameDir, force: force);
        if (!result.Success)
        {
            RestoreSiblings(disabledSiblings, gameDir);
            return new EnableResult
            {
                Success = false,
                Conflicts = result.Conflicts,
                Error = result.Error,
                Dependencies = dependencies,
            };
        }

        return new EnableResult { Success = true, Dependencies = dependencies };
    }

    /// <summary>Put back the sibling versions that were disabled for a hand-over that then failed.</summary>
    private static void RestoreSiblings(List<ModRecord> siblings, string gameDir)
    {
        foreach (var sibling in siblings
                     .OrderBy(m => m.Priority)
                     .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var restored = Deployer.Deploy(sibling, gameDir, force: true);
            if (restored.Success)
            {
                Log.Info($"启用新版本失败，已恢复原版本: {sibling.Name}");
            }
            else
            {
                Log.Warn($"启用新版本失败，且原版本恢复失败: {sibling.Name} — {restored.Error}");
            }
        }
    }

    public static void Disable(ModRecord record) => Deployer.Undeploy(record.Id);

    public static void Delete(ModRecord record)
    {
        Deployer.Undeploy(record.Id);
        ModStore.Delete(record);
        State.ReloadLibrary();
    }
}
