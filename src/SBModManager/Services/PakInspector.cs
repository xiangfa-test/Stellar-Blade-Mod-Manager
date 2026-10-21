using System.Text;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

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

    /// <summary>Map asset name -> mods that ship it, for enabled mods.</summary>
    public static Dictionary<string, List<string>> FindConflicts(IEnumerable<ModRecord> mods)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var asset in GetAssetNames(mod))
            {
                if (!map.TryGetValue(asset, out var owners))
                {
                    owners = new List<string>();
                    map[asset] = owners;
                }
                owners.Add(mod.Name);
            }
        }

        return map
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
