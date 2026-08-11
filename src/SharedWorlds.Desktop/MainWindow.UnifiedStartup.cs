namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeUnifiedStartupAsync()
    {
        // Recovery evidence is authoritative startup input. Load it before any game/World surface
        // can present an interrupted World as Ready.
        await InitializeRuntimeResponsibilityAsync();
        RegisterAdditionalProductionAdapters();
        InitializeUnifiedHostingPreference();
        await LoadDeviceSettingsAsync();

        // Peer gameplay uses the one App-owned Steam runtime and must not depend on remote HTTP/auth
        // configuration. Compose it immediately after the durable installation identity is available;
        // optional legacy remote authentication below may reuse the same Steam lifetime afterward.
        InitializeStewardPeerRuntime();

        await InitializeUnifiedGameUiAsync();
        InitializeOwnedPrivateWorldCatalogRefreshHooks();
        InitializeOwnedPrivateWorldBringHereAction();
        InitializeInstallationAwareWorldActions();
        InitializeWorldSearchUi();

        // Real sharing owns the common Share/Manage-access action from this point forward. Initialize
        // it before remote authentication so the obsolete UnifiedGames placeholder cannot surface
        // while authentication is in progress.
        InitializeWorldSharingUi();
        InitializePortableWorldExportUi();
        InitializePortableWorldImportUi();
        InitializePortableWorldDropUi();

        // Transitional remote services remain optional. They no longer gate Steam initialization or
        // peer gameplay composition; Steam authentication reuses the already-created App runtime when
        // both configurations carry the same AppID.
        await InitializeStewardRemoteSessionAsync();
        UpdateWorldSharingActionState();
        await InitializeWorldJoinUiAsync();

        InitializeUnifiedImportBrowser();
        InitializeImportAccessibility();
        await InitializeSafeWorldGamesHomeAsync();
        InitializeResponsibilityPresentation();
        InitializeWorldDeletionUi();
        await InitializeWorldInvitationsUiAsync();
        InitializeProfessionalProductShell();
        RehomeInvitationsToGlobalLobby();
        InitializeVisualDesignV2();
        InitializeVisualDesignV2Refinements();
        InitializeWorldHistoryUi();
        InitializeNavigationV2StateGuard();
        InitializeQuietStatusPresentation();
        InitializeGameAttentionProjection();
        InitializeResponsiveWorkspace();
    }
}
