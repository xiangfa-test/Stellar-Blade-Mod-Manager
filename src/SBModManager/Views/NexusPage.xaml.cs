using System.Collections.ObjectModel;
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
            var mods = await NexusBrowser.GetListAsync(State.Config.NexusGameDomain, key, kind,
                SearchBox.Text?.Trim());
            Items.Clear();
            foreach (var mod in mods.Take(60))
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
            StatusText.Text = $"{Items.Count} 个模组";
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

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        var domain = State.Config.NexusGameDomain;
        var url = $"https://www.nexusmods.com/{domain}/mods/{modId}";
        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        var item = Items.FirstOrDefault(i => i.ModId == modId);
        var domain = State.Config.NexusGameDomain;
        SetBusy(true);
        StatusText.Text = "获取文件列表…";
        try
        {
            var files = await NexusService.GetFilesAsync(domain, modId, key);
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
                    Text = $"将下载「{item?.Name ?? "mod " + modId}」的最新主文件：\n{main.Name}" +
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
                await Windows.System.Launcher.LaunchUriAsync(new Uri(item?.ModId > 0
                    ? $"https://www.nexusmods.com/{domain}/mods/{modId}?tab=files"
                    : $"https://www.nexusmods.com/{domain}/mods/{modId}"));
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
            if (item is not null)
            {
                record.Name = item.Name;
                record.NexusThumbnailUrl = item.Thumbnail is null ? "" : item.Thumbnail.UriSource?.ToString() ?? "";
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
            _ = Windows.System.Launcher.LaunchUriAsync(
                new Uri($"https://www.nexusmods.com/{State.Config.NexusGameDomain}/mods/{item.ModId}"));
        }
    }
}
