using SBModManager.Models;

namespace SBModManager.Services;

public sealed record CollectionInstallOptions(bool IncludeOptional, bool EnableAfter, bool SkipInstalled);

public sealed record CollectionInstallProgress(int Done, int Total, string Current, string Status);

public sealed class CollectionInstallReport
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> ImportedIds { get; } = new();
}

/// <summary>
/// Downloads and imports the mod files of a Nexus collection. Shared by the Mods page
/// quick-import dialog and the dedicated Collections page.
/// </summary>
public static class CollectionInstaller
{
    /// <summary>Tag given to mods the user imported manually, so they can be switched back to.</summary>
    public const string LocalTag = "合集:本地安装";

    public static string TagFor(NexusCollection collection) => "合集:" + collection.Name;

    public static bool IsCollectionTag(string tag)
        => tag.StartsWith("合集:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enable every mod of one collection, optionally disabling mods from other collections first.
    /// This is the "switch back to my own mods" operation.
    /// </summary>
    public static (int Enabled, int Failed, List<string> Errors) Activate(string tag, bool exclusive)
    {
        var state = AppState.Instance;
        var errors = new List<string>();
        if (state.GameDir is not { } gameDir)
        {
            errors.Add("还没有设置游戏目录。");
            return (0, 0, errors);
        }

        var target = state.Library
            .Where(r => r.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (exclusive)
        {
            foreach (var other in state.Library)
            {
                if (other.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) continue;
                if (!other.Tags.Any(IsCollectionTag)) continue;
                if (state.IsEnabled(other.Id))
                {
                    Deployer.Undeploy(other.Id);
                }
            }
        }

        var enabled = 0;
        foreach (var record in target)
        {
            if (state.IsEnabled(record.Id))
            {
                enabled++;
                continue;
            }
            var result = Deployer.Deploy(record, gameDir, force: false);
            if (result.Success)
            {
                enabled++;
            }
            else
            {
                errors.Add($"{record.Name}: {result.Error ?? "文件冲突"}");
            }
        }

        state.ReloadLibrary();
        return (enabled, errors.Count, errors);
    }

    public static (int Disabled, List<string> Errors) Deactivate(string tag)
    {
        var state = AppState.Instance;
        var errors = new List<string>();
        var disabled = 0;
        foreach (var record in state.Library
                     .Where(r => r.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            if (!state.IsEnabled(record.Id)) continue;
            try
            {
                Deployer.Undeploy(record.Id);
                disabled++;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.Name}: {ex.Message}");
            }
        }
        state.ReloadLibrary();
        return (disabled, errors);
    }

    /// <summary>
    /// One-time migration for libraries created before the local collection existed:
    /// mods without any collection tag become part of "本地安装".
    /// </summary>
    public static int MigrateUntaggedToLocal()
    {
        var migrated = 0;
        foreach (var record in AppState.Instance.Library)
        {
            if (record.Tags.Any(IsCollectionTag)) continue;
            record.Tags.Add(LocalTag);
            ModStore.Save(record);
            migrated++;
        }
        if (migrated > 0)
        {
            AppState.Instance.ReloadLibrary();
            Log.Info($"已把 {migrated} 个旧模组归入「{LocalTag}」合集");
        }
        return migrated;
    }

    /// <summary>Tag a manually imported mod as part of the local collection (unless it already has one).</summary>
    public static void TagAsLocal(ModRecord record, bool persist = true)
    {
        if (record.Tags.Any(IsCollectionTag)) return;
        record.Tags.Add(LocalTag);
        if (persist)
        {
            ModStore.Save(record);
        }
    }

    public static async Task<CollectionInstallReport> InstallAsync(
        NexusCollection collection,
        CollectionInstallOptions options,
        IProgress<CollectionInstallProgress>? progress,
        CancellationToken ct)
    {
        var state = AppState.Instance;
        var report = new CollectionInstallReport();
        var selected = collection.Mods
            .Where(m => options.IncludeOptional || !m.Optional)
            .ToList();
        if (selected.Count == 0)
        {
            return report;
        }

        var apiKey = state.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            report.Failed = selected.Count;
            report.Errors.Add("未配置 Nexus API Key（设置 → 网络与集成）。");
            return report;
        }

        var gameDir = state.GameDir;
        var enableAfter = options.EnableAfter && gameDir is not null;
        var tag = TagFor(collection);

        for (var i = 0; i < selected.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }

            var mod = selected[i];
            progress?.Report(new CollectionInstallProgress(i, selected.Count, mod.DisplayName, "准备下载…"));

            if (options.SkipInstalled &&
                state.Library.Any(r => r.NexusModId == mod.ModId && r.NexusFileId == mod.FileId))
            {
                report.Skipped++;
                continue;
            }

            string? temp = null;
            try
            {
                progress?.Report(new CollectionInstallProgress(i, selected.Count, mod.DisplayName, "获取下载地址…"));
                var uri = await NexusService.GetDownloadLinkAsync(
                    collection.GameDomain, mod.ModId, mod.FileId, apiKey, ct: ct);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    report.Failed++;
                    report.Errors.Add($"{mod.DisplayName}: Nexus 没有返回直链");
                    continue;
                }

                Directory.CreateDirectory(AppPaths.TempRoot);
                temp = Path.Combine(AppPaths.TempRoot, $"collection-{mod.ModId}-{mod.FileId}.zip");
                progress?.Report(new CollectionInstallProgress(i, selected.Count, mod.DisplayName, "下载中…"));
                await GitHubService.DownloadAsync(uri, temp, ct: ct);

                progress?.Report(new CollectionInstallProgress(i, selected.Count, mod.DisplayName, "解压并导入…"));
                var imported = ModImporter.ImportArchive(temp);
                if (imported.Mod is null)
                {
                    report.Failed++;
                    report.Errors.Add($"{mod.DisplayName}: {imported.Error}");
                    continue;
                }

                var record = imported.Mod;
                record.NexusModId = mod.ModId;
                record.NexusFileId = mod.FileId;
                record.NexusDomain = collection.GameDomain;
                if (!string.IsNullOrWhiteSpace(mod.Version))
                {
                    record.Version = mod.Version;
                }
                if (!record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    record.Tags.Add(tag);
                }
                record.Notes = $"来自 Nexus 合集「{collection.Name}」(rev {collection.RevisionNumber})" +
                               Environment.NewLine + collection.PageUrl;
                ModStore.Save(record);

                if (enableAfter)
                {
                    progress?.Report(new CollectionInstallProgress(i, selected.Count, mod.DisplayName, "部署中…"));
                    var deployed = Deployer.Deploy(record, gameDir!, force: false);
                    if (!deployed.Success)
                    {
                        report.Errors.Add($"{mod.DisplayName}: 已导入，但启用失败（{deployed.Error}）");
                    }
                }

                report.Imported++;
                report.ImportedIds.Add(record.Id);
            }
            catch (OperationCanceledException)
            {
                report.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Errors.Add($"{mod.DisplayName}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (temp is not null && File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                await Task.Delay(120, ct);
            }
            catch (OperationCanceledException)
            {
                report.Cancelled = true;
                break;
            }
        }

        state.ReloadLibrary();
        progress?.Report(new CollectionInstallProgress(
            report.Imported + report.Skipped + report.Failed, selected.Count, "", "完成"));
        return report;
    }

    /// <summary>Collection tags currently present in the library, with mod counts.</summary>
    public static List<(string Tag, string Name, int Count)> InstalledCollections()
    {
        return AppState.Instance.Library
            .SelectMany(r => r.Tags
                .Where(t => t.StartsWith("合集:", StringComparison.OrdinalIgnoreCase))
                .Select(t => (Tag: t, Mod: r)))
            .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Key["合集:".Length..], g.Count()))
            .OrderByDescending(x => x.Item3)
            .ToList();
    }
}
