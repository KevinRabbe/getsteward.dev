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
    }

    private void WorldList_WorldSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateWorldVersionPolicyUi();

    private async void KeepExactGameVersionCheckBox_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
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
                var settings = new WorldSettingsService(_storage);
                var updated = await settings.SetGameVersionPolicyAsync(world.Id, nextPolicy);
                _selectedWorld = updated;

                StatusText.Text = nextPolicy == WorldGameVersionPolicy.KeepExact
                    ? $"World '{updated.Name}' will stay on its exact known-good game version."
                    : $"World '{updated.Name}' may consider future update candidates; updates remain explicit.";

                await RefreshWorldsAsync(updated.Id, preserveStatus: true);
            });

        UpdateWorldVersionPolicyUi();
    }

    private void UpdateWorldVersionPolicyUi()
    {
        var world = _selectedWorld;
        if (world is null)
        {
            KeepExactGameVersionCheckBox.IsChecked = false;
            GameVersionPolicyText.Text = string.Empty;
            return;
        }

        var keepExact = world.GameVersionPolicy == WorldGameVersionPolicy.KeepExact;
        KeepExactGameVersionCheckBox.IsChecked = keepExact;
        GameVersionPolicyText.Text = keepExact
            ? "SharedWorlds treats the current exact environment as known-good and ignores newer game versions for this World."
            : "Newer versions may be offered only as explicit update candidates. The current environment stays known-good until a tested candidate is accepted.";
    }
}
