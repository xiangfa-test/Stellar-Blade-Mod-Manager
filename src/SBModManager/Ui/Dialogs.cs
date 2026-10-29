using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Services;

namespace SBModManager.Ui;

/// <summary>
/// The app's two ContentDialog shapes, in one place. Every page used to carry its own copy of a
/// "confirm" and an "info" helper; they drifted apart (some had a scroll viewer, some made the
/// text selectable, some were fire-and-forget), so long messages behaved differently per page.
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// Dialog body. Long messages (crash reports, conflict chains) scroll instead of stretching
    /// the dialog past the window; the text stays selectable so users can copy it into a report.
    /// </summary>
    private static ScrollViewer Body(string message) => new()
    {
        MaxHeight = 460,
        Content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        },
    };

    /// <summary>Yes/No question. Returns true only for the primary button; closing counts as no.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = Body(message),
            PrimaryButtonText = Localization.T("确定"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Message box with a single "OK" button; awaits until the user dismisses it.</summary>
    public static async Task InfoAsync(XamlRoot? root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = Body(message),
            CloseButtonText = Localization.T("好"),
        };
        await dialog.ShowAsync();
    }

    /// <summary>Fire-and-forget variant for event handlers that cannot await.</summary>
    public static void ShowInfo(XamlRoot? root, string title, string message)
        => UiTask.Safe(() => InfoAsync(root, title, message));
}
