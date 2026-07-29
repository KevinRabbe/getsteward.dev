namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal void InitializeNavigationV2StateGuard()
    {
        if (_globalLobbyButton is null)
        {
            return;
        }

        _globalLobbyButton.IsEnabledChanged += (_, _) =>
        {
            if (_globalLobbyVisible && RefreshButton.IsEnabled && !_globalLobbyButton.IsEnabled)
            {
                // Lobby is a selected navigation tab, not a one-shot command. Product-shell legacy
                // state used to disable it while open; keep it interactive so selected styling stays intact.
                _globalLobbyButton.IsEnabled = true;
            }
        };
    }
}
