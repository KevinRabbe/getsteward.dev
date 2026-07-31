using System.Windows;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async Task<bool> ConfirmGuardedQuitAsync(
        WorldLifecycleResponsibilitySnapshot responsibility)
    {
        OpenStewardWindow();

        var responsibleWorld = _allWorldItems.FirstOrDefault(item =>
            item.World.Id == responsibility.WorldId);
        if (responsibleWorld is not null)
        {
            if (_globalLobbyVisible)
            {
                HideGlobalLobby(showGames: false);
            }

            if (_globalSettingsVisible)
            {
                HideGlobalSettings();
            }

            OpenGameWorkspace(
                responsibleWorld.AdapterId,
                responsibleWorld.GameName,
                responsibleWorld.World.Id);
            WorldDetailsScroll.ScrollToTop();
            KeepTopLevelNavigationAvailable();
            UpdateResponsibilityPresentation();
        }

        var durableRecoveryExists = false;
        try
        {
            var records = await _workspaceRecoveryStore.ListAsync();
            durableRecoveryExists = responsibility.WorldId is not null &&
                                    records.Any(record => record.WorldId == responsibility.WorldId);
        }
        catch (Exception exception)
        {
            StatusText.Text =
                $"Safe World could not verify durable recovery evidence: {DesktopErrorMessage.Safe(exception)}";
        }

        var worldName = responsibleWorld?.Name ?? "the protected World";
        var dialog = new GuardedQuitDialog(
            worldName,
            responsibility,
            durableRecoveryExists)
        {
            Owner = this
        };

        return dialog.ShowDialog() == true;
    }

    private void CompleteExplicitQuit()
    {
        // Never clear the lifecycle tracker or remove recovery evidence merely to make Quit succeed.
        // With a durable Active record, next startup deliberately becomes InterruptedSession. Without
        // verified evidence, the dialog has already required an explicit possible-data-loss decision.
        _allowExplicitClose = true;
        DisposeTray();
        Close();
        Application.Current.Shutdown();
    }
}
