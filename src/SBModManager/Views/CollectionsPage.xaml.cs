using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SBModManager.Models;
using SBModManager.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using SBModManager.Ui;

namespace SBModManager.Views;

public sealed class CollectionModRow
{
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string InstalledText { get; set; } = "";
    public int ModId { get; set; }
    public int FileId { get; set; }
}

public sealed class InstalledCollectionRow
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public string CountText { get; set; } = "";
}

public sealed partial class CollectionsPage : Page
{
    private NexusCollection? _collection;
    private bool _suppressRevision;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CollectionModRow> Mods { get; } = new();
    public ObservableCollection<InstalledCollectionRow> Installed { get; } = new();
    public ObservableCollection<string> Recent { get; } = new();

    private static IAppState State => AppServices.State;

    private static readonly Queue<string> Pending = new();
    private static readonly object PendingLock = new();

    /// <summary>Queue a collection to open (safe before the page exists).</summary>
    public static void QueueOpen(string input)
    {
        lock (PendingLock)
        {
            Pending.Enqueue(input);
        }
    }

    public void HandlePendingOpen()
    {
        if (!IsLoaded) return;
        while (true)
        {
            string next;
            lock (PendingLock)
            {
                if (Pending.Count == 0) return;
                next = Pending.Dequeue();
            }
            UiTask.Safe(() => LoadAsync(next));
        }
    }

    public async Task LoadAsync(string input)
    {
        UrlBox.Text = input;
        await ParseAsync(input);
    }

    public CollectionsPage()
    {
        InitializeComponent();
        ModsList.ItemsSource = Mods;
        InstalledList.ItemsSource = Installed;
        RecentList.ItemsSource = Recent;
        Loaded += (_, _) =>
        {
            RefreshInstalled();
            HandlePendingOpen();
            if (Program.TakeCollectionSlug() is { Length: > 0 } slug)
            {
                UiTask.Safe(() => LoadAsync(slug));
            }
        };
    }

    private async void Parse_Click(object sender, RoutedEventArgs e)
        => await ParseAsync(UrlBox.Text);

