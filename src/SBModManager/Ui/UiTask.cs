using SBModManager.Services;

namespace SBModManager.Ui;

/// <summary>Runs fire-and-forget UI work without losing exceptions.</summary>
public static class UiTask
{
    public static void Safe(Windows.Foundation.IAsyncAction action) => Safe(async () => await action);

    public static void Safe<T>(Windows.Foundation.IAsyncOperation<T> operation) => Safe(async () => await operation);

    public static void Safe(Func<Task> action)
    {
        _ = RunAsync(action);
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("后台任务失败", ex);
        }
    }
}
