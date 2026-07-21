namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeUnifiedStartupAsync()
    {
        // The XAML still names transitional anchor handlers while UI-1 replaces the shell. They
        // contain no game-specific behavior and unified startup remains the only active pipeline.
        Loaded -= MainWindow_Loaded;

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
