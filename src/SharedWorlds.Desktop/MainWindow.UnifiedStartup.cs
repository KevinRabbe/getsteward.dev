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

        // Owned-private catalog and Bring Here are legacy backend reservation/location workflows. They
        // are useful only while an explicit migration runtime is actually established. The normal
        // AppID-only peer product must not even construct their hidden presentation surface.
        if (_remoteRuntime is not null)
        {
            InitializeOwnedPrivateWorldCatalogUi();
            InitializeOwnedPrivateWorldCatalogRefreshHooks();
            InitializeOwnedPrivateWorldBringHereAction();
        }

        UpdateWorldSharingActionState();
        await InitializeWorldJoinUiAsync();

        // Steam delivers accepted lobby invitations through GameLobbyJoinRequested_t while Steward is
        // running, but uses +connect_lobby <lobbyId> when the invite launches the app. Process that
        // cold-start form only after the same Join UI/runtime handlers are ready.
        InitializeSteamLobbyLaunchRequest();

        InitializeUnifiedImportBrowser();
        InitializeImportAccessibility();
        await InitializeSafeWorldGamesHomeAsync();
        InitializeResponsibilityPresentation();
        InitializeWorldDeletionUi();

        // The legacy backend invitation inbox belongs only to an actually-established migration runtime.
        // AppID-only peer installs use private Steam lobby invitations through WorldJoin instead and must
        // not construct or advertise the old remote invitation surface at all.
        if (_remoteRuntime is not null)
        {
            await InitializeWorldInvitationsUiAsync();
        }

        InitializeProfessionalProductShell();
        if (_remoteRuntime is not null)
        {
            RehomeInvitationsToGlobalLobby();
        }

        InitializeVisualDesignV2();
        InitializeVisualDesignV2Refinements();
        InitializeWorldHistoryUi();
        InitializeNavigationV2StateGuard();
        InitializeQuietStatusPresentation();
        InitializeGameAttentionProjection();
        InitializeResponsiveWorkspace();
    }
}
