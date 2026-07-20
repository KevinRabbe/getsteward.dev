using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioWorldOperations
{
    private const string PreparedSaveFileName = "world.zip";
    private const string WorkspaceConfigDirectoryName = "config";
    private const string WorkspaceConfigFileName = "config.ini";
    private const string WorkspaceUserDataDirectoryName = "user-data";
    private const string SavesDirectoryName = "saves";

    public static IReadOnlyList<DetectedWorld> DiscoverWorlds(GameInstallation installation)
    {
        var userDataPath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.UserDataPathKey);
        var savesPath = Path.Combine(userDataPath, SavesDirectoryName);

        if (!Directory.Exists(savesPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(savesPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Where(path => !IsAutosave(path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => new DetectedWorld(
                Id: Path.GetFullPath(path),
                DisplayName: Path.GetFileNameWithoutExtension(path),
                SourcePath: Path.GetFullPath(path)))
            .ToArray();
    }

    public static async Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(world.SourcePath))
        {
            throw new FileNotFoundException("The detected Factorio save no longer exists.", world.SourcePath);
        }

        var package = CreatePackagePath();
        await CopyFileAsync(world.SourcePath, package, overwrite: false, cancellationToken);
        return new CapturedState(
            new StatePackage(Path.GetFileNameWithoutExtension(package), package),
            DateTimeOffset.UtcNow);
    }

    public static async Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(requiredEnvironment.AdapterId, "factorio", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Factorio.",
                nameof(requiredEnvironment));
        }

        var workspace = Path.Combine(
            GetLocalWorkRoot(),
            Guid.NewGuid().ToString("N"));
        var workspaceConfigDirectory = Path.Combine(workspace, WorkspaceConfigDirectoryName);
        var workspaceUserDataDirectory = Path.Combine(workspace, WorkspaceUserDataDirectoryName);
        var workspaceSavesDirectory = Path.Combine(workspaceUserDataDirectory, SavesDirectoryName);

        Directory.CreateDirectory(workspaceConfigDirectory);
        Directory.CreateDirectory(workspaceSavesDirectory);

        await CreateWorkspaceConfigAsync(
            installation,
            Path.Combine(workspaceConfigDirectory, WorkspaceConfigFileName),
            workspaceUserDataDirectory,
            cancellationToken);

        return new PreparedWorld(installation, workspace, requiredEnvironment);
    }

    public static async Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(state.Path))
        {
            throw new FileNotFoundException("The Factorio state package does not exist.", state.Path);
        }

        var destination = GetPreparedSavePath(world);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyFileAsync(state.Path, destination, overwrite: true, cancellationToken);
    }

    public static async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var savesDirectory = GetWorkspaceSavesDirectory(world);
        var savePath = Directory.Exists(savesDirectory)
            ? Directory
                .EnumerateFiles(savesDirectory, "*.zip", SearchOption.TopDirectoryOnly)
                .Where(path => !IsAutosave(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (savePath is null)
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace has no non-autosave save to capture.",
                savesDirectory);
        }

        var package = CreatePackagePath();
        await CopyFileAsync(savePath, package, overwrite: false, cancellationToken);
        return new CapturedState(
            new StatePackage(Path.GetFileNameWithoutExtension(package), package),
            DateTimeOffset.UtcNow);
    }

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
        var sourceUserDataPath = GetRequiredMetadata(
            world.Installation,
            FactorioInstallationDiscovery.UserDataPathKey);
        var sourceModDirectory = Path.Combine(sourceUserDataPath, "mods");

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace config does not exist.",
                configPath);
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

        // Full mod isolation is a later adapter milestone. For now, keep the currently installed
        // mod set available explicitly while save/config/temp writes go to the isolated workspace.
        if (Directory.Exists(sourceModDirectory))
        {
            startInfo.ArgumentList.Add("--mod-directory");
            startInfo.ArgumentList.Add(sourceModDirectory);
        }

        foreach (var argument in operationArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
    }

    private static async Task CreateWorkspaceConfigAsync(
        GameInstallation installation,
        string destinationConfigPath,
        string workspaceUserDataDirectory,
        CancellationToken cancellationToken)
    {
        var sourceUserDataPath = GetRequiredMetadata(
            installation,
            FactorioInstallationDiscovery.UserDataPathKey);
        var sourceConfigPath = Path.Combine(
            sourceUserDataPath,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);

        string[] lines;
        if (File.Exists(sourceConfigPath))
        {
            lines = await File.ReadAllLinesAsync(sourceConfigPath, cancellationToken);
        }
        else
        {
            lines =
            [
                "[path]",
                "read-data=__PATH__system-read-data__",
                $"write-data={Path.GetFullPath(workspaceUserDataDirectory)}"
            ];
        }

        var rewritten = RewriteWriteDataPath(lines, Path.GetFullPath(workspaceUserDataDirectory));
        await File.WriteAllLinesAsync(destinationConfigPath, rewritten, cancellationToken);
    }

    private static IReadOnlyList<string> RewriteWriteDataPath(
        IReadOnlyList<string> lines,
        string workspaceUserDataDirectory)
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
                    result.Add($"write-data={workspaceUserDataDirectory}");
                    writeDataReplaced = true;
                }

                inPathSection = string.Equals(trimmed, "[path]", StringComparison.OrdinalIgnoreCase);
                pathSectionFound |= inPathSection;
                result.Add(line);
                continue;
            }

            if (inPathSection && trimmed.StartsWith("write-data=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"write-data={workspaceUserDataDirectory}");
                writeDataReplaced = true;
                continue;
            }

            result.Add(line);
        }

        if (inPathSection && !writeDataReplaced)
        {
            result.Add($"write-data={workspaceUserDataDirectory}");
        }

        if (!pathSectionFound)
        {
            result.Add(string.Empty);
            result.Add("[path]");
            result.Add("read-data=__PATH__system-read-data__");
            result.Add($"write-data={workspaceUserDataDirectory}");
        }

        return result;
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

    private static bool IsAutosave(string path)
        => Path.GetFileNameWithoutExtension(path)
            .StartsWith("_autosave", StringComparison.OrdinalIgnoreCase);

    private static string GetPreparedSavePath(PreparedWorld world)
        => Path.Combine(GetWorkspaceSavesDirectory(world), PreparedSaveFileName);

    private static string GetWorkspaceSavesDirectory(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            WorkspaceUserDataDirectoryName,
            SavesDirectoryName);

    private static string GetWorkspaceConfigPath(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);

    private static string CreatePackagePath()
    {
        var root = Path.Combine(GetLocalWorkRoot(), "packages");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Guid.NewGuid():N}.zip");
    }

    private static string GetLocalWorkRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "SharedWorlds", "factorio");
    }

    private static string GetRequiredMetadata(GameInstallation installation, string key)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Factorio installation '{installation.RootPath}' is missing required metadata '{key}'.");
        }

        return value;
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;

        await using var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

        await using var destinationStream = new FileStream(
            destination,
            destinationMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);

        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        await destinationStream.FlushAsync(cancellationToken);
    }
}
