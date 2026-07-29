namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal void InitializeNavigationV2StateGuard()
    {
        if (_gamesNavigationButton is null)
        {
            return;
        }

        // Operational busy state may disable actions that mutate or refresh World state, but it must
        // never turn top-level destinations into a navigation trap. Register after the legacy shell
        // state handlers so this presentation invariant wins whenever their old Refresh coupling runs.
        RefreshButton.IsEnabledChanged += (_, _) => KeepTopLevelNavigationAvailable();
        GamesLibraryPanel.IsVisibleChanged += (_, _) => KeepTopLevelNavigationAvailable();

        if (_globalLobbyPanel is not null)
        {
            _globalLobbyPanel.IsVisibleChanged += (_, _) => KeepTopLevelNavigationAvailable();
        }

        if (_globalSettingsPanel is not null)
        {
            _globalSettingsPanel.IsVisibleChanged += (_, _) => KeepTopLevelNavigationAvailable();
        }

        KeepTopLevelNavigationAvailable();
    }

    private void KeepTopLevelNavigationAvailable()
    {
        if (_gamesNavigationButton is not null)
        {
            _gamesNavigationButton.IsEnabled = true;
        }

        if (_globalLobbyButton is not null)
        {
            _globalLobbyButton.IsEnabled = true;
        }

        if (_globalSettingsButton is not null)
        {
            _globalSettingsButton.IsEnabled = true;
        }
    }
}
