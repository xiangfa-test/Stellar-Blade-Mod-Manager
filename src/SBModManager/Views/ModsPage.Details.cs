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
    private void AddFomodDescription(Panel panel, FomodPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Description)) return;
        panel.Children.Add(new TextBlock
        {
            Text = plugin.Description,
            Margin = new Thickness(28, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
            Style = ResourceStyle("CaptionTextBlockStyle"),
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });
    }

    private void UpdateDetails(ModItem? item)
    {
        if (item is null)
        {
            DetailsEmpty.Visibility = Visibility.Visible;
            DetailsScroll.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsEmpty.Visibility = Visibility.Collapsed;
        DetailsScroll.Visibility = Visibility.Visible;
        DetailName.Text = item.Name;
        DetailSubtitle.Text = item.Subtitle;
        DetailMeta.Text = item.ImportedText + "   ·   " + item.FileCountText +
                          (State.IsEnabled(item.Record.Id) ? "   ·   当前已部署" : "");
        DetailSource.Text = string.IsNullOrWhiteSpace(item.SourceText) ? "" : "来源: " + item.SourceText;
        DetailPreview.Source = null;
        DetailPreview.Visibility = Visibility.Collapsed;
        var previews = item.Record.PreviewImages.Count > 0
            ? item.Record.PreviewImages
            : (string.IsNullOrWhiteSpace(item.Record.PreviewImage) ? new List<string>() : new List<string> { item.Record.PreviewImage });
        PreviewMoreButton.Visibility = previews.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (previews.Count > 0)
        {
            var previewPath = Path.Combine(item.Record.ModDir, previews[0]);
            if (File.Exists(previewPath))
            {
                try
                {
                    DetailPreview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(previewPath));
                    DetailPreview.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    Log.Warn("加载预览图失败: " + ex.Message);
                }
            }
        }
        DetailTargetsText.Text = string.Join(Environment.NewLine, item.TargetPaths);
        DetailTags.Text = item.Record.Tags.Count == 0 ? "标签: （未设置）" : "标签: " + string.Join(" · ", item.Record.Tags);
        var cnsParts = new List<string>();
        if (item.Record.CnsFits.Count > 0) cnsParts.Add("服装: " + string.Join(" / ", item.Record.CnsFits.Take(6)));
        if (item.Record.CnsCharacters.Count > 0) cnsParts.Add("角色: " + string.Join(" / ", item.Record.CnsCharacters));
        if (item.Record.CnsOutfitTypes.Count > 0) cnsParts.Add("类型: " + string.Join(" / ", item.Record.CnsOutfitTypes.Take(4)));
        if (item.Record.CnsRequirements.Count > 0) cnsParts.Add("依赖: " + string.Join(" / ", item.Record.CnsRequirements.Take(4)));
        DetailCns.Text = cnsParts.Count == 0 ? "" : "CNS  " + string.Join("   ·   ", cnsParts);
        DetailCns.Visibility = DetailCns.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var deps = DependencyChecker.Check(item.Record);
        DetailDeps.Text = deps.Count == 0
            ? ""
            : "缺少依赖: " + string.Join(" / ", deps.Select(d => d.Missing)) +
              (deps.Any(d => d.Blocking) ? "（启用前需要先装）" : "");
        DetailDeps.Visibility = DetailDeps.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNotes.Text = string.IsNullOrWhiteSpace(item.Record.Notes) ? "" : "备注: " + item.Record.Notes;
        var showUpdate = item.Record.UpdateAvailable && !item.Record.IgnoreUpdate;
        UpdateModButton.Visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;
        IgnoreUpdateButton.Content = item.Record.IgnoreUpdate ? "恢复更新提示" : "忽略此更新";
        IgnoreUpdateButton.Visibility = item.Record.UpdateAvailable || item.Record.IgnoreUpdate
            ? Visibility.Visible
            : Visibility.Collapsed;
        NexusPageButton.Visibility = item.Record.NexusModId is > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailNexusThumb.Source = null;
        DetailNexusThumb.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(item.Record.NexusThumbnailUrl))
        {
            try
            {
                DetailNexusThumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(item.Record.NexusThumbnailUrl));
                DetailNexusThumb.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Warn("加载 Nexus 缩略图失败: " + ex.Message);
            }
        }
        DetailUpdate.Text = item.Record.IgnoreUpdate
            ? "已忽略此模组的更新提示"
            : item.Record.UpdateAvailable
            ? $"可更新: {item.Record.VersionLabel} → {item.Record.LatestVersion}"
            : (item.Record.LastCheckedAt is { } checkedAt
                ? $"已是最新（检查于 {checkedAt.LocalDateTime:MM-dd HH:mm}）"
                : "");
        DetailUpdate.Visibility = string.IsNullOrEmpty(DetailUpdate.Text) ? Visibility.Collapsed : Visibility.Visible;

        _suppressVersionChange = true;
        try
        {
            var siblings = Mods
                .Where(m => m.Record.Id != item.Record.Id &&
                            !string.IsNullOrWhiteSpace(m.Record.GroupKey) &&
                            string.Equals(m.Record.GroupKey, item.Record.GroupKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Record.ImportedAt)
                .ToList();
            if (siblings.Count == 0)
            {
                VersionPanel.Visibility = Visibility.Collapsed;
                VersionBox.ItemsSource = null;
            }
            else
            {
                var versions = new List<ModItem> { item };
                versions.AddRange(siblings);
                VersionPanel.Visibility = Visibility.Visible;
                VersionBox.ItemsSource = versions;
                VersionBox.SelectedItem = item;
            }
        }
        finally
        {
            _suppressVersionChange = false;
        }
    }

    private void OpenModDir_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is ModItem item)
        {
            GameLocator.OpenFolder(item.Record.ModDir);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVersionChange) return;
        if (VersionBox.SelectedItem is not ModItem selected) return;
        if (ModList.SelectedItem is ModItem current && current.Record.Id == selected.Record.Id) return;

        if (!selected.Enabled)
        {
            await SetModEnabledAsync(selected, true);
        }
        ModList.SelectedItem = selected;
        UpdateDetails(selected);
    }

    private async void EditMeta_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item)
        {
            ShowMessage(InfoBarSeverity.Warning, "未选择模组", "先在左侧选择一个模组。");
            return;
        }

        var localTag = CollectionInstaller.LocalTag;
        var tags = new List<string>(item.Record.Tags);

        // Existing user tags across the library, most used first, for one-click adding.
        var suggestions = State.Library
            .SelectMany(r => r.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t) &&
                        !t.StartsWith("合集:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .Take(16)
            .ToList();

        var tagPanel = new StackPanel { Spacing = 4 };
        var suggestPanel = new StackPanel { Spacing = 6 };
        var tagInput = new TextBox
        {
            PlaceholderText = "输入标签后按回车添加，例如：服装",
        };
        var tagHint = new TextBlock
        {
            Text = "「" + localTag + "」是默认合集标签，不可移除；其它标签点 × 删除。",
            TextWrapping = TextWrapping.Wrap,
            Style = ResourceStyle("CaptionTextBlockStyle"),
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        };

        void AddTag(string raw)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 24) text = text[..24];
            if (!tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add(text);
            }
        }

        void RebuildSuggestions()
        {
            suggestPanel.Children.Clear();
            var available = suggestions
                .Where(t => !tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (available.Count == 0)
            {
                suggestPanel.Children.Add(new TextBlock
                {
                    Text = "（暂无其它可用标签）",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });
                return;
            }

            // Lay the quick-add buttons out in rows of four.
            for (var i = 0; i < available.Count; i += 4)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var tag in available.Skip(i).Take(4))
                {
                    var add = new Button
                    {
                        Content = "+ " + tag,
                        Padding = new Thickness(8, 2, 8, 2),
                        FontSize = 12,
                    };
                    add.Click += (_, _) =>
                    {
                        AddTag(tag);
                        RebuildChips();
                    };
                    row.Children.Add(add);
                }
                suggestPanel.Children.Add(row);
            }
        }

        void RebuildChips()
        {
            tagPanel.Children.Clear();
            if (tags.Count == 0)
            {
                tagPanel.Children.Add(new TextBlock
                {
                    Text = "（还没有标签，输入后按回车添加）",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                });
            }

            foreach (var tag in tags.ToList())
            {
                var isLocal = string.Equals(tag, localTag, StringComparison.OrdinalIgnoreCase);

                var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                chipRow.Children.Add(new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center });

                if (isLocal)
                {
                    chipRow.Children.Add(new TextBlock
                    {
                        Text = "默认",
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = ResourceStyle("CaptionTextBlockStyle"),
                        Foreground = ResourceBrush("TextFillColorTertiaryBrush"),
                    });
                }
                else
                {
                    var remove = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                        Padding = new Thickness(4, 2, 4, 2),
                        Background = null,
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    ToolTipService.SetToolTip(remove, "移除标签");
                    remove.Click += (_, _) =>
                    {
                        tags.Remove(tag);
                        RebuildChips();
                    };
                    chipRow.Children.Add(remove);
                }

                tagPanel.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush"),
                    Background = ResourceBrush("SubtleFillColorSecondaryBrush"),
                    Child = chipRow,
                });
            }

            RebuildSuggestions();
        }

        tagInput.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            AddTag(tagInput.Text);
            tagInput.Text = "";
            RebuildChips();
        };

        RebuildChips();

        var notesBox = new TextBox
        {
            Text = item.Record.Notes,
            AcceptsReturn = true,
            Height = 96,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "自定义备注（仅本地保存）",
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "标签" });
        panel.Children.Add(tagPanel);
        panel.Children.Add(tagInput);
        panel.Children.Add(tagHint);
        panel.Children.Add(new TextBlock { Text = "快速添加已有标签" });
        panel.Children.Add(suggestPanel);
        panel.Children.Add(new TextBlock { Text = "备注" });
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑标签 / 备注",
            Content = new ScrollViewer { MaxHeight = 480, Content = panel },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // The default local collection tag is part of the collection membership and is kept.
        if (item.Record.Tags.Any(t => string.Equals(t, localTag, StringComparison.OrdinalIgnoreCase)) &&
            !tags.Any(t => string.Equals(t, localTag, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Insert(0, localTag);
        }

        item.Record.Tags = tags;
        item.Record.Notes = notesBox.Text.Trim();
        ModStore.Save(item.Record);
        item.Refresh();
        RebuildMods();
        UpdateDetails(item);
        ShowMessage(InfoBarSeverity.Success, "已保存", $"「{item.Name}」的标签与备注已更新。");
    }

    private async void OpenGallery_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModItem item) return;
        var previews = item.Record.PreviewImages.Count > 0
            ? item.Record.PreviewImages
            : (string.IsNullOrWhiteSpace(item.Record.PreviewImage) ? new List<string>() : new List<string> { item.Record.PreviewImage });
        var paths = previews
            .Select(rel => Path.Combine(item.Record.ModDir, rel))
            .Where(File.Exists)
            .ToList();
        if (paths.Count == 0) return;

        var image = new Image
        {
            MaxHeight = 520,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        var caption = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = ResourceStyle("CaptionTextBlockStyle"),
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(image);
        panel.Children.Add(caption);

        var index = 0;
        void Show(int i)
        {
            index = (i + paths.Count) % paths.Count;
            try
            {
                image.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(paths[index]));
            }
            catch
            {
                // ignore broken image
            }
            caption.Text = $"{index + 1} / {paths.Count}   {Path.GetFileName(paths[index])}";
        }
        Show(0);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.Name,
            Content = panel,
            PrimaryButtonText = "下一张",
            SecondaryButtonText = "上一张",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            Show(index + 1);
            args.Cancel = true;
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            Show(index - 1);
            args.Cancel = true;
        };
        await dialog.ShowAsync();
    }

    private void DetailPreview_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => OpenGallery_Click(sender, new RoutedEventArgs());


}
