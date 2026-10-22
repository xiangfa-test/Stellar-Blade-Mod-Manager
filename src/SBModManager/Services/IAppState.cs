using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// Read/write surface of the application state. Services depend on this instead of the
/// concrete singleton so they can be exercised with a fake state in tests.
/// </summary>
public interface IAppState
{
    AppConfig Config { get; }
    DeployState Deployed { get; }
    List<ModRecord> Library { get; }
    string? GameDir { get; }

    bool IsEnabled(string modId);
    void SaveConfig();
    void SaveDeployed();
    void ReloadLibrary();
}
