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

        // Remote sharing is optional. With no production/development remote configuration Safe World
        // stays local-only; with valid configuration it authenticates through Steam and refreshes the
        // same game-first library with canonical shared Worlds.
        await InitializeStewardRemoteSessionAsync();
        UpdateWorldSharingActionState();
        await InitializeWorldJoinUiAsync();

        InitializeUnifiedImportBrowser();
        InitializeImportAccessibility();
        await InitializeSafeWorldGamesHomeAsync();
        InitializeResponsibilityPresentation();
        InitializeWorldDeletionUi();
        InitializeProfessionalProductShell();
        InitializeQuietStatusPresentation();
        InitializeGameAttentionProjection();
        await InitializeWorldInvitationsUiAsync();
        InitializeResponsiveWorkspace();
    }
}
