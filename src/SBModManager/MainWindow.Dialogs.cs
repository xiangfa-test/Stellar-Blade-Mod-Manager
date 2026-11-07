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
            var state = Services.AppServices.State;
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
                    Title = Localization.T("添加到开始菜单？"),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = Localization.T("要在 Windows 开始菜单中添加「剑星 MOD 管理器」的快捷方式吗？") + "\n" +
                           Localization.T("添加后按 Win 键即可搜索到，随时可以在「设置 → 高级」里移除。"),
                },
                PrimaryButtonText = Localization.T("添加到开始菜单"),
                CloseButtonText = Localization.T("不用了"),
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await Ui.Dialogs.ShowAsync(dialog, Content.XamlRoot);
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
        var state = Services.AppServices.State;
        if (state.Config.WindowSizePromptDone) return;
        state.Config.WindowSizePromptDone = true;

        var dialog = new ContentDialog
        {
                Title = Localization.T("记住窗口大小？"),
            Content = $"下次启动时使用当前窗口大小（{size.Width} × {size.Height}）吗？\n\n" +
                      Localization.T("选择「记住」后，之后每次调整窗口都会自动记住。") +
                      Localization.T("想反悔可到「设置 → 窗口 → 重置为默认大小」清除。"),
            PrimaryButtonText = Localization.T("记住"),
            CloseButtonText = Localization.T("不记住"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await Ui.Dialogs.ShowAsync(dialog, RootGrid.XamlRoot);
        state.Config.RememberWindowSize = result == ContentDialogResult.Primary;
        if (state.Config.RememberWindowSize)
        {
            state.Config.WindowWidth = size.Width;
            state.Config.WindowHeight = size.Height;
        }
        state.SaveConfig();
    }

    /// <summary>
    /// The game started and vanished within seconds without a crash report. When the installed
    /// UE4SS carries a stale signature we know the fix, so offer it right away; otherwise only log
    /// (prompting on every unrelated quick exit would be noise).
    /// </summary>
    private static DateTimeOffset _lastStartupFixPrompt = DateTimeOffset.MinValue;

    private void OnStartupFailureSuspected(string diagnosis)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var status = Services.Ue4ssCompatFix.Inspect(Services.AppServices.State.GameDir);
                if (!status.CanApply)
                {
                    Services.Log.Info("疑似启动失败，但没有可自动修复的 UE4SS 签名，仅记录日志");
                    return;
                }

                // One prompt per launch attempt at most, and never twice in a row for the same mess.
                if (DateTimeOffset.Now - _lastStartupFixPrompt < TimeSpan.FromMinutes(5)) return;
                _lastStartupFixPrompt = DateTimeOffset.Now;

                var panel = new StackPanel { Spacing = 10, MaxWidth = 760 };
                panel.Children.Add(new TextBlock
                {
                    Text = Localization.T("游戏启动后很快退出，系统却没有生成崩溃报告——这通常是 UE4SS 与游戏新版本不兼容导致的注入失败。"),
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = string.Format(
                        Localization.T("检测到失效签名 {0}，会导致游戏启动后立即退出。"),
                        status.PendingSignature ?? ""),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new ScrollViewer
                {
                    MaxHeight = 240,
                    Content = new TextBlock
                    {
                        Text = diagnosis,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    },
                });

                var dialog = new ContentDialog
                {
                    Title = Localization.T("疑似启动失败"),
                    Content = panel,
                    PrimaryButtonText = Localization.T("立即修复"),
                    CloseButtonText = Localization.T("稍后"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await Ui.Dialogs.ShowAsync(dialog, Content.XamlRoot);
                if (result != ContentDialogResult.Primary) return;

                Services.Ue4ssCompatFix.Apply(Services.AppServices.State.GameDir, out var message);
                await Ui.Dialogs.InfoAsync(Content.XamlRoot, Localization.T("修复完成"),
                    message + "\n\n" + Localization.T("已应用修复，请启动游戏确认；若仍无法启动，可试着把 dwmapi.dll 改名以排除 UE4SS。"));
            }
            catch (Exception ex)
            {
                Services.Log.Warn("显示启动失败提示失败: " + ex.Message);
            }
        });
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
                            Title = Localization.T("检测到游戏崩溃"),
                    Content = panel,
                    PrimaryButtonText = Localization.T("导出报告"),
                    SecondaryButtonText = Localization.T("打开报告"),
                    CloseButtonText = Localization.T("关闭"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await Ui.Dialogs.ShowAsync(dialog, Content.XamlRoot);
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
