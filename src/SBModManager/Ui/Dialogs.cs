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

    /// <summary>
    /// WinUI allows only one open ContentDialog per window: showing a second one throws. Every
    /// dialog opened through this class takes this gate, so overlapping requests queue up instead
    /// of failing with "已有对话框" / "参数错误".
    /// </summary>
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    /// <summary>
    /// Show a dialog safely: skip it when the page has no XamlRoot yet (a dialog without one throws
    /// "参数错误"), and serialise concurrent dialogs.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot? root)
    {
        if (root is null)
        {
            Log.Warn($"对话框缺少 XamlRoot，已跳过: {dialog.Title}");
            return ContentDialogResult.None;
        }

        await DialogGate.WaitAsync();
        try
        {
            dialog.XamlRoot = root;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"显示对话框失败（{dialog.Title}）: {ex.Message}");
            return ContentDialogResult.None;
        }
        finally
        {
            DialogGate.Release();
        }
    }

    /// <summary>Yes/No question. Returns true only for the primary button; closing counts as no.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Body(message),
            PrimaryButtonText = Localization.T("确定"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, root) == ContentDialogResult.Primary;
    }

    /// <summary>Message box with a single "OK" button; awaits until the user dismisses it.</summary>
    public static async Task InfoAsync(XamlRoot? root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Body(message),
            CloseButtonText = Localization.T("好"),
        };
        await ShowAsync(dialog, root);
    }

    /// <summary>Fire-and-forget variant for event handlers that cannot await.</summary>
    public static void ShowInfo(XamlRoot? root, string title, string message)
        => UiTask.Safe(() => InfoAsync(root, title, message));
}
