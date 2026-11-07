using System.Text.Json;

namespace SBModManager.Services;

/// <summary>
/// Helpers used by <see cref="SelfTest"/>. They live in a partial file so the main file stays
/// focused on the check sequence; anything that does not need the suite's local state belongs here.
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// Run a check on the UI thread. The suite is also started from the debug page via Task.Run,
    /// i.e. on a pool thread, and creating or inspecting XAML objects there throws
    /// RPC_E_WRONG_THREAD (0x8001010E) — which used to abort the whole run at the first such check.
    /// </summary>
    private static T OnUiThread<T>(Func<T> action, T fallback)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return action();
        }

        var queue = SBModManager.App.MainWindow?.DispatcherQueue;
        if (queue is null)
        {
            Log.Warn("自检：没有可用的 UI 线程，跳过需要界面控件的检查");
            return fallback;
        }

        var result = fallback;
        using var finished = new ManualResetEventSlim(false);
        queue.TryEnqueue(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                Log.Warn("自检：界面检查失败 " + ex.Message);
            }
            finally
            {
                finished.Set();
            }
        });
        finished.Wait(TimeSpan.FromSeconds(10));
        return result;
    }

    /// <summary>Read "is this mod enabled" out of a UE4SS mods.json; null when unknown.</summary>
    private static bool? ModEnabledInJson(string path, string modName)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("mod_name", out var name) &&
                    string.Equals(name.GetString(), modName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TryGetProperty("mod_enabled", out var enabled))
                {
                    return enabled.ValueKind == JsonValueKind.True;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
