using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SBModManager.Services;

public sealed class NxmLink
{
    public string Raw { get; init; } = "";
    public string Domain { get; init; } = "";
    public int ModId { get; init; }
    public int FileId { get; init; }
    public string? Key { get; init; }
    public string? Expires { get; init; }

    public string PageUrl => $"https://www.nexusmods.com/{Domain}/mods/{ModId}";
}

/// <summary>
/// Optional nxm:// protocol handoff. When enabled, Nexus' "Mod Manager Download"
/// button launches this app instead of Vortex. The previous handler is backed up
/// and can be restored from Settings.
/// </summary>
public static class NxmHandler
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string CommandKey = ProtocolKey + @"\shell\open\command";

    public static bool TryParse(string url, out NxmLink link)
    {
        link = new NxmLink();
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = Regex.Match(url,
            @"^nxm://(?<domain>[^/]+)/mods/(?<mod>\d+)/files/(?<file>\d+)(?:\?(?<query>.*))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var query = match.Groups["query"].Value;
        string? key = null, expires = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("key", StringComparison.OrdinalIgnoreCase)) key = Uri.UnescapeDataString(parts[1]);
            else if (parts[0].Equals("expires", StringComparison.OrdinalIgnoreCase)) expires = parts[1];
        }

        link = new NxmLink
        {
            Raw = url,
            Domain = match.Groups["domain"].Value,
            ModId = int.Parse(match.Groups["mod"].Value),
            FileId = int.Parse(match.Groups["file"].Value),
            Key = key,
            Expires = expires,
        };
        return true;
    }

    public static string? GetCurrentCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CommandKey);
        return key?.GetValue(null) as string;
    }

    public static bool IsRegisteredTo(string exePath)
    {
        var command = GetCurrentCommand();
        return !string.IsNullOrWhiteSpace(command) &&
               command.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Register this exe for nxm:// and return the previous command (to be persisted).</summary>
    public static string? Register(string exePath)
    {
        var previous = GetCurrentCommand();
        if (!string.IsNullOrWhiteSpace(previous) &&
            previous.Contains(exePath, StringComparison.OrdinalIgnoreCase))
        {
            previous = null; // already ours; nothing to back up
        }

        using (var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey, true))
        {
            protocol?.SetValue(null, "URL:NXM Protocol");
            protocol?.SetValue("URL Protocol", "");
        }
        using (var commandKey = Registry.CurrentUser.CreateSubKey(CommandKey, true))
        {
            commandKey?.SetValue(null, $"\"{exePath}\" --nxm \"%1\"");
        }

        Log.Info("已注册 nxm:// 链接处理程序");
        return previous;
    }

    public static void Unregister(string? previousCommand)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(previousCommand))
            {
                using var commandKey = Registry.CurrentUser.CreateSubKey(CommandKey, true);
                commandKey?.SetValue(null, previousCommand);
                Log.Info("已把 nxm:// 处理程序恢复给: " + previousCommand);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
                Log.Info("已移除 nxm:// 链接处理程序");
            }
        }
        catch (Exception ex)
        {
            Log.Error("恢复 nxm:// 处理程序失败", ex);
            throw;
        }
    }
}
