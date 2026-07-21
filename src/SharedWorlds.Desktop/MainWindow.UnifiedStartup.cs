namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeUnifiedStartupAsync()
    {
        // The legacy Loaded handler refreshes Factorio-only view models. Remove it before the
        // window is shown so the unified adapter-driven refresh is the only startup pipeline.
        Loaded -= MainWindow_Loaded;

        // Recovery evidence is authoritative startup input. Load it before any game/World surface
        // can present an interrupted World as Ready.
        await InitializeRuntimeResponsibilityAsync();
        InitializeUnifiedHostingPreference();
        await LoadDeviceSettingsAsync();
        InitializeUnifiedGameUi();
        InitializeUnifiedImportBrowser();
    }
}
