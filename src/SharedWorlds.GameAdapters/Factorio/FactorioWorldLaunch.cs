using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    public static Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        EnsurePreparedSaveExists(savePath, "local Factorio session");

        var process = StartFactorio(
            world,
            ["--load-game", savePath]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    public static Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        EnsurePreparedSaveExists(savePath, "Factorio host");

        var process = StartFactorio(
            world,
            ["--host", savePath]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    public static Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var address = host.Port is null
            ? host.Address
            : $"{host.Address}:{host.Port.Value}";

        var process = StartFactorio(
            world,
            ["--mp-connect", address]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    internal static string GetExecutablePath(GameInstallation installation)
        => GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);

    private static Process StartFactorio(
        PreparedWorld world,
        IReadOnlyList<string> operationArguments)
    {
        var executable = GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var configPath = GetWorkspaceConfigPath(world);
        var workspaceModDirectory = GetWorkspaceModsDirectory(world);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace config does not exist.",
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
            // Steam Factorio resolves steam_appid.txt relative to the process working directory.
            // Starting in the installation root can trigger SteamAPI_RestartAppIfNecessary,
            // causing the bootstrap PID to exit before the actual game session starts.
            WorkingDirectory = executableDirectory,
            UseShellExecute = false
        };

        // The workspace config redirects Factorio's write-data directory away from the user's
        // normal %APPDATA%/Factorio tree. This is what makes save writes session-local.
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);

        // Factorio receives only the adapter-owned workspace mod directory. Required user mods
        // are copied there at exact manifest versions during preparation; unrelated live mods are
        // deliberately excluded so a World cannot silently inherit later changes to the user's mod set.
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(workspaceModDirectory);

        foreach (var argument in operationArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
    }

    private static void EnsurePreparedSaveExists(string savePath, string operation)
    {
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                $"RestoreStateAsync must be called before launching a {operation}.",
                savePath);
        }
    }
}
