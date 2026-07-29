using System.Windows.Controls;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _worldSettingsUiInitialized;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_worldSettingsUiInitialized)
        {
            return;
        }

        _worldSettingsUiInitialized = true;
        WorldList.SelectionChanged += WorldList_WorldSettingsSelectionChanged;
        UpdateWorldVersionPolicyUi();
        ResetEnvironmentReadinessUi();
    }

    private void WorldList_WorldSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWorldVersionPolicyUi();
        ResetEnvironmentReadinessUi();
    }

    private async void KeepExactGameVersionCheckBox_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            // Shared environment transitions need their own reviewed backend operation; do not route a
            // settings click through gameplay commit authority or mutate a disconnected legacy copy.
            KeepExactGameVersionCheckBox.IsChecked = true;
            StatusText.Text =
                "Shared Worlds stay on their current environment until an explicit shared update is available.";
            return;
        }

        var nextPolicy = world.GameVersionPolicy == WorldGameVersionPolicy.KeepExact
            ? WorldGameVersionPolicy.AllowUpdateCandidates
            : WorldGameVersionPolicy.KeepExact;

        await RunOperationAsync(
            nextPolicy == WorldGameVersionPolicy.KeepExact
                ? $"Keeping {world.Name} on its current game version..."
                : $"Allowing update candidates for {world.Name}...",
            async () =>
            {
                var settings = new WorldSettingsService(GetStorageForWorld(world));
                var updated = await settings.SetGameVersionPolicyAsync(world.Id, nextPolicy);
                _selectedWorld = updated;

                StatusText.Text = nextPolicy == WorldGameVersionPolicy.KeepExact
                    ? $"'{updated.Name}' will stay on its current game version."
                    : $"'{updated.Name}' may consider future game updates.";

                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });

        UpdateWorldVersionPolicyUi();
    }

    private void UpdateWorldVersionPolicyUi()
    {
        var world = _selectedWorld;
        if (world is null)
        {
            KeepExactGameVersionCheckBox.IsChecked = false;
            KeepExactGameVersionCheckBox.IsEnabled = false;
            GameVersionPolicyText.Text = string.Empty;
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            KeepExactGameVersionCheckBox.IsChecked = true;
            KeepExactGameVersionCheckBox.IsEnabled = false;
            GameVersionPolicyText.Text = HasAuthoritativeRuntimeForWorld(world)
                ? "This shared World stays on its shared game environment."
                : "Reconnect Safe World to manage this shared World's environment.";
            return;
        }

        var keepExact = world.GameVersionPolicy == WorldGameVersionPolicy.KeepExact;
        KeepExactGameVersionCheckBox.IsChecked = keepExact;
        KeepExactGameVersionCheckBox.IsEnabled = !_isBusy;
        GameVersionPolicyText.Text = keepExact
            ? "Safe World will keep this World on its current game environment."
            : "Game updates can be considered when you choose to update this World.";
    }
}
