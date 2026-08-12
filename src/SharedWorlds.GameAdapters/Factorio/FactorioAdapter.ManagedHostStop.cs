using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    private static readonly TimeSpan HostClientStopTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HostClientStartIdentityTolerance = TimeSpan.FromSeconds(2);

    Task IGameAdapter.RequestHostStopAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken)
        => RequestManagedHostStopAsync(session, cancellationToken);

    private async Task RequestManagedHostStopAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        // WaitForAdapterSessionEndAsync deliberately keeps this exact hosted-session entry alive until
        // the client-exit -> RCON save -> dedicated-server teardown path has finished. Refuse to act on
        // any process handle that is not still part of that managed Host lifecycle.
        if (!_hostedSessions.ContainsKey(session.ProcessId))
        {
            throw new InvalidOperationException(
                "Factorio does not have a running Steward-managed Host session for this process handle.");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(session.ProcessId);
        }
        catch (ArgumentException)
        {
            // The host client already ended between the UI request and process lookup. The normal
            // WaitForSessionEnd path remains responsible for the RCON save and server teardown.
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            // A numeric PID can be reused after a process exits. Never send WM_CLOSE to an unrelated
            // replacement process merely because the stale Host handle has the same integer PID.
            var observedStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (observedStart > session.StartedAt + HostClientStartIdentityTolerance)
            {
                throw new InvalidOperationException(
                    "The Factorio Host process ID was reused by another process. Steward refused to close it.");
            }

            // The managed authoritative server is separate from the graphical host player. Closing
            // the client normally makes the existing host lifecycle perform its RCON /server-save,
            // verify the save refresh, then stop the dedicated server before Core captures anything.
            // There is intentionally no kill fallback here: inability to request a normal client close
            // must fail closed and leave the protected host lifecycle running.
            if (!process.CloseMainWindow())
            {
                throw new InvalidOperationException(
                    "Factorio did not accept Steward's normal Host client close request. Close the Factorio window normally, then let Steward finish saving.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HostClientStopTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Factorio did not finish closing its Host client in time. Steward kept the World lifecycle protected instead of forcing the process closed.");
            }
        }
    }
}
