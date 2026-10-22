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
        DetailsScroll.SizeChanged += (_, _) => ApplyDetailButtonLayout();
        Ui.ScrollBarAutoHide.Attach(DetailsScroll);
        Ui.ScrollBarAutoHide.AttachToElement(ModList);
        Ui.ScrollBarAutoHide.AttachToElement(ExternalList);
        Loaded += (_, _) =>
        {
            ApplyDetailButtonLayout();
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

    public ObservableCollection<ModItem> Mods { get; } = new();

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
                foreach (var stat in Deployer.BuildConflictReport(gameDir).HighConflictMods)
                {
                    conflictCounts[stat.ModId] = stat;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("冲突统计失败: " + ex.Message);
            }
        }

        Mods.Clear();
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
            Mods.Add(item);
        }

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

    private void AddFomodDescription(Panel panel, FomodPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Description)) return;
        panel.Children.Add(new TextBlock
        {
            Text = plugin.Description,
            Margin = new Thickness(28, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
            Style = ResourceStyle("CaptionTextBlockStyle"),
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });
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
        // A recycled ListView container can report a stale item that no longer belongs to the
        // current list; never let it change the library.
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
                if (!string.IsNullOrWhiteSpace(item.Record.GroupKey))
                {
                    foreach (var sibling in Mods.Where(m => m != item && m.Enabled &&
                                 string.Equals(m.Record.GroupKey, item.Record.GroupKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        Deployer.Undeploy(sibling.Record.Id);
                        sibling.Enabled = false;
                    }
                }

                var result = Deployer.Deploy(item.Record, gameDir, force: false);
                if (!result.Success && result.Conflicts.Count > 0)
                {
                    var overwrite = await AskForceDeployAsync(item, result.Conflicts);
                    if (!overwrite)
                    {
                        ShowMessage(InfoBarSeverity.Warning, "已取消", "存在文件冲突，未启用该模组。");
                        return;
                    }
                    result = Deployer.Deploy(item.Record, gameDir, force: true);
                }

                if (!result.Success)
                {
                    ShowMessage(InfoBarSeverity.Error, "启用失败", result.Error ?? "未知错误");
                    return;
                }

                item.Enabled = true;
                ShowMessage(InfoBarSeverity.Success, "已启用", $"「{item.Name}」已部署到游戏目录。");
            }
            else
            {
                Deployer.Undeploy(item.Record.Id);
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
                var result = Deployer.Deploy(item.Record, gameDir, force: false);
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
            Deployer.Undeploy(item.Record.Id);
            item.Enabled = false;
        }
        UpdateStatus();
        UpdateDetails(ModList.SelectedItem as ModItem);
        ShowMessage(InfoBarSeverity.Success, "已全部停用", "所有由本工具部署的文件已从游戏目录移除。");
    }

    // ---------------------------------------------------------------- details

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDetails(ModList.SelectedItem as ModItem);

    private void UpdateDetails(ModItem? item)
    {
        if (item is null)
        {
            DetailsEmpty.Visibility = Visibility.Visible;
            DetailsScroll.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsEmpty.Visibility = Visibility.Collapsed;
        DetailsScroll.Visibility = Visibility.Visible;
        DetailName.Text = item.Name;
        DetailSubtitle.Text = item.Subtitle;
        DetailMeta.Text = item.ImportedText + "   ·   " + item.FileCountText +
                          (State.IsEnabled(item.Record.Id) ? "   ·   当前已部署" : "");
        DetailSource.Text = string.IsNullOrWhiteSpace(item.SourceText) ? "" : "来源: " + item.SourceText;
        DetailPreview.Source = null;
        DetailPreview.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(item.Record.PreviewImage))
        {
            var previewPath = Path.Combine(item.Record.ModDir, item.Record.PreviewImage);
            if (File.Exists(previewPath))
            {
                try
                {
                    DetailPreview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(previewPath));
                    DetailPreview.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    Log.Warn("加载预览图失败: " + ex.Message);
                }
            }
        }
        DetailTargetsText.Text = string.Join(Environment.NewLine, item.TargetPaths);
        ApplyDetailButtonLayout();
        DetailTags.Text = item.Record.Tags.Count == 0 ? "标签: （未设置）" : "标签: " + string.Join(" · ", item.Record.Tags);
        var cnsParts = new List<string>();
        if (item.Record.CnsFits.Count > 0) cnsParts.Add("服装: " + string.Join(" / ", item.Record.CnsFits.Take(6)));
        if (item.Record.CnsCharacters.Count > 0) cnsParts.Add("角色: " + string.Join(" / ", item.Record.CnsCharacters));
        if (item.Record.CnsOutfitTypes.Count > 0) cnsParts.Add("类型: " + string.Join(" / ", item.Record.CnsOutfitTypes.Take(4)));
        if (item.Record.CnsRequirements.Count > 0) cnsParts.Add("依赖: " + string.Join(" / ", item.Record.CnsRequirements.Take(4)));
        DetailCns.Text = cnsParts.Count == 0 ? "" : "CNS  " + string.Join("   ·   ", cnsParts);
        DetailCns.Visibility = DetailCns.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNotes.Text = string.IsNullOrWhiteSpace(item.Record.Notes) ? "" : "备注: " + item.Record.Notes;
        var showUpdate = item.Record.UpdateAvailable && !item.Record.IgnoreUpdate;
        UpdateModButton.Visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;
        IgnoreUpdateButton.Content = item.Record.IgnoreUpdate ? "恢复更新提示" : "忽略此更新";
        IgnoreUpdateButton.Visibility = item.Record.UpdateAvailable || item.Record.IgnoreUpdate
            ? Visibility.Visible
            : Visibility.Collapsed;
        NexusPageButton.Visibility = item.Record.NexusModId is > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailNexusThumb.Source = null;
        DetailNexusThumb.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(item.Record.NexusThumbnailUrl))
        {
            try
            {
                DetailNexusThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(item.Record.NexusThumbnailUrl));
                DetailNexusThumb.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Warn("加载 Nexus 缩略图失败: " + ex.Message);
            }
        }
        DetailUpdate.Text = item.Record.IgnoreUpdate
            ? "已忽略此模组的更新提示"
            : item.Record.UpdateAvailable
            ? $"可更新: {item.Record.VersionLabel} → {item.Record.LatestVersion}"
            : (item.Record.LastCheckedAt is { } checkedAt
                ? $"已是最新（检查于 {checkedAt.LocalDateTime:MM-dd HH:mm}）"
                : "");
        DetailUpdate.Visibility = string.IsNullOrEmpty(DetailUpdate.Text) ? Visibility.Collapsed : Visibility.Visible;

        _suppressVersionChange = true;
        try
        {
            var siblings = Mods
                .Where(m => m.Record.Id != item.Record.Id &&
                            !string.IsNullOrWhiteSpace(m.Record.GroupKey) &&
                            string.Equals(m.Record.GroupKey, item.Record.GroupKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Record.ImportedAt)
                .ToList();
            if (siblings.Count == 0)
            {
                VersionPanel.Visibility = Visibility.Collapsed;
                VersionBox.ItemsSource = null;
            }
            else
            {
                var versions = new List<ModItem> { item };
                versions.AddRange(siblings);
                VersionPanel.Visibility = Visibility.Visible;
                VersionBox.ItemsSource = versions;
                VersionBox.SelectedItem = item;
            }
        }
        finally
        {
            _suppressVersionChange = false;
        }
    }

    private void OpenModDir_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is ModItem item)
        {
            GameLocator.OpenFolder(item.Record.ModDir);
        }
    }

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
            Deployer.Undeploy(item.Record.Id);
            ModStore.Delete(item.Record);
            Reload();
            ShowMessage(InfoBarSeverity.Success, "已删除", $"「{item.Name}」已从模组库删除。");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "删除失败", ex.Message);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

    /// <summary>Stack the detail buttons vertically when the pane is too narrow for a row.</summary>
    private void ApplyDetailButtonLayout()
    {
        var width = DetailColumn.ActualWidth > 0 ? DetailColumn.ActualWidth : DetailColumn.Width.Value;
        if (width <= 0) return;
        var compact = width < 420;
        foreach (var row in new[] { ActionsRow, FolderRow })
        {
            row.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
            row.Spacing = compact ? 6 : 8;
        }
    }

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
        ApplyDetailButtonLayout();
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

    private async void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVersionChange) return;
        if (VersionBox.SelectedItem is not ModItem selected) return;
        if (ModList.SelectedItem is ModItem current && current.Record.Id == selected.Record.Id) return;

        if (!selected.Enabled)
        {
            await SetModEnabledAsync(selected, true);
        }
        ModList.SelectedItem = selected;
        UpdateDetails(selected);
    }

    private async void EditMeta_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item)
        {
            ShowMessage(InfoBarSeverity.Warning, "未选择模组", "先在左侧选择一个模组。");
            return;
        }

        var localTag = CollectionInstaller.LocalTag;
        var tags = new List<string>(item.Record.Tags);

        // Existing user tags across the library, most used first, for one-click adding.
        var suggestions = State.Library
            .SelectMany(r => r.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t) &&
                        !t.StartsWith("合集:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .Take(16)
            .ToList();

        var tagPanel = new StackPanel { Spacing = 4 };
        var suggestPanel = new StackPanel { Spacing = 6 };
        var tagInput = new TextBox
        {
            PlaceholderText = "输入标签后按回车添加，例如：服装",
        };
        var tagHint = new TextBlock
        {
            Text = "「" + localTag + "」是默认合集标签，不可移除；其它标签点 × 删除。",
            TextWrapping = TextWrapping.Wrap,
            Style = ResourceStyle("CaptionTextBlockStyle"),
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        };

        void AddTag(string raw)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 24) text = text[..24];
            if (!tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add(text);
            }
        }

        void RebuildSuggestions()
        {
            suggestPanel.Children.Clear();
            var available = suggestions
                .Where(t => !tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (available.Count == 0)
            {
                suggestPanel.Children.Add(new TextBlock
                {
                    Text = "（暂无其它可用标签）",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });
                return;
            }

            // Lay the quick-add buttons out in rows of four.
            for (var i = 0; i < available.Count; i += 4)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var tag in available.Skip(i).Take(4))
                {
                    var add = new Button
                    {
                        Content = "+ " + tag,
                        Padding = new Thickness(8, 2, 8, 2),
                        FontSize = 12,
                    };
                    add.Click += (_, _) =>
                    {
                        AddTag(tag);
                        RebuildChips();
                    };
                    row.Children.Add(add);
                }
                suggestPanel.Children.Add(row);
            }
        }

        void RebuildChips()
        {
            tagPanel.Children.Clear();
            if (tags.Count == 0)
            {
                tagPanel.Children.Add(new TextBlock
                {
                    Text = "（还没有标签，输入后按回车添加）",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });
            }

            foreach (var tag in tags.ToList())
            {
                var isLocal = string.Equals(tag, localTag, StringComparison.OrdinalIgnoreCase);

                var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                chipRow.Children.Add(new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center });

                if (isLocal)
                {
                    chipRow.Children.Add(new TextBlock
                    {
                        Text = "默认",
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = ResourceStyle("CaptionTextBlockStyle"),
                        Foreground = ResourceBrush("TextFillColorTertiaryBrush"),
                    });
                }
                else
                {
                    var remove = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                        Padding = new Thickness(4, 2, 4, 2),
                        Background = null,
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    ToolTipService.SetToolTip(remove, "移除标签");
                    remove.Click += (_, _) =>
                    {
                        tags.Remove(tag);
                        RebuildChips();
                    };
                    chipRow.Children.Add(remove);
                }

                tagPanel.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush"),
                    Background = ResourceBrush("SubtleFillColorSecondaryBrush"),
                    Child = chipRow,
                });
            }

            RebuildSuggestions();
        }

        tagInput.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            AddTag(tagInput.Text);
            tagInput.Text = "";
            RebuildChips();
        };

        RebuildChips();

        var notesBox = new TextBox
        {
            Text = item.Record.Notes,
            AcceptsReturn = true,
            Height = 96,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "自定义备注（仅本地保存）",
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "标签" });
        panel.Children.Add(tagPanel);
        panel.Children.Add(tagInput);
        panel.Children.Add(tagHint);
        panel.Children.Add(new TextBlock { Text = "快速添加已有标签" });
        panel.Children.Add(suggestPanel);
        panel.Children.Add(new TextBlock { Text = "备注" });
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑标签 / 备注",
            Content = new ScrollViewer { MaxHeight = 480, Content = panel },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // The default local collection tag is part of the collection membership and is kept.
        if (item.Record.Tags.Any(t => string.Equals(t, localTag, StringComparison.OrdinalIgnoreCase)) &&
            !tags.Any(t => string.Equals(t, localTag, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Insert(0, localTag);
        }

        item.Record.Tags = tags;
        item.Record.Notes = notesBox.Text.Trim();
        ModStore.Save(item.Record);
        item.Refresh();
        RebuildMods();
        UpdateDetails(item);
        ShowMessage(InfoBarSeverity.Success, "已保存", $"「{item.Name}」的标签与备注已更新。");
    }

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

    private async void CheckOneUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is ModItem item)
        {
            await CheckUpdateAsync(item);
        }
    }

    private async void CheckAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowMessage(InfoBarSeverity.Warning, "缺少 Nexus API Key", "请先在「设置」中填写 Nexus API Key 才能检查更新。");
            return;
        }

        var candidates = State.Library.Where(r => r.NexusModId is > 0).ToList();
        if (candidates.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "没有可检查的模组",
                "只有通过 nxm:// 链接导入、带 Nexus 模组 ID 的模组才能自动检查更新。");
            return;
        }

        SetBusy(true);
        var updates = 0;
        var errors = 0;
        try
        {
            foreach (var record in candidates)
            {
                if (record.IgnoreUpdate) continue;
                var info = await NexusService.CheckAsync(record, key, State.Config.NexusGameDomain);
                if (info.HasUpdate) updates++;
                if (!string.IsNullOrEmpty(info.Error)) errors++;
            }
        }
        finally
        {
            SetBusy(false);
        }

        foreach (var candidate in Mods)
        {
            candidate.Refresh();
        }
        UpdateDetails(ModList.SelectedItem as ModItem);

        var updated = candidates
            .Where(r => r.UpdateAvailable && !r.IgnoreUpdate)
            .Select(r => $"「{r.Name}」{r.VersionLabel} → {r.LatestVersion}")
            .Take(8)
            .ToList();
        var detailText = updates > 0
            ? "可更新的模组：\n" + string.Join("\n", updated) +
              (updates > updated.Count ? $"\n…共 {updates} 个" : "")
            : $"已检查 {candidates.Count} 个模组，没有发现更新。";
        if (errors > 0) detailText += $"\n{errors} 个检查失败（见日志）。";

        ShowMessage(updates > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            "更新检查完成", detailText);
    }

    private async Task CheckUpdateAsync(ModItem item)
    {
        SetBusy(true);
        try
        {
            var info = await NexusService.CheckAsync(item.Record, State.Config.NexusApiKey ?? "",
                State.Config.NexusGameDomain);
            if (!string.IsNullOrEmpty(info.Error))
            {
                ShowMessage(InfoBarSeverity.Warning, "检查更新失败", info.Error);
            }
            else if (info.HasUpdate)
            {
                ShowMessage(InfoBarSeverity.Success, "发现新版本",
                    $"「{item.Name}」: {info.CurrentVersion} → {info.LatestVersion}" +
                    (string.IsNullOrWhiteSpace(info.PageUrl) ? "" : "\n" + info.PageUrl));
            }
            else
            {
                ShowMessage(InfoBarSeverity.Informational, "已是最新", $"「{item.Name}」没有检测到更新。");
            }
        }
        finally
        {
            SetBusy(false);
            item.Refresh();
            UpdateDetails(item);
        }
    }

    private void ShowConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGame(out var gameDir)) return;

        var report = Deployer.BuildConflictReport(gameDir);
        var sb = new StringBuilder();
        sb.AppendLine($"共 {report.ConflictFileCount} 个文件被多个已启用模组同时写入。");

        if (report.HighConflictMods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("按冲突数量排序的模组（高冲突 = 更容易被覆盖）：");
            foreach (var stat in report.HighConflictMods.Take(12))
            {
                sb.AppendLine($"  {(stat.IsHigh ? "⚠" : "·")} {stat.ModName}  ×{stat.ConflictCount}");
            }
        }

        if (report.Chains.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("当前已启用的模组之间没有文件级冲突。");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("冲突链（左侧先加载，右侧覆盖左侧）：");
            foreach (var chain in report.Chains.Take(30))
            {
                sb.AppendLine();
                sb.AppendLine("  " + Path.GetFileName(chain.TargetPath));
                sb.AppendLine("    " + chain.ChainText);
            }
            if (report.Chains.Count > 30)
            {
                sb.AppendLine();
                sb.AppendLine($"…以及另外 {report.Chains.Count - 30} 个冲突文件");
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "冲突链分析",
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock
                {
                    Text = sb.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "关闭",
        };
        _ = dialog.ShowAsync();
    }

    private async void ShowAssetConflicts_Click(object sender, RoutedEventArgs e)
    {
        var enabled = State.Library.Where(m => State.IsEnabled(m.Id)).ToList();
        if (enabled.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "没有已启用的模组", "先启用模组再分析。");
            return;
        }

        var progressDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "资源级冲突分析",
            Content = new TextBlock { Text = "正在读取 pak 的 .utoc 资源清单…", TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "取消",
        };
        _ = progressDialog.ShowAsync();

        var conflicts = await Task.Run(() => PakInspector.FindConflicts(enabled));
        progressDialog.Hide();

        if (conflicts.Count == 0)
        {
            await ShowInfoDialogAsync("资源级冲突分析",
                "没有发现同名资源。\n\n注意：这是近似分析，只读取 .utoc 中明文存在的资源名；" +
                "加密或压缩的 TOC 无法解析。");
            return;
        }

        var lines = conflicts
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key)
            .Take(50)
            .Select(kv => $"• {kv.Key}\n    {string.Join(" / ", kv.Value.Distinct())}");
        await ShowInfoDialogAsync("资源级冲突分析",
            $"发现 {conflicts.Count} 个资源被多个模组包含（显示前 50 个）：\n\n" +
            string.Join("\n", lines) +
            "\n\n后加载的模组会覆盖前面的资源（可用 PAK 顺序前缀控制）。");
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

    private async void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var record = item.Record;
        if (record.NexusModId is not > 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "无法更新", "这个模组没有 Nexus 模组 ID。");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "下载并更新",
            Content = $"将从 Nexus 下载最新主文件并替换「{record.Name}」。\n标签、备注、加载顺序和启用状态会保留。",
            PrimaryButtonText = "更新",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(text => ShowMessage(InfoBarSeverity.Informational, "更新中", text));
            var updated = await ModUpdater.UpdateAsync(record, State.Config.NexusApiKey ?? "",
                State.Config.NexusApiKey, progress);
            Reload();
            ShowMessage(InfoBarSeverity.Success, "更新完成",
                $"「{updated.Name}」已更新到 {updated.VersionLabel}。");
        }
        catch (Exception ex)
        {
            Log.Error("模组更新失败", ex);
            ShowMessage(InfoBarSeverity.Error, "更新失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
            Reload();
        }
    }

    private async void OpenNexusPage_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var url = item.Record.NexusPageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private async void RenameNexusNames_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(State.Config.NexusApiKey))
        {
            ShowMessage(InfoBarSeverity.Warning, "缺少 Nexus API Key", "请先在「设置」中填写 Nexus API Key。");
            return;
        }

        SetBusy(true);
        try
        {
            var renamed = await ModUpdater.RenameGenericAsync();
            Reload();
            ShowMessage(renamed > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                "补全名称", renamed > 0 ? $"已从 N 网补全 {renamed} 个模组的名称。" : "没有需要补全的模组。");
        }
        catch (Exception ex)
        {
            Log.Error("补全 N 网名称失败", ex);
            ShowMessage(InfoBarSeverity.Error, "补全失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void IgnoreUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        item.Record.IgnoreUpdate = !item.Record.IgnoreUpdate;
        ModStore.Save(item.Record);
        UpdateDetails(item);
        ShowMessage(InfoBarSeverity.Informational, "更新提示",
            item.Record.IgnoreUpdate ? $"已忽略「{item.Name}」的更新提示。" : "已恢复更新提示。");
    }

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

    private async Task HandleNxmAsync(string url)
    {
        if (!NxmHandler.TryParse(url, out var link))
        {
            ShowMessage(InfoBarSeverity.Warning, "无法识别的 nxm 链接", url);
            return;
        }

        if (link.IsCollection)
        {
            (App.MainWindow as MainWindow)?.OpenCollection(link.CollectionSlug!);
            return;
        }

        var apiKey = State.Config.NexusApiKey;
        var domain = string.IsNullOrWhiteSpace(link.Domain) ? State.Config.NexusGameDomain : link.Domain;
        var hasKey = !string.IsNullOrWhiteSpace(link.Key);
        var fallbackUrl = $"https://www.nexusmods.com/{domain}/mods/{link.ModId}?tab=files&file_id={link.FileId}&nmm=1";

        // A keyless link can only be resolved by premium accounts (this is exactly what
        // Vortex does too: free users get sent to the website for those links).
        if (!hasKey && string.IsNullOrWhiteSpace(apiKey))
        {
            await ShowNxmFallbackAsync(link,
                "这个链接没有携带 Nexus 的下载授权 key（通常来自手动拼的 nxm 链接）。\n" +
                "请到 Nexus 文件页点「Mod Manager Download」重新获取链接（会带 key），或手动下载后导入。",
                fallbackUrl);
            return;
        }

        SetBusy(true);
        var downloaded = Path.Combine(AppPaths.TempRoot, $"nxm-{link.ModId}-{link.FileId}.zip");
        try
        {
            Directory.CreateDirectory(AppPaths.TempRoot);
            var uri = await NexusService.GetDownloadLinkAsync(domain, link.ModId, link.FileId, apiKey,
                link.Key, link.Expires);
            if (string.IsNullOrWhiteSpace(uri))
            {
                await ShowNxmFallbackAsync(link,
                    "Nexus 没有返回下载地址。\n免费账号需要带授权 key 的 nxm 链接（网页「Mod Manager Download」生成）；" +
                    "不带 key 的链接只有 Premium 能取直链。",
                    fallbackUrl);
                return;
            }

            await GitHubService.DownloadAsync(uri, downloaded);
            var result = ModImporter.ImportArchive(downloaded);
            if (result.Mod is null)
            {
                ShowMessage(InfoBarSeverity.Error, "导入失败", result.Error);
                return;
            }

            var record = result.Mod;
            record.NexusModId = link.ModId;
            record.NexusFileId = link.FileId;
            record.NexusDomain = domain;
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);

            // Keep the Nexus page title instead of the archive filename.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var info = await NexusService.GetModInfoAsync(domain, link.ModId, apiKey);
                if (info is { Name.Length: > 0 })
                {
                    record.Name = info.Value.Name.Trim();
                    record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
                    if (!string.IsNullOrWhiteSpace(info.Value.Version))
                    {
                        record.Version = info.Value.Version;
                    }
                    if (!string.IsNullOrWhiteSpace(info.Value.PictureUrl))
                    {
                        record.NexusThumbnailUrl = info.Value.PictureUrl;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(record.Version)) record.Version = "file " + link.FileId;
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
            CollectionInstaller.TagAsLocal(record);
            ModStore.Save(record);
            Reload();

            var ask = await ConfirmAsync("下载完成", $"已下载并导入「{record.Name}」。是否立即启用？");
            if (ask)
            {
                var item = Mods.FirstOrDefault(m => m.Record.Id == record.Id);
                if (item is not null) await SetModEnabledAsync(item, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("nxm 下载失败", ex);
            var message = ex.Message +
                          (string.IsNullOrWhiteSpace(apiKey)
                              ? "\n\n提示：请先在「设置 → 网络与集成」填写 Nexus API Key。"
                              : "") +
                          (hasKey ? "" : "\n\n提示：不带 key 的链接只有 Premium 账号能直接取直链。");
            await ShowNxmFallbackAsync(link, message, fallbackUrl);
        }
        finally
        {
            SetBusy(false);
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { /* ignore */ }
        }
    }

    private async Task ShowNxmFallbackAsync(NxmLink link, string message, string fallbackUrl)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Nexus 下载链接",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Text = message + "\n\n" + fallbackUrl,
            },
            PrimaryButtonText = "打开 Nexus 页面",
            CloseButtonText = "关闭",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri(fallbackUrl));
        }
    }


    private static Microsoft.UI.Xaml.Media.Brush? ResourceBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

    private static Style? ResourceStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    // ---------------------------------------------------------- Nexus collections

    private async void ImportCollection_Click(object sender, RoutedEventArgs e)
    {
        var urlBox = new TextBox
        {
            PlaceholderText = "https://www.nexusmods.com/games/stellarblade/collections/n7zgom",
            Text = await TryGetCollectionFromClipboardAsync(),
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "粘贴 Nexus 合集链接（或只填 slug）。解析只读取公开信息，不会下载任何文件。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(urlBox);
        panel.Children.Add(new TextBlock
        {
            Text = "下载模组需要 Nexus API Key；批量直链通常需要 Premium 账号，免费账号建议用「复制模组链接」逐个下载。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            Style = ResourceStyle("CaptionTextBlockStyle"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从 Nexus 合集导入",
            Content = panel,
            PrimaryButtonText = "解析合集",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var input = urlBox.Text.Trim();
        if (input.Length == 0) return;

        SetBusy(true);
        NexusCollection collection;
        try
        {
            collection = await NexusService.GetCollectionAsync(input, State.Config.NexusApiKey);
        }
        catch (Exception ex)
        {
            Log.Error("解析 Nexus 合集失败", ex);
            ShowMessage(InfoBarSeverity.Error, "合集解析失败", ex.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await ShowCollectionAsync(collection);
    }

    private static async Task<string> TryGetCollectionFromClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = (await view.GetTextAsync()).Trim();
                if (text.Contains("/collections/", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }
        }
        catch
        {
            // clipboard may be unavailable
        }
        return "";
    }

    private async Task ShowCollectionAsync(NexusCollection collection)
    {
        var required = collection.Mods.Count(m => !m.Optional);
        var info = new StackPanel { Spacing = 8 };
        info.Children.Add(new TextBlock
        {
            Text = collection.Name,
            Style = ResourceStyle("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"作者: {collection.Author}  ·  版本 {collection.RevisionNumber}  ·  " +
                   $"{collection.ModCount} 个模组  ·  {collection.SizeText}",
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(collection.Summary))
        {
            info.Children.Add(new TextBlock { Text = collection.Summary, TextWrapping = TextWrapping.Wrap, MaxLines = 4 });
        }
        info.Children.Add(new TextBlock
        {
            Text = $"必需 {required} 个，可选 {collection.OptionalCount} 个。",
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });

        var includeOptional = new CheckBox { Content = $"包含可选模组（{collection.OptionalCount} 个）", IsChecked = false };
        var enableAfter = new CheckBox { Content = "导入后自动启用（按合集顺序）", IsChecked = false };
        info.Children.Add(includeOptional);
        info.Children.Add(enableAfter);

        var preview = collection.Mods.Take(8)
            .Select(m => "• " + m.DisplayName + (string.IsNullOrWhiteSpace(m.Version) ? "" : $" ({m.Version})") +
                         (m.Optional ? " [可选]" : ""));
        var previewText = string.Join(Environment.NewLine, preview);
        if (collection.Mods.Count > 8)
        {
            previewText += Environment.NewLine + $"… 其余 {collection.Mods.Count - 8} 个";
        }
        info.Children.Add(new TextBlock
        {
            Text = previewText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "合集信息",
            Content = new ScrollViewer { MaxHeight = 380, Content = info },
            PrimaryButtonText = "下载并导入",
            SecondaryButtonText = "复制模组链接",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            CopyCollectionLinks(collection);
            return;
        }
        if (choice != ContentDialogResult.Primary) return;

        await DownloadCollectionAsync(collection, includeOptional.IsChecked == true, enableAfter.IsChecked == true);
    }

    private void CopyCollectionLinks(NexusCollection collection)
    {
        try
        {
            var text = string.Join(Environment.NewLine, collection.Mods
                .Select(m => $"{m.DisplayName}\t{m.PageUrl}"));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowMessage(InfoBarSeverity.Success, "已复制模组链接",
                $"已复制 {collection.Mods.Count} 个模组页面链接到剪贴板，可以逐个手动下载后拖回本工具导入。");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Warning, "复制失败", ex.Message);
        }
    }

    private async Task DownloadCollectionAsync(NexusCollection collection, bool includeOptional, bool enableAfter)
    {
        var apiKey = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var fallback = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "需要 Nexus API Key",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "合集批量下载需要 Nexus API Key（在「设置 → 网络与集成」填写），" +
                           "且 Nexus 的直链接口通常要求 Premium 账号。\n\n" +
                           "免费账号可以使用「复制模组链接」，逐个下载后拖回本工具导入。",
                },
                PrimaryButtonText = "复制模组链接",
                SecondaryButtonText = "打开合集页面",
                CloseButtonText = "关闭",
            };
            var result = await fallback.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                CopyCollectionLinks(collection);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Launcher.LaunchUriAsync(new Uri(collection.PageUrl));
            }
            return;
        }

        var hasGame = EnsureGame(out var gameDir);
        if (!hasGame) enableAfter = false;

        var selected = collection.Mods.Where(m => includeOptional || !m.Optional).ToList();
        if (selected.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "没有可下载的模组", "该合集没有匹配当前筛选条件的模组。");
            return;
        }

        using var cts = new CancellationTokenSource();
        var line = new TextBlock { Text = $"0 / {selected.Count}" };
        var bar = new ProgressBar { Maximum = selected.Count, Value = 0, Height = 4 };
        var detail = new TextBlock
        {
            Text = "准备中…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(line);
        stack.Children.Add(bar);
        stack.Children.Add(detail);

        var progressDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"下载合集：{collection.Name}",
            Content = stack,
            CloseButtonText = "取消",
        };
        progressDialog.CloseButtonClick += (_, _) => cts.Cancel();
        var dialogTask = progressDialog.ShowAsync().AsTask();

        var ok = 0;
        var failed = new List<string>();
        var noLink = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            if (cts.IsCancellationRequested) break;
            var mod = selected[i];
            var index = i;
            DispatcherQueue.TryEnqueue(() =>
            {
                bar.Value = index;
                line.Text = $"{index} / {selected.Count}";
                detail.Text = "正在处理: " + mod.DisplayName;
            });

            string? temp = null;
            try
            {
                var uri = await NexusService.GetDownloadLinkAsync(collection.GameDomain, mod.ModId, mod.FileId, apiKey);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    noLink++;
                    failed.Add($"{mod.DisplayName}: Nexus 没有返回直链");
                    continue;
                }

                Directory.CreateDirectory(AppPaths.TempRoot);
                temp = Path.Combine(AppPaths.TempRoot, $"collection-{mod.ModId}-{mod.FileId}.zip");
                await GitHubService.DownloadAsync(uri, temp);
                var imported = ModImporter.ImportArchive(temp);
                if (imported.Mod is null)
                {
                    failed.Add($"{mod.DisplayName}: {imported.Error}");
                    continue;
                }

                var record = imported.Mod;
                record.NexusModId = mod.ModId;
                record.NexusFileId = mod.FileId;
                record.NexusDomain = collection.GameDomain;
                if (!string.IsNullOrWhiteSpace(mod.Version)) record.Version = mod.Version;
                var tag = "合集:" + collection.Name;
                if (!record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    record.Tags.Add(tag);
                }
                record.Notes = $"来自 Nexus 合集「{collection.Name}」(rev {collection.RevisionNumber})" +
                               Environment.NewLine + collection.PageUrl;
                ModStore.Save(record);

                if (enableAfter)
                {
                    var deployed = Deployer.Deploy(record, gameDir, force: false);
                    if (!deployed.Success)
                    {
                        failed.Add($"{mod.DisplayName}: 已导入，但启用失败（{deployed.Error}）");
                    }
                }
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.DisplayName}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (temp is not null && File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                await Task.Delay(150, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            bar.Value = ok;
            line.Text = $"完成 {ok} / {selected.Count}";
            detail.Text = "正在整理结果…";
        });

        try
        {
            progressDialog.Hide();
        }
        catch
        {
            // already closed
        }
        try
        {
            await dialogTask;
        }
        catch
        {
            // ignore
        }

        Reload();

        var summary = $"合集「{collection.Name}」：成功 {ok} / {selected.Count}";

        if (failed.Count > 0 || noLink > 0)
        {
            var lines = failed.Take(8).ToList();
            if (failed.Count > 8)
            {
                lines.Add($"…以及另外 {failed.Count - 8} 个失败");
            }
            var hint = noLink > 0 || failed.Any(f => f.Contains("403") || f.Contains("Premium", StringComparison.OrdinalIgnoreCase))
                ? "\n\n提示：Nexus 直链下载通常需要 Premium；没有 Premium 时可用「复制模组链接」手动下载。"
                : "";
            ShowMessage(InfoBarSeverity.Warning, "合集下载完成（有失败）",
                summary + "\n\n" + string.Join(Environment.NewLine, lines) + hint);
        }
        else
        {
            ShowMessage(InfoBarSeverity.Success, "合集下载完成",
                summary + "\n\n已导入的模组带有「合集:" + collection.Name + "」标签，可按标签筛选。");
        }
    }

}
