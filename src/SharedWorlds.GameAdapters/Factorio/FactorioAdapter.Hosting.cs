using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter : IManagedHostEndpointProvider
{
    private static readonly TimeSpan DedicatedServerReadyTimeout = TimeSpan.FromMinutes(2);
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

    ManagedHostEndpoint? IManagedHostEndpointProvider.GetManagedHostEndpoint(GameSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _hostedSessions.TryGetValue(session.ProcessId, out var hostedSession)
            ? new ManagedHostEndpoint(hostedSession.GamePort, hostedSession.GamePassword)
            : null;
    }

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

            var currentLogPath = Path.Combine(
                world.WorkingDirectory,
                "user-data",
                "factorio-current.log");
            resolvedServerProcessId = await ResolveServerProcessIdAsync(
                serverLaunch.Process,
                processName,
                baselineProcessIds,
                serverLaunch.ConsoleLogPath,
                currentLogPath,
                rconPassword,
                gamePassword,
                cancellationToken);

            await WaitForDedicatedServerReadyAsync(
                resolvedServerProcessId.Value,
                serverLaunch.ConsoleLogPath,
                rconPort,
                rconPassword,
                gamePassword,
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
                GamePort: gamePort,
                GamePassword: gamePassword,
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

    private static async Task WaitForDedicatedServerReadyAsync(
        int serverProcessId,
        string consoleLogPath,
        int rconPort,
        string rconPassword,
        string gamePassword,
        CancellationToken cancellationToken)
    {
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readinessTask = FactorioRconClient.WaitUntilReadyAsync(
            "127.0.0.1",
            rconPort,
            rconPassword,
            DedicatedServerReadyTimeout,
            monitorCancellation.Token);
        var processExitTask = WaitForProcessExitSignalAsync(
            serverProcessId,
            monitorCancellation.Token);

        var completed = await Task.WhenAny(readinessTask, processExitTask);
        if (completed == processExitTask)
        {
            await processExitTask;
            monitorCancellation.Cancel();
            var logTail = await ReadServerLogTailAsync(
                consoleLogPath,
                rconPassword,
                gamePassword,
                CancellationToken.None);
            throw new InvalidOperationException(
                AppendServerLog(
                    "Factorio's dedicated server exited before its RCON endpoint became ready.",
                    logTail));
        }

        try
        {
            await readinessTask;
        }
        catch (TimeoutException exception)
        {
            var logTail = await ReadServerLogTailAsync(
                consoleLogPath,
                rconPassword,
                gamePassword,
                CancellationToken.None);
            throw new TimeoutException(
                AppendServerLog(exception.Message, logTail),
                exception);
        }
        finally
        {
            monitorCancellation.Cancel();
        }
    }

    private static async Task WaitForProcessExitSignalAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // The process already exited before observation began.
        }
    }

    private static async Task<int> ResolveServerProcessIdAsync(
        Process launchProcess,
        string processName,
        IReadOnlySet<int> baselineProcessIds,
        string consoleLogPath,
        string currentLogPath,
        string rconPassword,
        string gamePassword,
        CancellationToken cancellationToken)
    {
        var observationDeadline = DateTimeOffset.UtcNow + BootstrapExitThreshold;
        while (DateTimeOffset.UtcNow < observationDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (launchProcess.HasExited)
                {
                    break;
                }
            }
            catch (InvalidOperationException)
            {
                break;
            }

            await Task.Delay(ReplacementPollInterval, cancellationToken);
        }

        try
        {
            if (!launchProcess.HasExited)
            {
                return launchProcess.Id;
            }
        }
        catch (InvalidOperationException)
        {
            // Fall through to replacement discovery and startup diagnostics.
        }

        var excludedProcessIds = new HashSet<int>(baselineProcessIds)
        {
            launchProcess.Id
        };
        var replacementProcessId = await FindReplacementProcessAsync(
            processName,
            excludedProcessIds,
            cancellationToken);
        if (replacementProcessId is not null)
        {
            return replacementProcessId.Value;
        }

        var exitCode = TryGetExitCode(launchProcess);
        var consoleLogTail = await ReadServerLogTailAsync(
            consoleLogPath,
            rconPassword,
            gamePassword,
            CancellationToken.None);
        var currentLogTail = await ReadServerLogTailAsync(
            currentLogPath,
            rconPassword,
            gamePassword,
            CancellationToken.None);

        throw new InvalidOperationException(
            BuildEarlyServerExitMessage(exitCode, consoleLogTail, currentLogTail));
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string BuildEarlyServerExitMessage(
        int? exitCode,
        string? consoleLogTail,
        string? currentLogTail)
    {
        var sections = new List<string>
        {
            exitCode is { } code
                ? $"Factorio's dedicated-server process exited with code {code} before SharedWorlds could identify a running server process."
                : "Factorio's dedicated-server process exited before SharedWorlds could identify a running server process."
        };

        if (!string.IsNullOrWhiteSpace(consoleLogTail))
        {
            sections.Add($"Factorio server console log tail:{Environment.NewLine}{consoleLogTail}");
        }

        if (!string.IsNullOrWhiteSpace(currentLogTail))
        {
            sections.Add($"Factorio current log tail:{Environment.NewLine}{currentLogTail}");
        }

        if (sections.Count == 1)
        {
            sections.Add(
                "Factorio did not leave a readable server-console.log or factorio-current.log in the isolated workspace.");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static async Task<string?> ReadServerLogTailAsync(
        string path,
        string rconPassword,
        string gamePassword,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var tail = string.Join(Environment.NewLine, lines.TakeLast(20));
            if (string.IsNullOrWhiteSpace(tail))
            {
                return null;
            }

            return tail
                .Replace(rconPassword, "<redacted-rcon-secret>", StringComparison.Ordinal)
                .Replace(gamePassword, "<redacted-session-secret>", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string AppendServerLog(string message, string? logTail)
        => string.IsNullOrWhiteSpace(logTail)
            ? message
            : $"{message}{Environment.NewLine}{Environment.NewLine}Factorio server log tail:{Environment.NewLine}{logTail}";

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
        int GamePort,
        string GamePassword,
        int RconPort,
        string RconPassword,
        string SavePath,
        PreparedWorld PreparedWorld);
}
