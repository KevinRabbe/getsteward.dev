using System.IO;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Infrastructure.Diagnostics;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Normal local/peer storage mutations call this neutral seam. Until an explicit legacy remote
    /// migration runtime has been requested there is no journal or background worker to signal, so the
    /// normal AppID-only product pays only one nullable reference read.
    /// </summary>
    private void RequestOwnedWorldLocationPublication()
        => Volatile.Read(ref _ownedWorldLocationPublicationTrigger)?.Request();

    /// <summary>
    /// Creates the legacy owned-location publication state only when an authenticated migration runtime
    /// is being established. All replacements reuse the same journal/worker and publication gate.
    /// </summary>
    private (
        IOwnedWorldLocationPublicationJournal Journal,
        StewardOwnedWorldLocationPublicationTrigger Trigger)
        EnsureOwnedWorldLocationMigrationState()
    {
        lock (_ownedWorldLocationMigrationStateGate)
        {
            _ownedWorldLocationPublicationJournal ??=
                new LocalOwnedWorldLocationPublicationJournal(_storageRoot);
            _ownedWorldLocationPublicationTrigger ??=
                new StewardOwnedWorldLocationPublicationTrigger(
                    PublishCurrentOwnedWorldLocationsAsync,
                    RecordOwnedWorldLocationPublicationFailure);

            return (
                _ownedWorldLocationPublicationJournal,
                _ownedWorldLocationPublicationTrigger);
        }
    }

    private void DisposeOwnedWorldLocationMigrationState()
    {
        StewardOwnedWorldLocationPublicationTrigger? trigger;
        lock (_ownedWorldLocationMigrationStateGate)
        {
            trigger = _ownedWorldLocationPublicationTrigger;
            _ownedWorldLocationPublicationTrigger = null;
            _ownedWorldLocationPublicationJournal = null;
        }

        // Cancel the bounded worker outside the state lock so arbitrary cancellation continuations can
        // never deadlock migration-state teardown. The remote runtime is disposed immediately after this
        // method by MainWindow's Closed handler, preserving the existing worker-before-runtime order.
        trigger?.Dispose();
    }

    private async Task PublishCurrentOwnedWorldLocationsAsync(
        CancellationToken cancellationToken)
    {
        await _ownedWorldLocationPublicationGate.WaitAsync(cancellationToken);
        try
        {
            var remote = Volatile.Read(ref _remoteRuntime);
            if (remote is null)
            {
                return;
            }

            // The backend requires the exact source location head before it can authorize immutable
            // private snapshot bytes. Snapshot publication is auxiliary and occurs only after the
            // current canonical location claims have been reconciled and replayed.
            await remote.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);
            await remote.PublishCurrentOwnedWorldSnapshotsAsync(cancellationToken);
        }
        finally
        {
            _ownedWorldLocationPublicationGate.Release();
        }
    }

    private static void RecordOwnedWorldLocationPublicationFailure(Exception exception)
    {
        var diagnosticsRoot = Path.Combine(
            GetLocalDataRoot(),
            "SharedWorlds",
            "logs");
        LocalDiagnosticLog.TryWriteException(
            new InvalidOperationException(
                "Safe World could not accelerate owned-World location or private snapshot publication. Local World changes remain committed. Exact pending work remains durable, and resumable snapshot transfer can continue after the next storage change, authentication activation, or startup.",
                exception),
            diagnosticsRoot);
    }
}
