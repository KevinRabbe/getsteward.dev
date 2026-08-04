using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _ownedPrivateWorldBringHereButton;
    private CancellationTokenSource? _ownedPrivateWorldBringHereCancellation;

    private void InitializeOwnedPrivateWorldBringHereAction()
    {
        var panel = _ownedPrivateWorldDetailsPanel
            ?? throw new InvalidOperationException(
                "The private World detail panel must exist before Bring Here initializes.");
        var availability = _ownedPrivateWorldAvailabilityText
            ?? throw new InvalidOperationException(
                "The private World availability presentation must exist before Bring Here initializes.");

        _ownedPrivateWorldBringHereButton = new Button
        {
            Content = "Bring here",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 22, 0, 0),
            MinWidth = 150,
            Padding = new Thickness(20, 10, 20, 10)
        };
        AutomationProperties.SetName(
            _ownedPrivateWorldBringHereButton,
            "Bring private World to this PC");
        AutomationProperties.SetHelpText(
            _ownedPrivateWorldBringHereButton,
            "Download the exact private World from another owned Safe World installation and store it on this PC.");
        _ownedPrivateWorldBringHereButton.Click += OwnedPrivateWorldBringHereButton_Click;
        panel.Children.Add(_ownedPrivateWorldBringHereButton);

        _ownedPrivateWorldList!.SelectionChanged += (_, _) =>
            UpdateOwnedPrivateWorldBringHereActionState();
        RefreshButton.IsEnabledChanged += (_, _) =>
            UpdateOwnedPrivateWorldBringHereActionState();
        DependencyPropertyDescriptor.FromProperty(
                TextBlock.TextProperty,
                typeof(TextBlock))
            ?.AddValueChanged(
                availability,
                (_, _) => UpdateOwnedPrivateWorldBringHereActionState());
        Closed += (_, _) =>
            Volatile.Read(ref _ownedPrivateWorldBringHereCancellation)?.Cancel();

        UpdateOwnedPrivateWorldBringHereActionState();
    }

    private async void OwnedPrivateWorldBringHereButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var item = _selectedOwnedPrivateWorld;
        var remote = Volatile.Read(ref _remoteRuntime);
        if (item is null ||
            remote is null ||
            item.Entry.Availability != BringHereAvailability.Available ||
            item.Entry.Source is null ||
            item.Entry.ConflictingClaims.Count != 0)
        {
            StatusText.Text =
                "This private World is no longer unambiguously available. Refresh and try again.";
            UpdateOwnedPrivateWorldBringHereActionState();
            return;
        }

        var cancellation = new CancellationTokenSource();
        if (Interlocked.CompareExchange(
                ref _ownedPrivateWorldBringHereCancellation,
                cancellation,
                comparand: null) is not null)
        {
            cancellation.Dispose();
            return;
        }

        OwnedPrivateWorldMaterializationResult? result = null;
        SetBusy(true);
        _ownedPrivateWorldBringHereButton!.Content = "Bringing here…";
        StatusText.Text = $"Bringing '{item.Entry.Name}' to this PC...";
        try
        {
            result = await remote.BringOwnedPrivateWorldHereAsync(
                item.Entry.WorldId,
                cancellation.Token);

            // Clear the remote-only selection before canonical refresh. Otherwise the catalog refresh
            // would correctly remove the now-local entry and navigate back to Games after the unified
            // World refresh had already opened the newly materialized World.
            ShowGamesLibraryFromOwnedPrivateWorld();
            await RefreshUnifiedWorldsAsync(
                item.Entry.WorldId,
                preserveStatus: true);
            SetBusy(true);
            await RefreshOwnedPrivateWorldCatalogAsync(cancellation.Token);

            StatusText.Text = FormatMaterializationSuccess(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = result is null
                ? "Bring here was canceled."
                : $"'{result.World.Name}' is stored on this PC. The final view refresh was canceled.";
        }
        catch (Exception exception)
        {
            if (result is null)
            {
                StatusText.Text = "Bring here failed.";
                ShowError("Could not bring private World here", exception);
            }
            else
            {
                StatusText.Text =
                    $"'{result.World.Name}' is stored on this PC, but the view could not be refreshed.";
                ShowError("Private World stored, but refresh failed", exception);
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _ownedPrivateWorldBringHereCancellation,
                null,
                cancellation);
            cancellation.Dispose();
            _ownedPrivateWorldBringHereButton.Content = "Bring here";
            SetBusy(false);
            UpdateOwnedPrivateWorldBringHereActionState();
        }
    }

    private static string FormatMaterializationSuccess(
        OwnedPrivateWorldMaterializationResult result)
        => result.Status switch
        {
            OwnedPrivateWorldMaterializationStatus.Materialized =>
                $"'{result.World.Name}' is now stored on this PC.",
            OwnedPrivateWorldMaterializationStatus.AlreadyMaterialized =>
                $"'{result.World.Name}' was already stored on this PC.",
            _ => throw new InvalidOperationException(
                $"Unhandled private World materialization status {result.Status}.")
        };

    private void UpdateOwnedPrivateWorldBringHereActionState()
    {
        var button = _ownedPrivateWorldBringHereButton;
        if (button is null)
        {
            return;
        }

        var entry = _selectedOwnedPrivateWorld?.Entry;
        var available = entry is
        {
            Availability: BringHereAvailability.Available,
            Source: not null
        } && entry.ConflictingClaims.Count == 0;
        button.IsEnabled = !_isBusy &&
                           RefreshButton.IsEnabled &&
                           Volatile.Read(ref _remoteRuntime) is not null &&
                           available;
        button.ToolTip = available
            ? "Download and verify this exact World, then store it locally."
            : entry?.Reason ?? "Select an available private World from another PC.";
    }
}
