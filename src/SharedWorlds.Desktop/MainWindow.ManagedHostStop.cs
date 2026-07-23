using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _hostStopRequestInFlight;

    private async void StopHostingButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop))
        {
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        if (responsibility.Kind != WorldLifecycleResponsibilityKind.ActiveLifecycle ||
            responsibility.WorldId != world.Id ||
            responsibility.Mode != ManagedWorldSessionMode.Hosted ||
            responsibility.Phase != WorldLifecyclePhase.Running ||
            _hostStopRequestInFlight)
        {
            return;
        }

        _hostStopRequestInFlight = true;
        UpdateManagedHostStopUi();
        StatusText.Text = $"Saving and stopping {world.Name}...";

        try
        {
            var lifecycle = GetLifecycleForWorld(world);
            if (!await lifecycle.RequestHostStopAsync(world.Id))
            {
                StatusText.Text =
                    $"{adapter.DisplayName} is still entering its managed host session. Try Stop Hosting again once it is running.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not safely stop {world.Name}. The hosted session remains Steward responsibility.";
            ShowError("Could not safely stop hosting", exception);
        }
        finally
        {
            _hostStopRequestInFlight = false;
            UpdateManagedHostStopUi();
        }
    }

    private void UpdateManagedHostStopUi()
    {
        var world = _selectedWorld;
        var responsibility = _responsibilityTracker.Current;
        var adapterSupportsStop = world is not null &&
            TryGetAdapter(world.GameAdapterId, out var adapter) &&
            adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop);
        var canStop = adapterSupportsStop &&
            responsibility.Kind == WorldLifecycleResponsibilityKind.ActiveLifecycle &&
            responsibility.WorldId == world!.Id &&
            responsibility.Mode == ManagedWorldSessionMode.Hosted &&
            responsibility.Phase == WorldLifecyclePhase.Running;

        StopHostingButton.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        StopHostingButton.IsEnabled = canStop && !_hostStopRequestInFlight;
        StopHostingButton.Content = _hostStopRequestInFlight ? "Saving..." : "Stop Hosting";
        StopHostingButton.ToolTip = canStop
            ? "Save the World, shut down the dedicated server safely, restore its original runtime inputs, then let Steward capture and commit the new revision."
            : null;
    }
}
