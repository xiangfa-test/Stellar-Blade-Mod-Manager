using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace SBModManager.Ui;

/// <summary>
/// Windows-Settings style overlay scrollbar: visible while scrolling or hovering,
/// fades out shortly after the user stops interacting.
/// </summary>
public static class ScrollBarAutoHide
{
    private static readonly ConditionalWeakTable<ScrollViewer, object> Hooked = new();

    /// <summary>
    /// Hook every ScrollViewer inside a control (for example the one a ListView creates
    /// inside its template). Safe to call before the template is realised.
    /// </summary>
    public static void AttachToElement(FrameworkElement? element)
    {
        if (element is null) return;

        void TryHook()
        {
            foreach (var viewer in FindAll<ScrollViewer>(element))
            {
                Attach(viewer);
            }
        }

        if (element.IsLoaded)
        {
            TryHook();
        }
        else
        {
            element.Loaded += (_, _) => TryHook();
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }

    public static void Attach(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null) return;
        if (Hooked.TryGetValue(scrollViewer, out _)) return;
        Hooked.Add(scrollViewer, new object());

        if (scrollViewer.IsLoaded)
        {
            Hook(scrollViewer);
        }
        else
        {
            scrollViewer.Loaded += (_, _) => Hook(scrollViewer);
        }
    }

    private static void Hook(ScrollViewer scrollViewer)
    {
        var bars = new List<ScrollBar>();
        Collect(scrollViewer, bars);

        if (bars.Count == 0)
        {
            // The template (or a collapsed panel) may not have produced the scrollbars yet.
            // Retry on layout passes until they appear.
            var attempts = 0;
            void Retry(object? sender, object e)
            {
                bars.Clear();
                Collect(scrollViewer, bars);
                if (bars.Count > 0)
                {
                    scrollViewer.LayoutUpdated -= Retry;
                    Wire(scrollViewer, bars);
                }
                else if (++attempts > 40)
                {
                    scrollViewer.LayoutUpdated -= Retry;
                }
            }

            scrollViewer.LayoutUpdated += Retry;
            return;
        }

        Wire(scrollViewer, bars);
    }

    private static void Wire(ScrollViewer scrollViewer, List<ScrollBar> bars)
    {
        foreach (var bar in bars)
        {
            bar.Opacity = 0;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Fade(bars, 0);
        };

        void Show()
        {
            Fade(bars, 1);
            timer.Stop();
            timer.Start();
        }

        // Show only while scrolling or while the pointer is over the scrollbar itself,
        // so moving the mouse across the page does not keep the bar visible.
        scrollViewer.ViewChanged += (_, _) => Show();
        foreach (var bar in bars)
        {
            bar.PointerEntered += (_, _) => Show();
            bar.PointerExited += (_, _) =>
            {
                timer.Stop();
                timer.Start();
            };
        }
    }

    private static void Collect(DependencyObject root, List<ScrollBar> result)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar bar)
            {
                result.Add(bar);
            }
            Collect(child, result);
        }
    }

    private static void Fade(IEnumerable<ScrollBar> bars, double to)
    {
        foreach (var bar in bars)
        {
            if (Math.Abs(bar.Opacity - to) < 0.01) continue;
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, bar);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }
}
