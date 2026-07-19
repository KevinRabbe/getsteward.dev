using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioWorldOperations
{
    private const string PreparedSaveFileName = "world.zip";

    public static IReadOnlyList<DetectedWorld> DiscoverWorlds(GameInstallation installation)
    {
        var userDataPath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.UserDataPathKey);
        var savesPath = Path.Combine(userDataPath, "saves");

        if (!Directory.Exists(savesPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(savesPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileNameWithoutExtension(path)
                .StartsWith("_autosave", StringComparison.OrdinalIgnoreCase))
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

    public static Task<PreparedWorld> PrepareEnvironmentAsync(
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

        Directory.CreateDirectory(workspace);
        return Task.FromResult(new PreparedWorld(installation, workspace, requiredEnvironment));
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

        Directory.CreateDirectory(world.WorkingDirectory);
        var destination = GetPreparedSavePath(world);
        await CopyFileAsync(state.Path, destination, overwrite: true, cancellationToken);
    }

    public static async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var savePath = GetPreparedSavePath(world);
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "The prepared Factorio world has no canonical save to capture.",
                savePath);
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
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "RestoreStateAsync must be called before launching a local Factorio session.",
                savePath);
        }

        var process = StartFactorio(world.Installation, "--load-game", savePath);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    public static Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "RestoreStateAsync must be called before launching a Factorio host.",
                savePath);
        }

        var process = StartFactorio(world.Installation, "--host", savePath);
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

        var process = StartFactorio(world.Installation, "--mp-connect", address);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    private static Process StartFactorio(
        GameInstallation installation,
        string command,
        string value)
    {
        var executable = GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installation.RootPath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(value);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
    }

    private static string GetPreparedSavePath(PreparedWorld world)
        => Path.Combine(world.WorkingDirectory, PreparedSaveFileName);

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
