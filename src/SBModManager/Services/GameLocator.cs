using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SBModManager.Models;

namespace SBModManager.Services;

public static class GameLocator
{
    public const string SteamAppId = "3489700";
    public const string GameFolderName = "StellarBlade";

    public static bool IsValidGameDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return false;
        }
        return File.Exists(Path.Combine(dir, "SB.exe"))
               && Directory.Exists(Path.Combine(dir, "SB", "Content", "Paks"));
    }

    public static string? AutoDetect()
    {
        foreach (var lib in SteamLibraries())
        {
            var candidate = Path.Combine(lib, "steamapps", "common", GameFolderName);
            if (IsValidGameDir(candidate))
            {
                Log.Info($"自动检测到游戏目录: {candidate}");
                return candidate;
            }
        }

        var commonRoots = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common",
            @"C:\Program Files\Steam\steamapps\common",
            @"D:\SteamLibrary\steamapps\common",
            @"E:\SteamLibrary\steamapps\common",
            @"F:\SteamLibrary\steamapps\common",
            @"D:\Steam\steamapps\common",
            @"E:\Steam\steamapps\common",
        };
        foreach (var root in commonRoots)
        {
            var candidate = Path.Combine(root, GameFolderName);
            if (IsValidGameDir(candidate))
            {
                Log.Info($"在常见路径找到游戏: {candidate}");
                return candidate;
            }
        }

        Log.Warn("未能自动检测到《剑星》游戏目录。");
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 Steam 注册表失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            steamPath = @"C:\Program Files (x86)\Steam";
        }

        var libs = new List<string>();
        if (Directory.Exists(steamPath))
        {
            libs.Add(steamPath);
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    {
                        var p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            libs.Add(p);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"解析 libraryfolders.vdf 失败: {ex.Message}");
                }
            }
        }
        return libs;
    }

    /// <summary>Try to read the installed build id from appmanifest_3489700.acf.</summary>
    public static string? ReadSteamBuildId(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        try
        {
            var steamapps = Directory.GetParent(gameDir.TrimEnd('\\'))?.Parent?.FullName;
            var acf = Path.Combine(steamapps ?? "", $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(acf)) return null;
            var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s*\"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsGameRunning()
    {
        foreach (var name in new[] { "SB", "SB-Win64-Shipping" })
        {
            if (Process.GetProcessesByName(name).Length > 0) return true;
        }
        return false;
    }

    public static void Launch(string? gameDir, string mode)
    {
        if (mode == "steam")
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            return;
        }
        if (!IsValidGameDir(gameDir))
        {
            throw new InvalidOperationException("游戏目录无效，无法直接启动。");
        }
        Process.Start(new ProcessStartInfo(Path.Combine(gameDir!, "SB.exe"))
        {
            WorkingDirectory = gameDir!,
            UseShellExecute = true,
        });
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开目录失败: " + path, ex);
        }
    }
}
