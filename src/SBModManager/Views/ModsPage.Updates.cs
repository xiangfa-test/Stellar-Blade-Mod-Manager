using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SBModManager.Models;
using SBModManager.Services;
using SBModManager.ViewModels;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using SBModManager.Ui;


namespace SBModManager.Views;

public sealed partial class ModsPage
{
    private async void CheckOneUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (ModList.SelectedItem is ModItem item)
        {
            await CheckUpdateAsync(item);
        }
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: CheckOneUpdate_Click", ex);
        }
    }

    private async void CheckAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        var key = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowMessage(InfoBarSeverity.Warning, Localization.T("缺少 Nexus API Key"), Localization.T("请先在「设置」中填写 Nexus API Key 才能检查更新。"));
            return;
        }

        var candidates = State.Library.Where(r => r.NexusModId is > 0).ToList();
        if (candidates.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, Localization.T("没有可检查的模组"),
                Localization.T("只有通过 nxm:// 链接导入、带 Nexus 模组 ID 的模组才能自动检查更新。"));
            return;
        }

        SetBusy(true);
        var updates = 0;
        var errors = 0;
        try
        {
            foreach (var record in candidates)
            {
                if (record.IgnoreUpdate) continue;
                var info = await NexusService.CheckAsync(record, key, State.Config.NexusGameDomain);
                if (info.HasUpdate) updates++;
                if (!string.IsNullOrEmpty(info.Error)) errors++;
            }
        }
        finally
        {
            SetBusy(false);
        }

        foreach (var candidate in Mods)
        {
            candidate.Refresh();
        }
        UpdateDetails(ModList.SelectedItem as ModItem);

        var updated = candidates
            .Where(r => r.UpdateAvailable && !r.IgnoreUpdate)
            .Select(r => $"「{r.Name}」{r.VersionLabel} → {r.LatestVersion}")
            .Take(8)
            .ToList();
        var detailText = updates > 0
            ? "可更新的模组：\n" + string.Join("\n", updated) +
              (updates > updated.Count ? $"\n…共 {updates} 个" : "")
            : $"已检查 {candidates.Count} 个模组，没有发现更新。";
        if (errors > 0) detailText += $"\n{errors} 个检查失败（见日志）。";

        ShowMessage(updates > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            Localization.T("更新检查完成"), detailText);
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: CheckAllUpdates_Click", ex);
        }
    }

    private async Task CheckUpdateAsync(ModItem item)
    {
        SetBusy(true);
        try
        {
            var info = await NexusService.CheckAsync(item.Record, State.Config.NexusApiKey ?? "",
                State.Config.NexusGameDomain);
            if (!string.IsNullOrEmpty(info.Error))
            {
                ShowMessage(InfoBarSeverity.Warning, Localization.T("检查更新失败"), info.Error);
            }
            else if (info.HasUpdate)
            {
                ShowMessage(InfoBarSeverity.Success, Localization.T("发现新版本"),
                    $"「{item.Name}」: {info.CurrentVersion} → {info.LatestVersion}" +
                    (string.IsNullOrWhiteSpace(info.PageUrl) ? "" : "\n" + info.PageUrl));
            }
            else
            {
                ShowMessage(InfoBarSeverity.Informational, Localization.T("已是最新"), $"「{item.Name}」没有检测到更新。");
            }
        }
        finally
        {
            SetBusy(false);
            item.Refresh();
            UpdateDetails(item);
        }
    }

    private void ShowConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGame(out var gameDir)) return;

        var report = Deployer.BuildConflictReport(gameDir);
        var sb = new StringBuilder();
        sb.AppendLine($"共 {report.ConflictFileCount} 个文件被多个已启用模组同时写入。");

        if (report.HighConflictMods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("按冲突数量排序的模组（高冲突 = 更容易被覆盖）：");
            foreach (var stat in report.HighConflictMods.Take(12))
            {
                sb.AppendLine($"  {(stat.IsHigh ? "⚠" : "·")} {stat.ModName}  ×{stat.ConflictCount}");
            }
        }

        if (report.Chains.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("当前已启用的模组之间没有文件级冲突。");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("冲突链（左侧先加载，右侧覆盖左侧）：");
            foreach (var chain in report.Chains.Take(30))
            {
                sb.AppendLine();
                sb.AppendLine("  " + Path.GetFileName(chain.TargetPath));
                sb.AppendLine("    " + chain.ChainText);
            }
            if (report.Chains.Count > 30)
            {
                sb.AppendLine();
                sb.AppendLine($"…以及另外 {report.Chains.Count - 30} 个冲突文件");
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("冲突链分析"),
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock
                {
                    Text = sb.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = Localization.T("关闭"),
        };
        UiTask.Safe(dialog.ShowAsync());
    }

    private async void ShowAssetConflicts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        var enabled = State.Library.Where(m => State.IsEnabled(m.Id)).ToList();
        if (enabled.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, Localization.T("没有已启用的模组"), Localization.T("先启用模组再分析。"));
            return;
        }

        var progressDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("资源级冲突分析"),
            Content = new TextBlock { Text = Localization.T("正在读取 pak 的 .utoc 资源清单…"), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Localization.T("取消"),
        };
        UiTask.Safe(progressDialog.ShowAsync());

        var conflicts = await Task.Run(() => PakInspector.FindAssetConflicts(enabled));
        progressDialog.Hide();

        if (conflicts.Count == 0)
        {
            await ShowInfoDialogAsync("资源级冲突分析",
                "没有发现同名资源。\n\n注意：这是近似分析，只读取 .utoc 中明文存在的资源名；" +
                "加密或压缩的 TOC 无法解析。");
            return;
        }

        var list = new ListView
        {
            MaxHeight = 420,
            ItemsSource = conflicts.Take(200).Select(c => new AssetConflictItem
            {
                Asset = c.Asset,
                OrderText = string.Join(" → ", c.Owners),
                WinnerText = "生效: " + c.Winner,
            }).ToList(),
        };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Padding="0,6" Spacing="2">
                    <TextBlock Text="{Binding Asset}" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding OrderText}" Opacity="0.7" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding WinnerText}" Opacity="0.7" />
                </StackPanel>
            </DataTemplate>
            """);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"发现 {conflicts.Count} 个资源被多个模组包含（显示前 200 个）：",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock
        {
            Text = "「生效」是当前加载顺序下最终胜出的模组；调整「加载顺序」页或 PAK 前缀即可改变。\n" +
                   "注意：这是基于 .utoc 明文资源名的近似分析，加密/压缩的 TOC 无法读取。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("资源级冲突分析"),
            Content = panel,
            PrimaryButtonText = Localization.T("复制结果"),
            CloseButtonText = Localization.T("关闭"),
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var text = string.Join(Environment.NewLine, conflicts
                .Select(c => $"{c.Asset} | {string.Join(" -> ", c.Owners)} | 生效: {c.Winner}"));
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            args.Cancel = true;
        };
        await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("界面操作失败: ShowAssetConflicts_Click", ex);
        }
    }

    private sealed class AssetConflictItem
    {
        public string Asset { get; init; } = "";
        public string OrderText { get; init; } = "";
        public string WinnerText { get; init; } = "";
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = Localization.T("好"),
        };
        await dialog.ShowAsync();
    }

    private async void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var record = item.Record;
        if (record.NexusModId is not > 0)
        {
            ShowMessage(InfoBarSeverity.Warning, Localization.T("无法更新"), Localization.T("这个模组没有 Nexus 模组 ID。"));
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("下载并更新"),
            Content = $"将从 Nexus 下载最新主文件并替换「{record.Name}」。\n标签、备注、加载顺序和启用状态会保留。",
            PrimaryButtonText = Localization.T("更新"),
            CloseButtonText = Localization.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(text => ShowMessage(InfoBarSeverity.Informational, Localization.T("更新中"), text));
            var updated = await ModUpdater.UpdateAsync(record, State.Config.NexusApiKey ?? "",
                State.Config.NexusApiKey, progress);
            Reload();
            ShowMessage(InfoBarSeverity.Success, Localization.T("更新完成"),
                $"「{updated.Name}」已更新到 {updated.VersionLabel}。");
        }
        catch (Exception ex)
        {
            Log.Error("模组更新失败", ex);
            ShowMessage(InfoBarSeverity.Error, Localization.T("更新失败"), ex.Message);
        }
        finally
        {
            SetBusy(false);
            Reload();
        }
    }

    private async void FillNexusVersions_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(State.Config.NexusApiKey))
        {
            ShowMessage(InfoBarSeverity.Warning, Localization.T("缺少 Nexus API Key"), Localization.T("请先在「设置」中填写 Nexus API Key。"));
            return;
        }

        SetBusy(true);
        try
        {
            var filled = await ModUpdater.FillVersionsAsync();
            Reload();
            ShowMessage(filled > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                Localization.T("补全版本号"), filled > 0 ? $"已补全 {filled} 个模组的版本号。" : Localization.T("没有需要补全的版本号。"));
        }
        catch (Exception ex)
        {
            Log.Error("补全版本号失败", ex);
            ShowMessage(InfoBarSeverity.Error, Localization.T("补全失败"), ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RenameNexusNames_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(State.Config.NexusApiKey))
        {
            ShowMessage(InfoBarSeverity.Warning, Localization.T("缺少 Nexus API Key"), Localization.T("请先在「设置」中填写 Nexus API Key。"));
            return;
        }

        SetBusy(true);
        try
        {
            var renamed = await ModUpdater.RenameGenericAsync();
            Reload();
            ShowMessage(renamed > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                Localization.T("补全名称"), renamed > 0 ? $"已从 N 网补全 {renamed} 个模组的名称。" : Localization.T("没有需要补全的模组。"));
        }
        catch (Exception ex)
        {
            Log.Error("补全 N 网名称失败", ex);
            ShowMessage(InfoBarSeverity.Error, Localization.T("补全失败"), ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void IgnoreUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        item.Record.IgnoreUpdate = !item.Record.IgnoreUpdate;
        ModStore.Save(item.Record);
        UpdateDetails(item);
        ShowMessage(InfoBarSeverity.Informational, Localization.T("更新提示"),
            item.Record.IgnoreUpdate ? $"已忽略「{item.Name}」的更新提示。" : Localization.T("已恢复更新提示。"));
    }
}
