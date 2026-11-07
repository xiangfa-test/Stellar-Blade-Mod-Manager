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

    /// <summary>
    /// The suite deploys and undeploys real files in the target game folder, so it must only run
    /// against a throwaway directory. The debug page runs it with whatever config happens to be
    /// loaded — which once deployed test paks into the player's real Steam install.
    /// Returns a reason string when the target is unsafe, or null when it is fine.
    /// </summary>
    private static string? UnsafeGameDirReason(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return "游戏目录为空";
        if (Environment.GetEnvironmentVariable("SBMM_TEST_ALLOW_REAL_GAME") == "1") return null;

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var full = Path.GetFullPath(gameDir).TrimEnd(separators);

        foreach (var allowed in new[] { AppPaths.Root, Path.GetTempPath() })
        {
            var root = Path.GetFullPath(allowed).TrimEnd(separators);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (full.Contains("_scratch", StringComparison.OrdinalIgnoreCase)) return null;

        return "自检会真实部署/停用文件，拒绝在非测试目录上运行（" + full +
               "）；确需在真实目录运行请设置 SBMM_TEST_ALLOW_REAL_GAME=1";
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
