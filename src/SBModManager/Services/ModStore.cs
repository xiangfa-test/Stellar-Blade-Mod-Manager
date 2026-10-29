using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

public static class ModStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string MetaFileName = ".sbmm.json";

    public static List<ModRecord> LoadAll()
    {
        var result = new List<ModRecord>();
        try
        {
            Directory.CreateDirectory(AppPaths.ModsRoot);
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.ModsRoot))
            {
                var meta = Path.Combine(dir, MetaFileName);
                if (!File.Exists(meta)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<ModRecord>(File.ReadAllText(meta), JsonOptions);
                    if (record is not null)
                    {
                        result.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"读取模组元数据失败 {meta}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("扫描模组库失败", ex);
        }
        return result.OrderByDescending(r => r.ImportedAt).ToList();
    }

    public static void Save(ModRecord record)
    {
        Directory.CreateDirectory(record.ModDir);
        FileSafe.WriteAllText(Path.Combine(record.ModDir, MetaFileName),
            JsonSerializer.Serialize(record, JsonOptions));
    }

    /// <summary>Remove a mod from the library (files + metadata). Caller must undeploy first.</summary>
    public static void Delete(ModRecord record)
    {
        try
        {
            if (Directory.Exists(record.ModDir))
            {
                Directory.Delete(record.ModDir, recursive: true);
            }
            Log.Info($"已从库中删除模组: {record.Name}");
        }
        catch (Exception ex)
        {
            Log.Error("删除模组失败: " + record.Name, ex);
            throw;
        }
    }

    public static bool HasFilesOnDisk(ModRecord record)
        => record.Files.All(f => File.Exists(Path.Combine(record.ModDir, f)));
}
