using SBModManager.Models;
using SBModManager.Services;

namespace SBModManager.Ui;

/// <summary>
/// Localized display names for <see cref="ModType"/>. Lives in the UI layer so that
/// <c>Models</c> stays free of any dependency on services or the translation table.
/// </summary>
public static class ModTypeText
{
    public static string Of(ModType type) => Localization.T(type switch
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
    });
}
