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
    private const string ServerSettingsFileName = "server-settings.json";
    private const string ServerConsoleLogFileName = "server-console.log";
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
        // only inside the disposable SharedWorlds workspace and never modifies the Factorio install.
        await File.WriteAllTextAsync(
            Path.Combine(hostRuntimeDirectory, SteamAppIdFileName),
            FactorioSteamAppId,
            cancellationToken);

        var serverSettingsPath = Path.Combine(hostRuntimeDirectory, ServerSettingsFileName);
        var consoleLogPath = Path.Combine(hostRuntimeDirectory, ServerConsoleLogFileName);
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
                "--rcon-port",
                rconPort.ToString(CultureInfo.InvariantCulture),
                "--rcon-bind",
                $"127.0.0.1:{rconPort}",
                "--rcon-password",
                rconPassword,
                "--server-settings",
                serverSettingsPath,
                "--console-log",
                consoleLogPath
            ],
            workingDirectoryOverride: hostRuntimeDirectory);

        return new FactorioDedicatedServerLaunch(
            process,
            savePath,
            serverSettingsPath,
            consoleLogPath);
    }

    internal static async Task<GameSessionHandle> LaunchHostClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientConfigPath = await CreateHostClientConfigAsync(world, cancellationToken);
        var process = StartFactorio(
            world,
            clientConfigPath,
            BuildClientOperationArguments(host));
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

    internal static async Task PromoteHostClientPreferencesAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var clientConfigPath = GetHostClientConfigPath(world);
        if (!File.Exists(clientConfigPath))
        {
            return;
        }

        var workspaceConfigPath = GetWorkspaceConfigPath(world);
        await using var source = new FileStream(
            clientConfigPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        await using var destination = new FileStream(
            workspaceConfigPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
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
            ? "SharedWorlds"
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

    private static async Task<string> CreateHostClientConfigAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var sourceConfigPath = GetWorkspaceConfigPath(world);
        if (!File.Exists(sourceConfigPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace config does not exist.",
                sourceConfigPath);
        }

        var clientConfigPath = GetHostClientConfigPath(world);
        var clientUserDataDirectory = Path.Combine(
            world.WorkingDirectory,
            HostClientDirectoryName,
            UserDataDirectoryName);
        Directory.CreateDirectory(Path.GetDirectoryName(clientConfigPath)!);
        Directory.CreateDirectory(clientUserDataDirectory);

        var lines = await File.ReadAllLinesAsync(sourceConfigPath, cancellationToken);
        var rewritten = RewriteWriteDataPath(lines, Path.GetFullPath(clientUserDataDirectory));
        await File.WriteAllLinesAsync(clientConfigPath, rewritten, cancellationToken);
        return clientConfigPath;
    }

    private static IReadOnlyList<string> RewriteWriteDataPath(
        IReadOnlyList<string> lines,
        string writeDataDirectory)
    {
        var result = new List<string>(lines.Count + 2);
        var inPathSection = false;
        var pathSectionFound = false;
        var writeDataReplaced = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                if (inPathSection && !writeDataReplaced)
                {
                    result.Add($"write-data={writeDataDirectory}");
                    writeDataReplaced = true;
                }

                inPathSection = string.Equals(trimmed, "[path]", StringComparison.OrdinalIgnoreCase);
                pathSectionFound |= inPathSection;
                result.Add(line);
                continue;
            }

            if (inPathSection && trimmed.StartsWith("write-data=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"write-data={writeDataDirectory}");
                writeDataReplaced = true;
                continue;
            }

            result.Add(line);
        }

        if (inPathSection && !writeDataReplaced)
        {
            result.Add($"write-data={writeDataDirectory}");
        }

        if (!pathSectionFound)
        {
            result.Add(string.Empty);
            result.Add("[path]");
            result.Add("read-data=__PATH__system-read-data__");
            result.Add($"write-data={writeDataDirectory}");
        }

        return result;
    }

    private static Process StartFactorio(
        PreparedWorld world,
        string configPath,
        IReadOnlyList<string> operationArguments,
        string? workingDirectoryOverride = null)
    {
        var executable = FactorioWorldOperations.GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var workspaceModDirectory = Path.Combine(world.WorkingDirectory, ModsDirectoryName);

        if (!File.Exists(configPath))
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

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectoryOverride ?? executableDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(workspaceModDirectory);

        foreach (var argument in operationArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
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

    private static string GetHostClientConfigPath(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            HostClientDirectoryName,
            ConfigDirectoryName,
            ConfigFileName);
}

internal sealed record FactorioDedicatedServerLaunch(
    Process Process,
    string SavePath,
    string ServerSettingsPath,
    string ConsoleLogPath);
