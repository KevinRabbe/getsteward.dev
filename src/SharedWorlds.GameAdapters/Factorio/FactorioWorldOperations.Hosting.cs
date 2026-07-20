using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    private const string HostRuntimeDirectoryName = "host";
    private const string HostClientDirectoryName = "host-client";
    private const string ServerSettingsFileName = "server-settings.json";
    private const string ServerConsoleLogFileName = "server-console.log";

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
        EnsurePreparedSaveExists(savePath, "Factorio dedicated server");

        var hostRuntimeDirectory = Path.Combine(world.WorkingDirectory, HostRuntimeDirectoryName);
        Directory.CreateDirectory(hostRuntimeDirectory);

        var serverSettingsPath = Path.Combine(hostRuntimeDirectory, ServerSettingsFileName);
        var consoleLogPath = Path.Combine(hostRuntimeDirectory, ServerConsoleLogFileName);
        await CreatePrivateServerSettingsAsync(
            world,
            serverSettingsPath,
            gamePassword,
            cancellationToken);

        var process = StartFactorio(
            world,
            [
                "--start-server",
                savePath,
                "--port",
                gamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--rcon-port",
                rconPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--rcon-bind",
                $"127.0.0.1:{rconPort}",
                "--rcon-password",
                rconPassword,
                "--server-settings",
                serverSettingsPath,
                "--console-log",
                consoleLogPath
            ]);

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

        var hostClientDirectory = Path.Combine(world.WorkingDirectory, HostClientDirectoryName);
        var hostClientConfigDirectory = Path.Combine(hostClientDirectory, WorkspaceConfigDirectoryName);
        var hostClientUserDataDirectory = Path.Combine(hostClientDirectory, WorkspaceUserDataDirectoryName);
        Directory.CreateDirectory(hostClientConfigDirectory);
        Directory.CreateDirectory(hostClientUserDataDirectory);

        var clientConfigPath = Path.Combine(hostClientConfigDirectory, WorkspaceConfigFileName);
        await CreateWorkspaceConfigAsync(
            world.Installation,
            clientConfigPath,
            hostClientUserDataDirectory,
            cancellationToken);

        var process = StartFactorio(
            world,
            BuildClientOperationArguments(host),
            configPathOverride: clientConfigPath);
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

    internal static string GetPlayerPreferenceConfigPath(PreparedWorld world)
    {
        var hostedClientConfig = Path.Combine(
            world.WorkingDirectory,
            HostClientDirectoryName,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);
        return File.Exists(hostedClientConfig)
            ? hostedClientConfig
            : GetWorkspaceConfigPath(world);
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
}

internal sealed record FactorioDedicatedServerLaunch(
    Process Process,
    string SavePath,
    string ServerSettingsPath,
    string ConsoleLogPath);
