using System.Xml.Linq;

namespace SBModManager.Services;

public sealed class FomodFileEntry
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public bool IsFolder { get; init; }
}

public sealed class FomodFlag
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class FomodPlugin
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public List<FomodFileEntry> Entries { get; init; } = new();
    public List<FomodFlag> Flags { get; init; } = new();
}

public sealed class FomodGroup
{
    public string Name { get; init; } = "";
    /// <summary>SelectExactlyOne / SelectAtMostOne / SelectAny / SelectAll.</summary>
    public string Type { get; init; } = "SelectExactlyOne";
    public List<FomodPlugin> Plugins { get; init; } = new();
}

public sealed class FomodStep
{
    public string Name { get; init; } = "";
    public List<FomodGroup> Groups { get; init; } = new();
    /// <summary>Step is only shown when these conditions hold.</summary>
    public List<FomodDependency> Visible { get; init; } = new();
}

public sealed class FomodDependency
{
    /// <summary>flag / file / game.</summary>
    public string Kind { get; init; } = "flag";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public bool Negate { get; init; }
}

public sealed class FomodPattern
{
    public List<FomodDependency> Dependencies { get; init; } = new();
    public List<FomodFileEntry> Entries { get; init; } = new();
}

public sealed class FomodConfig
{
    public string ModuleName { get; init; } = "";
    public List<FomodFileEntry> Required { get; init; } = new();
    public List<FomodStep> Steps { get; init; } = new();
    public List<FomodPattern> Conditional { get; init; } = new();

    public bool HasChoices => Steps.Any(step => step.Groups.Any(g => g.Plugins.Count > 1)) ||
                              Conditional.Count > 0;
}

/// <summary>
/// FOMOD installer: supports required files, option groups, condition flags, step visibility and
/// conditional file installs. Script installers (script-extender) are not executed.
/// </summary>
public static class FomodInstaller
{
    public static bool IsFomod(string directory)
        => File.Exists(Path.Combine(directory, "fomod", "ModuleConfig.xml")) ||
           File.Exists(Path.Combine(directory, "FOMOD", "ModuleConfig.xml"));

