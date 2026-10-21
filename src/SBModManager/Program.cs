using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SBModManager.Models;

namespace SBModManager;

/// <summary>
/// Custom entry point instead of the XAML-generated one so that startup failures
/// are recorded in crash.log (WinUI otherwise exits with a silent 0xC000027B).
/// Supports --selftest, --screenshot, --page, --data-dir, --nxm and --no-elevate.
/// </summary>
public static class Program
{
    private static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "crash.log");

    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("SBMM_TRACE") == "1"
            ? Path.Combine(AppContext.BaseDirectory, "startup-trace.log")
            : null;

    public static string? ScreenshotPath { get; private set; }

    public static string? SelfTestPath { get; private set; }

    public static string? PageTag { get; private set; }

    public static string? DataDirOverride { get; private set; }

    public static bool SelectFirst { get; private set; }

    public static bool NoElevate { get; private set; }

    private static bool ElevatedChild { get; set; }

    /// <summary>Nav tag selected through the real NavigationView path (diagnostics).</summary>
    public static string? NavTag { get; private set; }

    /// <summary>Collection slug passed with --collection (opens the Collections page).</summary>
    public static string? CollectionSlug { get; private set; }

    public static string? TakeCollectionSlug()
    {
        var value = CollectionSlug;
        CollectionSlug = null;
        return value;
    }

    /// <summary>nxm:// URL passed by the browser / Nexus "Mod Manager Download" button.</summary>
    public static string? NxmUrl { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length:
                    ScreenshotPath = args[++i];
                    break;
                case "--selftest" when i + 1 < args.Length:
                    SelfTestPath = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    PageTag = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    DataDirOverride = args[++i];
                    break;
                case "--nxm" when i + 1 < args.Length:
                    NxmUrl = args[++i];
                    break;
                case "--select-first":
                    SelectFirst = true;
                    break;
                case "--no-elevate":
                    NoElevate = true;
                    break;
                case "--nav" when i + 1 < args.Length:
                    NavTag = args[++i];
                    break;
                case "--collection" when i + 1 < args.Length:
                    CollectionSlug = args[++i];
                    break;
                case "--elevated-child":
                    ElevatedChild = true;
                    break;
            }
        }

        // Single instance first: an nxm:// link clicked while the app is open must be
        // forwarded to the running window instead of opening a second one.
        Services.SingleInstance.Acquire(ElevatedChild, args, NxmUrl);
        if (!Services.SingleInstance.IsFirstInstance)
        {
            return;
        }

        // The user asked for administrator rights by default (the game folder and some
        // security products block writes otherwise). Test modes never elevate.
        if (ScreenshotPath is null && SelfTestPath is null && !NoElevate && !IsElevated() && ShouldElevate())
        {
            Services.SingleInstance.Release();
            if (RelaunchElevated(args))
            {
                return;
            }
            // Elevation was cancelled: keep running non-elevated and reclaim the instance lock.
            Services.SingleInstance.Acquire(false, args, null);
            if (!Services.SingleInstance.IsFirstInstance)
            {
                return;
            }
        }

        try
        {
            Trace("main start");
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Trace("com wrappers ok");
            Application.Start(_ =>
            {
                try
                {
                    Trace("Application.Start callback");
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    Trace("App created");
                }
                catch (Exception ex)
                {
                    Trace("callback failed: " + ex);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            throw;
        }
    }

    private static bool ShouldElevate()
    {
        try
        {
            var configPath = Services.AppPaths.ConfigFile;
            if (!File.Exists(configPath)) return true;
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));
            return config?.ElevateOnStartup ?? true;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool RelaunchElevated(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            var arguments = string.Join(" ", args.Append("--elevated-child").Select(QuoteArgument));
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
            return true;
        }
        catch (Exception ex)
        {
            // User declined UAC or elevation is not available: continue without admin rights.
            Trace("elevation cancelled/failed: " + ex.Message);
            return false;
        }
    }

    private static string QuoteArgument(string argument)
        => argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;

    public static void Trace(string message)
    {
        if (TracePath is null)
        {
            return;
        }
        try
        {
            File.AppendAllText(TracePath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    public static void WriteCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Show a blocking error box; used when startup cannot continue.</summary>
    public static void ShowFatalError(string title, string message)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, message, title, 0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
            // ignore
        }
    }
}
