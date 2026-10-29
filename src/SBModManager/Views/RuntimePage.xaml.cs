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

    private IAppState State => AppServices.State;

    private void Refresh()
    {
        var gameDir = State.GameDir;
        if (gameDir is null)
        {
            StatusLine.Text = Localization.T("尚未设置游戏目录。");
            PathLine.Text = "请先在「设置」中指定游戏安装目录。";
        }
        else if (Deployer.IsUe4ssInstalled(gameDir))
        {
            StatusLine.Text = Localization.T("已检测到 UE4SS 运行库 ✓（ue4ss 文件夹 + dwmapi.dll）");
            PathLine.Text = Deployer.BinariesDir(gameDir);
        }
        else
        {
            StatusLine.Text = Localization.T("未检测到 UE4SS 运行库。需要脚本模组时请先安装 UE4SS。");
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

    // ---------------------------------------------------------------- UE4SS install

    private async void InstallUe4ss_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null)
        {
            await ShowInfoAsync("提示", "请先在「设置」中指定《剑星》游戏目录。");
            return;
        }

        SetBusy(true);
        string? zip = null;
        try
        {
            ReleaseLine.Text = "正在查询 GitHub 最新 Release…";
            var release = await GitHubService.GetLatestReleaseAsync(State.Config.Ue4ssRepository);
            var asset = GitHubService.PickUe4ssAsset(release)
                        ?? throw new InvalidOperationException("该 Release 中没有可用的 UE4SS zip 资源。");

            ReleaseLine.Text = $"正在下载 {asset.Name}（{release.TagName}）…";
            Directory.CreateDirectory(AppPaths.TempRoot);
            zip = Path.Combine(AppPaths.TempRoot, "ue4ss-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            var progress = new Progress<double>(percent => ReleaseLine.Text = $"下载中 {percent:0}% …");
            await GitHubService.DownloadAsync(asset.DownloadUrl, zip, progress);

            var summary = await InstallArchiveAsync(zip, release.TagName.TrimStart('v', 'V'),
                release.HtmlUrl, $"已从 {State.Config.Ue4ssRepository} 下载 {asset.Name}");
            ReleaseLine.Text = summary;
        }
        catch (Exception ex)
        {
            Log.Error("安装 UE4SS 失败", ex);
            ReleaseLine.Text = "安装失败";
            await ShowInfoAsync("安装失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
            try
            {
                if (zip is not null && File.Exists(zip)) File.Delete(zip);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async void InstallLocalUe4ss_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null)
        {
            await ShowInfoAsync("提示", "请先在「设置」中指定《剑星》游戏目录。");
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = FileDialogService.PickFiles(
                App.MainWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow),
                "选择 UE4SS 压缩包",
                new FileDialogService.DialogFilter("压缩包", "*.zip;*.7z;*.rar"),
                new FileDialogService.DialogFilter("所有文件", "*.*"));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("选择文件失败", ex.Message);
            return;
        }

        if (files.Count == 0) return;

        SetBusy(true);
        try
        {
            var archive = files[0];
            ReleaseLine.Text = "正在导入 " + Path.GetFileName(archive) + " …";
            var summary = await InstallArchiveAsync(archive,
                Path.GetFileNameWithoutExtension(archive), archive, "本地压缩包: " + Path.GetFileName(archive));
            ReleaseLine.Text = summary;
        }
        catch (Exception ex)
        {
            Log.Error("从本地安装 UE4SS 失败", ex);
            ReleaseLine.Text = "安装失败";
            await ShowInfoAsync("安装失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Manual-install equivalent: take the archive's "ue4ss" folder + proxy dll and put them
    /// into SB/Binaries/Win64.
    /// </summary>
    private async Task<string> InstallArchiveAsync(string archive, string version, string sourceName, string sourceLabel)
    {
        var gameDir = State.GameDir ?? throw new InvalidOperationException("尚未设置游戏目录。");

        var imported = ModImporter.ImportArchive(archive);
        if (imported.Mod is null)
        {
            throw new InvalidOperationException(imported.Error ?? "解压/导入失败。");
        }

        var record = imported.Mod;
        var names = record.Files.Select(f => f.Replace('/', '\\')).ToList();
        var hasProxy = names.Any(f =>
        {
            var name = Path.GetFileName(f);
            return name.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("xinput1_3.dll", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("version.dll", StringComparison.OrdinalIgnoreCase);
        });
        var hasUe4ssFolder = names.Any(f => f.Contains(@"ue4ss\", StringComparison.OrdinalIgnoreCase));

        if (!hasUe4ssFolder)
        {
            ModStore.Delete(record);
            throw new InvalidOperationException(
                "这个压缩包不像 UE4SS：没有找到 `ue4ss` 文件夹。");
        }

        record.Version = version;
        record.Name = "UE4SS " + version;
        record.GroupKey = "ue4ss";
        record.SourceName = sourceName;
        record.Description = State.Config.Ue4ssRepository;
        ModStore.Save(record);

        foreach (var other in State.Library.Where(m =>
                     m.Type is ModType.Ue4ssRuntime or ModType.GameRoot &&
                     m.Id != record.Id && State.IsEnabled(m.Id)))
        {
            Deployer.Undeploy(other.Id);
        }

        var deploy = Deployer.Deploy(record, gameDir, force: true);
        if (!deploy.Success)
        {
            throw new InvalidOperationException(deploy.Error ?? "部署失败。");
        }

        State.ReloadLibrary();
        Refresh();

        var installedOk = Deployer.IsUe4ssInstalled(gameDir);
        var summary = $"已安装 UE4SS {version} → SB\\Binaries\\Win64（{record.Files.Count} 个文件）";

        var lines = new List<string>
        {
            sourceLabel,
            "",
            "已按手动安装方式处理：",
            "• ue4ss 文件夹 → SB\\Binaries\\Win64\\ue4ss",
            hasProxy ? "• dwmapi.dll → SB\\Binaries\\Win64\\dwmapi.dll" : "• 压缩包里没有 dwmapi.dll / xinput1_3.dll",
        };
        if (!installedOk)
        {
            lines.Add("");
            lines.Add("提示：没有检测到代理 DLL（dwmapi.dll 等），UE4SS 可能不会随游戏加载。");
        }
        await ShowInfoAsync("UE4SS 已安装", string.Join(Environment.NewLine, lines));
        return summary;
    }

    private void SetBusy(bool busy)
    {
        InstallUe4ssButton.IsEnabled = !busy;
        InstallLocalUe4ssButton.IsEnabled = !busy;
        CheckUe4ssButton.IsEnabled = !busy;
    }

    // ---------------------------------------------------------------- version check

    private async void CheckUe4ss_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ReleaseLine.Text = "正在查询 GitHub 最新 Release…";
            var release = await GitHubService.GetLatestReleaseAsync(State.Config.Ue4ssRepository);
            var asset = GitHubService.PickUe4ssAsset(release);
            var installed = State.Library
                .Where(m => m.Type is ModType.Ue4ssRuntime or ModType.GameRoot)
                .Select(m => m.Version)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .ToList();
            var installedText = installed.Count == 0 ? "（未安装）" : string.Join(", ", installed);
            ReleaseLine.Text = $"最新: {release.TagName}　已安装: {installedText}";
            await ShowInfoAsync("UE4SS 版本",
                $"仓库: {State.Config.Ue4ssRepository}\n最新 Release: {release.TagName}\n" +
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
            SetBusy(false);
        }
    }

    private Task ShowInfoAsync(string title, string message)
        => Ui.Dialogs.InfoAsync(XamlRoot, title, message);
}
