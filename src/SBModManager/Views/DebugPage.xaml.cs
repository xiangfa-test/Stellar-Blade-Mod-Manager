using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Services;

namespace SBModManager.Views;

/// <summary>Diagnostics for the current build: environment info and the self-test suite.</summary>
public sealed partial class DebugPage : Page, ILanguageAwarePage
{
    public DebugPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static IAppState State => AppServices.State;

    /// <summary>Re-render text this page built in code after a live language switch.</summary>
    public void OnLanguageChanged() => Refresh();

    private void Refresh()
    {
        VersionText.Text = Localization.T("版本:") + " " + AppUpdater.CurrentVersion;
        DataDirText.Text = Localization.T("数据目录:") + " " + AppPaths.Root;
        LibraryText.Text = Localization.T("模组库:") + " " + AppPaths.ModsRoot;
        GameDirText.Text = Localization.T("游戏目录:") + " " + (State.GameDir ?? Localization.T("（未设置）"));
        LogText.Text = Localization.T("日志目录:") + " " + AppPaths.LogsRoot;
        TempText.Text = Localization.T("临时目录:") + " " + AppPaths.TempRoot;
        ResultText.Text = "";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Log.OpenLogFolder();

    private void OpenData_Click(object sender, RoutedEventArgs e) => GameLocator.OpenFolder(AppPaths.Root);

    private void OpenCrashes_Click(object sender, RoutedEventArgs e)
        => GameLocator.OpenFolder(Path.Combine(AppPaths.Root, "crashes"));

    private void ClearTemp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(AppPaths.TempRoot))
            {
                ResultText.Text = Localization.T("临时目录不存在。");
                return;
            }

            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.TempRoot))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn("清理临时目录失败: " + ex.Message);
                }
            }
            foreach (var file in Directory.EnumerateFiles(AppPaths.TempRoot))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn("清理临时文件失败: " + ex.Message);
                }
            }

            ResultText.Text = string.Format(Localization.T("已清理 {0} 个临时项。"), removed);
        }
        catch (Exception ex)
        {
            ResultText.Text = Localization.T("清理失败:") + " " + ex.Message;
        }
    }

    private async void RunSelfTest_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        ResultText.Text = Localization.T("正在运行自检…");
        try
        {
            var report = Path.Combine(AppPaths.TempRoot, "selftest-ui.txt");
            Directory.CreateDirectory(AppPaths.TempRoot);
            await Task.Run(() => SelfTest.Run(report));
            var lines = File.Exists(report) ? File.ReadAllLines(report) : Array.Empty<string>();
            var failed = lines.Count(l => l.StartsWith("[FAIL]", StringComparison.Ordinal));
            var passed = lines.Count(l => l.StartsWith("[PASS]", StringComparison.Ordinal));
            ResultText.Text = string.Format(Localization.T("自检完成：通过 {0}，失败 {1}。"), passed, failed) + "\n" +
                              string.Join("\n", lines.Where(l => l.StartsWith("[FAIL]", StringComparison.Ordinal)).Take(10));
        }
        catch (Exception ex)
        {
            Log.Error("界面自检失败", ex);
            ResultText.Text = Localization.T("自检失败:") + " " + ex.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, new[]
        {
            Localization.T("版本:") + " " + AppUpdater.CurrentVersion,
            Localization.T("数据目录:") + " " + AppPaths.Root,
            Localization.T("模组库:") + " " + AppPaths.ModsRoot,
            Localization.T("游戏目录:") + " " + (State.GameDir ?? Localization.T("（未设置）")),
            Localization.T("日志目录:") + " " + AppPaths.LogsRoot,
            Localization.T("模组数量:") + " " + State.Library.Count,
            Localization.T("已启用:") + " " + State.Library.Count(r => State.IsEnabled(r.Id)),
        });
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ResultText.Text = Localization.T("诊断信息已复制到剪贴板。");
    }
}
