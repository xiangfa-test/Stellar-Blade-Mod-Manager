using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SBModManager.Services;
using SBModManager.Ui;

namespace SBModManager;

public sealed partial class MainWindow
{
    public async void PromptStartMenuOnce()
    {
        if (_startMenuPromptShown) return;
        _startMenuPromptShown = true;

        try
        {
            var state = Services.AppState.Instance;
            if (state.Config.StartMenuPromptDone) return;

            // Already present (installed before, or created manually): keep the flag in sync.
            if (Services.StartMenuShortcut.Exists())
            {
                state.Config.StartMenuPromptDone = true;
                state.Config.StartMenuShortcutEnabled = true;
                state.SaveConfig();
                return;
            }

            // The dialog needs a live XamlRoot; wait for the first layout pass.
            for (var i = 0; i < 40 && Content.XamlRoot is null; i++)
            {
                await Task.Delay(50);
            }
            if (Content.XamlRoot is null)
            {
                Services.Log.Warn("开始菜单提示跳过：窗口尚未就绪");
                _startMenuPromptShown = false;
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "添加到开始菜单？",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "要在 Windows 开始菜单中添加「剑星 MOD 管理器」的快捷方式吗？\n" +
                           "添加后按 Win 键即可搜索到，随时可以在「设置 → 高级」里移除。",
                },
                PrimaryButtonText = "添加到开始菜单",
                CloseButtonText = "不用了",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            state.Config.StartMenuPromptDone = true;

            if (result == ContentDialogResult.Primary)
            {
                var created = Services.StartMenuShortcut.Create();
                state.Config.StartMenuShortcutEnabled = created;
                Services.Log.Info("开始菜单快捷方式: " + (created ? "已创建" : "创建失败"));
            }
            else
            {
                state.Config.StartMenuShortcutEnabled = false;
            }

            state.SaveConfig();
        }
        catch (Exception ex)
        {
            Services.Log.Warn("开始菜单提示失败: " + ex.Message);
            // Leave the prompt uncompleted so the next launch can ask again.
            _startMenuPromptShown = false;
        }
    }

    public void ResetWindowSize()
    {
        var before = AppWindow.Size;
        _suppressNextResizePrompt = true;
        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        }
        catch (Exception ex)
        {
            Services.Log.Warn("重置窗口大小失败: " + ex.Message);
            _suppressNextResizePrompt = false;
            return;
        }

        // No actual size change means AppWindow.Changed never fires; clear the guard.
        if (AppWindow.Size == before)
        {
            _suppressNextResizePrompt = false;
        }
    }

    private async Task PromptWindowSizeAsync(Windows.Graphics.SizeInt32 size)
    {
        var state = Services.AppState.Instance;
        if (state.Config.WindowSizePromptDone) return;
        state.Config.WindowSizePromptDone = true;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "记住窗口大小？",
            Content = $"下次启动时使用当前窗口大小（{size.Width} × {size.Height}）吗？\n\n" +
                      "选择「记住」后，之后每次调整窗口都会自动记住。" +
                      "想反悔可到「设置 → 窗口 → 重置为默认大小」清除。",
            PrimaryButtonText = "记住",
            CloseButtonText = "不记住",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        state.Config.RememberWindowSize = result == ContentDialogResult.Primary;
        if (state.Config.RememberWindowSize)
        {
            state.Config.WindowWidth = size.Width;
            state.Config.WindowHeight = size.Height;
        }
        state.SaveConfig();
    }

    private void OnGameCrashDetected(Services.CrashReport report)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var panel = new StackPanel { Spacing = 10, MaxWidth = 720 };
                panel.Children.Add(new TextBlock
                {
                    Text = report.Summary,
                    Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "以下内容仅针对已知可能的崩溃原因做初步评估，不能完全确定问题所在。",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock
                    {
                        Text = report.Analysis,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    },
                });

                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "检测到游戏崩溃",
                    Content = panel,
                    PrimaryButtonText = "导出报告",
                    SecondaryButtonText = "打开报告",
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var zip = Services.CrashExporter.Export(report);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        Path.GetDirectoryName(zip)!)
                    {
                        UseShellExecute = true,
                    });
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(zip)
                    {
                        UseShellExecute = true,
                    });
                }
                else if (result == ContentDialogResult.Secondary && report.ReportPath.Length > 0)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report.ReportPath)
                    {
                        UseShellExecute = true,
                    });
                }
            }
            catch (Exception ex)
            {
                Services.Log.Warn("显示崩溃报告失败: " + ex.Message);
            }
        });
    }
}
