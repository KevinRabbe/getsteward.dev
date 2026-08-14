using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
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
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world,
            destination,
            "prepared save");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyFileAsync(state.Path, destination, overwrite: true, cancellationToken);
    }

    public static async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var savesDirectory = GetWorkspaceSavesDirectory(world);
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world,
            savesDirectory,
            "saves directory");

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

        FactorioWorkspaceOwnership.RequireOwnedPath(
            world,
            savePath,
            "captured save");

        var package = CreatePackagePath();
        await CopyFileAsync(savePath, package, overwrite: false, cancellationToken);
        return new CapturedState(
            new StatePackage(Path.GetFileNameWithoutExtension(package), package),
            DateTimeOffset.UtcNow);
    }
}
