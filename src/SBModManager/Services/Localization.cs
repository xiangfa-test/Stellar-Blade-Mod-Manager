using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SBModManager.Services;

/// <summary>
/// Table-driven UI text. All visible strings live in strings.json next to the executable;
/// the table maps the Chinese source text to the selected language, so wording can be edited
/// without touching XAML or code.
/// </summary>
public static class Localization
{
    public const string DefaultLanguage = "zh-CN";

    private static readonly ConditionalWeakTable<DependencyObject, string> Originals = new();
    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static string StringsPath => Path.Combine(AppContext.BaseDirectory, "strings.json");

    public static void Initialize(string? language)
    {
        CurrentLanguage = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;
        _table = LoadTable(CurrentLanguage);
    }

    public static string T(string source)
        => !string.IsNullOrEmpty(source) && _table.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : source;

    private static Dictionary<string, string> LoadTable(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(StringsPath))
            {
                Log.Warn("找不到翻译表: " + StringsPath);
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(StringsPath));
            if (!doc.RootElement.TryGetProperty(language, out var section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in section.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString() ?? "";
                }
            }
            Log.Info($"已加载翻译表: {language}（{result.Count} 条）");
        }
        catch (Exception ex)
        {
            Log.Warn("加载翻译表失败: " + ex.Message);
        }
        return result;
    }

    /// <summary>Translate every known string below <paramref name="root"/>.</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        try
        {
            ApplyElement(root);
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                Apply(VisualTreeHelper.GetChild(root, i));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("应用翻译失败: " + ex.Message);
        }
    }

    private static string Original(DependencyObject element, string current, string fallbackKey)
    {
        if (Originals.TryGetValue(element, out var stored)) return stored;
        var value = !string.IsNullOrWhiteSpace(current) ? current : fallbackKey;
        Originals.Add(element, value);
        return value;
    }

    private static void ApplyElement(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock text:
                text.Text = T(Original(text, text.Text, ""));
                break;

            case Button button when button.Content is string content:
                button.Content = T(Original(button, content, ""));
                break;

            case HyperlinkButton link when link.Content is string linkText:
                link.Content = T(Original(link, linkText, ""));
                break;

            case CheckBox check when check.Content is string checkText:
                check.Content = T(Original(check, checkText, ""));
                break;

            case RadioButton radio when radio.Content is string radioText:
                radio.Content = T(Original(radio, radioText, ""));
                break;

            case ToggleSwitch toggle:
                if (toggle.OnContent is string on) toggle.OnContent = T(Original(toggle, on, ""));
                if (toggle.OffContent is string off) toggle.OffContent = T(Original(toggle, off, ""));
                break;

            case TextBox box when !string.IsNullOrWhiteSpace(box.PlaceholderText):
                box.PlaceholderText = T(Original(box, box.PlaceholderText, ""));
                break;

            case PasswordBox password when !string.IsNullOrWhiteSpace(password.PlaceholderText):
                password.PlaceholderText = T(Original(password, password.PlaceholderText, ""));
                break;

            case InfoBar bar:
                bar.Title = T(Original(bar, bar.Title, ""));
                bar.Message = T(Original(bar, bar.Message, ""));
                break;

            case NavigationViewItem nav when nav.Content is string navText:
                nav.Content = T(Original(nav, navText, ""));
                break;

            case ComboBoxItem comboItem when comboItem.Content is string comboText:
                comboItem.Content = T(Original(comboItem, comboText, ""));
                break;

            case MenuFlyoutItem menuItem:
                menuItem.Text = T(Original(menuItem, menuItem.Text, ""));
                break;

            case MenuFlyoutSubItem subItem:
                subItem.Text = T(Original(subItem, subItem.Text, ""));
                break;

            case ToolTip tip when tip.Content is string tipText:
                tip.Content = T(Original(tip, tipText, ""));
                break;
        }
    }
}
