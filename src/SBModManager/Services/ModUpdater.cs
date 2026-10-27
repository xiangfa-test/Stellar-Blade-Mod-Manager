using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// In-place update of Nexus mods: download the newest main file and replace the existing library
/// entry (keeping id/tags/notes/priority and the enabled state) instead of adding a duplicate.
/// </summary>
public static class ModUpdater
{

    /// <summary>State used by this service; replaceable for tests.</summary>
    public static IAppState State { get; set; } = AppServices.State;

    private static readonly Regex GenericName = new(@"^(nxm-\d+-\d+|file \d+|未命名模组|\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsGenericName(string name)
        => string.IsNullOrWhiteSpace(name) || GenericName.IsMatch(name.Trim());

    /// <summary>Fill generic names (nxm-123-456) from the Nexus page title; also caches the thumbnail.</summary>
    public static async Task<int> RenameGenericAsync(IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var state = State;
        var key = state.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return -1;

        var renamed = 0;
        foreach (var record in state.Library.Where(r => r.NexusModId is > 0).ToList())
        {
            if (!IsGenericName(record.Name) && !string.IsNullOrWhiteSpace(record.NexusThumbnailUrl)) continue;

            var domain = string.IsNullOrWhiteSpace(record.NexusDomain)
                ? state.Config.NexusGameDomain
                : record.NexusDomain!;
            var info = await NexusService.GetModInfoAsync(domain, record.NexusModId!.Value, key, ct);
            if (info is not { } modInfo) continue;

            if (!string.IsNullOrWhiteSpace(modInfo.Name))
            {
                record.Name = modInfo.Name.Trim();
                record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
            }
            if (!string.IsNullOrWhiteSpace(modInfo.PictureUrl))
            {
                record.NexusThumbnailUrl = modInfo.PictureUrl;
            }
            if (!string.IsNullOrWhiteSpace(modInfo.Version) &&
                (string.IsNullOrWhiteSpace(record.Version) ||
                 record.Version.StartsWith("file ", StringComparison.OrdinalIgnoreCase)))
            {
                record.Version = modInfo.Version;
            }

            ModStore.Save(record);
            renamed++;
            progress?.Report("已补全 " + record.Name);
        }

        if (renamed > 0) state.ReloadLibrary();
        return renamed;
    }

    /// <summary>Replace fallback versions ("file 17084") with the Nexus page version.</summary>
    public static async Task<int> FillVersionsAsync(IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var state = State;
        var key = state.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return -1;

        var filled = 0;
        foreach (var record in state.Library.Where(r => r.NexusModId is > 0).ToList())
        {
            var needsFill = string.IsNullOrWhiteSpace(record.Version) ||
                            record.Version.StartsWith("file ", StringComparison.OrdinalIgnoreCase);
            if (!needsFill) continue;

            var domain = string.IsNullOrWhiteSpace(record.NexusDomain)
                ? state.Config.NexusGameDomain
                : record.NexusDomain!;
            var info = await NexusService.GetModInfoAsync(domain, record.NexusModId!.Value, key, ct);
            if (info is not { Version.Length: > 0 }) continue;

            record.Version = info.Value.Version;
            ModStore.Save(record);
            filled++;
            progress?.Report($"{record.Name} → {record.Version}");
        }

        if (filled > 0) state.ReloadLibrary();
        return filled;
    }

    /// <summary>Returns the newest main-category file, or null when unchanged/unavailable.</summary>
    public static async Task<NexusFileInfo?> GetLatestFileAsync(ModRecord record, string apiKey,
        string domain, CancellationToken ct = default)
    {
        var files = await NexusService.GetFilesAsync(domain, record.NexusModId!.Value, apiKey, ct);
        return files
            .Where(f => f.FileId > 0 && !string.IsNullOrWhiteSpace(f.Name))
            .OrderByDescending(f => f.UploadedTimestamp)
            .ThenByDescending(f => f.FileId)
            .FirstOrDefault();
    }

    /// <summary>Download and replace the record's files. Returns the updated record or throws.</summary>
    public static async Task<ModRecord> UpdateAsync(ModRecord record, string apiKey, string? apiKeyForLinks,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var state = State;
        if (state.GameDir is not { } gameDir) throw new InvalidOperationException("请先在设置里选择游戏目录。");
        if (record.NexusModId is not > 0) throw new InvalidOperationException("这个模组没有 Nexus 模组 ID。");

        var domain = string.IsNullOrWhiteSpace(record.NexusDomain) ? state.Config.NexusGameDomain : record.NexusDomain!;
        progress?.Report("查询最新文件…");
        var latest = await GetLatestFileAsync(record, apiKey, domain, ct)
                     ?? throw new InvalidOperationException("Nexus 没有返回可用的主文件。");

        progress?.Report("获取下载地址…");
        var uri = await NexusService.GetDownloadLinkAsync(domain, record.NexusModId.Value, latest.FileId,
            apiKeyForLinks ?? apiKey, null, null);
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("Nexus 没有返回下载地址（免费账号通常需要带授权 key 的链接，或先手动下载）。");
        }

        progress?.Report("下载 " + latest.Name + "…");
        Directory.CreateDirectory(AppPaths.TempRoot);
        var downloaded = Path.Combine(AppPaths.TempRoot, $"update-{record.Id}-{latest.FileId}.zip");
        try
        {
            await GitHubService.DownloadAsync(uri, downloaded, ct: ct);
            progress?.Report("导入新版本…");
            var imported = ModImporter.ImportArchive(downloaded);
            if (imported.Mod is null) throw new InvalidOperationException(imported.Error ?? "导入失败。");

            var replacement = imported.Mod;
            var wasEnabled = state.IsEnabled(record.Id);

            // Preserve user metadata; refresh identity/version/thumbnail from Nexus.
            replacement.Name = string.IsNullOrWhiteSpace(record.Name) ? replacement.Name : record.Name;
            replacement.Tags = record.Tags.ToList();
            replacement.Notes = record.Notes;
            replacement.Priority = record.Priority;
            replacement.Favorite = record.Favorite;
            replacement.NexusThumbnailUrl = record.NexusThumbnailUrl;
            replacement.NexusModId = record.NexusModId;
            replacement.NexusDomain = record.NexusDomain;
            replacement.NexusFileId = latest.FileId;
            if (!string.IsNullOrWhiteSpace(latest.Version)) replacement.Version = latest.Version;

            var pageInfo = await NexusService.GetModInfoAsync(domain, record.NexusModId.Value, apiKey, ct);
            if (pageInfo is { } info)
            {
                if (!string.IsNullOrWhiteSpace(info.Name)) replacement.Name = info.Name.Trim();
                if (!string.IsNullOrWhiteSpace(info.PictureUrl)) replacement.NexusThumbnailUrl = info.PictureUrl;
                if (!string.IsNullOrWhiteSpace(info.Version)) replacement.Version = info.Version;
            }
            replacement.GroupKey = ModImporter.GroupKeyFor(replacement.Name, replacement.NexusModId);

            if (wasEnabled) Deployer.Undeploy(record.Id);

            var oldDir = AppPaths.ModDir(record.Id);
            var newDir = AppPaths.ModDir(replacement.Id);
            try
            {
                if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true);
                Directory.CreateDirectory(oldDir);
                foreach (var file in Directory.EnumerateFiles(newDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(newDir, file);
                    var target = Path.Combine(oldDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                }
                Directory.Delete(newDir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn("替换模组目录失败: " + ex.Message);
                throw;
            }

            replacement.Id = record.Id;
            ModStore.Save(replacement);
            state.ReloadLibrary();

            if (wasEnabled)
            {
                progress?.Report("重新部署…");
                Deployer.Deploy(replacement, gameDir, force: true);
            }

            progress?.Report($"已更新「{replacement.Name}」到 {replacement.VersionLabel}");
            return replacement;
        }
        finally
        {
            try
            {
                if (File.Exists(downloaded)) File.Delete(downloaded);
            }
            catch
            {
                // ignore
            }
        }
    }
}
