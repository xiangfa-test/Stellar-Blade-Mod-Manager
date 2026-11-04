using SBModManager.Models;

namespace SBModManager.Services;

/// <summary>
/// One-time data migrations for libraries created by older versions. They used to sit inline in
/// <see cref="AppState.Load"/>, which made that method hard to follow and untestable; keeping them
/// here means the state object only loads and saves, and each migration can be reasoned about (and
/// re-run) on its own. Every step is idempotent and guarded by a flag in <see cref="AppConfig"/>.
/// </summary>
public static class Migrations
{
    /// <summary>Runs every pending migration. Returns true when the library was changed.</summary>
    public static bool RunPending(IAppState state)
    {
        var libraryChanged = false;
        var configChanged = false;

        // Mods imported before the local collection existed join it.
        if (!state.Config.LocalCollectionMigrated)
        {
            state.Config.LocalCollectionMigrated = true;
            configChanged = true;
            if (CollectionInstaller.MigrateUntaggedToLocal() > 0)
            {
                libraryChanged = true;
            }
        }

        // Packs that ship an SB/ tree must merge into the game root instead of ~mods.
        if (!state.Config.GameRootOverlayMigrated)
        {
            state.Config.GameRootOverlayMigrated = true;
            configChanged = true;
            if (ModImporter.MigrateGameRootOverlays() > 0)
            {
                libraryChanged = true;
            }
        }

        if (!state.Config.GameRootOverlayMigratedV2)
        {
            state.Config.GameRootOverlayMigratedV2 = true;
            configChanged = true;
            if (ModImporter.MigrateGameRootOverlaysV2() > 0)
            {
                libraryChanged = true;
            }
        }

        // Nexus mods group by mod id so separate branches never merge into one version list.
        if (!state.Config.GroupKeyMigrated)
        {
            state.Config.GroupKeyMigrated = true;
            configChanged = true;
            var fixedCount = 0;
            foreach (var record in state.Library)
            {
                if (record.NexusModId is not > 0) continue;
                var key = ModImporter.GroupKeyFor(record.Name, record.NexusModId);
                if (record.GroupKey == key) continue;
                record.GroupKey = key;
                ModStore.Save(record);
                fixedCount++;
            }
            if (fixedCount > 0)
            {
                libraryChanged = true;
                Log.Info($"已修正 {fixedCount} 个 N 网模组的版本分组");
            }
        }

        // CNS outfit metadata for mods imported before it was parsed.
        if (!state.Config.CnsMetadataMigrated)
        {
            state.Config.CnsMetadataMigrated = true;
            configChanged = true;
            CnsAnalyzer.MigrateMetadata();
        }

        if (configChanged)
        {
            state.SaveConfig();
        }
        if (libraryChanged)
        {
            state.ReloadLibrary();
        }
        return libraryChanged;
    }
}
