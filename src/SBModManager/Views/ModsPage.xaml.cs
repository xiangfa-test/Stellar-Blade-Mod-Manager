using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SBModManager.Models;
using SBModManager.Services;
using SBModManager.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage;

namespace SBModManager.Views;

public sealed partial class ModsPage : Page
{
    private bool _initialized;
    private static bool _shownDataDirNotice;
    private bool _suppressVersionChange;
    private bool _nxmHandled;
    private DateTime _suppressToggleUntil;
    private static readonly Queue<string> PendingExternal = new();
    private static readonly object PendingLock = new();

    private bool _splitterDragging;
    private double _splitterStartX;
    private double _splitterStartWidth;
    private const double MinListWidth = 320;
    private const double MinDetailWidth = 260;
    private const double DefaultDetailWidth = 300;

    public ModsPage()
    {
        InitializeComponent();
        try
        {
            var saved = State.Config.ModsDetailWidth;
            DetailColumn.Width = new GridLength(
                Math.Clamp(saved <= 0 ? DefaultDetailWidth : saved, MinDetailWidth, 900));
        }
        catch
        {
            DetailColumn.Width = new GridLength(DefaultDetailWidth);
        }
        // The detail pane starts collapsed; hook its scrollbar anyway so the first
        // selection already gets the auto-hiding behaviour.
        SizeChanged += OnPageSizeChanged;
        Ui.ScrollBarAutoHide.Attach(DetailsScroll);
        Ui.ScrollBarAutoHide.AttachToElement(ModList);
        Ui.ScrollBarAutoHide.AttachToElement(ExternalList);
        Loaded += (_, _) =>
        {
                if (!_initialized)
            {
                _initialized = true;
                Reload();
                HandlePendingExternal();
                if (!_nxmHandled && Program.NxmUrl is { } nxm)
                {
                    _nxmHandled = true;
                    _ = HandleNxmAsync(nxm);
                }
            }
        };
    }

    public BulkObservableCollection<ModItem> Mods { get; } = new();

    public ObservableCollection<ExternalItem> Externals { get; } = new();

    private AppState State => AppState.Instance;

    private string? GameDir => State.GameDir;

    // ---------------------------------------------------------------- detail splitter

