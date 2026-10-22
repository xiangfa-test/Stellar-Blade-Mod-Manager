using System.Text;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class AssetConflict
{
    public string Asset { get; init; } = "";
    /// <summary>Owners ordered by load order; the last one wins.</summary>
    public List<string> Owners { get; init; } = new();
    public string Winner => Owners.Count == 0 ? "" : Owners[^1];
}

/// <summary>
/// Best-effort resource-level inspection: UE5 IoStore keeps asset names as plain strings in
/// the .utoc, so we can list asset file names without fully parsing the TOC. It is an
/// approximation (encrypted/compressed TOCs yield nothing) but catches same-asset overlap.
/// </summary>
public static class PakInspector
{
    private static readonly Regex AssetPattern = new(
        @"[A-Za-z0-9_\-./\\]+\.(?:uasset|uexp|ubulk|umap)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> GetAssetNames(ModRecord mod)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in mod.Files)
        {
            if (!rel.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(mod.ModDir, rel);
            if (!File.Exists(path)) continue;
            foreach (var asset in ExtractAssetNames(path))
            {
                names.Add(asset);
            }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> ExtractAssetNames(string utocPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bytes = File.ReadAllBytes(utocPath);
            var text = Encoding.ASCII.GetString(bytes);
            foreach (Match match in AssetPattern.Matches(text))
            {
                var name = match.Value.Replace('\\', '/');
                var slash = name.LastIndexOf('/');
                if (slash >= 0) name = name[(slash + 1)..];
                if (name.Length > 3) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 .utoc 资源清单失败: " + ex.Message);
        }
        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Map asset name -> mods that ship it, ordered by load order (last wins).</summary>
    public static Dictionary<string, List<string>> FindConflicts(IEnumerable<ModRecord> mods)
        => FindAssetConflicts(mods).ToDictionary(c => c.Asset, c => c.Owners, StringComparer.OrdinalIgnoreCase);

    public static List<AssetConflict> FindAssetConflicts(IEnumerable<ModRecord> mods)
    {
        var ordered = mods.OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in ordered)
        {
            foreach (var asset in GetAssetNames(mod))
            {
                if (!map.TryGetValue(asset, out var owners))
                {
                    owners = new List<string>();
                    map[asset] = owners;
                }
                if (!owners.Contains(mod.Name, StringComparer.OrdinalIgnoreCase))
                {
                    owners.Add(mod.Name);
                }
            }
        }

        return map
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new AssetConflict { Asset = kv.Key, Owners = kv.Value })
            .OrderByDescending(c => c.Owners.Count)
            .ThenBy(c => c.Asset, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Assets where a lower-priority mod is fully shadowed by a higher-priority one.</summary>
    public static List<AssetConflict> FindShadowed(IEnumerable<ModRecord> mods)
        => FindAssetConflicts(mods).Where(c => c.Owners.Count > 1).ToList();
}
