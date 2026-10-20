using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Models;
using SBModManager.Services;
using SBModManager.ViewModels;

namespace SBModManager.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public ObservableCollection<ModItem> Ue4ssMods { get; } = new();

    private AppState State => AppState.Instance;

    private void Refresh()
    {
        var gameDir = State.GameDir;
        if (gameDir is null)
        {
            StatusLine.Text = "尚未设置游戏目录。";
            PathLine.Text = "请先在「设置」中指定游戏安装目录。";
        }
        else if (Deployer.IsUe4ssInstalled(gameDir))
        {
            StatusLine.Text = "已检测到 UE4SS 运行库 ✓";
            PathLine.Text = Deployer.BinariesDir(gameDir);
        }
        else
        {
            StatusLine.Text = "未检测到 UE4SS 运行库。需要脚本模组时请先导入 UE4SS 压缩包。";
            PathLine.Text = "预期位置: " + Deployer.BinariesDir(gameDir);
        }

        Ue4ssMods.Clear();
        foreach (var record in State.Library.Where(r => r.Type is ModType.Ue4ss or ModType.Ue4ssRuntime))
        {
            Ue4ssMods.Add(new ModItem(record, State.IsEnabled(record.Id)));
        }
        Ue4ssList.ItemsSource = Ue4ssMods;
        EmptyHint.Visibility = Ue4ssMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenBinaries_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null) return;
        GameLocator.OpenFolder(Deployer.BinariesDir(State.GameDir));
    }

    private void OpenUe4ssMods_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null) return;
        GameLocator.OpenFolder(Deployer.Ue4ssModsDir(State.GameDir));
    }
}