    private async void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ParseAsync(UrlBox.Text);
        }
    }

    private async Task ParseAsync(string input, int? revision = null)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0)
        {
            StatusLine.Text = "先粘贴一个 Nexus 合集链接。";
            return;
        }

        ParseButton.IsEnabled = false;
        StatusLine.Text = "正在解析合集…";
        try
        {
            var collection = await NexusService.GetCollectionAsync(input, State.Config.NexusApiKey, revision);
            _collection = collection;
            ShowCollection(collection);
            RememberCollection(input);
            StatusLine.Text = $"已解析「{collection.Name}」：{collection.Mods.Count} 个模组（rev {collection.RevisionNumber}）。";
        }
        catch (Exception ex)
        {
            Log.Error("解析合集失败", ex);
            StatusLine.Text = "解析失败: " + ex.Message;
        }
        finally
        {
            ParseButton.IsEnabled = true;
        }
    }

    private void ShowCollection(NexusCollection collection)
    {
        CollectionCard.Visibility = Visibility.Visible;
        ModsCard.Visibility = Visibility.Visible;

        CollectionName.Text = collection.Name;
        CollectionMeta.Text = $"作者: {collection.Author}  ·  版本 {collection.RevisionNumber}  ·  " +
                              $"{collection.ModCount} 个模组  ·  {collection.SizeText}";
        CollectionSummary.Text = collection.Summary;
        CollectionSummary.Visibility = string.IsNullOrWhiteSpace(collection.Summary)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _suppressRevision = true;
        try
        {
            var revisions = collection.Revisions.Count > 0
                ? collection.Revisions
                : new List<int> { collection.RevisionNumber };
            RevisionBox.ItemsSource = revisions.Select(r => $"rev {r}").ToList();
            RevisionBox.SelectedIndex = Math.Max(0, revisions.IndexOf(collection.RevisionNumber));
        }
        finally
        {
            _suppressRevision = false;
        }

        IncludeOptionalBox.Content = $"包含可选模组（{collection.OptionalCount} 个）";
        if (collection.OptionalCount == 0)
        {
            IncludeOptionalBox.IsChecked = false;
            IncludeOptionalBox.IsEnabled = false;
        }
        else
        {
            IncludeOptionalBox.IsEnabled = true;
        }

        RefreshMods(collection);
    }

    private void RefreshMods(NexusCollection collection)
    {
        Mods.Clear();
        foreach (var mod in collection.Mods)
        {
            var installed = State.Library.Any(r =>
                r.NexusModId == mod.ModId && r.NexusFileId == mod.FileId);
            var subtitle = string.IsNullOrWhiteSpace(mod.Version) ? mod.FileName : mod.Version;
            if (mod.Optional) subtitle += "  ·  可选";
            Mods.Add(new CollectionModRow
            {
                Name = mod.DisplayName,
                Subtitle = subtitle,
                SizeText = mod.SizeText,
                InstalledText = installed ? "已安装" : "",
                ModId = mod.ModId,
                FileId = mod.FileId,
            });
        }
    }

    private async void RevisionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRevision || _collection is null) return;
        if (RevisionBox.SelectedIndex < 0) return;

        var revisions = _collection.Revisions.Count > 0
            ? _collection.Revisions
            : new List<int> { _collection.RevisionNumber };
        if (RevisionBox.SelectedIndex >= revisions.Count) return;
        var revision = revisions[RevisionBox.SelectedIndex];
        if (revision == _collection.RevisionNumber) return;

        await ParseAsync(_collection.PageUrl, revision);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_collection is null) return;

        if (string.IsNullOrWhiteSpace(State.Config.NexusApiKey))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "需要 Nexus API Key",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "合集批量下载需要 Nexus API Key（设置 → 网络与集成）。\n" +
                           "Nexus 的直链接口通常要求 Premium 账号；免费账号可以先「复制模组链接」再手动下载。",
                },
                PrimaryButtonText = "复制模组链接",
                CloseButtonText = "关闭",
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                CopyLinks();
            }
            return;
        }

        var options = new CollectionInstallOptions(
            IncludeOptionalBox.IsChecked == true,
            EnableAfterBox.IsChecked == true,
            SkipInstalledBox.IsChecked == true);

        _cts = new CancellationTokenSource();
        SetRunning(true);
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.Value = 0;
        InstallProgress.Maximum = 1;
        ProgressText.Text = "准备下载…";

        var progress = new Progress<CollectionInstallProgress>(p =>
        {
            InstallProgress.Maximum = Math.Max(1, p.Total);
            InstallProgress.Value = p.Done;
            ProgressText.Text = $"{p.Done}/{p.Total}" +
                                (string.IsNullOrWhiteSpace(p.Current) ? "" : $"  {p.Current}") +
                                (string.IsNullOrWhiteSpace(p.Status) ? "" : $"  ·  {p.Status}");
        });

        try
        {
            var report = await CollectionInstaller.InstallAsync(_collection, options, progress, _cts.Token);
            await ShowReportAsync(_collection, report);
        }
        catch (Exception ex)
        {
            Log.Error("安装合集失败", ex);
            ProgressText.Text = "安装失败: " + ex.Message;
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            RefreshInstalled();
            if (_collection is not null) RefreshMods(_collection);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ProgressText.Text = "正在停止…";
    }

    private void SetRunning(bool running)
    {
        InstallButton.IsEnabled = !running;
        ParseButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        IncludeOptionalBox.IsEnabled = !running;
        SkipInstalledBox.IsEnabled = !running;
        EnableAfterBox.IsEnabled = !running;
    }

    private async Task ShowReportAsync(NexusCollection collection, CollectionInstallReport report)
    {
        var lines = new List<string>
        {
            $"合集「{collection.Name}」",
            $"导入 {report.Imported} 个，跳过 {report.Skipped} 个，失败 {report.Failed} 个。",
        };
        if (report.Cancelled)
        {
            lines.Add("（已按请求停止）");
        }
        if (report.Errors.Count > 0)
        {
            lines.Add("");
            lines.AddRange(report.Errors.Take(10));
            if (report.Errors.Count > 10)
            {
                lines.Add($"…以及另外 {report.Errors.Count - 10} 条失败");
            }
        }

        var hasErrors = report.Failed > 0 || report.Errors.Count > 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = hasErrors ? "合集安装完成（有失败）" : "合集安装完成",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, lines),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = "查看合集模组",
            CloseButtonText = "关闭",
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            (App.MainWindow as MainWindow)?.OpenModsWithFilter(CollectionInstaller.TagFor(collection));
        }
    }

    private void CopyLinks_Click(object sender, RoutedEventArgs e) => CopyLinks();

    private void CopyLinks()
    {
        if (_collection is null) return;
        try
        {
            var text = string.Join(Environment.NewLine, _collection.Mods
                .Select(m => $"{m.DisplayName}\t{m.PageUrl}"));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusLine.Text = $"已复制 {_collection.Mods.Count} 个模组页面链接。";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "复制失败: " + ex.Message;
        }
    }

    private async void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        var url = _collection?.PageUrl;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            url = UrlBox.Text.Trim();
        }
        if (!string.IsNullOrWhiteSpace(url))
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                UrlBox.Text = (await view.GetTextAsync()).Trim();
            }
        }
        catch (Exception ex)
        {
            StatusLine.Text = "读取剪贴板失败: " + ex.Message;
        }
    }

    private void RefreshInstalled()
    {
        Installed.Clear();
        var groups = CollectionInstaller.InstalledCollections();
        var localCount = groups
            .Where(g => string.Equals(g.Tag, CollectionInstaller.LocalTag, StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Count)
            .FirstOrDefault();
        var localEnabled = State.Library
            .Where(r => r.Tags.Any(t => string.Equals(t, CollectionInstaller.LocalTag, StringComparison.OrdinalIgnoreCase)))
            .Count(r => State.IsEnabled(r.Id));

        // The built-in "local installs" collection is always present, even when empty.
        Installed.Add(new InstalledCollectionRow
        {
            Tag = CollectionInstaller.LocalTag,
            Name = "本地安装（自己导入的模组）",
            CountText = $"{localCount} 个模组" + (localCount > 0 ? $"，已启用 {localEnabled}" : ""),
        });
        foreach (var (tag, name, count) in groups
                     .Where(g => !string.Equals(g.Tag, CollectionInstaller.LocalTag, StringComparison.OrdinalIgnoreCase)))
        {
            Installed.Add(new InstalledCollectionRow
            {
                Tag = tag,
                Name = name,
                CountText = $"{count} 个模组",
            });
        }
        InstalledEmpty.Visibility = Visibility.Collapsed;
        InstalledList.Visibility = Visibility.Visible;

        Recent.Clear();
        foreach (var item in State.Config.RecentCollections)
        {
            Recent.Add(item);
        }
        RecentCard.Visibility = Recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RememberCollection(string input)
    {
        var key = input.Trim();
        var slugMatch = System.Text.RegularExpressions.Regex.Match(key,
            @"collections/([A-Za-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (slugMatch.Success)
        {
            key = slugMatch.Groups[1].Value.ToLowerInvariant();
        }

        var list = State.Config.RecentCollections;
        list.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, key);
        if (list.Count > 8)
        {
            list.RemoveRange(8, list.Count - 8);
        }
        State.SaveConfig();
        RefreshInstalled();
    }

    private async void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string value) return;
        UrlBox.Text = value;
        await ParseAsync(value);
    }

    private async void ActivateInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || string.IsNullOrWhiteSpace(tag)) return;

        try
        {
        var (enabled, failed, errors) = CollectionInstaller.Activate(tag, exclusive: true);
        RefreshInstalled();
        var message = $"已启用「{tag["合集:".Length..]}」：{enabled} 个模组" +
                      (failed > 0 ? $"，{failed} 个失败" : "") +
                      "。其它合集的模组已停用。";
        if (errors.Count > 0)
        {
            message += Environment.NewLine + string.Join(Environment.NewLine, errors.Take(6));
        }
        StatusLine.Text = message;
        }
        catch (Exception ex)
        {
            Log.Error("启用合集失败", ex);
            StatusLine.Text = "启用合集失败: " + ex.Message;
        }
        await Task.CompletedTask;
    }

    private async void DeactivateInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || string.IsNullOrWhiteSpace(tag)) return;

        try
        {
        var (disabled, errors) = CollectionInstaller.Deactivate(tag);
        RefreshInstalled();
        StatusLine.Text = $"已停用「{tag["合集:".Length..]}」：{disabled} 个模组" +
                          (errors.Count > 0 ? $"（{errors.Count} 个失败）" : "") + "。";
        }
        catch (Exception ex)
        {
            Log.Error("停用合集失败", ex);
            StatusLine.Text = "停用合集失败: " + ex.Message;
        }
        await Task.CompletedTask;
    }

    private void FilterInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
        {
            (App.MainWindow as MainWindow)?.OpenModsWithFilter(tag);
        }
    }
}
