using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    private static readonly TimeSpan DedicatedServerReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ServerSaveTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServerStopTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<int, FactorioHostedSession> _hostedSessions = new();

    Task<GameSessionHandle> IGameAdapter.LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
        => LaunchAuthoritativeHostAsync(world, cancellationToken);

    Task IGameAdapter.WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken)
        => WaitForAdapterSessionEndAsync(session, cancellationToken);

    private async Task<GameSessionHandle> LaunchAuthoritativeHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();

        var executable = FactorioWorldOperations.GetExecutablePath(world.Installation);
        var processName = Path.GetFileNameWithoutExtension(executable);
        var baselineProcessIds = GetProcessIds(processName);
        var gamePort = GetAvailableUdpPort();
        var rconPort = GetAvailableTcpPort();
        var gamePassword = CreateSessionSecret();
        var rconPassword = CreateSessionSecret();

        FactorioDedicatedServerLaunch? serverLaunch = null;
        int? resolvedServerProcessId = null;

        try
        {
            serverLaunch = await FactorioHostingOperations.LaunchDedicatedServerAsync(
                world,
                gamePort,
                rconPort,
                gamePassword,
                rconPassword,
                cancellationToken);

            await FactorioRconClient.WaitUntilReadyAsync(
                "127.0.0.1",
                rconPort,
                rconPassword,
                DedicatedServerReadyTimeout,
                cancellationToken);

            resolvedServerProcessId = await ResolveServerProcessIdAsync(
                serverLaunch.Process,
                processName,
                baselineProcessIds,
                cancellationToken);

            var connection = new HostConnection(
                Address: "127.0.0.1",
                Port: gamePort,
                JoinToken: gamePassword);

            var clientSession = await LaunchTrackedAsync(
                world,
                (preparedWorld, token) => FactorioHostingOperations.LaunchHostClientAsync(
                    preparedWorld,
                    connection,
                    token),
                cancellationToken);

            _hostedSessions[clientSession.ProcessId] = new FactorioHostedSession(
                ServerProcessId: resolvedServerProcessId.Value,
                RconPort: rconPort,
                RconPassword: rconPassword,
                SavePath: serverLaunch.SavePath,
                PreparedWorld: world);

            return clientSession;
        }
        catch
        {
            if (resolvedServerProcessId is { } serverProcessId)
            {
                await TryStopProcessAsync(serverProcessId);
            }
            else if (serverLaunch is not null)
            {
                await TryStopProcessAsync(serverLaunch.Process.Id);
            }

            throw;
        }
        finally
        {
            serverLaunch?.Process.Dispose();
        }
    }

    private async Task WaitForAdapterSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!_hostedSessions.TryRemove(session.ProcessId, out var hostedSession))
        {
            await WaitForSessionEndAsync(session, cancellationToken);
            return;
        }

        await WaitForHostedSessionEndAsync(session, hostedSession, cancellationToken);
    }

    private async Task WaitForHostedSessionEndAsync(
        GameSessionHandle clientSession,
        FactorioHostedSession hostedSession,
        CancellationToken cancellationToken)
    {
        try
        {
            // The host player is a normal client. Ending that client ends the current local hosting
            // session for now; future host handoff can keep the authority alive until a successor is ready.
            await WaitForSessionEndAsync(clientSession, cancellationToken);

            EnsureProcessIsAlive(
                hostedSession.ServerProcessId,
                "The Factorio dedicated server exited before the host player session ended.");

            var previousWriteTime = File.GetLastWriteTimeUtc(hostedSession.SavePath);
            var previousLength = new FileInfo(hostedSession.SavePath).Length;

            using var saveTimeout = new CancellationTokenSource(ServerSaveTimeout);
            using var linkedSaveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                saveTimeout.Token);

            await FactorioRconClient.ExecuteAsync(
                "127.0.0.1",
                hostedSession.RconPort,
                hostedSession.RconPassword,
                "/server-save",
                linkedSaveCancellation.Token);

            await WaitForSaveRefreshAsync(
                hostedSession.SavePath,
                previousWriteTime,
                previousLength,
                linkedSaveCancellation.Token);
        }
        finally
        {
            await TryStopProcessAsync(hostedSession.ServerProcessId);
        }

        // The graphical host client has its own isolated write-data directory. Copy its config back
        // into the prepared workspace only after the server is gone; existing finalization then merges
        // player-owned preferences while preserving the real player's [path] section.
        await FactorioHostingOperations.PromoteHostClientPreferencesAsync(
            hostedSession.PreparedWorld,
            CancellationToken.None);
    }

    private static async Task<int> ResolveServerProcessIdAsync(
        Process launchProcess,
        string processName,
        IReadOnlySet<int> baselineProcessIds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!launchProcess.HasExited)
            {
                return launchProcess.Id;
            }
        }
        catch (InvalidOperationException)
        {
            // Fall through to Steam bootstrap replacement discovery.
        }

        var excludedProcessIds = new HashSet<int>(baselineProcessIds)
        {
            launchProcess.Id
        };
        var replacementProcessId = await FindReplacementProcessAsync(
            processName,
            excludedProcessIds,
            cancellationToken);
        return replacementProcessId
            ?? throw new InvalidOperationException(
                "Factorio's dedicated-server bootstrap exited, but no replacement server process could be identified.");
    }

    private static async Task WaitForSaveRefreshAsync(
        string savePath,
        DateTime previousWriteTime,
        long previousLength,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(savePath))
            {
                var info = new FileInfo(savePath);
                if (info.LastWriteTimeUtc > previousWriteTime || info.Length != previousLength)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static void EnsureProcessIsAlive(int processId, string message)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        catch (InvalidOperationException)
        {
            // Process disappeared while being inspected.
        }

        throw new InvalidOperationException(message);
    }

    private static async Task TryStopProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            using var stopTimeout = new CancellationTokenSource(ServerStopTimeout);
            try
            {
                await process.WaitForExitAsync(stopTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Best effort only. The caller is already preserving the workspace on failure.
            }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (InvalidOperationException)
        {
            // Process disappeared while being inspected.
        }
    }

    private static int GetAvailableUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateSessionSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private sealed record FactorioHostedSession(
        int ServerProcessId,
        int RconPort,
        string RconPassword,
        string SavePath,
        PreparedWorld PreparedWorld);
}
