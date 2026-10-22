using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SBModManager.Views;
using Windows.Graphics;

namespace SBModManager;

public sealed partial class MainWindow : Window
{
    private bool _insetsApplied;

    public MainWindow()
    {
        InitializeComponent();
        Title = "剑星 MOD 管理器";

        Services.GameCrashWatcher.CrashDetected += OnGameCrashDetected;

        // Merge the caption area into the app content: the custom title bar uses the
        // Mica backdrop and WinUI-typed caption buttons instead of the system frame.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        try
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("无法设置标题栏高度: " + ex.Message);
        }

        ApplyConfiguredTheme();
        ApplyTitleBarTheme();
        AttachSizeMemory();
        ApplyDebugVisibility();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        Activated += (_, _) => ApplyCaptionInsets();

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        }
        catch
        {
            // icon is optional
        }

        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min(1440, (int)(area.Width * 0.86));
            var height = Math.Min(920, (int)(area.Height * 0.86));
            var x = area.X + (area.Width - width) / 2;
            var y = area.Y + (area.Height - height) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            // resizing is best effort
        }

        TryApplyBackdrop();

        ContentFrame.Navigated += (_, _) =>
        {
            UpdateBackButton();
            AnimatePageIn();
        };
        SelectPage(Program.PageTag ?? "mods");

        if (Program.NavTag is { Length: > 0 } navTag)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var navItem = NavView.MenuItems.Concat(NavView.FooterMenuItems)
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (i.Tag as string) == navTag);
                if (navItem is not null)
                {
                    NavView.SelectedItem = navItem;
                }

                if (Program.NexusDetailId is { Length: > 0 } nexusDetail &&
                    int.TryParse(nexusDetail, out var nexusId))
                {
                    DispatcherQueue.TryEnqueue(() => ContentFrame.Navigate(typeof(NexusDetailPage), nexusId));
                }
            });
        }
    }

    /// <summary>
    /// Windows-Settings style page transition: the new page slides up slightly while fading in.
    /// Also attaches the auto-hiding scrollbar to whatever ScrollViewer the page contains.
    /// </summary>
    private void AnimatePageIn()
    {
        if (ContentFrame.Content is not FrameworkElement page) return;

        var transform = new TranslateTransform { Y = 26 };
        page.RenderTransform = transform;
        page.Opacity = 0;

        var slide = new DoubleAnimation
        {
            From = 26,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var viewer in FindScrollViewers(page))
            {
                Ui.ScrollBarAutoHide.Attach(viewer);
            }
        });
    }

    private static List<ScrollViewer> FindScrollViewers(DependencyObject root)
    {
        var result = new List<ScrollViewer>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                result.Add(viewer);
            }
            result.AddRange(FindScrollViewers(child));
        }
        return result;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    /// <summary>Title slides right by this much while the back button is visible.</summary>
    private const double TitleBackInset = 34;

    /// <summary>Pages reachable from the navigation pane; they never show a back button.</summary>
    private static readonly Type[] RootPages =
    {
        typeof(ModsPage), typeof(NexusPage), typeof(CollectionsPage), typeof(LoadOrderPage),
        typeof(ProfilesPage), typeof(RuntimePage), typeof(DebugPage), typeof(SettingsPage),
        typeof(AboutPage),
    };

    private static bool IsRootPage(Type? pageType)
        => pageType is not null && RootPages.Contains(pageType);

    private bool _backButtonShown;

    /// <summary>
    /// Task-Manager style layout: the back button appears only on secondary pages (when the
    /// frame can go back). Showing it slides the title to the right; hiding reverses that.
    /// </summary>
    private void UpdateBackButton()
    {
        // Only secondary pages (pushed on top of a root page) get a back button.
        var show = ContentFrame.CanGoBack && !IsRootPage(ContentFrame.CurrentSourcePageType);
        if (show == _backButtonShown) return;
        _backButtonShown = show;

        if (show)
        {
            BackButton.Visibility = Visibility.Visible;
            BackButton.IsHitTestVisible = true;
        }

        AnimateTitleShift(show ? TitleBackInset : 0);

        var fade = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, BackButton);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (!show)
        {
            storyboard.Completed += (_, _) =>
            {
                BackButton.Visibility = Visibility.Collapsed;
                BackButton.IsHitTestVisible = false;
            };
        }
        storyboard.Begin();
    }

    private void AnimateTitleShift(double to)
    {
        var slide = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, TitleShift);
        Storyboard.SetTargetProperty(slide, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private bool _startupUpdateChecked;

    public async void RunStartupUpdateCheck()
    {
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        if (!Services.AppState.Instance.Config.AutoCheckUpdates) return;
        if (!Services.AppUpdater.StartupCheckAllowed()) return;

        try
        {
            var info = await Services.AppUpdater.CheckAsync();
            if (!info.IsNewer) return;

            UpdateInfoBar.Title = $"发现新版本 {info.Tag}";
            UpdateInfoBar.Message = $"当前版本 {Services.AppUpdater.CurrentVersion}，可在「设置 → 应用更新」中下载。";
            var action = new Button { Content = "查看更新" };
            action.Click += (_, _) =>
            {
                UpdateInfoBar.IsOpen = false;
                SelectPage("settings");
            };
            UpdateInfoBar.ActionButton = action;
            Ui.InfoBarAnimation.Show(UpdateInfoBar);
        }
        catch (Exception ex)
        {
            Services.Log.Warn("启动时检查更新失败: " + ex.Message);
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTimer;
    private bool _applyingSavedSize;
    private bool _suppressNextResizePrompt;

    private void AttachSizeMemory()
    {
        var config = Services.AppState.Instance.Config;
        if (config.RememberWindowSize && config.WindowWidth >= 640 && config.WindowHeight >= 480)
        {
            try
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)config.WindowWidth, (int)config.WindowHeight));
                _applyingSavedSize = true;
            }
            catch (Exception ex)
            {
                Services.Log.Warn("恢复窗口大小失败: " + ex.Message);
            }
        }

        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange) return;
            _resizeTimer ??= CreateResizeTimer();
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateResizeTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => OnWindowResized();
        return timer;
    }

    private void OnWindowResized()
    {
        if (_suppressNextResizePrompt)
        {
            // Reset-to-default is a programmatic resize: don't ask again for it.
            _suppressNextResizePrompt = false;
            return;
        }

        var state = Services.AppState.Instance;
        var config = state.Config;
        var size = AppWindow.Size;

        if (config.RememberWindowSize)
        {
            config.WindowWidth = size.Width;
            config.WindowHeight = size.Height;
            state.SaveConfig();
        }

        if (_applyingSavedSize)
        {
            _applyingSavedSize = false;
            return;
        }

        if (!config.WindowSizePromptDone)
        {
            _ = PromptWindowSizeAsync(size);
        }
    }

    /// <summary>Resize to the default size without triggering the "remember size?" prompt.</summary>
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

    /// <summary>Show the debug page only when enabled in settings.</summary>
    public void ApplyDebugVisibility()
    {
        var visible = Services.AppState.Instance.Config.ShowDebugPage;
        DebugNavItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && ContentFrame.CurrentSourcePageType == typeof(DebugPage))
        {
            SelectPage("mods");
        }
    }

    private void ApplyConfiguredTheme()
    {
        RootGrid.RequestedTheme = Services.AppState.Instance.Config.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTitleBarTheme();
            ApplyCaptionInsets();
        });
    }

    private void ApplyCaptionInsets()
    {
        if (_insetsApplied)
        {
            return;
        }
        try
        {
            var inset = AppWindow.TitleBar.RightInset;
            if (inset > 0)
            {
                DragRegion.Margin = new Thickness(0, 0, inset, 0);
            }
            _insetsApplied = true;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("无法读取标题栏按钮内边距: " + ex.Message);
        }
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x12, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x1E, 0x00, 0x00, 0x00);
            titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("标题栏配色失败: " + ex.Message);
        }
    }

    private void TryApplyBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Mica 背景不可用: " + ex.Message);
        }

        // Fallback for systems without Mica: use the normal page background brush.
        if (Application.Current.Resources.TryGetValue("ApplicationPageBackgroundThemeBrush", out var brush)
            && brush is Brush pageBrush)
        {
            RootGrid.Background = pageBrush;
        }
    }

    public string CurrentPageName => ContentFrame.CurrentSourcePageType?.Name ?? "null";

    /// <summary>Handles a request forwarded from a second launch (e.g. an nxm:// link).</summary>
    public void HandleExternalRequest(string? nxmUrl)
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
            Activate();
            if (nxmUrl is not null)
            {
                ModsPage.QueueExternalNxm(nxmUrl);
                SelectPage("mods");
                if (ContentFrame.Content is ModsPage page)
                {
                    page.HandlePendingExternal();
                }
            }
            else
            {
                SelectPage("mods");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Warn("处理外部启动请求失败: " + ex.Message);
        }
    }

    public void SelectPage(string tag)
    {
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }

        var pageType = tag switch
        {
            "nexus" => typeof(NexusPage),
            "collections" => typeof(CollectionsPage),
            "loadorder" => typeof(LoadOrderPage),
            "profiles" => typeof(ProfilesPage),
            "runtime" => typeof(RuntimePage),
            "debug" => typeof(DebugPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(ModsPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>Open the Collections page and parse a collection URL/slug.</summary>
    public void OpenCollection(string input)
    {
        CollectionsPage.QueueOpen(input);
        SelectPage("collections");
        if (ContentFrame.Content is CollectionsPage page)
        {
            page.HandlePendingOpen();
        }
    }

    /// <summary>Navigate to the mod list and filter it (used by the collections page).</summary>
    public void OpenModsWithFilter(string filter)
    {
        SelectPage("mods");
        if (ContentFrame.Content is ModsPage page)
        {
            page.SetSearchFilter(filter);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "mods" => typeof(ModsPage),
            "nexus" => typeof(NexusPage),
            "collections" => typeof(CollectionsPage),
            "loadorder" => typeof(LoadOrderPage),
            "profiles" => typeof(ProfilesPage),
            "runtime" => typeof(RuntimePage),
            "debug" => typeof(DebugPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(ModsPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            try
            {
                var ok = ContentFrame.Navigate(pageType);
                ContentFrame.BackStack.Clear();
                UpdateBackButton();
                Services.Log.Info($"导航到 {pageType.Name}: {ok}");
            }
            catch (Exception ex)
            {
                Services.Log.Error("导航失败: " + pageType.Name, ex);
            }
        }
    }
}
