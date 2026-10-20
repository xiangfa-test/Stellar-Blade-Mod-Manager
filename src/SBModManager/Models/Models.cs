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
        _ => "未知",
    };
}

public sealed class AppConfig
{
    public string? GameDir { get; set; }
    public string? LibraryDir { get; set; }
    public bool UseHardLinks { get; set; } = true;
    public bool AutoDetectGame { get; set; } = true;
    public string LaunchMode { get; set; } = "steam";
    public bool ConfirmOverwrite { get; set; } = true;
    public string Theme { get; set; } = "system";
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
