using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Keeps UE4SS's mod list in sync. UE4SS 3.x decides what loads from
/// ue4ss/Mods/mods.json (mod_enabled), so deploying only files is not enough.
/// </summary>
public static class Ue4ssConfig
{
    private static readonly Regex ModFolderPattern = new(
        @"(?i)ue4ss\\Mods\\(?<name>[^\\]+)\\",
        RegexOptions.Compiled);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static void Apply(ModRecord record, string gameDir, bool enabled)
    {
        if (gameDir is null) return;
        // The UE4SS runtime ships its own mods.json with sane defaults; never rewrite it.
        if (record.Type == ModType.Ue4ssRuntime) return;

        var modsDir = Path.Combine(gameDir, "SB", "Binaries", "Win64", "ue4ss", "Mods");
        if (!Directory.Exists(modsDir))
        {
            Log.Warn("未找到 UE4SS Mods 目录，跳过 mods.json 同步: " + modsDir);
            return;
        }

        foreach (var name in ModNames(record))
        {
            SetEntry(modsDir, name, enabled);
        }
    }

    /// <summary>Used when the library record is gone but the deployment list still exists.</summary>
    public static void ApplyFromPaths(IEnumerable<string> deployedFiles, string gameDir, bool enabled)
    {
        var modsDir = Path.Combine(gameDir, "SB", "Binaries", "Win64", "ue4ss", "Mods");
        if (!Directory.Exists(modsDir)) return;

        foreach (var file in deployedFiles)
        {
            var match = ModFolderPattern.Match(file.Replace('/', '\\'));
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                SetEntry(modsDir, name, enabled);
            }
        }
    }

    /// <summary>Script mods use FolderName; game-root bundles carry ue4ss/Mods/&lt;name&gt;/ paths.</summary>
    public static IEnumerable<string> ModNames(ModRecord record)
    {
        var names = new List<string>();
        if (record.Type == ModType.Ue4ss && !string.IsNullOrWhiteSpace(record.FolderName))
        {
            names.Add(record.FolderName);
        }

        foreach (var file in record.Files)
        {
            var match = ModFolderPattern.Match(file.Replace('/', '\\'));
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void SetEntry(string modsDir, string modName, bool enabled)
    {
        var jsonPath = Path.Combine(modsDir, "mods.json");
        try
        {
            JsonArray array;
            if (File.Exists(jsonPath))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(jsonPath), documentOptions: JsonOptions);
                array = parsed as JsonArray ?? new JsonArray();
            }
            else
            {
                array = new JsonArray();
            }

            JsonObject? entry = null;
            foreach (var node in array)
            {
                if (node is JsonObject obj &&
                    string.Equals(obj["mod_name"]?.GetValue<string>(), modName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = obj;
                    break;
                }
            }

            if (entry is null)
            {
                if (!enabled)
                {
                    UpdateModsTxt(modsDir, modName, enabled: false);
                    return; // nothing to disable
                }
                entry = new JsonObject
                {
                    ["mod_name"] = modName,
                    ["mod_enabled"] = true,
                };
                array.Add(entry);
            }
            else
            {
                entry["mod_enabled"] = enabled;
            }

            var json = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (!File.Exists(jsonPath) || File.ReadAllText(jsonPath) != json)
            {
                FileSafe.WriteAllText(jsonPath, json);
            }
            Log.Info($"UE4SS mods.json: {modName} -> {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"更新 mods.json 失败 ({modName}): {ex.Message}");
        }

        UpdateModsTxt(modsDir, modName, enabled);
    }

    /// <summary>Older UE4SS builds read mods.txt ("Name : 1"); keep it consistent when present.</summary>
    private static void UpdateModsTxt(string modsDir, string modName, bool enabled)
    {
        var txtPath = Path.Combine(modsDir, "mods.txt");
        try
        {
            if (!File.Exists(txtPath)) return;

            var lines = File.ReadAllLines(txtPath).ToList();
            var flag = enabled ? "1" : "0";
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var parts = lines[i].Split(':', 2);
                if (parts.Length == 2 &&
                    parts[0].Trim().Equals(modName, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{modName} : {flag}";
                    found = true;
                    break;
                }
            }

            if (!found && enabled)
            {
                lines.Add($"{modName} : 1");
            }

            FileSafe.WriteAllLines(txtPath, lines);
        }
        catch (Exception ex)
        {
            Log.Warn($"更新 mods.txt 失败 ({modName}): {ex.Message}");
        }
    }
}
