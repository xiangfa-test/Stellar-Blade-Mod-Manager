using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Services;

namespace SBModManager.Views;

public sealed class ProfileItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed partial class ProfilesPage : Page
{
    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    public ProfilesPage()
    {
        InitializeComponent();
        ProfileList.ItemsSource = Profiles;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        Profiles.Clear();
        foreach (var profile in ProfileStore.LoadAll())
        {
            var enabled = profile.Entries.Count(e => e.Enabled);
            Profiles.Add(new ProfileItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Summary = $"{enabled} 个启用 · 共 {profile.Entries.Count} 个模组 · {profile.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}",
            });
        }
        StatusText.Text = Profiles.Count == 0 ? "还没有方案，先点「保存当前为方案」。" : $"共 {Profiles.Count} 个方案。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private async void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        var box = new TextBox
        {
            Text = "方案 " + DateTime.Now.ToString("MM-dd HH:mm"),
            SelectionStart = 1000,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("保存当前状态为方案"),
            Content = box,
            PrimaryButtonText = Localization.T("保存"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;

        var profile = ProfileStore.Capture(name);
        ProfileStore.Save(profile);
        Reload();
        StatusText.Text = $"已保存方案「{name}」（{profile.Entries.Count(e => e.Enabled)} 个启用）。";
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: SaveCurrent_Click", ex);
        }
    }

    private ModProfile? Find(string id) => ProfileStore.LoadAll().FirstOrDefault(p => p.Id == id);

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (sender is not Button { Tag: string id }) return;
        var profile = Find(id);
        if (profile is null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("应用方案"),
            Content = $"切换到「{profile.Name}」会停用不在该方案里的模组，并按方案顺序重新部署。",
            PrimaryButtonText = Localization.T("应用"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = "正在应用方案…";
        var progress = new Progress<string>(text => StatusText.Text = text);
        var result = await Task.Run(() => ProfileStore.Apply(profile, progress));
        if (result.Enabled < 0)
        {
            StatusText.Text = "请先在设置里选择游戏目录。";
            return;
        }

        StatusText.Text = $"方案「{profile.Name}」已应用：启用 {result.Enabled} 个，失败 {result.Failed} 个。";
        Reload();
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: ApplyProfile_Click", ex);
        }
    }

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (sender is not Button { Tag: string id }) return;
        var profile = Find(id);
        if (profile is null) return;

        var box = new TextBox { Text = profile.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("重命名方案"),
            Content = box,
            PrimaryButtonText = Localization.T("保存"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;
        profile.Name = name;
        ProfileStore.Save(profile);
        Reload();
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: RenameProfile_Click", ex);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (sender is not Button { Tag: string id }) return;
        var profile = Find(id);
        if (profile is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("删除方案"),
            Content = $"确定删除「{profile.Name}」吗？只删除方案记录，不会改动已安装的模组。",
            PrimaryButtonText = Localization.T("删除"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        ProfileStore.Delete(id);
        Reload();
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: DeleteProfile_Click", ex);
        }
    }
}