    public static FomodConfig? Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "fomod", "ModuleConfig.xml");
            if (!File.Exists(path)) path = Path.Combine(directory, "FOMOD", "ModuleConfig.xml");
            if (!File.Exists(path)) return null;

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return null;

            var steps = new List<FomodStep>();
            foreach (var stepNode in root.Descendants().Where(e => Local(e) == "installStep"))
            {
                var groups = new List<FomodGroup>();
                foreach (var groupNode in stepNode.Descendants().Where(e => Local(e) == "group"))
                {
                    var plugins = groupNode.Descendants().Where(e => Local(e) == "plugin")
                        .Select(ParsePlugin)
                        .ToList();
                    groups.Add(new FomodGroup
                    {
                        Name = Attr(groupNode, "name"),
                        Type = string.IsNullOrWhiteSpace(Attr(groupNode, "type"))
                            ? "SelectExactlyOne"
                            : Attr(groupNode, "type"),
                        Plugins = plugins,
                    });
                }

                steps.Add(new FomodStep
                {
                    Name = Attr(stepNode, "name"),
                    Groups = groups,
                    Visible = ParseDependencies(stepNode, "visible"),
                });
            }

            var required = new List<FomodFileEntry>();
            var requiredNode = root.Descendants().FirstOrDefault(e => Local(e) == "requiredInstallFiles");
            if (requiredNode is not null) required.AddRange(ParseEntries(requiredNode));

            var patterns = new List<FomodPattern>();
            var conditionalNode = root.Descendants().FirstOrDefault(e => Local(e) == "conditionalFileInstalls");
            if (conditionalNode is not null)
            {
                foreach (var patternNode in conditionalNode.Descendants().Where(e => Local(e) == "pattern"))
                {
                    patterns.Add(new FomodPattern
                    {
                        Dependencies = patternNode.Descendants()
                            .Where(e => Local(e) is "flagDependency" or "fileDependency" or "gameDependency")
                            .Select(ParseDependency)
                            .ToList(),
                        Entries = ParseEntries(patternNode),
                    });
                }
            }

            return new FomodConfig
            {
                ModuleName = root.Descendants().FirstOrDefault(e => Local(e) == "moduleName")?.Value.Trim() ?? "",
                Required = required,
                Steps = steps,
                Conditional = patterns,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("解析 FOMOD 配置失败: " + ex.Message);
            return null;
        }
    }

    private static FomodPlugin ParsePlugin(XElement pluginNode)
    {
        var flags = new List<FomodFlag>();
        foreach (var flagNode in pluginNode.Descendants().Where(e => Local(e) == "flag"))
        {
            var flagValue = Attr(flagNode, "value");
            if (string.IsNullOrWhiteSpace(flagValue)) flagValue = flagNode.Value.Trim();
            flags.Add(new FomodFlag
            {
                Name = Attr(flagNode, "name"),
                Value = flagValue,
            });
        }

        return new FomodPlugin
        {
            Name = Attr(pluginNode, "name"),
            Description = pluginNode.Descendants().FirstOrDefault(e => Local(e) == "description")?.Value.Trim() ?? "",
            Type = pluginNode.Descendants().FirstOrDefault(e => Local(e) == "typeDescriptor") is { } type
                ? Attr(type, "name")
                : "",
            Entries = ParseEntries(pluginNode),
            Flags = flags,
        };
    }

    private static List<FomodFileEntry> ParseEntries(XElement container)
    {
        var entries = new List<FomodFileEntry>();
        foreach (var node in container.Descendants())
        {
            if (Local(node) == "file")
            {
                entries.Add(new FomodFileEntry
                {
                    Source = Attr(node, "source"),
                    Destination = Attr(node, "destination"),
                    IsFolder = false,
                });
            }
            else if (Local(node) == "folder")
            {
                entries.Add(new FomodFileEntry
                {
                    Source = Attr(node, "source"),
                    Destination = Attr(node, "destination"),
                    IsFolder = true,
                });
            }
        }
        return entries;
    }

    private static List<FomodDependency> ParseDependencies(XElement container, string property)
    {
        var node = container.Descendants().FirstOrDefault(e => Local(e) == property);
        if (node is null) return new List<FomodDependency>();

        return node.Descendants()
            .Where(e => Local(e) is "flagDependency" or "fileDependency" or "gameDependency")
            .Select(ParseDependency)
            .ToList();
    }

    private static FomodDependency ParseDependency(XElement node)
    {
        var kind = Local(node) switch
        {
            "fileDependency" => "file",
            "gameDependency" => "game",
            _ => "flag",
        };

        return new FomodDependency
        {
            Kind = kind,
            Name = kind == "file" ? Attr(node, "file") : Attr(node, "flag"),
            Value = kind == "game" ? Attr(node, "version") : Attr(node, "value"),
            Negate = Attr(node, "value").Equals("Off", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Copy required files, selected options and matching conditional files into staging.</summary>
    public static int Apply(string sourceRoot, string staging, IEnumerable<FomodPlugin> selected,
        FomodConfig? config = null)
    {
        var chosen = selected.ToList();
        var copied = 0;

        if (config is not null)
        {
            foreach (var entry in config.Required)
            {
                copied += CopyEntry(sourceRoot, staging, entry);
            }
        }

        foreach (var plugin in chosen)
        {
            foreach (var entry in plugin.Entries)
            {
                copied += CopyEntry(sourceRoot, staging, entry);
            }
        }

        if (config is not null && config.Conditional.Count > 0)
        {
            var flags = chosen
                .SelectMany(p => p.Flags)
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

            foreach (var pattern in config.Conditional)
            {
                if (!Matches(pattern.Dependencies, flags, sourceRoot, staging)) continue;
                foreach (var entry in pattern.Entries)
                {
                    copied += CopyEntry(sourceRoot, staging, entry);
                }
            }
        }

        return copied;
    }

    private static bool Matches(IEnumerable<FomodDependency> dependencies, IReadOnlyDictionary<string, string> flags,
        string sourceRoot, string staging)
    {
        foreach (var dependency in dependencies)
        {
            var ok = dependency.Kind switch
            {
                "file" => File.Exists(Path.Combine(staging, dependency.Name.Replace('/', '\\'))) ||
                          File.Exists(Path.Combine(sourceRoot, dependency.Name.Replace('/', '\\'))),
                "game" => true, // game version checks are not meaningful for Stellar Blade FOMODs
                _ => flags.TryGetValue(dependency.Name, out var value) &&
                     (string.IsNullOrWhiteSpace(dependency.Value) ||
                      string.Equals(value, dependency.Value, StringComparison.OrdinalIgnoreCase)),
            };

            if (dependency.Negate) ok = !ok;
            if (!ok) return false;
        }
        return true;
    }

    private static int CopyEntry(string sourceRoot, string staging, FomodFileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Source)) return 0;

        var source = Path.Combine(sourceRoot, entry.Source.Replace('/', '\\'));
        if (entry.IsFolder)
        {
            if (!Directory.Exists(source)) return 0;
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var destination = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
            return copied;
        }

        if (!File.Exists(source)) return 0;
        var target = StagingPath(staging, entry, source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return 1;
    }

    private static string StagingPath(string staging, FomodFileEntry entry, string sourceFile)
    {
        var destination = entry.Destination.Replace('/', '\\').Trim('\\');
        if (string.IsNullOrWhiteSpace(destination) ||
            destination.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(".", StringComparison.Ordinal))
        {
            return Path.Combine(staging, Path.GetFileName(sourceFile));
        }

        if (destination.StartsWith("SB", StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith("Content", StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith("Binaries", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(staging, destination, Path.GetFileName(sourceFile));
        }

        return Path.Combine(staging, Path.GetFileName(sourceFile));
    }

    private static string Local(XElement element) => element.Name.LocalName;

    private static string Attr(XElement element, string name)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
}
