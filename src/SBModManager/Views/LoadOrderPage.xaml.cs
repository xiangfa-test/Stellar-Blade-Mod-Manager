using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Models;
using SBModManager.Services;

namespace SBModManager.Views;

public sealed class LoadOrderItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string EnabledText { get; set; } = "";
    public string PriorityText { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed partial class LoadOrderPage : Page
{
    public ObservableCollection<LoadOrderItem> Items { get; } = new();

    private static IAppState State => AppServices.State;

    public LoadOrderPage()
    {
        InitializeComponent();
        OrderList.ItemsSource = Items;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var selectedId = (OrderList.SelectedItem as LoadOrderItem)?.Id;
        Items.Clear();

        var ordered = State.Library
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var record = ordered[i];
            Items.Add(new LoadOrderItem
            {
                Id = record.Id,
                Name = record.Name,
                Subtitle = record.DisplayType + (string.IsNullOrWhiteSpace(record.Version) ? "" : "  ·  v" + record.Version.TrimStart('v', 'V')),
                EnabledText = State.IsEnabled(record.Id) ? "已启用" : "未启用",
                Enabled = State.IsEnabled(record.Id),
                PriorityText = i.ToString(),
            });
        }

        if (selectedId is not null)
        {
            OrderList.SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
        }

        StatusText.Text = $"{Items.Count} 个模组，拖动排序后点「应用顺序」。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (OrderList.SelectedItem is not LoadOrderItem item) return;
        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count) return;

        Items.Move(index, target);
        OrderList.SelectedItem = item;
        SaveOrder();
    }

    private void OrderList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => SaveOrder();

    private void SaveOrder()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.PriorityText = i.ToString();
            var record = State.Library.FirstOrDefault(r => r.Id == item.Id);
            if (record is null) continue;
            if (record.Priority == i) continue;
            record.Priority = i;
            ModStore.Save(record);
        }

        State.ReloadLibrary();
        StatusText.Text = "顺序已保存（尚未应用到游戏目录）。";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (State.GameDir is not { } gameDir)
        {
            StatusText.Text = "请先在设置里选择游戏目录。";
            return;
        }

        SaveOrder();
        var (ok, failed) = Deployer.RedeployAll(gameDir);
        State.ReloadLibrary();
        Reload();
        StatusText.Text = $"已按加载顺序重建部署：成功 {ok}，失败 {failed}。";

        if (failed > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "部分模组部署失败",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"成功 {ok} 个，失败 {failed} 个。\n请查看日志或「模组管理 → 冲突链分析」了解冲突情况。",
                },
                CloseButtonText = "好",
            };
            await dialog.ShowAsync();
        }
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: Apply_Click", ex);
        }
    }
}
