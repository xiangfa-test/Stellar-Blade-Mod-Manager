using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SBModManager.Models;
using Microsoft.UI.Xaml;
using SBModManager.Services;

namespace SBModManager.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class ModItem : ObservableObject
{
    private bool _enabled;
    private bool _busy;

    public ModItem(ModRecord record, bool enabled)
    {
        Record = record;
        _enabled = enabled;
    }

    public ModRecord Record { get; }

    public string Name => Record.Name;
    public string TypeLabel => Record.DisplayType;
    public string AuthorText => string.IsNullOrWhiteSpace(Record.Author) ? "" : $"作者: {Record.Author}";
    public string VersionText => string.IsNullOrWhiteSpace(Record.Version) ? "" : $"v{Record.Version}";
    public string SizeText => FormatSize(Record.SizeBytes);
    public string Subtitle => string.Join("  ·  ", new[] { TypeLabel, Record.CnsSummary, SizeText, VersionText }
        .Where(s => !string.IsNullOrEmpty(s)));

    public string ImportedText => $"导入时间 {Record.ImportedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
    public string FileCountText => $"{Record.Files.Count} 个文件";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrushKey));
            }
        }
    }

    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    public string StatusText => _enabled ? "已启用" : "未启用";

    public string StatusBrushKey => _enabled ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";

    public string DetailText
    {
        get
        {
            var lines = new List<string>
            {
                ImportedText,
                FileCountText,
            };
            if (!string.IsNullOrWhiteSpace(Record.Description))
            {
                lines.Add(Record.Description);
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public IReadOnlyList<string> TargetPaths
    {
        get
        {
            var gameDir = AppState.Instance.GameDir;
            if (gameDir is null) return Array.Empty<string>();
            try
            {
                return Deployer.ComputeTargets(Record, gameDir)
                    .Select(t => Path.GetRelativePath(gameDir, t.Target))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"计算部署路径失败: {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }

    public string SourceText => string.IsNullOrWhiteSpace(Record.SourceName) ? "" : Record.SourceName;

    public string VersionLabel => Record.VersionLabel;
    public string TagsText => Record.Tags.Count == 0 ? "" : string.Join(" · ", Record.Tags);
    public string NotesText => Record.Notes;
    public string UpdateText => Record.UpdateAvailable
        ? $"有更新{(string.IsNullOrWhiteSpace(Record.LatestVersion) ? "" : " → " + Record.LatestVersion)}"
        : "";
    public Visibility UpdateVisibility => Record.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

    public int Priority => Record.Priority;
    public string PriorityText => $"#{Record.Priority}";

    /// <summary>Filled by the mods page from the conflict report.</summary>
    public int ConflictCount { get; set; }
    public bool IsHighConflict { get; set; }
    public string ConflictText => ConflictCount <= 0 ? "" : (IsHighConflict ? $"高冲突 ×{ConflictCount}" : $"冲突 ×{ConflictCount}");
    public Visibility ConflictVisibility => ConflictCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ConflictBadgeBackground => IsHighConflict ? "#33C42B1C" : "#22FFB900";
    public string ConflictBadgeForeground => IsHighConflict ? "#FFC42B1C" : "#FF9D5D00";

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class ExternalItem : ObservableObject
{
    public ExternalItem(ExternalMod mod)
    {
        Mod = mod;
    }

    public ExternalMod Mod { get; }
    public string Name => Mod.Name;
    public string TypeLabel => Mod.Type;
    public string SizeText => ModItem.FormatSize(Mod.SizeBytes);
    public string FilesText => $"{Mod.Files.Count} 个文件";
    public string Subtitle => $"{TypeLabel}  ·  {SizeText}  ·  {FilesText}";
    public IReadOnlyList<string> Files => Mod.Files;
}
