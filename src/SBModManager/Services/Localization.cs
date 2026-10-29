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

    /// <summary>
    /// Translate now and again after the next layout pass. Controls created by a DataTemplate,
    /// rebuilt in code, or living inside a collapsed ScrollViewer do not exist yet during the
    /// one-time pass that runs on navigation, so they would keep their Chinese text.
    /// </summary>
    public static void ApplyDeferred(DependencyObject? root)
    {
        if (root is null) return;
        Apply(root);
        if (root is FrameworkElement element && element.DispatcherQueue is { } queue)
        {
            queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Apply(root));
        }
    }

    /// <summary>
    /// Translate every known string below <paramref name="root"/>. Failures are isolated per node:
    /// WinUI throws for some elements (for example inside a collapsed ScrollViewer), and a single
    /// throw used to abort the whole subtree, silently leaving later controls in Chinese.
    /// </summary>
    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;

        try
        {
            ApplyElement(root);
        }
        catch (Exception ex)
        {
            Log.Warn($"应用翻译失败（{root.GetType().Name}）: {ex.GetType().Name} {ex.Message}");
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(root);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取子元素失败（{root.GetType().Name}）: {ex.GetType().Name} {ex.Message}");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            DependencyObject? child;
            try
            {
                child = VisualTreeHelper.GetChild(root, i);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取子元素失败（{root.GetType().Name}[{i}]）: {ex.GetType().Name} {ex.Message}");
                continue;
            }
            Apply(child);
        }
    }

    private static string Original(DependencyObject element, string current, string fallbackKey)
    {
        if (Originals.TryGetValue(element, out var stored))
        {
            // The cache only helps for text that can actually be translated. A page that
            // re-populated this element after a language switch stores a composed string
            // (e.g. "Current: D:\..."), which is not a table key; keeping it would make the
            // next Apply write the previous language back.
            if (_table.ContainsKey(stored)) return stored;
            Originals.Remove(element);
        }
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
                // Template-provided "on/off" labels are localized by the OS language, which may
                // not match the in-app language; set them explicitly.
                toggle.OnContent = T("开");
                toggle.OffContent = T("关");
                break;

            case ComboBox combo:
            {
                // Snapshot first: replacing an item while enumerating a WinRT ItemCollection
                // throws a COMException, which aborted this ComboBox entirely (and, before the
                // per-node isolation, every control after it in the tree).
                var items = new List<object>();
                foreach (var item in combo.Items)
                {
                    items.Add(item);
                }

                var selectedIndex = combo.SelectedIndex;
                var selectedTextChanged = false;
                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    if (item is ComboBoxItem comboItem)
                    {
                        // Keep the item object: pages read its Tag / SelectedItem.
                        var before = comboItem.Content as string;
                        ApplyElement(comboItem);
                        if (index == selectedIndex &&
                            !string.Equals(before, comboItem.Content as string, StringComparison.Ordinal))
                        {
                            selectedTextChanged = true;
                        }
                    }
                    else if (combo.ItemsSource is null &&
                             item is string text &&
                             _table.TryGetValue(text, out var translated) &&
                             !string.IsNullOrEmpty(translated) &&
                             translated != text)
                    {
                        combo.Items[index] = translated;
                        if (index == selectedIndex)
                        {
                            selectedTextChanged = true;
                        }
                    }
                }

                // The closed ComboBox is painted from a cached SelectionBoxItem, so translating
                // the items alone leaves the old text on screen. Re-selecting the same index
                // refreshes it; page handlers ignore the transient empty selection (see the
                // "_loading / SelectedItem is null" guards in the settings page).
                if (selectedTextChanged && selectedIndex >= 0)
                {
                    combo.SelectedIndex = -1;
                    combo.SelectedIndex = selectedIndex;
                }
                break;
            }

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
