using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using SBModManager.Services;
using SBModManager.Ui;

namespace SBModManager.Views;

/// <summary>Full-screen Nexus mod detail, expanded from the browse list.</summary>
public sealed partial class NexusDetailPage : Page
{
    private int _modId;
    private string _domain = "stellarblade";
    private NexusModDetail? _detail;
    private string? _translated;

    private static AppState State => AppState.Instance;

    public NexusDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is int modId) _modId = modId;
        _domain = State.Config.NexusGameDomain;
        TitleText.Text = "加载中…";

        if (e.NavigationMode == NavigationMode.New)
        {
            Loaded += (_, _) =>
            {
                var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("nexusDetail");
                if (animation is not null)
                {
                    animation.TryStart(HeroHost);
                }
            };
        }

        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("nexusDetailBack", HeroHost);
        }
        catch
        {
            // animation is best-effort
        }
    }

    private async Task LoadAsync()
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            TitleText.Text = "需要 Nexus API Key";
            StatusText.Text = "请先在「设置」里填写 Nexus API Key。";
            DownloadButton.IsEnabled = false;
            TranslateButton.IsEnabled = false;
            return;
        }

        BusyRing.IsActive = true;
        try
        {
            _detail = await NexusDetailService.GetAsync(_domain, _modId, key);
            if (_detail is null)
            {
                TitleText.Text = "模组 " + _modId;
                StatusText.Text = "详情加载失败（检查网络或 API Key）。";
                return;
            }

            TitleText.Text = _detail.Name;
            MetaText.Text = _detail.MetaText();
            SummaryText.Text = _detail.Summary;
            WebButton.IsEnabled = true;

            if (Uri.TryCreate(_detail.PictureUrl, UriKind.Absolute, out var picture))
            {
                HeroImage.Source = new BitmapImage(picture);
            }
            else
            {
                HeroHost.Height = 140;
            }

            var markdown = HtmlToMarkdown.Convert(_detail.Description);
            DescriptionHost.Content = MarkdownRenderer.Render(
                string.IsNullOrWhiteSpace(markdown) ? "（该模组没有说明）" : markdown);
            TranslateButton.IsEnabled = !string.IsNullOrWhiteSpace(markdown);
            FilesText.Text = _detail.Files.Count == 0
                ? "（没有文件信息）"
                : string.Join("\n", _detail.Files.Take(12).Select(f =>
                    $"• {f.Name}" +
                    (string.IsNullOrWhiteSpace(f.Version) ? "" : $"  v{f.Version}") +
                    (string.IsNullOrWhiteSpace(f.Category) ? "" : $"  [{f.Category}]")));
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri($"https://www.nexusmods.com/{_domain}/mods/{_modId}"));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        BusyRing.IsActive = true;
        DownloadButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var (record, error) = await NexusInstaller.InstallAsync(_domain, _modId, _detail, key, progress);
            StatusText.Text = record is not null
                ? $"已导入「{record.Name}」。"
                : "下载失败: " + (error ?? "未知错误");
        }
        finally
        {
            BusyRing.IsActive = false;
            DownloadButton.IsEnabled = true;
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;

        if (_translated is not null)
        {
            DescriptionHost.Content = MarkdownRenderer.Render(HtmlToMarkdown.Convert(_detail.Description));
            _translated = null;
            TranslateButton.Content = "翻译";
            return;
        }

        BusyRing.IsActive = true;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "正在翻译…";
        try
        {
            var plain = TranslationService.StripMarkdown(HtmlToMarkdown.Convert(_detail.Description));
            var toEnglish = TranslationService.LooksChinese(plain);
            var result = await TranslationService.TranslateAsync(plain, toEnglish ? "zh-CN" : "en",
                toEnglish ? "en" : "zh-CN");
            if (string.IsNullOrWhiteSpace(result))
            {
                StatusText.Text = "翻译服务不可用（可能超限），已复制原文。";
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(plain);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return;
            }

            _translated = result;
            DescriptionHost.Content = new TextBlock
            {
                Text = result,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            TranslateButton.Content = "原文";
            StatusText.Text = "已翻译（机器翻译，仅供参考）。";
        }
        finally
        {
            BusyRing.IsActive = false;
            TranslateButton.IsEnabled = true;
        }
    }
}
