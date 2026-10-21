using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Services;
using Windows.Storage.Pickers;

namespace SBModManager.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    private AppState State => AppState.Instance;

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            GameDirText.Text = State.GameDir is { } dir
                ? "当前: " + dir
                : "尚未设置。点击「自动检测」或「浏览…」选择 StellarBlade 安装目录（包含 SB.exe 的目录）。";

            HardLinkToggle.IsOn = State.Config.UseHardLinks;
            NexusKeyBox.Password = State.Config.NexusApiKey ?? "";
            ElevateToggle.IsOn = State.Config.ElevateOnStartup;
            NxmToggle.IsOn = State.Config.HandleNxmLinks;
            LaunchModeBox.SelectedIndex = State.Config.LaunchMode == "direct" ? 1 : 0;
            LibraryPathText.Text = "模组库: " + AppPaths.ModsRoot;
            DataPathText.Text = "数据目录: " + AppPaths.Root;
            ThemeBox.SelectedIndex = State.Config.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        if (!GameLocator.IsValidGameDir(folder.Path))
        {
            ShowError("该目录不是有效的《剑星》安装目录（需要包含 SB.exe 与 SB/Content/Paks）。");
            return;
        }
        SetGameDir(folder.Path);
        LoadSettings();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var dir = GameLocator.AutoDetect();
        if (dir is null)
        {
            ShowError("未找到游戏目录。请手动选择 StellarBlade 安装位置。");
            return;
        }
        SetGameDir(dir);
        LoadSettings();
    }

    private void SetGameDir(string dir)
    {
        State.Config.GameDir = dir;
        State.Config.AutoDetectGame = true;
        State.SaveConfig();
        Log.Info("设置游戏目录: " + dir);
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null)
        {
            ShowError("尚未设置有效的游戏目录。");
            return;
        }
        var build = GameLocator.ReadSteamBuildId(State.GameDir);
        var mods = Deployer.PaksModsDir(State.GameDir);
        var ue4ss = Deployer.IsUe4ssInstalled(State.GameDir);
        var lines = new List<string>
        {
            "游戏目录有效 ✓",
            "~mods 目录: " + (Directory.Exists(mods) ? "存在" : "不存在（首次启用模组时会自动创建）"),
            "UE4SS 运行库: " + (ue4ss ? "已安装" : "未安装"),
        };
        if (!string.IsNullOrEmpty(build)) lines.Add("Steam buildid: " + build);
        ShowInfo("安装检查", string.Join(Environment.NewLine, lines));
    }

    private void HardLink_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.Config.UseHardLinks = HardLinkToggle.IsOn;
        State.SaveConfig();
    }

    private void LaunchMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = (LaunchModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "steam";
        State.Config.LaunchMode = tag;
        State.SaveConfig();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        State.Config.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
        State.SaveConfig();
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = State.Config.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void OpenGameDir_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null) { ShowError("尚未设置游戏目录。"); return; }
        GameLocator.OpenFolder(State.GameDir);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => GameLocator.OpenFolder(AppPaths.ModsRoot);

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (State.GameDir is null) { ShowError("尚未设置游戏目录。"); return; }
        GameLocator.OpenFolder(Deployer.PaksModsDir(State.GameDir));
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => GameLocator.OpenFolder(AppPaths.GameConfigDir);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Log.OpenLogFolder();

    private void ShowInfo(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
        };
        _ = dialog.ShowAsync();
    }

    private void ShowError(string message) => ShowInfo("提示", message);

    private void SaveNexusKey_Click(object sender, RoutedEventArgs e)
    {
        State.Config.NexusApiKey = string.IsNullOrWhiteSpace(NexusKeyBox.Password)
            ? null
            : NexusKeyBox.Password.Trim();
        State.SaveConfig();
        ShowInfo("已保存", State.Config.NexusApiKey is null
            ? "已清除 Nexus API Key。"
            : "Nexus API Key 已保存在本机配置中。");
    }

    private void Elevate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.Config.ElevateOnStartup = ElevateToggle.IsOn;
        State.SaveConfig();
    }

    private void Nxm_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var exe = Environment.ProcessPath;
        try
        {
            if (NxmToggle.IsOn)
            {
                if (string.IsNullOrWhiteSpace(exe))
                {
                    throw new InvalidOperationException("无法确定当前程序路径。");
                }
                var previous = NxmHandler.Register(exe);
                if (!string.IsNullOrWhiteSpace(previous))
                {
                    State.Config.PreviousNxmHandler = previous;
                }
                State.Config.HandleNxmLinks = true;
                State.SaveConfig();
                ShowInfo("已接管 nxm:// 链接",
                    "现在 Nexus 的「Mod Manager Download」会交给本工具。\n" +
                    "关闭此开关可恢复原来的处理程序（例如 Vortex）。");
            }
            else
            {
                NxmHandler.Unregister(State.Config.PreviousNxmHandler);
                State.Config.HandleNxmLinks = false;
                State.Config.PreviousNxmHandler = null;
                State.SaveConfig();
                ShowInfo("已恢复 nxm:// 链接", "已把 nxm:// 处理程序恢复为之前的程序。");
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换 nxm:// 接管失败", ex);
            _loading = true;
            NxmToggle.IsOn = !NxmToggle.IsOn;
            _loading = false;
            ShowError("切换失败: " + ex.Message);
        }
    }

}
