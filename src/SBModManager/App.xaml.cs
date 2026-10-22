using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SBModManager;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        Program.Trace("app ctor");
        InitializeComponent();
        Program.Trace("app InitializeComponent done");
        UnhandledException += (_, e) =>
        {
            Services.Log.Error("未处理异常", e.Exception);
            e.Handled = true;
        };
    }

    private static async Task RunSelfUpdateAsync()
    {
        try
        {
            var info = await Services.AppUpdater.CheckAsync();
            Services.Log.Info($"自更新: 最新 {info.Tag}，当前 {Services.AppUpdater.CurrentVersion}，有更新={info.IsNewer}");
            if (!info.IsNewer)
            {
                Current.Exit();
                return;
            }

            var staged = await Services.AppUpdater.PrepareAsync(info);
            Services.AppUpdater.StartUpdater(staged);
            await Task.Delay(800);
        }
        catch (Exception ex)
        {
            Services.Log.Error("自更新失败", ex);
        }
        Current.Exit();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Program.Trace("onlaunched");
            Services.AppUpdater.CleanupTemp();
            Services.AppState.Instance.Load();
            Services.DeployJournal.Recover();
            Program.Trace("state loaded");

            if (Program.SelfTestPath is { } testPath)
            {
                Services.SelfTest.Run(testPath);
                Current.Exit();
                return;
            }

            Program.Trace("creating window");
            MainWindow = new MainWindow();
            Program.Trace("window created");
            MainWindow.Activate();
            Program.Trace("window activated");
            StartRequestPump(MainWindow);

            try
            {
                MainWindow.RunStartupUpdateCheck();
            }
            catch (Exception ex)
            {
                Services.Log.Warn("启动更新检查异常: " + ex.Message);
            }

            if (Program.SelfUpdateRequested)
            {
                _ = RunSelfUpdateAsync();
            }

            if (Services.AppState.Instance.Config.HandleNxmLinks)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    Services.NxmHandler.EnsureRegistered(exePath);
                }
            }

            if (Program.ScreenshotPath is { } shotPath)
            {
                _ = CaptureAsync(MainWindow, shotPath);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("启动失败", ex);
            Program.WriteCrash(ex);
            Program.ShowFatalError(
                "剑星 MOD 管理器 启动失败",
                $"启动时发生错误，应用无法继续。\n\n{ex.Message}\n\n详细日志:\n{Path.Combine(AppContext.BaseDirectory, "crash.log")}");
            throw;
        }
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _requestTimer;

    private static void StartRequestPump(MainWindow window)
    {
        var timer = window.DispatcherQueue.CreateTimer();
        _requestTimer = timer;
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            foreach (var request in Services.SingleInstance.Drain())
            {
                var nxm = request.Kind == "nxm" && !string.IsNullOrWhiteSpace(request.Payload)
                    ? request.Payload
                    : null;
                window.HandleExternalRequest(nxm);
            }
        };
        timer.Start();
    }

    /// <summary>Render the window content to a PNG (used for automated UI checks).</summary>
    private static async Task CaptureAsync(Window window, string path)
    {
        try
        {
            await Task.Delay(900);
            MainWindow?.SelectPage(Program.PageTag ?? "mods");
            await Task.Delay(900);

            if (window.Content is not Grid root)
            {
                return;
            }

            try
            {
                root.Background = (Brush)Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
            catch
            {
                root.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            }

            root.UpdateLayout();
            await Task.Delay(200);

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();

            var data = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(data);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
            await encoder.FlushAsync();

            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex)
        {
            try
            {
                await File.WriteAllTextAsync(path + ".err", ex.ToString());
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            Current.Exit();
        }
    }

}
