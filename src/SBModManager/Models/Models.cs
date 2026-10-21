using System.Text.Json.Serialization;

namespace SBModManager.Models;

public enum ModType
{
    Unknown = 0,
    Pak,
    Loose,
    Ue4ss,
    Ue4ssRuntime,
    IniTweak,
    /// <summary>Dekita CustomNanosuitSystem addon (.dekcns.json / .dekani.json).</summary>
    Cns,
    /// <summary>Archive that ships an SB/ tree and must be merged into the game root.</summary>
    GameRoot,
    /// <summary>Home-menu video replacement (.bk2 files -> SB\Content\Movies).</summary>
    Video,
    /// <summary>BPModLoader pak that lives in SB\Content\Paks\LogicMods.</summary>
    LogicMod,
}

public sealed class ModRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public ModType Type { get; set; }
    public string SourceName { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public long SizeBytes { get; set; }
    /// <summary>Folder name used below ~mods / ue4ss Mods for loose mods.</summary>
    public string FolderName { get; set; } = "";
    /// <summary>Files stored under the mod library folder, relative paths.</summary>
    public List<string> Files { get; set; } = new();
    public bool Favorite { get; set; }

    /// <summary>Normalized name used to group multiple installed versions of the same mod.</summary>
    public string GroupKey { get; set; } = "";

    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = "";

    /// <summary>True when the mod ships Custom Nanosuit System json (.dekcns.json / .dekani.json).</summary>
    public bool HasCnsContent { get; set; }

    /// <summary>Display names of the CNS outfits (from DisplayName in the json).</summary>
    public List<string> CnsFits { get; set; } = new();

    /// <summary>Characters the outfits target (Eve / Lily / Adam / Drone).</summary>
    public List<string> CnsCharacters { get; set; } = new();

    /// <summary>Outfit categories advertised by CNS (Dress, Body, ...).</summary>
    public List<string> CnsOutfitTypes { get; set; } = new();

    /// <summary>Requirements declared by the CNS json (anything other than "None").</summary>
    public List<string> CnsRequirements { get; set; } = new();

    /// <summary>Relative path (under the mod folder) of a preview image shipped with the mod.</summary>
    public string PreviewImage { get; set; } = "";

    /// <summary>Nexus mod page thumbnail (https URL), filled when the mod has a Nexus id.</summary>
    public string NexusThumbnailUrl { get; set; } = "";

    [JsonIgnore]
    public string NexusPageUrl => NexusModId is > 0
        ? $"https://www.nexusmods.com/{NexusDomain ?? "stellarblade"}/mods/{NexusModId}"
        : "";

    [JsonIgnore]
    public bool LooksCnsCompatible =>
        HasCnsContent || Name.Contains("CNS", StringComparison.OrdinalIgnoreCase) ||
        Files.Any(f => f.EndsWith(".dekcns.json", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".dekani.json", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string CnsSummary
    {
        get
        {
            if (!LooksCnsCompatible) return "";
            var parts = new List<string> { "CNS" };
            if (CnsCharacters.Count > 0) parts.Add(string.Join(" / ", CnsCharacters));
            if (CnsFits.Count > 0) parts.Add(string.Join(" / ", CnsFits.Take(3)) + (CnsFits.Count > 3 ? " …" : ""));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Load-order priority: lower values deploy first, higher values win file conflicts.</summary>
    public int Priority { get; set; }

    public int? NexusModId { get; set; }
    public int? NexusFileId { get; set; }
    public string? NexusDomain { get; set; }

    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }

    [JsonIgnore]
    public string ModDir => Services.AppPaths.ModDir(Id);

    [JsonIgnore]
    public string DisplayType => Type switch
    {
        ModType.Pak => "PAK 模组",
        ModType.Loose => "资源模组",
        ModType.Ue4ss => "UE4SS 模组",
        ModType.Ue4ssRuntime => "UE4SS 运行库",
        ModType.IniTweak => "INI 配置",
        ModType.Cns => "CNS 外观",
        ModType.GameRoot => "游戏根目录",
        ModType.Video => "主页视频",
        ModType.LogicMod => "逻辑模组",
        _ => "未知",
    };

    [JsonIgnore]
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "未标注版本" : "v" + Version.TrimStart('v', 'V');
}

public sealed class AppConfig
{
    public string? GameDir { get; set; }
    public string? LibraryDir { get; set; }
    public bool UseHardLinks { get; set; } = true;

    /// <summary>Prefix PAK files with the load-order number so UE mounts them in that order.</summary>
    public bool PakOrderPrefixEnabled { get; set; } = true;
    public bool AutoDetectGame { get; set; } = true;
    public string LaunchMode { get; set; } = "steam";
    public bool ConfirmOverwrite { get; set; } = true;
    public string Theme { get; set; } = "system";

    /// <summary>Nexus Mods personal API key (used for update checks and nxm:// downloads).</summary>
    public string? NexusApiKey { get; set; }

    public string NexusGameDomain { get; set; } = "stellarblade";

    /// <summary>Ask for administrator rights on startup (relaunches itself elevated).</summary>
    public bool ElevateOnStartup { get; set; } = true;

    /// <summary>Register this app as the handler for nxm:// links (replaces Vortex temporarily).</summary>
    public bool HandleNxmLinks { get; set; }

    /// <summary>Previous HKCU value of nxm\shell\open\command, kept so it can be restored.</summary>
    public string? PreviousNxmHandler { get; set; }

    /// <summary>Conflict count at which a mod is labelled as a high-conflict ("high debt") mod.</summary>
    public int HighConflictThreshold { get; set; } = 3;

    public string Ue4ssRepository { get; set; } = "Chrisr0/RE-UE4SS";

    /// <summary>Recently parsed Nexus collections (URLs or slugs), newest first.</summary>
    public List<string> RecentCollections { get; set; } = new();

    /// <summary>One-time migration: put pre-1.4.3 untagged mods into the local collection.</summary>
    public bool LocalCollectionMigrated { get; set; }

    /// <summary>Width of the mod details pane in the Mods page.</summary>
    public double ModsDetailWidth { get; set; } = 300;

    /// <summary>One-time pass that parses CNS outfit json for already imported mods.</summary>
    public bool CnsMetadataMigrated { get; set; }

    /// <summary>Check GitHub for a new app release when the app starts.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Remember the window size between runs (asked after the first manual resize).</summary>
    public bool RememberWindowSize { get; set; } = true;
    public bool WindowSizePromptDone { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>One-time migration for packs that should merge into the game root.</summary>
    public bool GameRootOverlayMigrated { get; set; }

    /// <summary>Second pass: packs recorded without the SB\ prefix (Binaries/Content at root).</summary>
    public bool GameRootOverlayMigratedV2 { get; set; }
}

public sealed class DeployState
{
    public Dictionary<string, DeployedMod> Mods { get; set; } = new();
}

public sealed class DeployedMod
{
    public List<string> Files { get; set; } = new();
    public DateTimeOffset DeployedAt { get; set; } = DateTimeOffset.Now;
    public bool HardLinked { get; set; }

    /// <summary>Target path -> backup path for game files this mod overwrote (restored on undeploy).</summary>
    public Dictionary<string, string> Backups { get; set; } = new();

    /// <summary>Target path -> other mod id whose file this mod replaced (re-deployed when this mod is disabled).</summary>
    public Dictionary<string, string> Displaced { get; set; } = new();

    /// <summary>Target path -> library source of the displaced file, so restore works even after a restart.</summary>
    public Dictionary<string, string> DisplacedSources { get; set; } = new();

    /// <summary>Target path -> library source for every deployed file (used when restoring takeovers).</summary>
    public Dictionary<string, string> Sources { get; set; } = new();
}

public sealed record DeployFile(string SourceRelPath, string TargetRelPath);

public sealed class ModConflict
{
    public required string TargetPath { get; init; }
    public required string ModName { get; init; }
    public required string OtherModName { get; init; }
}

public sealed class ImportResult
{
    public ModRecord? Mod { get; init; }
    public string? Error { get; init; }
    public bool IsExternal { get; init; }
}

/// <summary>Flattened conflict report used by the conflict-chain visualisation.</summary>
public sealed class ConflictReport
{
    public List<ConflictChain> Chains { get; init; } = new();
    public List<ModConflictStat> HighConflictMods { get; init; } = new();
    public int ConflictFileCount => Chains.Count;
}

public sealed class ConflictChain
{
    public required string TargetPath { get; init; }
    /// <summary>Mod names ordered by load priority (last one wins).</summary>
    public List<string> Mods { get; init; } = new();
    public string DisplayPath => TargetPath;
    public string ChainText => string.Join("  →  ", Mods);
}

public sealed class ModConflictStat
{
    public required string ModId { get; init; }
    public required string ModName { get; init; }
    public int ConflictCount { get; init; }
    public bool IsHigh { get; init; }
}

public sealed class UpdateInfo
{
    public required string ModId { get; init; }
    public required string ModName { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public int? LatestFileId { get; init; }
    public bool HasUpdate { get; init; }
    public string? Error { get; init; }
    public string? PageUrl { get; init; }
}
