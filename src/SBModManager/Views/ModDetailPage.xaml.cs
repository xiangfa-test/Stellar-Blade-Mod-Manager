using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using SBModManager.Models;
using SBModManager.Services;
using SBModManager.Ui;

namespace SBModManager.Views;

/// <summary>Full-screen mod detail, expanded from the list row like the Microsoft Store.</summary>
public sealed partial class ModDetailPage : Page
{
    private ModRecord? _record;
    private string? _translated;

    private static AppState State => AppState.Instance;

    public ModDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id)
        {
            _record = State.Library.FirstOrDefault(m => m.Id == id);
        }
        Render();

        if (e.NavigationMode == NavigationMode.New)
        {
            Loaded += (_, _) =>
            {
                var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("modDetail");
                if (animation is not null)
                {
                    animation.TryStart(HeroHost);
                }
            };
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("modDetailBack", HeroHost);
        }
        catch
        {
            // animation is best-effort
        }
    }

    private void Render()
    {
        if (_record is null)
        {
            TitleText.Text = "模组不存在";
            return;
        }

        TitleText.Text = _record.Name;
        MetaText.Text = string.Join("  ·  ", new[]
        {
            _record.DisplayType,
            string.IsNullOrWhiteSpace(_record.Version) ? "" : "v" + _record.Version,
            _record.SizeBytes > 0 ? FormatSize(_record.SizeBytes) : "",
            _record.ImportedAt.LocalDateTime.ToString("yyyy-MM-dd"),
        }.Where(s => !string.IsNullOrEmpty(s)));

        CnsText.Text = _record.CnsSummary;
        ToggleButton.Content = State.IsEnabled(_record.Id) ? "停用" : "启用";
        NexusButton.IsEnabled = _record.NexusModId is > 0;

        var hero = ResolveHeroImage(_record);
        if (hero is not null)
        {
            HeroImage.Source = new BitmapImage(hero);
        }
        else
        {
            HeroHost.Height = 140;
        }

        DescriptionHost.Content = MarkdownRenderer.Render(
            string.IsNullOrWhiteSpace(_record.Description) ? "（该模组没有说明）" : _record.Description);
        NotesHost.Content = MarkdownRenderer.Render(
            string.IsNullOrWhiteSpace(_record.Notes) ? "（没有备注）" : _record.Notes);

        var targets = _record.Files.Count == 0
            ? "（尚未部署）"
            : string.Join("\n", _record.Files.Take(60)) +
              (_record.Files.Count > 60 ? $"\n… 共 {_record.Files.Count} 个文件" : "");
        TargetsText.Text = targets;
        TranslateButton.IsEnabled = !string.IsNullOrWhiteSpace(_record.Description);
    }

    private static Uri? ResolveHeroImage(ModRecord record)
    {
        foreach (var rel in record.PreviewImages.Count > 0
                     ? record.PreviewImages
                     : new List<string> { record.PreviewImage })
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var path = Path.Combine(record.ModDir, rel);
            if (File.Exists(path)) return new Uri(path);
        }

        if (Uri.TryCreate(record.NexusThumbnailUrl, UriKind.Absolute, out var remote)) return remote;
        return null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_record is null) return;
        try
        {
            if (State.IsEnabled(_record.Id))
            {
                ModActions.Disable(_record);
                StatusText.Text = "已停用。";
            }
            else
            {
                var result = ModActions.Enable(_record);
                StatusText.Text = result.Success ? "已启用。" : "启用失败: " + (result.Error ?? "未知错误");
            }

            ToggleButton.Content = State.IsEnabled(_record.Id) ? "停用" : "启用";
        }
        catch (Exception ex)
        {
            StatusText.Text = "操作失败: " + ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_record is not null) GameLocator.OpenFolder(_record.ModDir);
    }

    private async void OpenNexus_Click(object sender, RoutedEventArgs e)
    {
        if (_record?.NexusPageUrl is { Length: > 0 } url)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_record is null) return;

        if (_translated is not null)
        {
            DescriptionHost.Content = MarkdownRenderer.Render(_record.Description);
            _translated = null;
            TranslateButton.Content = "翻译说明";
            return;
        }

        TranslateRing.IsActive = true;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "正在翻译…";
        try
        {
            var source = _record.Description;
            var result = await TranslationService.TranslateAsync(source);
            if (string.IsNullOrWhiteSpace(result))
            {
                StatusText.Text = "翻译服务不可用（可能超限），已改为复制原文。";
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(source);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return;
            }

            _translated = result;
            DescriptionHost.Content = MarkdownRenderer.Render(result);
            TranslateButton.Content = "显示原文";
            StatusText.Text = "已翻译（机器翻译，仅供参考）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "翻译失败: " + ex.Message;
        }
        finally
        {
            TranslateRing.IsActive = false;
            TranslateButton.IsEnabled = true;
        }
    }
}
