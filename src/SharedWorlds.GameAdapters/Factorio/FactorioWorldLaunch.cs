using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    private const string ManagedLaunchRuntimeDirectoryName = "launch-runtime";

    public static Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            savePath,
            "prepared save");
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
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            savePath,
            "prepared save");
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

        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            configPath,
            "config");
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            workspaceModDirectory,
            "mod directory");

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

        var workingDirectory = executableDirectory;
        if (string.Equals(world.Installation.Source, "steam", StringComparison.OrdinalIgnoreCase))
        {
            // Steam Factorio may call SteamAPI_RestartAppIfNecessary when started directly. That
            // replacement is launched by Steam rather than by Steward and therefore cannot inherit
            // Steward's KILL_ON_JOB_CLOSE lifetime job. Keep Steam's restart check satisfied from an
            // adapter-owned disposable directory so the exact process Steward starts remains the
            // managed process for local play and Join as well as for the dedicated-host path.
            var launchRuntimeDirectory = Path.Combine(
                world.WorkingDirectory,
                ManagedLaunchRuntimeDirectoryName);
            FactorioWorkspaceOwnership.RequireOwnedPath(
                world.WorkingDirectory,
                launchRuntimeDirectory,
                "managed launch runtime");
            Directory.CreateDirectory(launchRuntimeDirectory);
            File.WriteAllText(
                Path.Combine(launchRuntimeDirectory, SteamAppIdFileName),
                FactorioSteamAppId);
            workingDirectory = launchRuntimeDirectory;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
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

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
        try
        {
            FactorioManagedProcessLifetime.RequireAttached(process);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
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
