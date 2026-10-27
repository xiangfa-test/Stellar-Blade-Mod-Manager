using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using SBModManager.Services;
using SBModManager.Ui;

namespace SBModManager.Views;

/// <summary>Parameters handed to <see cref="NexusDetailPage"/> from the browse list.</summary>
public sealed class NexusDetailArgs
{
    public int ModId { get; init; }
    public string Name { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
}

/// <summary>Full-screen Nexus mod detail, expanded from the browse list.</summary>
public sealed partial class NexusDetailPage : Page
{
    private int _modId;
    private string _thumbnailFallback = "";
    private string _domain = "stellarblade";
    private NexusModDetail? _detail;
    private string? _translated;
    private string _originalSummary = "";
    private DownloadFallbackController? _fallback;

    private static AppState State => AppState.Instance;

    public NexusDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        switch (e.Parameter)
        {
            case NexusDetailArgs args:
                _modId = args.ModId;
                _thumbnailFallback = args.ThumbnailUrl;
                if (!string.IsNullOrWhiteSpace(args.Name)) TitleText.Text = args.Name;
                break;
            case int modId:
                _modId = modId;
                break;
        }

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

        UiTask.Safe(LoadAsync);
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
            _originalSummary = _detail.Summary;
            SummaryText.Text = _detail.Summary;
            WebButton.IsEnabled = true;

            var heroUrl = !string.IsNullOrWhiteSpace(_detail.PictureUrl)
                ? _detail.PictureUrl
                : _thumbnailFallback;

            if (string.IsNullOrWhiteSpace(heroUrl))
            {
                // v3 mod details carry no picture; the legacy endpoint still does.
                var info = await NexusService.GetModInfoAsync(_domain, _modId, key);
                if (!string.IsNullOrWhiteSpace(info?.PictureUrl))
                {
                    heroUrl = info.Value.PictureUrl;
                }
            }
            if (Uri.TryCreate(heroUrl, UriKind.Absolute, out var picture))
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
            var files = _detail.Files;
            if (files.Count == 0)
            {
                // v3 details carry no file list without extra calls; GraphQL exposes it publicly.
                files = await NexusGraphQl.GetModFilesAsync(_domain, _modId);
            }

            FilesText.Text = files.Count == 0
                ? "（没有读取到文件信息，可点「打开网页」查看）"
                : string.Join("\n", files
                    .OrderByDescending(f => f.UploadedTimestamp)
                    .Take(12)
                    .Select(f =>
                        $"• {f.Name}" +
                        (string.IsNullOrWhiteSpace(f.Version) ? "" : $"  v{f.Version}") +
                        (string.IsNullOrWhiteSpace(f.Category) ? "" : $"  [{DescribeCategory(f.Category)}]")));
        }
        catch (Exception ex)
        {
            Log.Error("加载 N 网详情失败", ex);
            StatusText.Text = "详情加载失败: " + ex.Message;
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
            if (record is not null)
            {
                StatusText.Text = $"已导入「{record.Name}」。";
            }
            else if (DownloadFallbackController.IsPremiumOnly(error))
            {
                StatusText.Text = "免费账号需要通过网页下载。";
                OpenFallback($"https://www.nexusmods.com/{_domain}/mods/{_modId}?tab=files");
            }
            else
            {
                StatusText.Text = "下载失败: " + (error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            Log.Error("下载导入失败", ex);
            StatusText.Text = "下载失败: " + ex.Message;
        }
        finally
        {
            BusyRing.IsActive = false;
            DownloadButton.IsEnabled = true;
        }
    }

    private static string DescribeCategory(string category) => category.ToUpperInvariant() switch
    {
        "MAIN" => "主文件",
        "UPDATE" => "更新",
        "OPTIONAL" => "可选",
        "MISCELLANEOUS" => "其他",
        "ARCHIVED" => "旧版",
        "OLD_VERSION" => "旧版",
        _ => category,
    };

    private void OpenFallback(string url)
    {
        _fallback ??= new DownloadFallbackController(DownloadFallbackBar,
            target => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(target)));
        _fallback.Show(url);
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;

        if (_translated is not null)
        {
            DescriptionHost.Content = MarkdownRenderer.Render(HtmlToMarkdown.Convert(_detail.Description));
            SummaryText.Text = _originalSummary;
            _translated = null;
            TranslateButton.Content = "翻译";
            return;
        }

        BusyRing.IsActive = true;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "正在翻译…";
        try
        {
            var markdown = HtmlToMarkdown.Convert(_detail.Description);
            var plain = TranslationService.StripMarkdown(markdown);
            var summary = _originalSummary;

            var toEnglish = TranslationService.LooksChinese(plain + " " + summary);
            var from = toEnglish ? "zh-CN" : "en";
            var to = toEnglish ? "en" : "zh-CN";

            var result = string.IsNullOrWhiteSpace(plain)
                ? null
                : await TranslationService.TranslateAsync(plain, from, to);
            var translatedSummary = string.IsNullOrWhiteSpace(summary)
                ? ""
                : await TranslationService.TranslateAsync(summary, from, to) ?? summary;
            if (string.IsNullOrWhiteSpace(result) && string.IsNullOrWhiteSpace(translatedSummary))
            {
                StatusText.Text = "翻译服务不可用（可能超限），已复制原文。";
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(plain);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return;
            }

            if (!string.IsNullOrWhiteSpace(translatedSummary))
            {
                SummaryText.Text = translatedSummary;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                _translated = result;
                DescriptionHost.Content = new TextBlock
                {
                    Text = result,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
            }

            TranslateButton.Content = "原文";
            StatusText.Text = "已翻译（机器翻译，仅供参考）。";
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            StatusText.Text = "翻译失败: " + ex.Message;
        }
        finally
        {
            BusyRing.IsActive = false;
            TranslateButton.IsEnabled = true;
        }
    }
}
