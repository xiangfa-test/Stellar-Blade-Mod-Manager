using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SBModManager.Models;
using SBModManager.Services;
using SBModManager.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SBModManager.Views;

public sealed partial class ModsPage : Page
{
    private bool _initialized;
    private static bool _shownDataDirNotice;

    public ModsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;
                Reload();
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
        Mods.Clear();
        foreach (var record in State.Library.OrderByDescending(r => r.ImportedAt))
        {
            if (filter.Length > 0 &&
                !record.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.DisplayType.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Mods.Add(new ModItem(record, State.IsEnabled(record.Id)));
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
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            foreach (var ext in new[] { ".zip", ".7z", ".rar" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            InitPicker(picker);
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await ImportFilesAsync(files.Select(f => f.Path).ToList());
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
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            InitPicker(picker);
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            SetBusy(true);
            var result = ModImporter.ImportPakFiles(files.Select(f => f.Path).ToList());
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
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            InitPicker(picker);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            SetBusy(true);
            var result = ModImporter.ImportDirectory(folder.Path, folder.Name, folder.Path, move: false);
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

    private static void InitPicker(object picker)
    {
        if (App.MainWindow is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

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
}
