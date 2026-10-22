using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SBModManager.Services;

namespace SBModManager.Views;

public sealed class NexusItem
{
    public int ModId { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Meta { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
}

public sealed partial class NexusPage : Page
{
    public ObservableCollection<NexusItem> Items { get; } = new();

    private const int PageSize = 40;
    private List<NexusModSummary> _all = new();
    private int _shown;

    private static AppState State => AppState.Instance;

    public NexusPage()
    {
        InitializeComponent();
        ModList.ItemsSource = Items;
        Loaded += async (_, _) =>
        {
            if (Items.Count == 0) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "请先在「设置」填写 Nexus API Key。";
            return;
        }

        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "latest_updated";
        SetBusy(true);
        StatusText.Text = "加载中…";
        try
        {
            _all = await NexusBrowser.GetListAsync(State.Config.NexusGameDomain, key, kind,
                SearchBox.Text?.Trim(), 120);
            Items.Clear();
            _shown = 0;
            AppendPage();
            StatusText.Text = $"{_all.Count} 个模组";
        }
        catch (Exception ex)
        {
            Log.Error("加载 N 网列表失败", ex);
            StatusText.Text = "加载失败: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AppendPage()
    {
        foreach (var mod in _all.Skip(_shown).Take(PageSize))
        {
            Items.Add(new NexusItem
            {
                ModId = mod.ModId,
                Name = mod.Name,
                Summary = string.IsNullOrWhiteSpace(mod.Summary) ? mod.Category : mod.Summary,
                Meta = mod.MetaText,
                Thumbnail = TryImage(mod.PictureUrl),
            });
        }

        _shown = Items.Count;
        LoadMoreButton.Visibility = _shown < _all.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage? TryImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return new BitmapImage(new Uri(url));
        }
        catch
        {
            return null;
        }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async void Search_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => await LoadAsync();

    private async void OpenSite_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri($"https://www.nexusmods.com/{State.Config.NexusGameDomain}"));

    private void LoadMore_Click(object sender, RoutedEventArgs e) => AppendPage();

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        await ShowDetailAsync(modId);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        await DownloadModAsync(modId, null);
    }

    private async Task ShowDetailAsync(int modId)
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        SetBusy(true);
        StatusText.Text = "加载详情…";
        try
        {
            var domain = State.Config.NexusGameDomain;
            var detail = await NexusDetailService.GetAsync(domain, modId, key);
            if (detail is null)
            {
                StatusText.Text = "详情加载失败。";
                return;
            }

            var panel = new StackPanel { Spacing = 10, MaxWidth = 640 };
            if (!string.IsNullOrWhiteSpace(detail.PictureUrl))
            {
                panel.Children.Add(new Image
                {
                    MaxHeight = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    Source = TryImage(detail.PictureUrl),
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = detail.Name,
                Style = ResourceStyle("SubtitleTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = detail.MetaText(),
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                Style = ResourceStyle("CaptionTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(detail.Summary))
            {
                panel.Children.Add(new TextBlock { Text = detail.Summary, TextWrapping = TextWrapping.Wrap });
            }
            if (!string.IsNullOrWhiteSpace(detail.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = StripHtml(detail.Description),
                    TextWrapping = TextWrapping.Wrap,
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });
            }
            if (detail.Files.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "文件",
                    Style = ResourceStyle("BodyStrongTextBlockStyle"),
                });
                foreach (var file in detail.Files.Take(6))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"• {file.Name}" +
                               (string.IsNullOrWhiteSpace(file.Version) ? "" : $"  v{file.Version}") +
                               (string.IsNullOrWhiteSpace(file.Category) ? "" : $"  [{file.Category}]"),
                        Style = ResourceStyle("CaptionTextBlockStyle"),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "模组详情",
                Content = new ScrollViewer { MaxHeight = 560, Content = panel },
                PrimaryButtonText = "下载导入",
                SecondaryButtonText = "打开网页",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await DownloadModAsync(modId, detail);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri($"https://www.nexusmods.com/{domain}/mods/{modId}"));
            }
        }
        catch (Exception ex)
        {
            Log.Error("加载 N 网详情失败", ex);
            StatusText.Text = "详情加载失败: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            StatusText.Text = $"{_all.Count} 个模组";
        }
    }

    private async Task DownloadModAsync(int modId, NexusModDetail? detail)
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        var domain = State.Config.NexusGameDomain;
        var item = Items.FirstOrDefault(i => i.ModId == modId);
        SetBusy(true);
        StatusText.Text = "获取文件列表…";
        try
        {
            var files = detail?.Files is { Count: > 0 } known
                ? known
                : await NexusService.GetFilesAsync(domain, modId, key);
            var main = files
                .Where(f => f.FileId > 0)
                .OrderByDescending(f => f.UploadedTimestamp)
                .FirstOrDefault();
            if (main is null)
            {
                StatusText.Text = "该模组没有可下载的主文件。";
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "下载并导入",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将下载「{detail?.Name ?? item?.Name ?? "mod " + modId}」的最新主文件：\n{main.Name}" +
                           (string.IsNullOrWhiteSpace(main.Version) ? "" : $"（v{main.Version}）") +
                           "\n\n免费账号需要带授权 key 的下载链接；如果失败会提示改用网页下载。",
                },
                PrimaryButtonText = "下载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            StatusText.Text = "获取下载地址…";
            var uri = await NexusService.GetDownloadLinkAsync(domain, modId, main.FileId, key);
            if (string.IsNullOrWhiteSpace(uri))
            {
                StatusText.Text = "Nexus 没有返回下载地址（免费账号可能受限），已打开网页。";
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri($"https://www.nexusmods.com/{domain}/mods/{modId}?tab=files"));
                return;
            }

            StatusText.Text = "下载中…";
            Directory.CreateDirectory(AppPaths.TempRoot);
            var temp = Path.Combine(AppPaths.TempRoot, $"nexus-{modId}-{main.FileId}.zip");
            await GitHubService.DownloadAsync(uri, temp);

            StatusText.Text = "导入中…";
            var imported = await ModImporter.ImportArchiveAsync(temp, null);
            if (imported.Mod is null)
            {
                StatusText.Text = "导入失败: " + imported.Error;
                return;
            }

            var record = imported.Mod;
            record.NexusModId = modId;
            record.NexusFileId = main.FileId;
            record.NexusDomain = domain;
            if (!string.IsNullOrWhiteSpace(main.Version)) record.Version = main.Version;
            if (detail is not null)
            {
                record.Name = detail.Name;
                record.NexusThumbnailUrl = detail.PictureUrl;
                if (!string.IsNullOrWhiteSpace(detail.Version)) record.Version = detail.Version;
            }
            else if (item is not null)
            {
                record.Name = item.Name;
                record.NexusThumbnailUrl = item.Thumbnail?.UriSource?.ToString() ?? "";
            }
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
            CollectionInstaller.TagAsLocal(record);
            ModStore.Save(record);
            State.ReloadLibrary();

            StatusText.Text = $"已导入「{record.Name}」";
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            Log.Error("N 网下载导入失败", ex);
            StatusText.Text = "下载失败: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ModList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NexusItem item)
        {
            _ = ShowDetailAsync(item.ModId);
        }
    }

    private static string StripHtml(string html)
    {
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\n{3,}", "\n\n").Trim();
    }

    private static Style? ResourceStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Microsoft.UI.Xaml.Media.Brush? ResourceBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;
}
