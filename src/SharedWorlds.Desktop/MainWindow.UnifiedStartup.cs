namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeUnifiedStartupAsync()
    {
        // Recovery evidence is authoritative startup input. Load it before any game/World surface
        // can present an interrupted World as Ready.
        await InitializeRuntimeResponsibilityAsync();
        InitializeUnifiedHostingPreference();
        await LoadDeviceSettingsAsync();
        InitializeUnifiedGameUi();
        InitializeUnifiedImportBrowser();
        InitializeResponsibilityPresentation();
    }
}
