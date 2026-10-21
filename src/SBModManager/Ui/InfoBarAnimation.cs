using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace SBModManager.Ui;

/// <summary>
/// Entrance/exit animations for InfoBar notifications: slide-down + fade in,
/// slide-up + fade out when the user closes it.
/// </summary>
public static class InfoBarAnimation
{
    private static readonly ConditionalWeakTable<InfoBar, object> Attached = new();

    public static void Show(InfoBar? bar)
    {
        if (bar is null) return;
        Attach(bar);
        bar.IsOpen = true;
        FadeIn(bar, -14, 260);
    }

    private static void Attach(InfoBar bar)
    {
        if (!Attached.TryAdd(bar, new object())) return;

        // The InfoBar closes itself when the X is pressed. Reopen on the next tick so the
        // internal close has settled, then animate the exit and close for real.
        bar.CloseButtonClick += (_, _) =>
        {
            bar.DispatcherQueue.TryEnqueue(() =>
            {
                bar.IsOpen = true;
                Hide(bar);
            });
        };
    }

    private static void Hide(InfoBar bar)
    {
        var transform = bar.RenderTransform as TranslateTransform ?? new TranslateTransform();
        bar.RenderTransform = transform;

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, bar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = -14,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) =>
        {
            bar.IsOpen = false;
            // Reset for the next notification.
            bar.Opacity = 1;
            if (bar.RenderTransform is TranslateTransform reset)
            {
                reset.Y = 0;
            }
        };
        storyboard.Begin();
    }

    public static void FadeIn(FrameworkElement? element, double offsetY = 0, int durationMs = 200)
    {
        if (element is null) return;

        var transform = new TranslateTransform { Y = offsetY };
        element.RenderTransform = transform;
        element.Opacity = 0;

        var slide = new DoubleAnimation
        {
            From = offsetY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(Math.Max(120, durationMs - 40)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
