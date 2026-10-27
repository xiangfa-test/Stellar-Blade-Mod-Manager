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
    public static IAppState State { get; set; } = AppServices.State;

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

        if (!string.IsNullOrWhiteSpace(record.GroupKey))
        {
            foreach (var sibling in State.Library.Where(m =>
                         m.Id != record.Id && State.IsEnabled(m.Id) &&
                         string.Equals(m.GroupKey, record.GroupKey, StringComparison.OrdinalIgnoreCase)))
            {
                Deployer.Undeploy(sibling.Id);
            }
        }

        var result = Deployer.Deploy(record, gameDir, force: force);
        if (!result.Success && result.Conflicts.Count > 0)
        {
            return new EnableResult { Success = false, Conflicts = result.Conflicts, Error = "存在文件冲突。" };
        }

        return new EnableResult { Success = result.Success, Error = result.Error, Dependencies = dependencies };
    }

    public static void Disable(ModRecord record) => Deployer.Undeploy(record.Id);

    public static void Delete(ModRecord record)
    {
        Deployer.Undeploy(record.Id);
        ModStore.Delete(record);
        State.ReloadLibrary();
    }
}
