namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private void InitializeWorldLobbyUi()
    {
        // Lobby is a top-level cross-game destination. The authoritative data remains World-scoped
        // (membership + ephemeral players + current Host), but selected World details no longer carry
        // a second full lobby presentation. Global Lobby composition is initialized after all remote
        // and action surfaces exist in InitializeProfessionalProductShell().
        InitializeCompletenessControlsUi();
    }
}
