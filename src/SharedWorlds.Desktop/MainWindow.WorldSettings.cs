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

        if (_remoteWorldIds.Contains(world.Id))
        {
            // Remote environment transitions need their own reviewed backend operation; do not route a
            // settings click through gameplay commit authority just to mutate desktop metadata.
            KeepExactGameVersionCheckBox.IsChecked = true;
            StatusText.Text =
                "Shared Worlds stay on their current canonical environment until the explicit remote update flow is connected.";
            return;
        }

        var nextPolicy = world.GameVersionPolicy == WorldGameVersionPolicy.KeepExact
            ? WorldGameVersionPolicy.AllowUpdateCandidates
            : WorldGameVersionPolicy.KeepExact;

        await RunOperationAsync(
            nextPolicy == WorldGameVersionPolicy.KeepExact
                ? $"Locking {world.Name} to its exact game version..."
                : $"Allowing update candidates for {world.Name}...",
            async () =>
            {
                var settings = new WorldSettingsService(GetStorageForWorld(world));
                var updated = await settings.SetGameVersionPolicyAsync(world.Id, nextPolicy);
                _selectedWorld = updated;

                StatusText.Text = nextPolicy == WorldGameVersionPolicy.KeepExact
                    ? $"World '{updated.Name}' will stay on its exact known-good game version."
                    : $"World '{updated.Name}' may consider future update candidates; updates remain explicit.";

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

        if (_remoteWorldIds.Contains(world.Id))
        {
            KeepExactGameVersionCheckBox.IsChecked = true;
            KeepExactGameVersionCheckBox.IsEnabled = false;
            GameVersionPolicyText.Text =
                "This shared World uses its canonical Steward environment. Environment upgrades remain explicit and are not changed by a local checkbox.";
            return;
        }

        var keepExact = world.GameVersionPolicy == WorldGameVersionPolicy.KeepExact;
        KeepExactGameVersionCheckBox.IsChecked = keepExact;
        KeepExactGameVersionCheckBox.IsEnabled = !_isBusy;
        GameVersionPolicyText.Text = keepExact
            ? "SharedWorlds treats the current exact environment as known-good and ignores newer game versions for this World."
            : "Newer versions may be offered only as explicit update candidates. The current environment stays known-good until a tested candidate is accepted.";
    }
}
