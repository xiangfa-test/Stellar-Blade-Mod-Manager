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

    // ---------------------------------------------------------- UE4SS one-click install

    private async void InstallUe4ss_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (state.GameDir is null)
        {
            await ShowInfoAsync("提示", "请先在「设置」中指定《剑星》游戏目录。");
            return;
        }

        InstallUe4ssButton.IsEnabled = false;
        CheckUe4ssButton.IsEnabled = false;
        string? zip = null;
        try
        {
            ReleaseLine.Text = "正在查询 GitHub 最新 Release…";
            var release = await GitHubService.GetLatestReleaseAsync(state.Config.Ue4ssRepository);
            var asset = GitHubService.PickUe4ssAsset(release)
                        ?? throw new InvalidOperationException("该 Release 中没有可用的 UE4SS zip 资源。");

            ReleaseLine.Text = $"正在下载 {asset.Name}（{release.TagName}）…";
            Directory.CreateDirectory(AppPaths.TempRoot);
            zip = Path.Combine(AppPaths.TempRoot, "ue4ss-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            var progress = new Progress<double>(percent => ReleaseLine.Text = $"下载中 {percent:0}% …");
            await GitHubService.DownloadAsync(asset.DownloadUrl, zip, progress);

            var result = ModImporter.ImportArchive(zip);
            if (result.Mod is null)
            {
                throw new InvalidOperationException(result.Error ?? "解压/导入失败。");
            }

            var record = result.Mod;
            var version = release.TagName.TrimStart('v', 'V');
            record.Version = version;
            record.Name = "UE4SS " + version;
            record.GroupKey = "ue4ss";
            record.SourceName = release.HtmlUrl;
            record.Description = state.Config.Ue4ssRepository;
            ModStore.Save(record);

            foreach (var other in state.Library.Where(m => m.Type == ModType.Ue4ssRuntime &&
                                                           m.Id != record.Id && state.IsEnabled(m.Id)))
            {
                Deployer.Undeploy(other.Id);
            }

            var deploy = Deployer.Deploy(record, state.GameDir!, force: true);
            if (!deploy.Success)
            {
                throw new InvalidOperationException(deploy.Error ?? "部署失败。");
            }

            state.ReloadLibrary();
            Refresh();
            ReleaseLine.Text = $"已安装 UE4SS {version}（{release.TagName}）";
            await ShowInfoAsync("UE4SS 已安装",
                $"已从 {state.Config.Ue4ssRepository} 下载 {asset.Name}\n并部署到 SB\\Binaries\\Win64。");
        }
        catch (Exception ex)
        {
            Log.Error("安装 UE4SS 失败", ex);
            ReleaseLine.Text = "安装失败";
            await ShowInfoAsync("安装失败", ex.Message);
        }
        finally
        {
            InstallUe4ssButton.IsEnabled = true;
            CheckUe4ssButton.IsEnabled = true;
            try { if (zip is not null && File.Exists(zip)) File.Delete(zip); } catch { /* ignore */ }
        }
    }

    private async void CheckUe4ss_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        CheckUe4ssButton.IsEnabled = false;
        InstallUe4ssButton.IsEnabled = false;
        try
        {
            ReleaseLine.Text = "正在查询 GitHub 最新 Release…";
            var release = await GitHubService.GetLatestReleaseAsync(state.Config.Ue4ssRepository);
            var asset = GitHubService.PickUe4ssAsset(release);
            var installed = state.Library
                .Where(m => m.Type == ModType.Ue4ssRuntime)
                .Select(m => m.Version)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .ToList();
            var installedText = installed.Count == 0 ? "（未安装）" : string.Join(", ", installed);
            ReleaseLine.Text = $"最新: {release.TagName}　已安装: {installedText}";
            await ShowInfoAsync("UE4SS 版本",
                $"仓库: {state.Config.Ue4ssRepository}\n最新 Release: {release.TagName}\n" +
                $"下载资源: {asset?.Name ?? "无"}\n已安装版本: {installedText}\n\n" +
                $"Release 页面: {release.HtmlUrl}");
        }
        catch (Exception ex)
        {
            Log.Error("检查 UE4SS 版本失败", ex);
            ReleaseLine.Text = "检查失败";
            await ShowInfoAsync("检查失败", ex.Message);
        }
        finally
        {
            CheckUe4ssButton.IsEnabled = true;
            InstallUe4ssButton.IsEnabled = true;
        }
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

}
