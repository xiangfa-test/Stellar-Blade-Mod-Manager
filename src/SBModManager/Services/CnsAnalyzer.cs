using System.Text.Json;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Reads Custom Nanosuit System json (.dekcns.json / .dekani.json) and extracts the outfit
/// names, target characters and outfit categories so CNS outfits can be told apart in the UI.
/// </summary>
public static class CnsAnalyzer
{

    /// <summary>Resolved live from the composition root, so tests can swap it via AppServices.WithState.</summary>
    private static IAppState State => AppServices.State;

    // CNS json files often contain trailing commas and comments.
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Regex CharacterPattern = new(
        @"(?i)\b(?<name>Eve|Lily|Adam|Drone|Kaya)\b", RegexOptions.Compiled);

    public static bool IsCnsJson(string rel)
        => rel.EndsWith(".dekcns.json", StringComparison.OrdinalIgnoreCase) ||
           rel.EndsWith(".dekani.json", StringComparison.OrdinalIgnoreCase);

    public static bool IsCnsAnimationJson(string rel)
        => rel.EndsWith(".dekani.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fill record.HasCnsContent / CnsFits / CnsCharacters / CnsOutfitTypes.</summary>
    public static void Analyze(ModRecord record)
    {
        var jsonFiles = record.Files.Where(IsCnsJson).Take(80).ToList();
        record.HasCnsContent = jsonFiles.Count > 0;

        var fits = new List<string>();
        var types = new List<string>();
        var requirements = new List<string>();
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in jsonFiles)
        {
            CollectFromText(rel, characters);
            var path = Path.Combine(record.ModDir, rel);
            if (!File.Exists(path))
            {
                Log.Warn("CNS json 不存在: " + path);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        ParseOutfit(item, fits, types, requirements, characters);
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ParseOutfit(doc.RootElement, fits, types, requirements, characters);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析 CNS json 失败: " + path + " — " + ex.Message);
            }
        }

        record.CnsFits = fits
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        record.CnsOutfitTypes = types
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        record.CnsRequirements = requirements
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        record.CnsCharacters = characters
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ParseOutfit(JsonElement item, List<string> fits, List<string> types,
        List<string> requirements, HashSet<string> characters)
    {
        var display = GetString(item, "DisplayName");
        var description = GetString(item, "Description");
        var fitId = GetString(item, "UniqueFitID");
        var meshType = GetString(item, "FitMeshType");

        var requirement = GetString(item, "Requirement");
        if (!string.IsNullOrWhiteSpace(requirement) &&
            !requirement.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !requirement.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            requirements.Add(requirement.Trim());
        }

        if (!string.IsNullOrWhiteSpace(display)) fits.Add(display.Trim());
        if (!string.IsNullOrWhiteSpace(meshType)) types.Add(meshType.Trim());

        if (item.TryGetProperty("OutfitTypes", out var outfitTypes) &&
            outfitTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in outfitTypes.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) types.Add(text);
                }
            }
        }

        var paths = "";
        if (item.TryGetProperty("OutfitPaths", out var outfitPaths) &&
            outfitPaths.ValueKind == JsonValueKind.Array)
        {
            paths = string.Join(" ", outfitPaths.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()));
        }

        CollectFromText(string.Join(" ", fitId, display, description, paths), characters);
    }

    private static void CollectFromText(string text, HashSet<string> characters)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (Match match in CharacterPattern.Matches(text))
        {
            characters.Add(Canonical(match.Groups["name"].Value));
        }
    }

    private static string Canonical(string name) => name.ToLowerInvariant() switch
    {
        "eve" => "Eve",
        "lily" => "Lily",
        "adam" => "Adam",
        "drone" => "Drone",
        "kaya" => "Kaya",
        _ => name,
    };

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>One-time pass for mods imported before CNS metadata existed.</summary>
    public static int MigrateMetadata()
    {
        var state = State;
        var updated = 0;
        foreach (var record in state.Library)
        {
            if (record.Files.Count == 0) continue;
            if (!record.Files.Any(IsCnsJson)) continue;
            if (record.HasCnsContent && record.CnsFits.Count > 0) continue;

            Analyze(record);
            ModStore.Save(record);
            updated++;
        }

        if (updated > 0)
        {
            state.ReloadLibrary();
            Log.Info($"已解析 {updated} 个 CNS 模组的服装信息");
        }
        return updated;
    }
}
