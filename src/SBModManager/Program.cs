using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SBModManager;

/// <summary>
/// Custom entry point instead of the XAML-generated one so that startup failures
/// are recorded in crash.log (WinUI otherwise exits with a silent 0xC000027B).
/// Supports --selftest, --screenshot, --page and --data-dir.
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
                case "--select-first":
                    SelectFirst = true;
                    break;
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
