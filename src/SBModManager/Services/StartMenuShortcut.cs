using System.Diagnostics;

namespace SBModManager.Services;

/// <summary>Creates or removes the Start Menu shortcut for the manager.</summary>
public static class StartMenuShortcut
{
    public const string ShortcutName = "剑星 MOD 管理器";

    public static string ProgramsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

    public static string PathFor(string name = ShortcutName)
        => Path.Combine(ProgramsDirectory, name + ".lnk");

    public static bool Exists(string name = ShortcutName) => File.Exists(PathFor(name));

    /// <summary>True when the current process runs with administrator rights.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool Create(string name = ShortcutName)
    {
        // Writing into a shared location can be blocked for standard users, so ask for
        // administrator rights first and let the elevated helper create the link.
        if (!IsElevated())
        {
            Log.Info("当前不是管理员，正在请求提权以创建开始菜单快捷方式");
            if (CreateElevated(name)) return true;
            Log.Warn("提权创建开始菜单快捷方式未完成（可能被取消）");
            return false;
        }

        try
        {
            var viaCom = CreateWithCom(name);
            if (viaCom) return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WScript.Shell 创建快捷方式失败，改用 PowerShell: " + ex.Message);
        }

        return CreateWithPowerShell(name);
    }

    /// <summary>Create the shortcut from an elevated PowerShell helper (triggers a UAC prompt).</summary>
    private static bool CreateElevated(string name)
    {
        try
        {
            var script = BuildScript(name);
            if (script is null) return false;

            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(info);
            if (process is null) return false;
            if (!process.WaitForExit(120000)) return false;

            var ok = File.Exists(PathFor(name));
            Log.Info(ok ? "已通过提权创建开始菜单快捷方式: " + PathFor(name)
                        : "提权创建开始菜单快捷方式失败（退出码 " + process.ExitCode + "）");
            return ok;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = the user cancelled the UAC prompt.
            Log.Warn("提权请求未获批准: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("提权创建快捷方式失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>PowerShell snippet that writes the shortcut; null when the exe path is unknown.</summary>
    private static string? BuildScript(string name)
    {
        var target = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(target)) return null;

        var link = PathFor(name);
        var workDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string Quote(string value) => value.Replace("'", "''");

        return "$ws=New-Object -ComObject WScript.Shell;" +
               $"$s=$ws.CreateShortcut('{Quote(link)}');" +
               $"$s.TargetPath='{Quote(target)}';" +
               $"$s.WorkingDirectory='{Quote(workDir)}';" +
               $"$s.IconLocation='{Quote(target)},0';" +
               "$s.Description='剑星 MOD 管理器';" +
               "$s.Save()";
    }

    private static bool CreateWithCom(string name)
    {
        try
        {
            Directory.CreateDirectory(ProgramsDirectory);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(PathFor(name));

            var target = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = Process.GetCurrentProcess().MainModule?.FileName;
            }
            if (string.IsNullOrWhiteSpace(target)) return false;

            link.TargetPath = target;
            link.WorkingDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            link.IconLocation = target + ",0";
            link.Description = "剑星 MOD 管理器 — 模组管理与 N 网浏览";
            link.Save();
            if (File.Exists(PathFor(name)))
            {
                Log.Info("已创建开始菜单快捷方式: " + PathFor(name));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("创建开始菜单快捷方式失败: " + ex);
            return false;
        }
    }

    /// <summary>Fallback for hosts where the WScript COM object cannot create the link.</summary>
    private static bool CreateWithPowerShell(string name)
    {
        var script = BuildScript(name);
        if (script is null) return false;

        try
        {
            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process is null) return false;
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }

            var ok = File.Exists(PathFor(name));
            if (!ok)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                Log.Warn("PowerShell 创建快捷方式失败（退出码 " + process.ExitCode + "）: " + error);
            }
            else
            {
                Log.Info("已通过 PowerShell 创建开始菜单快捷方式: " + PathFor(name));
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("PowerShell 创建快捷方式失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>Runs the shortcut script without elevation (used by the self-test).</summary>
    internal static bool CreateForTest(string name) => CreateWithPowerShell(name);

    public static bool Remove(string name = ShortcutName)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Info("已删除开始菜单快捷方式: " + path);
            }
            return !File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Warn("删除开始菜单快捷方式失败: " + ex.Message);
            return false;
        }
    }
}
