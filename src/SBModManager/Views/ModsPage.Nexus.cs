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


namespace SBModManager.Views;

public sealed partial class ModsPage
{
    private async void OpenNexusPage_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var url = item.Record.NexusPageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private async Task HandleNxmAsync(string url)
    {
        if (!NxmHandler.TryParse(url, out var link))
        {
            ShowMessage(InfoBarSeverity.Warning, "无法识别的 nxm 链接", url);
            return;
        }

        if (link.IsCollection)
        {
            (App.MainWindow as MainWindow)?.OpenCollection(link.CollectionSlug!);
            return;
        }

        var apiKey = State.Config.NexusApiKey;
        var domain = string.IsNullOrWhiteSpace(link.Domain) ? State.Config.NexusGameDomain : link.Domain;
        var hasKey = !string.IsNullOrWhiteSpace(link.Key);
        var fallbackUrl = $"https://www.nexusmods.com/{domain}/mods/{link.ModId}?tab=files&file_id={link.FileId}&nmm=1";

        // A keyless link can only be resolved by premium accounts (this is exactly what
        // Vortex does too: free users get sent to the website for those links).
        if (!hasKey && string.IsNullOrWhiteSpace(apiKey))
        {
            await ShowNxmFallbackAsync(link,
                "这个链接没有携带 Nexus 的下载授权 key（通常来自手动拼的 nxm 链接）。\n" +
                "请到 Nexus 文件页点「Mod Manager Download」重新获取链接（会带 key），或手动下载后导入。",
                fallbackUrl);
            return;
        }

        SetBusy(true);
        var downloaded = Path.Combine(AppPaths.TempRoot, $"nxm-{link.ModId}-{link.FileId}.zip");
        try
        {
            Directory.CreateDirectory(AppPaths.TempRoot);
            var uri = await NexusService.GetDownloadLinkAsync(domain, link.ModId, link.FileId, apiKey,
                link.Key, link.Expires);
            if (string.IsNullOrWhiteSpace(uri))
            {
                await ShowNxmFallbackAsync(link,
                    "Nexus 没有返回下载地址。\n免费账号需要带授权 key 的 nxm 链接（网页「Mod Manager Download」生成）；" +
                    "不带 key 的链接只有 Premium 能取直链。",
                    fallbackUrl);
                return;
            }

            await GitHubService.DownloadAsync(uri, downloaded);
            var result = ModImporter.ImportArchive(downloaded);
            if (result.Mod is null)
            {
                ShowMessage(InfoBarSeverity.Error, "导入失败", result.Error ?? "未知错误");
                return;
            }

            var record = result.Mod;
            record.NexusModId = link.ModId;
            record.NexusFileId = link.FileId;
            record.NexusDomain = domain;
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);

            // Keep the Nexus page title instead of the archive filename.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var info = await NexusService.GetModInfoAsync(domain, link.ModId, apiKey);
                if (info is { Name.Length: > 0 })
                {
                    record.Name = info.Value.Name.Trim();
                    record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
                    if (!string.IsNullOrWhiteSpace(info.Value.Version))
                    {
                        record.Version = info.Value.Version;
                    }
                    if (!string.IsNullOrWhiteSpace(info.Value.PictureUrl))
                    {
                        record.NexusThumbnailUrl = info.Value.PictureUrl;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(record.Version)) record.Version = "file " + link.FileId;
            record.GroupKey = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
            CollectionInstaller.TagAsLocal(record);
            ModStore.Save(record);
            Reload();

            var ask = await ConfirmAsync("下载完成", $"已下载并导入「{record.Name}」。是否立即启用？");
            if (ask)
            {
                var item = Mods.FirstOrDefault(m => m.Record.Id == record.Id);
                if (item is not null) await SetModEnabledAsync(item, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("nxm 下载失败", ex);
            var message = ex.Message +
                          (string.IsNullOrWhiteSpace(apiKey)
                              ? "\n\n提示：请先在「设置 → 网络与集成」填写 Nexus API Key。"
                              : "") +
                          (hasKey ? "" : "\n\n提示：不带 key 的链接只有 Premium 账号能直接取直链。");
            await ShowNxmFallbackAsync(link, message, fallbackUrl);
        }
        finally
        {
            SetBusy(false);
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { /* ignore */ }
        }
    }

    private async Task ShowNxmFallbackAsync(NxmLink link, string message, string fallbackUrl)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Nexus 下载链接",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Text = message + "\n\n" + fallbackUrl,
            },
            PrimaryButtonText = "打开 Nexus 页面",
            CloseButtonText = "关闭",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri(fallbackUrl));
        }
    }

    private async void ImportCollection_Click(object sender, RoutedEventArgs e)
    {
        var urlBox = new TextBox
        {
            PlaceholderText = "https://www.nexusmods.com/games/stellarblade/collections/n7zgom",
            Text = await TryGetCollectionFromClipboardAsync(),
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "粘贴 Nexus 合集链接（或只填 slug）。解析只读取公开信息，不会下载任何文件。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(urlBox);
        panel.Children.Add(new TextBlock
        {
            Text = "下载模组需要 Nexus API Key；批量直链通常需要 Premium 账号，免费账号建议用「复制模组链接」逐个下载。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            Style = ResourceStyle("CaptionTextBlockStyle"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从 Nexus 合集导入",
            Content = panel,
            PrimaryButtonText = "解析合集",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var input = urlBox.Text.Trim();
        if (input.Length == 0) return;

        SetBusy(true);
        NexusCollection collection;
        try
        {
            collection = await NexusService.GetCollectionAsync(input, State.Config.NexusApiKey);
        }
        catch (Exception ex)
        {
            Log.Error("解析 Nexus 合集失败", ex);
            ShowMessage(InfoBarSeverity.Error, "合集解析失败", ex.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await ShowCollectionAsync(collection);
    }

    private static async Task<string> TryGetCollectionFromClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = (await view.GetTextAsync()).Trim();
                if (text.Contains("/collections/", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }
        }
        catch
        {
            // clipboard may be unavailable
        }
        return "";
    }

    private async Task ShowCollectionAsync(NexusCollection collection)
    {
        var required = collection.Mods.Count(m => !m.Optional);
        var info = new StackPanel { Spacing = 8 };
        info.Children.Add(new TextBlock
        {
            Text = collection.Name,
            Style = ResourceStyle("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"作者: {collection.Author}  ·  版本 {collection.RevisionNumber}  ·  " +
                   $"{collection.ModCount} 个模组  ·  {collection.SizeText}",
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(collection.Summary))
        {
            info.Children.Add(new TextBlock { Text = collection.Summary, TextWrapping = TextWrapping.Wrap, MaxLines = 4 });
        }
        info.Children.Add(new TextBlock
        {
            Text = $"必需 {required} 个，可选 {collection.OptionalCount} 个。",
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });

        var includeOptional = new CheckBox { Content = $"包含可选模组（{collection.OptionalCount} 个）", IsChecked = false };
        var enableAfter = new CheckBox { Content = "导入后自动启用（按合集顺序）", IsChecked = false };
        info.Children.Add(includeOptional);
        info.Children.Add(enableAfter);

        var preview = collection.Mods.Take(8)
            .Select(m => "• " + m.DisplayName + (string.IsNullOrWhiteSpace(m.Version) ? "" : $" ({m.Version})") +
                         (m.Optional ? " [可选]" : ""));
        var previewText = string.Join(Environment.NewLine, preview);
        if (collection.Mods.Count > 8)
        {
            previewText += Environment.NewLine + $"… 其余 {collection.Mods.Count - 8} 个";
        }
        info.Children.Add(new TextBlock
        {
            Text = previewText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "合集信息",
            Content = new ScrollViewer { MaxHeight = 380, Content = info },
            PrimaryButtonText = "下载并导入",
            SecondaryButtonText = "复制模组链接",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            CopyCollectionLinks(collection);
            return;
        }
        if (choice != ContentDialogResult.Primary) return;

        await DownloadCollectionAsync(collection, includeOptional.IsChecked == true, enableAfter.IsChecked == true);
    }

    private void CopyCollectionLinks(NexusCollection collection)
    {
        try
        {
            var text = string.Join(Environment.NewLine, collection.Mods
                .Select(m => $"{m.DisplayName}\t{m.PageUrl}"));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowMessage(InfoBarSeverity.Success, "已复制模组链接",
                $"已复制 {collection.Mods.Count} 个模组页面链接到剪贴板，可以逐个手动下载后拖回本工具导入。");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Warning, "复制失败", ex.Message);
        }
    }

    private async Task DownloadCollectionAsync(NexusCollection collection, bool includeOptional, bool enableAfter)
    {
        var apiKey = State.Config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var fallback = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "需要 Nexus API Key",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "合集批量下载需要 Nexus API Key（在「设置 → 网络与集成」填写），" +
                           "且 Nexus 的直链接口通常要求 Premium 账号。\n\n" +
                           "免费账号可以使用「复制模组链接」，逐个下载后拖回本工具导入。",
                },
                PrimaryButtonText = "复制模组链接",
                SecondaryButtonText = "打开合集页面",
                CloseButtonText = "关闭",
            };
            var result = await fallback.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                CopyCollectionLinks(collection);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Launcher.LaunchUriAsync(new Uri(collection.PageUrl));
            }
            return;
        }

        var hasGame = EnsureGame(out var gameDir);
        if (!hasGame) enableAfter = false;

        var selected = collection.Mods.Where(m => includeOptional || !m.Optional).ToList();
        if (selected.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "没有可下载的模组", "该合集没有匹配当前筛选条件的模组。");
            return;
        }

        using var cts = new CancellationTokenSource();
        var line = new TextBlock { Text = $"0 / {selected.Count}" };
        var bar = new ProgressBar { Maximum = selected.Count, Value = 0, Height = 4 };
        var detail = new TextBlock
        {
            Text = "准备中…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(line);
        stack.Children.Add(bar);
        stack.Children.Add(detail);

        var progressDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"下载合集：{collection.Name}",
            Content = stack,
            CloseButtonText = "取消",
        };
        progressDialog.CloseButtonClick += (_, _) => cts.Cancel();
        var dialogTask = progressDialog.ShowAsync().AsTask();

        var ok = 0;
        var failed = new List<string>();
        var noLink = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            if (cts.IsCancellationRequested) break;
            var mod = selected[i];
            var index = i;
            DispatcherQueue.TryEnqueue(() =>
            {
                bar.Value = index;
                line.Text = $"{index} / {selected.Count}";
                detail.Text = "正在处理: " + mod.DisplayName;
            });

            string? temp = null;
            try
            {
                var uri = await NexusService.GetDownloadLinkAsync(collection.GameDomain, mod.ModId, mod.FileId, apiKey);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    noLink++;
                    failed.Add($"{mod.DisplayName}: Nexus 没有返回直链");
                    continue;
                }

                Directory.CreateDirectory(AppPaths.TempRoot);
                temp = Path.Combine(AppPaths.TempRoot, $"collection-{mod.ModId}-{mod.FileId}.zip");
                await GitHubService.DownloadAsync(uri, temp);
                var imported = ModImporter.ImportArchive(temp);
                if (imported.Mod is null)
                {
                    failed.Add($"{mod.DisplayName}: {imported.Error}");
                    continue;
                }

                var record = imported.Mod;
                record.NexusModId = mod.ModId;
                record.NexusFileId = mod.FileId;
                record.NexusDomain = collection.GameDomain;
                if (!string.IsNullOrWhiteSpace(mod.Version)) record.Version = mod.Version;
                var tag = "合集:" + collection.Name;
                if (!record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    record.Tags.Add(tag);
                }
                record.Notes = $"来自 Nexus 合集「{collection.Name}」(rev {collection.RevisionNumber})" +
                               Environment.NewLine + collection.PageUrl;
                ModStore.Save(record);

                if (enableAfter)
                {
                    var deployed = Deployer.Deploy(record, gameDir, force: false);
                    if (!deployed.Success)
                    {
                        failed.Add($"{mod.DisplayName}: 已导入，但启用失败（{deployed.Error}）");
                    }
                }
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.DisplayName}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (temp is not null && File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                await Task.Delay(150, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            bar.Value = ok;
            line.Text = $"完成 {ok} / {selected.Count}";
            detail.Text = "正在整理结果…";
        });

        try
        {
            progressDialog.Hide();
        }
        catch
        {
            // already closed
        }
        try
        {
            await dialogTask;
        }
        catch
        {
            // ignore
        }

        Reload();

        var summary = $"合集「{collection.Name}」：成功 {ok} / {selected.Count}";

        if (failed.Count > 0 || noLink > 0)
        {
            var lines = failed.Take(8).ToList();
            if (failed.Count > 8)
            {
                lines.Add($"…以及另外 {failed.Count - 8} 个失败");
            }
            var hint = noLink > 0 || failed.Any(f => f.Contains("403") || f.Contains("Premium", StringComparison.OrdinalIgnoreCase))
                ? "\n\n提示：Nexus 直链下载通常需要 Premium；没有 Premium 时可用「复制模组链接」手动下载。"
                : "";
            ShowMessage(InfoBarSeverity.Warning, "合集下载完成（有失败）",
                summary + "\n\n" + string.Join(Environment.NewLine, lines) + hint);
        }
        else
        {
            ShowMessage(InfoBarSeverity.Success, "合集下载完成",
                summary + "\n\n已导入的模组带有「合集:" + collection.Name + "」标签，可按标签筛选。");
        }
    }
}
