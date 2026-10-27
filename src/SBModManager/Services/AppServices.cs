using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Single composition point for services. Services resolve the application state through this
/// container instead of touching <see cref="AppState.Instance"/> directly, so tests can swap
/// in a fake state without changing production code.
/// </summary>
public static class AppServices
{
    private static IAppState _state = AppState.Instance;

    public static IAppState State
    {
        get => _state;
        set => _state = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Restore production defaults (used by tests that override the state).</summary>
    public static void Reset() => _state = AppState.Instance;

    /// <summary>Run an action against a temporary state, restoring the previous one afterwards.</summary>
    public static T WithState<T>(IAppState state, Func<T> action)
    {
        var previous = _state;
        _state = state;
        try
        {
            return action();
        }
        finally
        {
            _state = previous;
        }
    }
}
