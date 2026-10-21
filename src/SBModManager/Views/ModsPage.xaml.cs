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

    public ModsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;
                Reload();
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

    // ---------------------------------------------------------------- reload

    private void Reload()
    {
        State.ReloadLibrary();
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

    private void RebuildMods()
    {
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
                !record.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
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
        MsgBar.IsOpen = true;
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
                : ModImporter.ImportArchive(path);
            if (result.Mod is not null) imported++;
            else errors.Add($"{Path.GetFileName(path)}: {result.Error}");
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

    private void ApplyImportResult(ImportResult result)
    {
        Reload();
        if (result.Mod is not null)
        {
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
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not ModItem item) return;
        if (item.Enabled == toggle.IsOn) return;
        await SetModEnabledAsync(item, toggle.IsOn);
    }

    private async Task SetModEnabledAsync(ModItem item, bool enable)
    {
        if (item.Busy) return;
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
        DetailTargetsText.Text = string.Join(Environment.NewLine, item.TargetPaths);
        DetailTags.Text = item.Record.Tags.Count == 0 ? "标签: （未设置）" : "标签: " + string.Join(" · ", item.Record.Tags);
        DetailNotes.Text = string.IsNullOrWhiteSpace(item.Record.Notes) ? "" : "备注: " + item.Record.Notes;
        DetailPriority.Text = $"加载顺序 {item.PriorityText}";
        DetailUpdate.Text = item.Record.UpdateAvailable
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
        if (!await ConfirmAsync("删除模组", $"确定要从库中删除「{item.Name}」吗？\n如果它已启用，游戏中的文件也会一并移除。")) return;

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

        var tagsBox = new TextBox
        {
            Text = string.Join(", ", item.Record.Tags),
            PlaceholderText = "用逗号分隔，例如：服装, NSFW, 替换",
        };
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
        panel.Children.Add(tagsBox);
        panel.Children.Add(new TextBlock { Text = "备注" });
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑标签 / 备注",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        item.Record.Tags = tagsBox.Text
            .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private void MovePriorityUp_Click(object sender, RoutedEventArgs e) => MovePriority(-1);

    private void MovePriorityDown_Click(object sender, RoutedEventArgs e) => MovePriority(+1);

    private void MovePriority(int delta)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        EnsurePriorities();
        var ordered = Mods.OrderBy(m => m.Record.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var index = ordered.FindIndex(m => m.Record.Id == item.Record.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return;

        var other = ordered[target];
        (item.Record.Priority, other.Record.Priority) = (other.Record.Priority, item.Record.Priority);
        ModStore.Save(item.Record);
        ModStore.Save(other.Record);
        RebuildMods();
        UpdateDetails(Mods.FirstOrDefault(m => m.Record.Id == item.Record.Id));
        ShowMessage(InfoBarSeverity.Informational, "加载顺序已调整", "点击「按加载顺序重建部署」让新顺序生效。");
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
                var info = await NexusService.CheckAsync(record, key, State.Config.NexusGameDomain);
                if (info.HasUpdate) updates++;
                if (!string.IsNullOrEmpty(info.Error)) errors++;
            }
        }
        finally
        {
            SetBusy(false);
        }

        RebuildMods();
        UpdateDetails(ModList.SelectedItem as ModItem);
        ShowMessage(updates > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            "更新检查完成",
            $"已检查 {candidates.Count} 个模组，发现 {updates} 个更新" + (errors > 0 ? $"，{errors} 个失败" : "") + "。");
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
            RebuildMods();
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

    private void RedeployInOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGame(out var gameDir)) return;
        EnsurePriorities();
        var (ok, failed) = Deployer.RedeployAll(gameDir);
        RebuildMods();
        UpdateStatus();
        UpdateDetails(ModList.SelectedItem as ModItem);
        ShowMessage(failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            "加载顺序已应用",
            $"成功重建 {ok} 个模组" + (failed > 0 ? $"，{failed} 个失败（见日志）" : "") + "。");
    }

    // ---------------------------------------------------------- nxm:// handoff

    private async Task HandleNxmAsync(string url)
    {
        if (!NxmHandler.TryParse(url, out var link))
        {
            ShowMessage(InfoBarSeverity.Warning, "无法识别的 nxm 链接", url);
            return;
        }

        var key = State.Config.NexusApiKey;
        var domain = string.IsNullOrWhiteSpace(link.Domain) ? State.Config.NexusGameDomain : link.Domain;
        if (string.IsNullOrWhiteSpace(key))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "收到 Nexus 下载链接",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{link.PageUrl}\n\n自动下载需要 Nexus API Key（Premium 账号才能取直链）。\n请到「设置」填写 API Key，或先打开页面手动下载。",
                },
                PrimaryButtonText = "打开 Nexus 页面",
                CloseButtonText = "关闭",
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await Launcher.LaunchUriAsync(new Uri(link.PageUrl));
            }
            return;
        }

        SetBusy(true);
        var downloaded = Path.Combine(AppPaths.TempRoot, $"nxm-{link.ModId}-{link.FileId}.zip");
        try
        {
            Directory.CreateDirectory(AppPaths.TempRoot);
            var uri = await NexusService.GetDownloadLinkAsync(domain, link.ModId, link.FileId, key);
            if (string.IsNullOrWhiteSpace(uri))
            {
                ShowMessage(InfoBarSeverity.Warning, "无法获取下载地址", "Nexus 没有返回直链（可能不是 Premium 账号）。");
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
            if (string.IsNullOrWhiteSpace(record.Version)) record.Version = "file " + link.FileId;
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
            ShowMessage(InfoBarSeverity.Error, "nxm 下载失败",
                ex.Message + "\n\n提示：Nexus 直链下载需要 Premium 账号 + API Key。");
        }
        finally
        {
            SetBusy(false);
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { /* ignore */ }
        }
    }

}
