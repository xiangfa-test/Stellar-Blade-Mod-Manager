using SBModManager.Models;

namespace SBModManager.Services;

public sealed class DependencyIssue
{
    public string ModName { get; init; } = "";
    public string Missing { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool Blocking { get; init; }

    public override string ToString() => $"「{ModName}」缺少 {Missing}";
}

/// <summary>
/// Checks what a mod needs before it can work: CNS base for CNS outfits, UE4SS for script mods,
/// and CNS json Requirement entries. Results are advisory but block obvious no-op installs.
/// </summary>
public static class DependencyChecker
{
    public static List<DependencyIssue> Check(ModRecord record)
    {
        var state = AppState.Instance;
        var issues = new List<DependencyIssue>();

        var needsCns = record.CnsFits.Count > 0 ||
                       record.CnsCharacters.Count > 0 ||
                       record.CnsOutfitTypes.Count > 0 ||
                       record.Files.Any(f => f.EndsWith(".dekcns.json", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".dekani.json", StringComparison.OrdinalIgnoreCase));

        if (needsCns && !HasCnsBase(state))
        {
            issues.Add(new DependencyIssue
            {
                ModName = record.Name,
                Missing = "Custom Nanosuit System 基础包",
                Hint = "在 N 网安装 CNS 基础包（含 ue4ss/Mods/DekCNS 与 ~mods/CustomNanosuitSystem）后重试。",
                Blocking = true,
            });
        }

        if (record.Type == ModType.Ue4ss && !Deployer.IsUe4ssInstalled(state.GameDir ?? ""))
        {
            issues.Add(new DependencyIssue
            {
                ModName = record.Name,
                Missing = "UE4SS 运行库",
                Hint = "到「运行环境」安装 UE4SS（ue4ss 文件夹 + dwmapi.dll）。",
                Blocking = true,
            });
        }

        foreach (var requirement in record.CnsRequirements)
        {
            if (string.IsNullOrWhiteSpace(requirement)) continue;
            if (requirement.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;

            if (requirement.Contains("CNS", StringComparison.OrdinalIgnoreCase) && !HasCnsBase(state))
            {
                issues.Add(new DependencyIssue
                {
                    ModName = record.Name,
                    Missing = "CNS 基础包（json Requirement: " + requirement + "）",
                    Hint = "先安装 Custom Nanosuit System 基础包。",
                    Blocking = true,
                });
                continue;
            }

            var satisfied = state.Library.Any(m =>
                !string.Equals(m.Id, record.Id, StringComparison.OrdinalIgnoreCase) &&
                m.Name.Contains(requirement, StringComparison.OrdinalIgnoreCase));
            if (!satisfied)
            {
                issues.Add(new DependencyIssue
                {
                    ModName = record.Name,
                    Missing = requirement,
                    Hint = "Nexus 页面的 Requirements 里通常有对应前置。",
                    Blocking = false,
                });
            }
        }

        return issues;
    }

    private static bool HasCnsBase(AppState state)
    {
        return state.Library.Any(m =>
            m.Files.Any(f => f.Contains(@"CustomNanosuitSystem", StringComparison.OrdinalIgnoreCase) ||
                             f.Contains(@"ue4ss\Mods\DekCNS", StringComparison.OrdinalIgnoreCase) ||
                             f.Contains(@"ue4ss/Mods/DekCNS", StringComparison.OrdinalIgnoreCase)));
    }
}
