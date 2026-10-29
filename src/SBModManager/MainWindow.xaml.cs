using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SBModManager.Views;
using Windows.Graphics;
using SBModManager.Ui;

namespace SBModManager;

public sealed partial class MainWindow : Window
{
    private bool _insetsApplied;

    public MainWindow()
    {
        InitializeComponent();

        Services.GameCrashWatcher.CrashDetected += OnGameCrashDetected;
        Services.Localization.Initialize(Services.AppServices.State.Config.Language);
        Title = Services.Localization.T("剑星 MOD 管理器");

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

            // A freshly navigated page builds its visual tree after this event; apply twice.
            if (ContentFrame.Content is FrameworkElement page)
            {
                page.Loaded += (_, _) => Services.Localization.Apply(page);
                DispatcherQueue.TryEnqueue(() => Services.Localization.Apply(page));
            }
        };
        RootGrid.Loaded += (_, _) => Services.Localization.Apply(RootGrid);
        DispatcherQueue.TryEnqueue(() => Services.Localization.Apply(RootGrid));
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

    private bool _startMenuPromptShown;

    /// <summary>First-run prompt: ask whether to add a Start Menu shortcut.</summary>

    private bool _startupUpdateChecked;

    public async void RunStartupUpdateCheck()
    {
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        if (!Services.AppServices.State.Config.AutoCheckUpdates) return;
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
        var config = Services.AppServices.State.Config;
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

        var state = Services.AppServices.State;
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
            UiTask.Safe(() => PromptWindowSizeAsync(size));
        }
    }

    /// <summary>Resize to the default size without triggering the "remember size?" prompt.</summary>



    /// <summary>Re-apply UI text after the language setting changes.</summary>
    public void ApplyLanguage()
    {
        Services.Localization.Initialize(Services.AppServices.State.Config.Language);

        // The window title is plain text, not part of the visual tree Apply walks.
        Title = Services.Localization.T("剑星 MOD 管理器");

        Services.Localization.Apply(NavView);

        // Pages that format text in code cannot be repaired by re-applying the table (the
        // original Chinese is no longer present), so let them rebuild themselves first.
        if (ContentFrame.Content is ILanguageAwarePage page)
        {
            page.OnLanguageChanged();
        }
        // RootGrid (not just NavView) so the custom title bar and the caption text are covered.
        Services.Localization.Apply(RootGrid);
    }

    /// <summary>Show the debug page only when enabled in settings.</summary>
    public void ApplyDebugVisibility()
    {
        var visible = Services.AppServices.State.Config.ShowDebugPage;
        DebugNavItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && ContentFrame.CurrentSourcePageType == typeof(DebugPage))
        {
            SelectPage("mods");
        }
    }

    private void ApplyConfiguredTheme()
    {
        RootGrid.RequestedTheme = Services.AppServices.State.Config.Theme switch
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

        var pageType = PageTypeForTag(tag);
        if (pageType is null) return;

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            try
            {
                ContentFrame.Navigate(pageType);
                ContentFrame.BackStack.Clear();
                UpdateBackButton();
            }
            catch (Exception ex)
            {
                Services.Log.Error("导航失败: " + pageType.Name, ex);
            }
        }
    }

    private static Type? PageTypeForTag(string tag) => tag switch
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

        // "问题反馈" opens GitHub Issues instead of navigating to a page.
        if (tag == "feedback")
        {
            UiTask.Safe(Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/xiangfa-test/Stellar-Blade-Mod-Manager/issues")));
            DispatcherQueue.TryEnqueue(() =>
            {
                var current = ContentFrame.CurrentSourcePageType;
                var restore = NavView.MenuItems.Concat(NavView.FooterMenuItems)
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag is string t && PageTypeForTag(t) == current);
                if (restore is not null) NavView.SelectedItem = restore;
            });
            return;
        }

        var pageType = PageTypeForTag(tag);
        if (pageType is null) return;

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            try
            {
                ContentFrame.Navigate(pageType);
                ContentFrame.BackStack.Clear();
                UpdateBackButton();
            }
            catch (Exception ex)
            {
                Services.Log.Error("导航失败: " + pageType.Name, ex);
            }
        }
    }

}
