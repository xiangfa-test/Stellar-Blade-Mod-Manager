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

    private const int PageSize = 40;
    private List<NexusModSummary> _all = new();
    private int _shown;

    private static AppState State => AppState.Instance;

    public NexusPage()
    {
        InitializeComponent();
        ModList.ItemsSource = Items;
        KindBox.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            await LoadTagsAsync();
            if (Items.Count == 0) await LoadAsync();
        };
    }

    /// <summary>Populate the tag filter with tags used by this game's popular mods.</summary>
    private async Task LoadTagsAsync()
    {
        if (TagBox.Items.Count > 0) return;
        try
        {
            var tags = await NexusGraphQl.GetUsedTagsAsync(State.Config.NexusGameDomain);
            if (tags.Count == 0) return;

            TagBox.Items.Clear();
            TagBox.Items.Add("全部标签");
            foreach (var tag in tags) TagBox.Items.Add(tag);
            TagBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn("加载标签失败: " + ex.Message);
        }
    }

    private async Task LoadAsync()
    {
        var key = State.Config.NexusApiKey;
        var missingKey = string.IsNullOrWhiteSpace(key);
        ApiKeyBar.IsOpen = missingKey;
        GetKeyButton.Visibility = missingKey ? Visibility.Visible : Visibility.Collapsed;

        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "latest_updated";
        var tag = TagBox.SelectedItem as string;
        if (tag is null || tag.StartsWith("全部", StringComparison.Ordinal))
        {
            var typed = TagBox.Text?.Trim();
            tag = string.IsNullOrWhiteSpace(typed) || typed.StartsWith("全部", StringComparison.Ordinal)
                ? null
                : typed;
        }
        var text = SearchBox.Text?.Trim();

        SetBusy(true);
        StatusText.Text = "加载中…";
        try
        {
            // Tag filtering and all-time rankings need GraphQL; the REST search cannot do them.
            if (tag is not null || kind is "top_downloads" or "top_endorsed")
            {
                var sortField = kind switch
                {
                    "top_downloads" => "downloads",
                    "top_endorsed" => "endorsements",
                    "latest_added" => "createdAt",
                    _ => "updatedAt",
                };
                _all = await NexusGraphQl.SearchAsync(State.Config.NexusGameDomain, tag, text, sortField, 100);
            }
            else
            {
                _all = await NexusBrowser.GetListAsync(State.Config.NexusGameDomain, key, kind, text, 120);
            }

            Items.Clear();
            _shown = 0;
            AppendPage();
            if (_all.Count == 0 && (tag is not null || kind is "top_downloads" or "top_endorsed"))
            {
                StatusText.Text = "没有匹配的模组" +
                    (tag is null ? "" : $"（标签：{tag}；标签区分大小写，需与 Nexus 完全一致）");
            }
            else
            {
                StatusText.Text = string.IsNullOrWhiteSpace(key)
                    ? $"{_all.Count} 个模组（未配置 API Key：可浏览，详情与下载需要 Key）"
                    : $"{_all.Count} 个模组";
            }
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

    private async void Tag_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async void Tag_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (!IsLoaded) return;
        // Typed text wins over a previously selected tag.
        TagBox.SelectedItem = null;
        await LoadAsync();
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

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back)
        {
            Loaded += (_, _) =>
            {
                try
                {
                    var animation = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService
                        .GetForCurrentView().GetAnimation("nexusDetailBack");
                    if (animation is not null && ModList.SelectedItem is NexusItem item &&
                        ModList.ContainerFromItem(item) is FrameworkElement container)
                    {
                        animation.TryStart(container);
                    }
                }
                catch
                {
                    // best-effort
                }
            };
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async void Search_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => await LoadAsync();

    private async void OpenApiKeys_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://www.nexusmods.com/settings/api-keys"));

    private async void OpenSite_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri($"https://www.nexusmods.com/{State.Config.NexusGameDomain}"));

    private void LoadMore_Click(object sender, RoutedEventArgs e) => AppendPage();

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        OpenDetail(modId, null);
    }

    private void ModList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NexusItem item) return;
        OpenDetail(item.ModId, item);
    }

    private void OpenDetail(int modId, NexusItem? item)
    {
        try
        {
            if (item is not null && ModList.ContainerFromItem(item) is FrameworkElement container)
            {
                Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService
                    .GetForCurrentView()
                    .PrepareToAnimate("nexusDetail", container);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("准备 N 网详情动画失败: " + ex.Message);
        }

        Frame.Navigate(typeof(NexusDetailPage), new NexusDetailArgs
        {
            ModId = modId,
            Name = item?.Name ?? "",
            ThumbnailUrl = item?.Thumbnail?.UriSource?.ToString() ?? "",
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int modId }) return;
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        var item = Items.FirstOrDefault(i => i.ModId == modId);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "下载并导入",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"将下载「{item?.Name ?? "mod " + modId}」的最新主文件并导入。\n\n" +
                       "免费账号需要带授权 key 的下载链接；如果失败会提示改用网页下载。",
            },
            PrimaryButtonText = "下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var (record, error) = await NexusInstaller.InstallAsync(
                State.Config.NexusGameDomain, modId, null, key, progress);
            StatusText.Text = record is not null
                ? $"已导入「{record.Name}」"
                : "下载失败: " + (error ?? "未知错误");
        }
        catch (Exception ex)
        {
            Log.Error("下载导入失败", ex);
            StatusText.Text = "下载失败: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
