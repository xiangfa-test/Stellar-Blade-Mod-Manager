using System.Text;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

public sealed class AssetConflict
{
    /// <summary>Normalized asset path inside the pak (e.g. SB/Content/.../Foo.uasset).</summary>
    public string Asset { get; init; } = "";
    /// <summary>Owner names ordered by load order; the last one wins.</summary>
    public List<string> Owners { get; init; } = new();
    /// <summary>Owner mod ids, same order as Owners.</summary>
    public List<string> OwnerIds { get; init; } = new();
    public string Winner => Owners.Count == 0 ? "" : Owners[^1];
    public string WinnerId => OwnerIds.Count == 0 ? "" : OwnerIds[^1];
    /// <summary>Number of mods that lose this asset (all but the winner).</summary>
    public int Losers => Math.Max(0, Owners.Count - 1);
}

/// <summary>
/// Best-effort resource-level inspection: UE5 IoStore keeps asset names as plain strings in
/// the .utoc, so we can list asset paths without fully parsing the TOC. Directory entries and
/// file names are separate strings; we pair them so same-named files in different folders do
/// not show up as false conflicts.
/// </summary>
public static class PakInspector
{
    private static readonly Regex AssetFilePattern = new(
        @"\.(?:uasset|uexp|ubulk|umap)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> GetAssetNames(ModRecord mod)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in mod.Files)
        {
            if (!rel.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(mod.ModDir, rel);
            if (!File.Exists(path)) continue;
            foreach (var asset in ExtractAssetPaths(path))
            {
                names.Add(asset);
            }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> ExtractAssetPaths(string utocPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bytes = File.ReadAllBytes(utocPath);
            var text = Encoding.ASCII.GetString(bytes);
            var directory = "";
            var pending = new List<string>();

            foreach (var raw in text.Split((char)0))
            {
                var token = raw.Trim('\r', '\n', ' ');
                if (token.Length < 3) continue;

                if (token.EndsWith('/') || token.EndsWith('\\'))
                {
                    Flush(directory, pending, result);
                    directory = Normalize(token);
                    continue;
                }

                if (AssetFilePattern.IsMatch(token))
                {
                    pending.Add(token);
                }
            }

            Flush(directory, pending, result);

            // Fallback: some TOCs pack strings without NUL separators; scan the raw text for
            // asset file names so we still catch overlaps (bare names only).
            foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9_\-]+\.(?:uasset|uexp|ubulk|umap)"))
            {
                var name = match.Value;
                if (AssetFilePattern.IsMatch(name)) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 .utoc 资源清单失败: " + ex.Message);
        }

        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Flush(string directory, List<string> pending, HashSet<string> result)
    {
        foreach (var file in pending)
        {
            var name = file.Replace('\\', '/');
            if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
            var combined = directory.Length == 0 ? name : directory.TrimEnd('/') + "/" + name;
            result.Add(combined);
        }
        pending.Clear();
    }

    private static string Normalize(string token)
    {
        var value = token.Replace('\\', '/').Trim();
        while (value.StartsWith("../", StringComparison.Ordinal)) value = value[3..];
        return value;
    }

    public static List<AssetConflict> FindAssetConflicts(IEnumerable<ModRecord> mods)
    {
        var ordered = mods.OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, (List<string> Names, List<string> Ids)>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in ordered)
        {
            foreach (var asset in GetAssetNames(mod))
            {
                if (!map.TryGetValue(asset, out var owners))
                {
                    owners = (new List<string>(), new List<string>());
                    map[asset] = owners;
                }
                if (!owners.Ids.Contains(mod.Id, StringComparer.OrdinalIgnoreCase))
                {
                    owners.Names.Add(mod.Name);
                    owners.Ids.Add(mod.Id);
                }
            }
        }

        return map
            .Where(kv => kv.Value.Ids.Count > 1)
            .Select(kv => new AssetConflict
            {
                Asset = kv.Key,
                Owners = kv.Value.Names,
                OwnerIds = kv.Value.Ids,
            })
            .OrderByDescending(c => c.Owners.Count)
            .ThenBy(c => c.Asset, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Dictionary<string, List<string>> FindConflicts(IEnumerable<ModRecord> mods)
        => FindAssetConflicts(mods).ToDictionary(c => c.Asset, c => c.Owners, StringComparer.OrdinalIgnoreCase);
}
