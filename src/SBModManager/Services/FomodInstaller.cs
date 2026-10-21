using System.Xml.Linq;

namespace SBModManager.Services;

public sealed class FomodFileEntry
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public bool IsFolder { get; init; }
}

public sealed class FomodPlugin
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<FomodFileEntry> Entries { get; init; } = new();
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
}

public sealed class FomodConfig
{
    public List<FomodStep> Steps { get; init; } = new();
    public bool HasChoices => Steps.Any(step => step.Groups.Any(g => g.Plugins.Count > 1));
}

/// <summary>
/// Minimal FOMOD support: reads fomod/ModuleConfig.xml, lets the caller pick options and copies
/// the selected file/folder mappings into a staging folder that the normal importer can handle.
/// Script installers / conditional flags are not executed.
/// </summary>
public static class FomodInstaller
{
    public static bool IsFomod(string directory)
    {
        return File.Exists(Path.Combine(directory, "fomod", "ModuleConfig.xml")) ||
               File.Exists(Path.Combine(directory, "FOMOD", "ModuleConfig.xml"));
    }

    public static FomodConfig? Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "fomod", "ModuleConfig.xml");
            if (!File.Exists(path)) path = Path.Combine(directory, "FOMOD", "ModuleConfig.xml");
            if (!File.Exists(path)) return null;

            var doc = XDocument.Load(path);
            var steps = new List<FomodStep>();
            foreach (var stepNode in doc.Descendants().Where(e => Local(e) == "installStep"))
            {
                var groups = new List<FomodGroup>();
                foreach (var groupNode in stepNode.Descendants().Where(e => Local(e) == "group"))
                {
                    var plugins = new List<FomodPlugin>();
                    foreach (var pluginNode in groupNode.Descendants().Where(e => Local(e) == "plugin"))
                    {
                        var entries = new List<FomodFileEntry>();
                        foreach (var node in pluginNode.Descendants())
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

                        plugins.Add(new FomodPlugin
                        {
                            Name = Attr(pluginNode, "name"),
                            Description = pluginNode.Descendants().FirstOrDefault(e => Local(e) == "description")?.Value.Trim() ?? "",
                            Entries = entries,
                        });
                    }

                    groups.Add(new FomodGroup
                    {
                        Name = Attr(groupNode, "name"),
                        Type = string.IsNullOrWhiteSpace(Attr(groupNode, "type")) ? "SelectExactlyOne" : Attr(groupNode, "type"),
                        Plugins = plugins,
                    });
                }

                steps.Add(new FomodStep { Name = Attr(stepNode, "name"), Groups = groups });
            }

            return new FomodConfig { Steps = steps };
        }
        catch (Exception ex)
        {
            Log.Warn("解析 FOMOD 配置失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>Copy the selected plugins' sources into <paramref name="staging"/>. Returns file count.</summary>
    public static int Apply(string sourceRoot, string staging, IEnumerable<FomodPlugin> selected)
    {
        var copied = 0;
        foreach (var plugin in selected)
        {
            foreach (var entry in plugin.Entries)
            {
                var source = Path.Combine(sourceRoot, entry.Source.Replace('/', '\\'));
                if (entry.IsFolder)
                {
                    if (!Directory.Exists(source)) continue;
                    copied += CopyFolderContents(source, staging);
                }
                else if (File.Exists(source))
                {
                    var destination = StagingPath(staging, entry, source);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                    copied++;
                }
            }
        }

        return copied;
    }

    private static int CopyFolderContents(string sourceFolder, string staging)
    {
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceFolder, file);
            var destination = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }
        return copied;
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

        // Game-specific FOMODs point at game folders; keep those paths.
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
