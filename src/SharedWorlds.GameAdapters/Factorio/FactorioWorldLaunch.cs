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
            ["--load-game", savePath],
            useNativePlayerProfile: false);
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
            ["--host", savePath],
            useNativePlayerProfile: false);
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

        // A joining player must be the same Factorio player they are outside SafeWorld. Using the
        // native Factorio profile preserves language, controls and multiplayer identity, so an
        // existing save reconnects the player to the same character/inventory instead of creating a
        // fresh player. The exact World mod set is still supplied from the isolated workspace.
        var process = StartFactorio(
            world,
            ["--mp-connect", address],
            useNativePlayerProfile: true);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    internal static string GetExecutablePath(GameInstallation installation)
        => GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);

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

    private static Process StartFactorio(
        PreparedWorld world,
        IReadOnlyList<string> operationArguments,
        bool useNativePlayerProfile)
    {
        var executable = GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var configPath = useNativePlayerProfile ? null : GetWorkspaceConfigPath(world);
        var workspaceModDirectory = GetWorkspaceModsDirectory(world);

        if (!useNativePlayerProfile)
        {
            FactorioWorkspaceOwnership.RequireOwnedPath(
                world.WorkingDirectory,
                configPath!,
                "config");
        }
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            workspaceModDirectory,
            "mod directory");

        if (!useNativePlayerProfile && !File.Exists(configPath))
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
            // replacement is launched by Steam rather than by SafeWorld and therefore cannot inherit
            // SafeWorld's KILL_ON_JOB_CLOSE lifetime job. Keep Steam's restart check satisfied from an
            // adapter-owned disposable directory so the exact process SafeWorld starts remains the
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

        foreach (var argument in BuildProcessArguments(
                     configPath,
                     workspaceModDirectory,
                     operationArguments))
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