    private void DetailSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(ModsView).Position.X;
        _splitterStartWidth = DetailColumn.ActualWidth;
        DetailSplitter.CapturePointer(e.Pointer);
        DetailSplitterLine.Opacity = 1;
        e.Handled = true;
    }

    private void DetailSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;

        var pointerX = e.GetCurrentPoint(ModsView).Position.X;
        var delta = _splitterStartX - pointerX; // dragging left widens the details pane
        var available = ModsView.ActualWidth;
        var maxDetail = Math.Max(MinDetailWidth, available - MinListWidth - DetailSplitter.ActualWidth - 40);
        var width = Math.Clamp(_splitterStartWidth + delta, MinDetailWidth, maxDetail);

        DetailColumn.Width = new GridLength(width);
        e.Handled = true;
    }

    private void DetailSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        DetailSplitter.ReleasePointerCapture(e.Pointer);
        DetailSplitterLine.Opacity = 0.55;
        SaveDetailWidth();
        e.Handled = true;
    }

    private void DetailSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = false;
        DetailSplitterLine.Opacity = 0.55;
    }

    private void DetailSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        => DetailSplitterLine.Opacity = 1;

    private void DetailSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            DetailSplitterLine.Opacity = 0.55;
        }
    }

    private void DetailSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        DetailColumn.Width = new GridLength(DefaultDetailWidth);
        SaveDetailWidth();
        e.Handled = true;
    }

    private void SaveDetailWidth()
    {
        try
        {
            State.Config.ModsDetailWidth = DetailColumn.ActualWidth;
            State.SaveConfig();
        }
        catch
        {
            // best effort
        }
    }

    // ---------------------------------------------------------------- reload

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back)
        {
            Loaded += (_, _) => RestoreDetailAnimation();
        }
    }

    private void RestoreDetailAnimation()
    {
        if (ModList.SelectedItem is not ModItem item) return;
        try
        {
            var animation = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService
                .GetForCurrentView()
                .GetAnimation("modDetailBack");
            if (animation is not null && ModList.ContainerFromItem(item) is FrameworkElement container)
            {
                animation.TryStart(container);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void Reload()
    {
        State.ReloadLibrary();
        UpdateCnsFilters();
        RebuildMods();
        RebuildExternals();
        UpdateHeader();
        UpdateStatus();

        if ((Program.ScreenshotPath is not null || Program.SelectFirst) && Mods.Count > 0)
        {
            ModList.SelectedIndex = 0;
        }

        if (!_shownDataDirNotice && AppPaths.RootResolutionLog.Count > 1)
        {
            _shownDataDirNotice = true;
            ShowMessage(InfoBarSeverity.Informational, "数据目录已回退",
                $"默认数据目录不可写，已改用: {AppPaths.Root}");
        }
    }

    /// <summary>Set the search box (used when jumping in from the collections page).</summary>
    public void SetSearchFilter(string text)
    {
        SearchBox.Text = text ?? "";
        RebuildMods();
    }

    private static readonly object ConflictCacheLock = new();
    private static ConflictReport? _conflictCache;
    private static DateTimeOffset _conflictCacheAt;
    private static string _conflictCacheGameDir = "";

    /// <summary>Conflict stats are expensive; reuse them until the deployment state changes.</summary>
    private static ConflictReport GetConflictReport(string gameDir)
    {
        lock (ConflictCacheLock)
        {
            var deployedVersion = AppState.Instance.Deployed.Mods.Count;
            var fresh = _conflictCache is not null &&
                        _conflictCacheGameDir == gameDir &&
                        _conflictCache.DeployedCount == deployedVersion;
            if (fresh) return _conflictCache!;

            var report = Deployer.BuildConflictReport(gameDir);
            _conflictCache = report;
            _conflictCacheAt = DateTimeOffset.Now;
            _conflictCacheGameDir = gameDir;
            return report;
        }
    }

    public static void InvalidateConflictCache()
    {
        lock (ConflictCacheLock)
        {
            _conflictCache = null;
        }
    }

    private void RebuildMods()
    {
        // Rebuilding the ItemsSource recycles ListView containers; a reused ToggleSwitch can
        // raise Toggled with a stale DataContext and disable the wrong mod. Ignore these.
        _suppressToggleUntil = DateTime.UtcNow.AddMilliseconds(800);
        var filter = SearchBox?.Text?.Trim() ?? "";
        var selectedId = (ModList.SelectedItem as ModItem)?.Record.Id;

        var conflictCounts = new Dictionary<string, ModConflictStat>(StringComparer.OrdinalIgnoreCase);
        if (GameDir is { } gameDir)
        {
            try
            {
                foreach (var stat in GetConflictReport(gameDir).HighConflictMods)
                {
                    conflictCounts[stat.ModId] = stat;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("冲突统计失败: " + ex.Message);
            }
        }

        var rebuilt = new List<ModItem>(State.Library.Count);
        foreach (var record in State.Library.OrderByDescending(r => r.ImportedAt))
        {
            if (filter.Length > 0 &&
                !record.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.DisplayType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.CnsSummary.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (RoleFilter.SelectedItem is string role && !role.StartsWith("全部") &&
                !record.CnsCharacters.Contains(role, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TypeFilter.SelectedItem is string outfitType && !outfitType.StartsWith("全部") &&
                !record.CnsOutfitTypes.Contains(outfitType, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = new ModItem(record, State.IsEnabled(record.Id));
            if (conflictCounts.TryGetValue(record.Id, out var stat))
            {
                item.ConflictCount = stat.ConflictCount;
                item.IsHighConflict = stat.ConflictCount >= Math.Max(2, State.Config.HighConflictThreshold);
            }
            rebuilt.Add(item);
        }

        Mods.ReplaceAll(rebuilt);

        if (selectedId is not null)
        {
            var again = Mods.FirstOrDefault(m => m.Record.Id == selectedId);
            if (again is not null)
            {
                ModList.SelectedItem = again;
            }
        }
    }

    private void RebuildExternals()
    {
        Externals.Clear();
        if (GameDir is null) return;
        foreach (var external in Deployer.ScanExternal(GameDir))
        {
            Externals.Add(new ExternalItem(external));
        }
    }

    private void UpdateHeader()
    {
        GamePathText.Text = GameDir is null
            ? "尚未设置游戏目录 — 请到「设置」中选择 StellarBlade 安装位置"
            : "游戏目录: " + GameDir;
    }

    private void UpdateStatus()
    {
        var enabled = State.Library.Count(m => State.IsEnabled(m.Id));
        var total = State.Library.Count;
        var running = GameDir is not null && GameLocator.IsGameRunning();
        StatusLineText.Text = $"已启用 {enabled}/{total}" + (running ? " · 游戏运行中" : " · 游戏未运行");
        Ui.InfoBarAnimation.FadeIn(StatusLineText, 4, 160);
        CountText.Text = GameDir is null ? "" : (Directory.Exists(Deployer.PaksModsDir(GameDir)) ? "~mods 目录已就绪" : "~mods 目录尚未创建");
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        ImportButton.IsEnabled = !busy;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MsgBar.Severity = severity;
        MsgBar.Title = title;
        MsgBar.Message = message;
        Ui.InfoBarAnimation.Show(MsgBar);
    }

    private bool EnsureGame(out string gameDir)
    {
        if (GameDir is { } dir)
        {
            gameDir = dir;
            return true;
        }
        gameDir = "";
        ShowMessage(InfoBarSeverity.Error, "没有找到游戏目录", "请先在「设置」页面选择 StellarBlade 的安装目录。");
        return false;
    }

    // ---------------------------------------------------------------- import

    private async void ImportArchive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = FileDialogService.PickFiles(
                OwnerHandle(),
                "选择模组压缩包",
                new FileDialogService.DialogFilter("压缩包", "*.zip;*.7z;*.rar"),
                new FileDialogService.DialogFilter("所有文件", "*.*"));
            if (paths.Count == 0) return;
            await ImportFilesAsync(paths.ToList());
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "导入失败", ex.Message);
            Log.Error("选择压缩包失败", ex);
        }
    }

    private async void ImportPak_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = FileDialogService.PickFiles(
                OwnerHandle(),
                "选择 .pak / .ucas / .utoc",
                new FileDialogService.DialogFilter("Unreal 包", "*.pak;*.ucas;*.utoc"),
                new FileDialogService.DialogFilter("所有文件", "*.*"));
            if (paths.Count == 0) return;

            SetBusy(true);
            var result = ModImporter.ImportPakFiles(paths.ToList());
            SetBusy(false);
            ApplyImportResult(result);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowMessage(InfoBarSeverity.Error, "导入失败", ex.Message);
            Log.Error("导入 PAK 失败", ex);
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = FileDialogService.PickFolder(OwnerHandle(), "选择模组文件夹");
            if (string.IsNullOrWhiteSpace(folder)) return;

            SetBusy(true);
            var result = ModImporter.ImportDirectory(folder, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), folder, move: false);
            SetBusy(false);
            ApplyImportResult(result);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowMessage(InfoBarSeverity.Error, "导入失败", ex.Message);
            Log.Error("导入文件夹失败", ex);
        }
    }

    private async Task ImportFilesAsync(List<string> paths)
    {
        SetBusy(true);
        var imported = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            var result = ArchiveService.IsPakPart(path) && !ArchiveService.IsArchive(path)
                ? ModImporter.ImportPakFiles(new[] { path })
                : await ModImporter.ImportArchiveAsync(path, ShowFomodDialogAsync);
            if (result.Mod is not null)
            {
                CollectionInstaller.TagAsLocal(result.Mod);
                imported++;
            }
            else
            {
                errors.Add($"{Path.GetFileName(path)}: {result.Error}");
            }
        }
        SetBusy(false);
        Reload();
        if (imported > 0)
        {
            ShowMessage(InfoBarSeverity.Success, "导入完成", $"成功导入 {imported} 个模组。");
        }
        if (errors.Count > 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "部分文件导入失败", string.Join(Environment.NewLine, errors.Take(6)));
        }
    }

    /// <summary>Simple FOMOD option picker: radio for "exactly one", checkboxes for "any".</summary>
    private async Task<IReadOnlyList<FomodPlugin>?> ShowFomodDialogAsync(FomodConfig config)
    {
        var panel = new StackPanel { Spacing = 10 };
        var radios = new List<(FomodGroup Group, RadioButton Button, FomodPlugin Plugin)>();
        var boxes = new List<(FomodPlugin Plugin, CheckBox Box)>();

        foreach (var step in config.Steps)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(step.Name) ? "安装选项" : step.Name,
                Style = ResourceStyle("BodyStrongTextBlockStyle"),
            });

            foreach (var group in step.Groups)
            {
                if (group.Plugins.Count == 0) continue;

                panel.Children.Add(new TextBlock
                {
                    Text = group.Name,
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });

                var single = group.Type.Contains("ExactlyOne", StringComparison.OrdinalIgnoreCase) ||
                             group.Type.Contains("AtMostOne", StringComparison.OrdinalIgnoreCase) ||
                             group.Type.Equals("Required", StringComparison.OrdinalIgnoreCase);

                if (single)
                {
                    var groupName = "fomod-" + Guid.NewGuid().ToString("N")[..6];
                    for (var i = 0; i < group.Plugins.Count; i++)
                    {
                        var plugin = group.Plugins[i];
                        var button = new RadioButton
                        {
                            GroupName = groupName,
                            Content = plugin.Name,
                            IsChecked = i == 0,
                            Tag = plugin,
                        };
                        radios.Add((group, button, plugin));
                        panel.Children.Add(button);
                        AddFomodDescription(panel, plugin);
                    }
                }
                else
                {
                    foreach (var plugin in group.Plugins)
                    {
                        var box = new CheckBox { Content = plugin.Name, Tag = plugin, IsChecked = false };
                        boxes.Add((plugin, box));
                        panel.Children.Add(box);
                        AddFomodDescription(panel, plugin);
                    }
                }
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "FOMOD 安装选项",
            Content = new ScrollViewer { MaxHeight = 480, Content = panel },
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var selected = new List<FomodPlugin>();
        selected.AddRange(radios.Where(r => r.Button.IsChecked == true).Select(r => r.Plugin));
        selected.AddRange(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Plugin));
        return selected;
    }


    private void ApplyImportResult(ImportResult result)
    {
        Reload();
        if (result.Mod is not null)
        {
            CollectionInstaller.TagAsLocal(result.Mod);
            ShowMessage(InfoBarSeverity.Success, "导入完成", $"已导入「{result.Mod.Name}」（{result.Mod.DisplayType}）");
        }
        else
        {
            ShowMessage(InfoBarSeverity.Error, "导入失败", result.Error ?? "未知错误");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // The button also owns a flyout; this handler only runs when the flyout is dismissed.
    }

    private static IntPtr OwnerHandle()
        => App.MainWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

    // ---------------------------------------------------------------- toggles

    private async void ModToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _suppressToggleUntil) return;
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not ModItem item) return;
        if (item.Enabled == toggle.IsOn) return;
        await SetModEnabledAsync(item, toggle.IsOn);
    }

    private async Task SetModEnabledAsync(ModItem item, bool enable)
    {
        if (item.Busy) return;
        if (!Mods.Contains(item)) return;
        if (!EnsureGame(out var gameDir))
        {
            item.Enabled = false;
            return;
        }

        item.Busy = true;
        SetBusy(true);
        try
        {
            if (enable)
            {
                // Dependency gate is a UI concern: ask before touching the game folder.
                var issues = DependencyChecker.Check(item.Record);
                var blocking = issues.Where(d => d.Blocking).ToList();
                if (issues.Count > 0)
                {
                    var lines = issues.Select(d =>
                        $"• {d.Missing}" + (string.IsNullOrWhiteSpace(d.Hint) ? "" : "\n    " + d.Hint));
                    var dependencyDialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = blocking.Count > 0 ? "缺少必要依赖" : "依赖提示",
                        Content = new ScrollViewer
                        {
                            MaxHeight = 360,
                            Content = new TextBlock
                            {
                                TextWrapping = TextWrapping.Wrap,
                                Text = string.Join("\n", lines) +
                                       (blocking.Count > 0 ? "\n\n缺少必要依赖时模组可能完全不起作用。" : ""),
                            },
                        },
                        PrimaryButtonText = blocking.Count > 0 ? "仍然启用" : "知道了",
                        CloseButtonText = "取消",
                        DefaultButton = blocking.Count > 0 ? ContentDialogButton.Close : ContentDialogButton.Primary,
                    };
                    var dependencyChoice = await dependencyDialog.ShowAsync();
                    if (blocking.Count > 0 && dependencyChoice != ContentDialogResult.Primary)
                    {
                        ShowMessage(InfoBarSeverity.Warning, "已取消", "缺少依赖，未启用该模组。");
                        return;
                    }
                }

                var result = ModActions.Enable(item.Record);
                if (!result.Success && result.Conflicts.Count > 0)
                {
                    if (!await AskForceDeployAsync(item, result.Conflicts))
                    {
                        ShowMessage(InfoBarSeverity.Warning, "已取消", "存在文件冲突，未启用该模组。");
                        return;
                    }

                    result = ModActions.Enable(item.Record, force: true);
                }

                if (!result.Success)
                {
                    ShowMessage(InfoBarSeverity.Error, "启用失败", result.Error ?? "未知错误");
                    return;
                }

                foreach (var sibling in Mods.Where(m => m != item &&
                             string.Equals(m.Record.GroupKey, item.Record.GroupKey, StringComparison.OrdinalIgnoreCase)))
                {
                    sibling.Enabled = State.IsEnabled(sibling.Record.Id);
                }

                item.Enabled = true;
                ShowMessage(InfoBarSeverity.Success, "已启用", $"「{item.Name}」已部署到游戏目录。");
            }
            else
            {
                ModActions.Disable(item.Record);
                item.Enabled = false;
                ShowMessage(InfoBarSeverity.Success, "已停用", $"「{item.Name}」的文件已从游戏目录移除。");
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "操作失败", ex.Message);
            Log.Error("切换模组状态失败", ex);
        }
        finally
        {
            item.Busy = false;
            SetBusy(false);
            UpdateStatus();
            UpdateDetails(item);
        }
    }

    private async Task<bool> AskForceDeployAsync(ModItem item, List<ModConflict> conflicts)
    {
        var text = string.Join(Environment.NewLine, conflicts.Take(12).Select(c =>
            $"• {Path.GetFileName(c.TargetPath)}  （已被 {c.OtherModName} 占用）"));
        if (conflicts.Count > 12) text += Environment.NewLine + $"… 以及另外 {conflicts.Count - 12} 个文件";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "文件冲突",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = $"「{item.Name}」要写入以下文件，但目标位置已有其他模组/文件：\n\n{text}\n\n强制启用会覆盖这些文件。",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "强制启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void EnableAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGame(out var gameDir)) return;
        SetBusy(true);
        var failed = new List<string>();
        try
        {
            foreach (var item in Mods.Where(m => !m.Enabled).ToList())
            {
                var result = ModActions.Enable(item.Record);
                if (result.Success)
                {
                    item.Enabled = true;
                }
                else
                {
                    failed.Add(item.Name + (result.Conflicts.Count > 0 ? "（冲突）" : "（" + result.Error + "）"));
                }
            }
        }
        finally
        {
            SetBusy(false);
            UpdateStatus();
        }

        ShowMessage(failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            "批量启用完成",
            failed.Count == 0 ? "所有模组都已启用。" : "部分模组未启用:\n" + string.Join(Environment.NewLine, failed.Take(8)));
    }

    private void DisableAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Mods.Where(m => m.Enabled).ToList())
        {
            ModActions.Disable(item.Record);
            item.Enabled = false;
        }
        UpdateStatus();
        UpdateDetails(ModList.SelectedItem as ModItem);
        ShowMessage(InfoBarSeverity.Success, "已全部停用", "所有由本工具部署的文件已从游戏目录移除。");
    }

    // ---------------------------------------------------------------- details

    private void ModList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModItem item) return;

        // Expand the clicked row into the full-screen detail page (Store-style).
        try
        {
            if (ModList.ContainerFromItem(item) is FrameworkElement container)
            {
                Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService
                    .GetForCurrentView()
                    .PrepareToAnimate("modDetail", container);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("准备详情动画失败: " + ex.Message);
        }

        Frame.Navigate(typeof(ModDetailPage), item.Record.Id);
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDetails(ModList.SelectedItem as ModItem);



    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var deleteMessage = $"确定要从库中删除「{item.Name}」吗？\n如果它已启用，游戏中的文件也会一并移除。";
        if (item.Record.Type == ModType.Ue4ssRuntime)
        {
            deleteMessage += "\n\n注意：这是 UE4SS 运行库，删除会把 ue4ss 文件夹和 dwmapi.dll 从 SB\\Binaries\\Win64 移除，" +
                             "所有脚本类模组都会停止工作。";
        }
        if (!await ConfirmAsync("删除模组", deleteMessage)) return;

        try
        {
            ModActions.Delete(item.Record);
            Reload();
            ShowMessage(InfoBarSeverity.Success, "已删除", $"「{item.Name}」已从模组库删除。");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "删除失败", ex.Message);
        }
    }


    // ---------------------------------------------------------------- context menus

    private void ModList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ModItem item) return;
        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开模组目录" };
        open.Click += (_, _) => GameLocator.OpenFolder(item.Record.ModDir);
        flyout.Items.Add(open);

        var toggle = new MenuFlyoutItem { Text = item.Enabled ? "停用" : "启用" };
        toggle.Click += async (_, _) => await SetModEnabledAsync(item, !item.Enabled);
        flyout.Items.Add(toggle);

        flyout.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "删除模组…" };
        delete.Click += async (_, _) =>
        {
            ModList.SelectedItem = item;
            DeleteMod_Click(this, new RoutedEventArgs());
            await Task.CompletedTask;
        };
        flyout.Items.Add(delete);

        flyout.ShowAt(ModList, e.GetPosition(ModList));
    }

    private async void ExternalList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ExternalItem item) return;
        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "在资源管理器中显示" };
        open.Click += (_, _) =>
        {
            var first = item.Files.FirstOrDefault();
            if (first is not null)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{first}\"");
            }
        };
        flyout.Items.Add(open);

        var delete = new MenuFlyoutItem { Text = "删除这些文件…" };
        delete.Click += async (_, _) =>
        {
            if (!await ConfirmAsync("删除外部文件", $"确定删除「{item.Name}」下的 {item.Mod.Files.Count} 个文件吗？此操作不可撤销。")) return;
            Deployer.DeleteExternal(item.Mod);
            RebuildExternals();
            ShowMessage(InfoBarSeverity.Success, "已删除", $"「{item.Name}」的外部文件已删除。");
        };
        flyout.Items.Add(delete);

        flyout.ShowAt(ExternalList, e.GetPosition(ExternalList));
    }

    // ---------------------------------------------------------------- misc

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildMods();
        }
    }

    private double _lastAvailableWidth;

    /// <summary>Keep the detail pane proportional when the window grows/shrinks.</summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width;
        if (available <= 0) return;
        var current = DetailColumn.ActualWidth > 0 ? DetailColumn.ActualWidth : DetailColumn.Width.Value;
        if (_lastAvailableWidth <= 0 || _lastAvailableWidth == available)
        {
            _lastAvailableWidth = available;
            return;
        }

        var ratio = current / _lastAvailableWidth;
        var max = Math.Max(MinDetailWidth, available * 0.45);
        var target = Math.Clamp(ratio * available, MinDetailWidth, max);
        DetailColumn.Width = new GridLength(target);
        State.Config.ModsDetailWidth = target;
        _lastAvailableWidth = available;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterRebuild) return;
        RebuildMods();
    }

    private bool _suppressFilterRebuild;

    private void UpdateCnsFilters()
    {
        _suppressFilterRebuild = true;
        try
        {
            var selectedRole = RoleFilter.SelectedItem as string;
            var selectedType = TypeFilter.SelectedItem as string;

            var roles = State.Library.SelectMany(r => r.CnsCharacters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var types = State.Library.SelectMany(r => r.CnsOutfitTypes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RoleFilter.ItemsSource = new[] { "全部角色" }.Concat(roles).ToList();
            TypeFilter.ItemsSource = new[] { "全部类型" }.Concat(types).ToList();
            RoleFilter.SelectedItem = selectedRole is not null && roles.Contains(selectedRole, StringComparer.OrdinalIgnoreCase)
                ? selectedRole
                : "全部角色";
            TypeFilter.SelectedItem = selectedType is not null && types.Contains(selectedType, StringComparer.OrdinalIgnoreCase)
                ? selectedType
                : "全部类型";
        }
        finally
        {
            _suppressFilterRebuild = false;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        ShowMessage(InfoBarSeverity.Informational, "已刷新", "模组库与游戏目录已重新扫描。");
    }

    private void Selector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var external = sender.SelectedItem is { } item && sender.Items.IndexOf(item) > 0;
        ModsView.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        ExternalView.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDir is null)
        {
            ShowMessage(InfoBarSeverity.Warning, "未设置游戏目录", "请先在「设置」中指定游戏安装目录。");
            return;
        }
        GameLocator.OpenFolder(Deployer.PaksModsDir(GameDir));
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
        => GameLocator.OpenFolder(AppPaths.ModsRoot);

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDir is null)
        {
            ShowMessage(InfoBarSeverity.Warning, "未设置游戏目录", "请先在「设置」中指定游戏安装目录。");
            return;
        }
        GameLocator.OpenFolder(GameDir);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Log.OpenLogFolder();

    private void More_Click(object sender, RoutedEventArgs e)
    {
        // Flyout is shown by the button itself.
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GameLocator.Launch(GameDir, State.Config.LaunchMode);
            ShowMessage(InfoBarSeverity.Informational, "正在启动", "已发送启动请求，请稍候…");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "启动失败", ex.Message);
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "导入到模组库";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFile file && (ArchiveService.IsArchive(file.Path) || ArchiveService.IsPakPart(file.Path)))
            {
                files.Add(file.Path);
            }
            else if (item is StorageFolder folder)
            {
                SetBusy(true);
                var result = ModImporter.ImportDirectory(folder.Path, folder.Name, folder.Path, move: false);
                SetBusy(false);
                ApplyImportResult(result);
            }
        }
        if (files.Count > 0)
        {
            await ImportFilesAsync(files);
        }
    }

    // ---------------------------------------------------------- versions / tags / order



    private void EnsurePriorities()
    {
        var ordered = Mods.OrderBy(m => m.Record.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var needsFix = ordered.Select(m => m.Record.Priority).Distinct().Count() != ordered.Count;
        if (!needsFix) return;
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Record.Priority = i;
            ModStore.Save(ordered[i].Record);
        }
    }

    // ---------------------------------------------------------- updates / conflicts














    private void OpenLoadOrder_Click(object sender, RoutedEventArgs e)
        => (App.MainWindow as MainWindow)?.SelectPage("loadorder");

    // ---------------------------------------------------------- nxm:// handoff

    /// <summary>Queue an nxm request from another launch; safe to call before the page loads.</summary>
    public static void QueueExternalNxm(string url)
    {
        lock (PendingLock)
        {
            PendingExternal.Enqueue(url);
        }
    }

    /// <summary>Drain queued external nxm requests (called when the page is loaded or re-activated).</summary>
    public void HandlePendingExternal()
    {
        if (!IsLoaded) return;
        while (true)
        {
            string url;
            lock (PendingLock)
            {
                if (PendingExternal.Count == 0) return;
                url = PendingExternal.Dequeue();
            }
            _ = HandleNxmAsync(url);
        }
    }




    private static Microsoft.UI.Xaml.Media.Brush? ResourceBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

    private static Style? ResourceStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    // ---------------------------------------------------------- Nexus collections






}
