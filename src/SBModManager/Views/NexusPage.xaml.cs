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

        Frame.Navigate(typeof(NexusDetailPage), modId);
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
        finally
        {
            SetBusy(false);
        }
    }
}
