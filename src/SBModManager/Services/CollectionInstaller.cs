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
    public static string TagFor(NexusCollection collection) => "合集:" + collection.Name;

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
