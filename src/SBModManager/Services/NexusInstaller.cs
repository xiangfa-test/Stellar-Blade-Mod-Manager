using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>Downloads a Nexus file and imports it into the local library.</summary>
public static class NexusInstaller
{
    public static async Task<(ModRecord? Mod, string? Error)> InstallAsync(string domain, int modId,
        NexusModDetail? detail, string apiKey, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var files = detail?.Files is { Count: > 0 } known
                ? known
                : await NexusService.GetFilesAsync(domain, modId, apiKey, ct).ConfigureAwait(false);
            var main = files
                .Where(f => f.FileId > 0)
                .OrderByDescending(f => f.UploadedTimestamp)
                .FirstOrDefault();
            if (main is null) return (null, "该模组没有可下载的主文件。");

            progress?.Report("获取下载地址…");
            var uri = await NexusService.GetDownloadLinkAsync(domain, modId, main.FileId, apiKey)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(uri))
            {
                return (null, "Nexus 没有返回下载地址（免费账号可能受限），请改用网页下载。");
            }

            Directory.CreateDirectory(AppPaths.TempRoot);
            var temp = Path.Combine(AppPaths.TempRoot, $"nexus-{modId}-{main.FileId}.zip");
            progress?.Report("下载中…");
            await ResumableDownloader.DownloadAsync(uri, temp, null, ct).ConfigureAwait(false);

            progress?.Report("导入中…");
            var imported = await ModImporter.ImportArchiveAsync(temp, null).ConfigureAwait(false);
            if (imported.Mod is null)
            {
                return (null, imported.Error ?? "导入失败。");
            }

            var record = imported.Mod;
            record.NexusModId = modId;
            record.NexusFileId = main.FileId;
            record.NexusDomain = domain;
            if (!string.IsNullOrWhiteSpace(main.Version)) record.Version = main.Version;
            if (detail is not null)
            {
                record.Name = detail.Name;
                record.NexusThumbnailUrl = detail.PictureUrl;
                if (!string.IsNullOrWhiteSpace(detail.Version)) record.Version = detail.Version;
            }
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
            CollectionInstaller.TagAsLocal(record);
            ModStore.Save(record);
            AppServices.State.ReloadLibrary();

            try
            {
                File.Delete(temp);
            }
            catch
            {
                // ignore
            }

            progress?.Report("完成");
            return (record, null);
        }
        catch (Exception ex)
        {
            Log.Error("N 网下载导入失败", ex);
            return (null, ex.Message);
        }
    }
}
