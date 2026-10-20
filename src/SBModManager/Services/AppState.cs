using System.Text.Json;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class AppState
{
    public static AppState Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppConfig Config { get; private set; } = new();
    public DeployState Deployed { get; private set; } = new();
    public List<ModRecord> Library { get; private set; } = new();

    public string? GameDir
        => GameLocator.IsValidGameDir(Config.GameDir) ? Config.GameDir : null;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), JsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取配置失败，使用默认配置: " + ex.Message);
        }

        try
        {
            if (File.Exists(AppPaths.StateFile))
            {
                Deployed = JsonSerializer.Deserialize<DeployState>(File.ReadAllText(AppPaths.StateFile), JsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取部署状态失败: " + ex.Message);
        }

        if (Config.AutoDetectGame && GameDir is null)
        {
            var found = GameLocator.AutoDetect();
            if (found is not null)
            {
                Config.GameDir = found;
                SaveConfig();
            }
        }

        ReloadLibrary();
        Log.Info("数据目录: " + AppPaths.Root + (AppPaths.RootResolutionLog.Count > 1 ? " (候选: " + string.Join("; ", AppPaths.RootResolutionLog) + ")" : ""));
        Log.Info("剑星 MOD 管理器 1.0.0 | 作者: xiangfa-test | 辅助开发: DeepSeek (DS)");
        Log.Info($"启动完成。游戏目录={GameDir ?? "(未设置)"}，库内模组={Library.Count}，已启用={Deployed.Mods.Count}");
    }

    public void ReloadLibrary() => Library = ModStore.LoadAll();

    public void SaveConfig()
    {
        try
        {
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(Config, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败", ex);
        }
    }

    public void SaveDeployed()
    {
        try
        {
            File.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(Deployed, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("保存部署状态失败", ex);
        }
    }

    public bool IsEnabled(string modId) => Deployed.Mods.ContainsKey(modId);

    public string PaksModsDir => GameDir is null ? "" : Deployer.PaksModsDir(GameDir);

    public string LibraryPath => AppPaths.ModsRoot;
}
