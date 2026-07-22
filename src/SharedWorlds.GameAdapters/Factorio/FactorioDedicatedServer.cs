using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

/// <summary>
/// Starts the actual Factorio dedicated/headless server path and does not return a session handle
/// until Factorio itself has logged that the multiplayer socket is hosting. Process existence alone
/// is not treated as server readiness.
/// </summary>
internal static class FactorioDedicatedServer
{
    private const string PreparedSaveFileName = "world.zip";
    private const string WorkspaceConfigDirectoryName = "config";
    private const string WorkspaceConfigFileName = "config.ini";
    private const string WorkspaceUserDataDirectoryName = "user-data";
    private const string SavesDirectoryName = "saves";
    private const string ModsDirectoryName = "mods";
    private const string ConsoleLogFileName = "server-console.log";

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumReadyLifetime = TimeSpan.FromSeconds(3);

    public static async Task<GameSessionHandle> LaunchReadyAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio save required for dedicated hosting does not exist.",
                savePath);
        }

        var consoleLogPath = GetConsoleLogPath(world);
        DeleteExistingConsoleLog(consoleLogPath);

        var startInfo = CreateStartInfo(world, consoleLogPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start Factorio dedicated server '{startInfo.FileName}'.");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await WaitForReadinessAsync(
                process,
                consoleLogPath,
                startedAt,
                ReadinessTimeout,
                cancellationToken);
            return new GameSessionHandle(process.Id, startedAt);
        }
        catch
        {
            TryTerminateProcess(process);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        PreparedWorld world,
        string consoleLogPath)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(consoleLogPath);

        var executable = FactorioWorldOperations.GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var configPath = Path.Combine(
            world.WorkingDirectory,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);
        var modDirectory = Path.Combine(world.WorkingDirectory, ModsDirectoryName);
        var savePath = GetPreparedSavePath(world);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace config does not exist.",
                configPath);
        }

        if (!Directory.Exists(modDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The isolated Factorio workspace mod directory does not exist: '{modDirectory}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            // Steam Factorio resolves steam_appid.txt relative to the executable directory. Keeping
            // the same working-directory rule as local play avoids a needless Steam bootstrap PID.
            WorkingDirectory = executableDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(modDirectory);
        startInfo.ArgumentList.Add("--start-server");
        startInfo.ArgumentList.Add(savePath);
        startInfo.ArgumentList.Add("--console-log");
        startInfo.ArgumentList.Add(Path.GetFullPath(consoleLogPath));
        return startInfo;
    }

    internal static bool IsReadyLogLine(string line)
        => !string.IsNullOrWhiteSpace(line) &&
           line.Contains("Hosting game at ", StringComparison.Ordinal);

    private static async Task WaitForReadinessAsync(
        Process process,
        string consoleLogPath,
        DateTimeOffset startedAt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        long consumedLength = 0;
        var hostingObserved = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasExited(process))
            {
                var exitCode = TryGetExitCode(process);
                throw new InvalidOperationException(
                    exitCode is null
                        ? "Factorio dedicated server exited before readiness could be proven."
                        : $"Factorio dedicated server exited with code {exitCode.Value} before readiness could be proven.");
            }

            if (File.Exists(consoleLogPath))
            {
                var read = await ReadNewLogForReadinessAsync(
                    consoleLogPath,
                    consumedLength,
                    cancellationToken);
                consumedLength = read.ConsumedLength;
                hostingObserved |= read.Ready;
            }

            if (hostingObserved && DateTimeOffset.UtcNow - startedAt >= MinimumReadyLifetime)
            {
                // Re-check liveness after observing the authoritative Factorio log marker. The
                // stability window also prevents the generic session observer from mistaking a very
                // short but valid headless session for a Steam bootstrap process.
                if (HasExited(process))
                {
                    throw new InvalidOperationException(
                        "Factorio reported a hosting socket but exited before the server remained stable.");
                }

                return;
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"Factorio dedicated server did not report 'Hosting game at' within {timeout.TotalMinutes:0.#} minutes.");
    }

    private static async Task<ReadinessLogRead> ReadNewLogForReadinessAsync(
        string path,
        long consumedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            if (consumedLength > stream.Length)
            {
                consumedLength = 0;
            }

            stream.Position = consumedLength;
            using var reader = new StreamReader(stream);
            var ready = false;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                ready |= IsReadyLogLine(line);
            }

            return new ReadinessLogRead(stream.Position, ready);
        }
        catch (IOException)
        {
            // Factorio may briefly hold the log while startup is still in progress. Retry without
            // advancing the read position; readiness still has to be observed from this launch.
            return new ReadinessLogRead(consumedLength, Ready: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ReadinessLogRead(consumedLength, Ready: false);
        }
    }

    private static string GetPreparedSavePath(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            WorkspaceUserDataDirectoryName,
            SavesDirectoryName,
            PreparedSaveFileName);

    private static string GetConsoleLogPath(PreparedWorld world)
        => Path.Combine(world.WorkingDirectory, ConsoleLogFileName);

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
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

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already ended.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort only; the lifecycle still treats launch as failed and preserves its rules.
        }
    }

    private static void DeleteExistingConsoleLog(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException(
                $"Refusing to start Factorio dedicated hosting with stale readiness log '{path}'.");
        }
    }

    private sealed record ReadinessLogRead(long ConsumedLength, bool Ready);
}
