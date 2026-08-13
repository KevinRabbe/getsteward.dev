using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

/// <summary>
/// Factorio-specific authoritative hosting primitives. This stays outside Core: only the adapter
/// knows that Factorio uses a headless server, RCON, server-settings.json, and --mp-connect.
/// </summary>
internal static class FactorioHostingOperations
{
    private const string ConfigDirectoryName = "config";
    private const string ConfigFileName = "config.ini";
    private const string UserDataDirectoryName = "user-data";
    private const string SavesDirectoryName = "saves";
    private const string ModsDirectoryName = "mods";
    private const string HostRuntimeDirectoryName = "host";
    private const string HostClientDirectoryName = "host-client";
    private const string ServerConsoleLogFileName = "server-console.log";
    private const string FactorioConsoleLogFileName = "factorio-server.log";
    private const string ServerSettingsFileName = "server-settings.json";
    private const string SteamAppIdFileName = "steam_appid.txt";
    private const string FactorioSteamAppId = "427520";

    internal static async Task<FactorioDedicatedServerLaunch> LaunchDedicatedServerAsync(
        PreparedWorld world,
        int gamePort,
        int rconPort,
        string gamePassword,
        string rconPassword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "RestoreStateAsync must be called before launching a Factorio dedicated server.",
                savePath);
        }

        var hostRuntimeDirectory = Path.Combine(world.WorkingDirectory, HostRuntimeDirectoryName);
        Directory.CreateDirectory(hostRuntimeDirectory);

        // The Steam build otherwise calls SteamAPI_RestartAppIfNecessary when started directly.
        // A workspace-local steam_appid.txt keeps the dedicated server as the process we launched,
        // so its exact --start-server arguments and lifecycle remain adapter-owned. The marker lives
        // only inside the disposable SafeWorld workspace and never modifies the Factorio install.
        await File.WriteAllTextAsync(
            Path.Combine(hostRuntimeDirectory, SteamAppIdFileName),
            FactorioSteamAppId,
            cancellationToken);

        var serverSettingsPath = Path.Combine(hostRuntimeDirectory, ServerSettingsFileName);
        var capturedConsolePath = Path.Combine(hostRuntimeDirectory, ServerConsoleLogFileName);
        var factorioConsoleLogPath = Path.Combine(hostRuntimeDirectory, FactorioConsoleLogFileName);
        await CreatePrivateServerSettingsAsync(
            world,
            serverSettingsPath,
            gamePassword,
            cancellationToken);

        var process = StartFactorio(
            world,
            GetWorkspaceConfigPath(world),
            [
                "--start-server",
                savePath,
                "--port",
                gamePort.ToString(CultureInfo.InvariantCulture),
                // Factorio treats --rcon-port and --rcon-bind as mutually exclusive.
                // Bind loopback and select the RCON port in one argument so RCON stays local-only.
                "--rcon-bind",
                $"127.0.0.1:{rconPort}",
                "--rcon-password",
                rconPassword,
                "--server-settings",
                serverSettingsPath,
                "--console-log",
                factorioConsoleLogPath
            ],
            workingDirectoryOverride: hostRuntimeDirectory,
            diagnosticLogPath: capturedConsolePath);

        return new FactorioDedicatedServerLaunch(
            process,
            savePath,
            serverSettingsPath,
            capturedConsolePath);
    }

    internal static async Task<GameSessionHandle> LaunchHostClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hostClientDirectory = Path.Combine(world.WorkingDirectory, HostClientDirectoryName);
        Directory.CreateDirectory(hostClientDirectory);

        // The graphical host is a normal Factorio player connecting to SafeWorld's isolated
        // dedicated server. Do not redirect the graphical client's write-data directory: Factorio
        // must use the player's real local profile so language, controls and multiplayer identity
        // remain exactly the same as when the player starts Factorio normally. Only the dedicated
        // server owns the authoritative save and therefore needs the isolated workspace config.
        //
        // Keep the graphical client launch adapter-owned too. Without the local Steam App ID marker,
        // the Steam build can restart itself through Steam, which causes Steam to intercept our
        // --mp-connect/--password arguments and show a confirmation prompt before joining.
        await File.WriteAllTextAsync(
            Path.Combine(hostClientDirectory, SteamAppIdFileName),
            FactorioSteamAppId,
            cancellationToken);

        var process = StartFactorio(
            world,
            configPath: null,
            BuildClientOperationArguments(host),
            workingDirectoryOverride: hostClientDirectory);
        return new GameSessionHandle(process.Id, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<string> BuildClientOperationArguments(HostConnection host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Address);

        var address = host.Port is null
            ? host.Address
            : $"{host.Address}:{host.Port.Value}";
        var arguments = new List<string>
        {
            "--mp-connect",
            address
        };

        if (!string.IsNullOrWhiteSpace(host.JoinToken))
        {
            arguments.Add("--password");
            arguments.Add(host.JoinToken);
        }

        return arguments;
    }

    internal static IReadOnlyList<string> BuildProcessArguments(
        string? configPath,
        string workspaceModDirectory,
        IReadOnlyList<string> operationArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceModDirectory);
        ArgumentNullException.ThrowIfNull(operationArguments);

        var arguments = new List<string>(operationArguments.Count + 4);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            arguments.Add("--config");
            arguments.Add(configPath);
        }

        arguments.Add("--mod-directory");
        arguments.Add(workspaceModDirectory);
        arguments.AddRange(operationArguments);
        return arguments;
    }

    internal static async Task CreatePrivateServerSettingsAsync(
        PreparedWorld world,
        string destinationPath,
        string gamePassword,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePassword);

        var examplePath = Path.Combine(
            world.Installation.RootPath,
            "data",
            "server-settings.example.json");
        if (!File.Exists(examplePath))
        {
            throw new FileNotFoundException(
                "Factorio's server-settings.example.json is required to create an adapter-owned private server configuration.",
                examplePath);
        }

        var source = await File.ReadAllTextAsync(examplePath, cancellationToken);
        var root = JsonNode.Parse(source) as JsonObject
            ?? throw new JsonException("Factorio's server settings example did not contain a JSON object.");

        root["name"] = string.IsNullOrWhiteSpace(world.DisplayName)
            ? "SafeWorld"
            : world.DisplayName;
        root["game_password"] = gamePassword;
        root["require_user_verification"] = false;

        if (root["visibility"] is not JsonObject visibility)
        {
            visibility = new JsonObject();
            root["visibility"] = visibility;
        }

        visibility["public"] = false;
        visibility["lan"] = false;
        if (visibility.ContainsKey("steam"))
        {
            visibility["steam"] = false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(
            destinationPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static Process StartFactorio(
        PreparedWorld world,
        string? configPath,
        IReadOnlyList<string> operationArguments,
        string? workingDirectoryOverride = null,
        string? diagnosticLogPath = null)
    {
        var executable = FactorioWorldOperations.GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var workspaceModDirectory = Path.Combine(world.WorkingDirectory, ModsDirectoryName);

        if (!string.IsNullOrWhiteSpace(configPath) && !File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The Factorio session config does not exist.",
                configPath);
        }

        if (!Directory.Exists(workspaceModDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The isolated Factorio workspace mod directory does not exist: '{workspaceModDirectory}'.");
        }

        var captureDiagnostics = !string.IsNullOrWhiteSpace(diagnosticLogPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectoryOverride ?? executableDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = captureDiagnostics,
            RedirectStandardError = captureDiagnostics,
            CreateNoWindow = captureDiagnostics
        };

        foreach (var argument in BuildProcessArguments(
                     configPath,
                     workspaceModDirectory,
                     operationArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        object? diagnosticGate = null;
        if (captureDiagnostics)
        {
            diagnosticGate = new object();
            Directory.CreateDirectory(Path.GetDirectoryName(diagnosticLogPath!)!);
            File.WriteAllText(diagnosticLogPath!, string.Empty);
            process.OutputDataReceived += (_, eventArgs) =>
                TryAppendDiagnosticLine(diagnosticLogPath!, diagnosticGate, "stdout", eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) =>
                TryAppendDiagnosticLine(diagnosticLogPath!, diagnosticGate, "stderr", eventArgs.Data);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
        }

        try
        {
            FactorioManagedProcessLifetime.RequireAttached(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        if (captureDiagnostics)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    private static void TryAppendDiagnosticLine(
        string path,
        object gate,
        string streamName,
        string? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            lock (gate)
            {
                File.AppendAllText(
                    path,
                    $"[{streamName}] {line}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Startup diagnostics are best-effort and must never change server lifecycle behavior.
        }
        catch (UnauthorizedAccessException)
        {
            // Startup diagnostics are best-effort and must never change server lifecycle behavior.
        }
    }

    private static string GetPreparedSavePath(PreparedWorld world)
    {
        var savesDirectory = Path.Combine(
            world.WorkingDirectory,
            UserDataDirectoryName,
            SavesDirectoryName);
        var savePath = Directory.Exists(savesDirectory)
            ? Directory
                .EnumerateFiles(savesDirectory, "*.zip", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileNameWithoutExtension(path)
                    .StartsWith("_autosave", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return savePath
            ?? Path.Combine(savesDirectory, "world.zip");
    }

    private static string GetWorkspaceConfigPath(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            ConfigDirectoryName,
            ConfigFileName);
}

internal sealed record FactorioDedicatedServerLaunch(
    Process Process,
    string SavePath,
    string ServerSettingsPath,
    string ConsoleLogPath);
