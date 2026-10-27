using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SBModManager.Models;
using SBModManager.Services;

namespace SBModManager.Views;

public sealed partial class ModsPage
{
    private void DetailSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(ModsView).Position.X;
        _splitterStartWidth = DetailColumn.ActualWidth;
        DetailSplitter.CapturePointer(e.Pointer);
        DetailSplitterLine.Opacity = 1;
        e.Handled = true;
    }

    private void DetailSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;

        var pointerX = e.GetCurrentPoint(ModsView).Position.X;
        var delta = _splitterStartX - pointerX; // dragging left widens the details pane
        var available = ModsView.ActualWidth;
        var maxDetail = Math.Max(MinDetailWidth, available - MinListWidth - DetailSplitter.ActualWidth - 40);
        var width = Math.Clamp(_splitterStartWidth + delta, MinDetailWidth, maxDetail);

        DetailColumn.Width = new GridLength(width);
        e.Handled = true;
    }

    private void DetailSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        DetailSplitter.ReleasePointerCapture(e.Pointer);
        DetailSplitterLine.Opacity = 0.55;
        SaveDetailWidth();
        e.Handled = true;
    }

    private void DetailSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = false;
        DetailSplitterLine.Opacity = 0.55;
    }

    private void DetailSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        => DetailSplitterLine.Opacity = 1;

    private void DetailSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            DetailSplitterLine.Opacity = 0.55;
        }
    }

    private void DetailSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        DetailColumn.Width = new GridLength(DefaultDetailWidth);
        SaveDetailWidth();
        e.Handled = true;
    }

    private void SaveDetailWidth()
    {
        try
        {
            State.Config.ModsDetailWidth = DetailColumn.ActualWidth;
            State.SaveConfig();
        }
        catch
        {
            // best effort
        }
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildMods();
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width;
        if (available <= 0) return;
        var current = DetailColumn.ActualWidth > 0 ? DetailColumn.ActualWidth : DetailColumn.Width.Value;
        if (_lastAvailableWidth <= 0 || _lastAvailableWidth == available)
        {
            _lastAvailableWidth = available;
            return;
        }

        var ratio = current / _lastAvailableWidth;
        var max = Math.Max(MinDetailWidth, available * 0.45);
        var target = Math.Clamp(ratio * available, MinDetailWidth, max);
        DetailColumn.Width = new GridLength(target);
        State.Config.ModsDetailWidth = target;
        _lastAvailableWidth = available;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterRebuild) return;
        RebuildMods();
    }

    private void UpdateCnsFilters()
    {
        _suppressFilterRebuild = true;
        try
        {
            var selectedRole = RoleFilter.SelectedItem as string;
            var selectedType = TypeFilter.SelectedItem as string;

            var roles = State.Library.SelectMany(r => r.CnsCharacters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var types = State.Library.SelectMany(r => r.CnsOutfitTypes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RoleFilter.ItemsSource = new[] { "全部角色" }.Concat(roles).ToList();
            TypeFilter.ItemsSource = new[] { "全部类型" }.Concat(types).ToList();
            RoleFilter.SelectedItem = selectedRole is not null && roles.Contains(selectedRole, StringComparer.OrdinalIgnoreCase)
                ? selectedRole
                : "全部角色";
            TypeFilter.SelectedItem = selectedType is not null && types.Contains(selectedType, StringComparer.OrdinalIgnoreCase)
                ? selectedType
                : "全部类型";
        }
        finally
        {
            _suppressFilterRebuild = false;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        ShowMessage(InfoBarSeverity.Informational, "已刷新", "模组库与游戏目录已重新扫描。");
    }

    private void Selector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var external = sender.SelectedItem is { } item && sender.Items.IndexOf(item) > 0;
        ModsView.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        ExternalView.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDir is null)
        {
            ShowMessage(InfoBarSeverity.Warning, "未设置游戏目录", "请先在「设置」中指定游戏安装目录。");
            return;
        }
        GameLocator.OpenFolder(Deployer.PaksModsDir(GameDir));
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
        => GameLocator.OpenFolder(AppPaths.ModsRoot);

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDir is null)
        {
            ShowMessage(InfoBarSeverity.Warning, "未设置游戏目录", "请先在「设置」中指定游戏安装目录。");
            return;
        }
        GameLocator.OpenFolder(GameDir);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Log.OpenLogFolder();

    private void More_Click(object sender, RoutedEventArgs e)
    {
        // Flyout is shown by the button itself.
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GameLocator.Launch(GameDir, State.Config.LaunchMode);
            ShowMessage(InfoBarSeverity.Informational, "正在启动", "已发送启动请求，请稍候…");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "启动失败", ex.Message);
        }
    }
}
