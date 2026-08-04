using System.IO;
using SharedWorlds.Infrastructure.Diagnostics;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
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
