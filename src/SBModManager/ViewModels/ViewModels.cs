using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SBModManager.Models;
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
    public string Subtitle => string.Join("  ·  ", new[] { TypeLabel, SizeText, VersionText }
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

public sealed class LogItem
{
    public string Text { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
}

public sealed class LogFeed
{
    public ObservableCollection<LogItem> Items { get; } = new();

    public LogFeed()
    {
        Log.LineWritten += line =>
        {
            Items.Add(new LogItem { Text = line });
            while (Items.Count > 400) Items.RemoveAt(0);
        };
    }
}
