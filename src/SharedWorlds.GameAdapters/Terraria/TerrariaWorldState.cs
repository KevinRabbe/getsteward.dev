using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.Terraria;

internal static class TerrariaWorldState
{
    private const string PreparedWorldFileName = "world.wld";

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureFileAsync(world.SourcePath, Path.GetFileNameWithoutExtension(world.SourcePath), cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        TerrariaWorkspaceOwnership.RequireOwned(world);
        return CaptureFileAsync(
            Path.Combine(world.WorkingDirectory, PreparedWorldFileName),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        TerrariaEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = TerrariaWorkspaceOwnership.Create();
        return new PreparedWorld(
            installation,
            workspace,
            requiredEnvironment,
            DisplayName: null);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        TerrariaEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = Path.GetFullPath(preparation.ManagedWorkingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "Terraria managed workspace must be created by Core before adapter materialization.");
        }

        return new PreparedWorld(
            installation,
            workspace,
            requiredEnvironment,
            DisplayName: null,
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());
    }

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        TerrariaWorkspaceOwnership.RequireOwned(world);

        var sourcePath = Path.GetFullPath(state.Path);
        RequireRegularFile(sourcePath, "Terraria state package");

        var destinationPath = Path.Combine(world.WorkingDirectory, PreparedWorldFileName);
        if (File.Exists(destinationPath))
        {
            RequireRegularFile(destinationPath, "existing prepared Terraria World");
        }

        var stagingPath = destinationPath + ".sharedworlds-staging-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyFileAsync(sourcePath, stagingPath, cancellationToken);
            File.Move(stagingPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(stagingPath);
            throw;
        }
    }

    public static Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        TerrariaWorkspaceOwnership.RequireOwned(world);

        if (disposition == PreparedWorldDisposition.Discard)
        {
            TerrariaWorkspaceOwnership.DeleteOwned(world);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureFileAsync(
        string sourcePath,
        string worldName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        RequireRegularFile(fullSourcePath, "Terraria World");
        if (!string.Equals(Path.GetExtension(fullSourcePath), ".wld", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Terraria World capture requires a .wld file: {fullSourcePath}");
        }

        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "terraria",
            worldName,
            ".wld");
        try
        {
            await CopyFileAsync(fullSourcePath, packagePath, cancellationToken);
            return new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(packagePath), packagePath),
                DateTimeOffset.UtcNow);
        }
        catch
        {
            TryDeleteFile(packagePath);
            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static void RequireRegularFile(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"{description} was not found.", path, exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}",
                exception);
        }

        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked file: {path}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
