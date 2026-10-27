using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace SBModManager.Ui;

/// <summary>
/// Shows an in-page reminder when Nexus refuses an API download (premium-only), counts down a
/// few seconds and then opens the mod's files page. Includes a slide/fade transition.
/// </summary>
public sealed class DownloadFallbackController
{
    private readonly InfoBar _bar;
    private readonly DispatcherQueue _queue;
    private readonly Action<string> _openUrl;
    private DispatcherQueueTimer? _timer;
    private string _url = "";
    private int _seconds;

    public DownloadFallbackController(InfoBar bar, Action<string> openUrl)
    {
        _bar = bar;
        _queue = bar.DispatcherQueue;
        _openUrl = openUrl;
        _bar.RenderTransform = new TranslateTransform();
        _bar.IsOpen = false;
        _bar.Closed += (_, _) => StopCountdown();
        _bar.Unloaded += (_, _) => StopCountdown();
    }

    /// <summary>True when the failure means "free account cannot use the API download".</summary>
    public static bool IsPremiumOnly(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("403", StringComparison.Ordinal) ||
               error.Contains("401", StringComparison.Ordinal);
    }

    public void Show(string url, int seconds = 3)
    {
        _url = url;
        _seconds = seconds;
        _bar.Severity = InfoBarSeverity.Warning;
        _bar.Title = "无法通过 API 下载";
        _bar.Message = BuildMessage();
        _bar.ActionButton = _bar.ActionButton ?? new Button { Content = "立即打开" };
        if (_bar.ActionButton is Button button)
        {
            button.Click -= OpenNow_Click;
            button.Click += OpenNow_Click;
        }

        Services.Log.Info($"下载回退提示: url={url}, 初始秒数={_seconds}");
        _bar.IsOpen = true;
        AnimateIn();

        _timer?.Stop();
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            _seconds--;
            if (_seconds <= 0)
            {
                OpenAndHide();
                return;
            }
            _bar.Message = BuildMessage();
        };
        _timer.Start();
    }

    private void StopCountdown()
    {
        _timer?.Stop();
        _timer = null;
    }

    private string BuildMessage()
        => "Nexus 免费账号不能通过 API 获取下载链接。" +
           $"{Math.Max(_seconds, 1)} 秒后自动打开网页下载，也可以点「立即打开」。";

    private void OpenNow_Click(object sender, RoutedEventArgs e) => OpenAndHide();

    private void OpenAndHide()
    {
        Services.Log.Info("下载回退提示: 打开网页 " + _url);
        _timer?.Stop();
        _timer = null;
        var url = _url;
        Hide();
        if (!string.IsNullOrWhiteSpace(url)) _openUrl(url);
    }

    private void Hide()
    {
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, _bar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => _bar.IsOpen = false;
        storyboard.Begin();
    }

    private void AnimateIn()
    {
        _bar.Opacity = 0;
        if (_bar.RenderTransform is TranslateTransform shift) shift.Y = -10;

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, _bar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        if (_bar.RenderTransform is TranslateTransform transform)
        {
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "Y");
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (_bar.RenderTransform is TranslateTransform) storyboard.Children.Add(slide);
        storyboard.Begin();
    }
}
