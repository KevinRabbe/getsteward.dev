namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeUnifiedStartupAsync()
    {
        // The legacy Loaded handler refreshes Factorio-only view models. Remove it before the
        // window is shown so the unified adapter-driven refresh is the only startup pipeline.
        Loaded -= MainWindow_Loaded;

        InitializeUnifiedHostingPreference();
        await LoadDeviceSettingsAsync();
        InitializeUnifiedGameUi();
        InitializeUnifiedImportBrowser();
    }
}
