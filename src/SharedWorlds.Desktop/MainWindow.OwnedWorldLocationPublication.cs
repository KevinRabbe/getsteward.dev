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

            await remote.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);
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
                "Safe World could not accelerate owned-World location publication. Exact pending work remains durable and will be reconciled by the next storage change, authentication activation, or startup.",
                exception),
            diagnosticsRoot);
    }
}
