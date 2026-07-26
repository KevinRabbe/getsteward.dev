using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Raft;

internal static class RaftWorldState
{
    private const string PreparedSaveDirectoryName = "save";
    private const string PreparedWorldFileName = "world.rgd";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(DetectedWorld world, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureFileAsync(world.SourcePath, world.DisplayName, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(PreparedWorld world, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        RaftWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureFileAsync(GetPreparedWorldPath(world.WorkingDirectory), world.DisplayName ?? "world", cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(GameInstallation installation, EnvironmentManifest requiredEnvironment)
    {
        RaftEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(installation, RaftWorkspaceOwnership.Create(), requiredEnvironment, DisplayName: null);
    }

    public static async Task RestorePreparedWorldAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        RaftWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        var sourcePath = Path.GetFullPath(state.Path);
        RequireRegularWorldFile(sourcePath, "Raft state package");
        EnsureSufficientFreeSpace(world.WorkingDirectory, new FileInfo(sourcePath).Length);
        var destinationPath = GetPreparedWorldPath(world.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = destinationPath + ".sharedworlds-staging-" + operationId;
        var rollbackPath = destinationPath + ".sharedworlds-rollback-" + operationId;
        var movedExisting = false;
        try
        {
            await CopyFileAsync(sourcePath, stagingPath, cancellationToken);
            if (File.Exists(destinationPath))
            {
                RequireRegularWorldFile(destinationPath, "existing prepared Raft World");
                File.Move(destinationPath, rollbackPath);
                movedExisting = true;
            }
            File.Move(stagingPath, destinationPath);
            if (movedExisting) TryDeleteFile(rollbackPath);
        }
        catch (Exception restoreException)
        {
            TryDeleteFile(stagingPath);
            if (movedExisting && !File.Exists(destinationPath) && File.Exists(rollbackPath))
            {
                try { File.Move(rollbackPath, destinationPath); }
                catch (Exception rollbackException)
                {
                    throw new AggregateException("Raft state restore failed and the previous prepared World could not be rolled back automatically.", restoreException, rollbackException);
                }
            }
            throw;
        }
    }

    public static Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        RaftWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        if (disposition == PreparedWorldDisposition.Discard && Directory.Exists(world.WorkingDirectory))
        {
            RaftWorkspaceOwnership.RequireOwnedTree(world.WorkingDirectory);
            Directory.Delete(world.WorkingDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureFileAsync(string sourcePath, string worldName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        RequireRegularWorldFile(fullSourcePath, "Raft World");
        var packagePath = CreatePackagePath(worldName);
        try
        {
            await CopyFileAsync(fullSourcePath, packagePath, cancellationToken);
            return new CapturedState(new StatePackage(Path.GetFileNameWithoutExtension(packagePath), packagePath), DateTimeOffset.UtcNow);
        }
        catch { TryDeleteFile(packagePath); throw; }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        if (source.Length != destination.Length) throw new InvalidDataException("Raft World copy did not finish at the expected byte length.");
    }

    private static string GetPreparedWorldPath(string workingDirectory)
        => Path.GetFullPath(Path.Combine(workingDirectory, PreparedSaveDirectoryName, PreparedWorldFileName));

    private static void RequireRegularWorldFile(string path, string description)
    {
        if (!string.Equals(Path.GetExtension(path), ".rgd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{description} must be a .rgd file: {path}");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"{description} could not be inspected safely: {path}", ex); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException($"{description} must be a regular non-linked file: {path}");
        if (new FileInfo(path).Length == 0) throw new InvalidOperationException($"{description} is empty and cannot represent a usable Raft World: {path}");
    }

    private static void EnsureSufficientFreeSpace(string workingDirectory, long packageBytes)
    {
        long required;
        try { required = checked(packageBytes + MinimumFreeSpaceReserveBytes); }
        catch (OverflowException ex) { throw new InvalidDataException("Raft state package declares an impossible file size.", ex); }
        var root = Path.GetPathRoot(Path.GetFullPath(workingDirectory));
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
                throw new IOException($"Raft state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
        }
        catch (DriveNotFoundException) { }
    }

    private static string CreatePackagePath(string worldName)
    {
        var root = GetPackageRoot();
        Directory.CreateDirectory(root);
        var safeName = SanitizeFileName(worldName);
        return Path.Combine(root, $"{safeName}-{Guid.NewGuid():N}.rgd");
    }

    private static string GetPackageRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
        return Path.Combine(localData, "SharedWorlds", "raft", "packages");
    }

    private static string SanitizeFileName(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "world" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(result) ? "world" : result;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class RaftWorkspaceOwnership
{
    public static string Create()
    {
        var root = GetExpectedWorkRoot();
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }

    public static void RequireOwned(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workspace = Path.GetFullPath(workingDirectory);
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        if (!Guid.TryParseExact(leaf, "N", out _) || Directory.GetParent(workspace)?.FullName is not string ownerRoot || !PathsEqual(ownerRoot, GetExpectedWorkRoot()))
            throw Refuse(workingDirectory);
        RejectReparsePoint(ownerRoot, workingDirectory);
        if (Directory.Exists(workspace)) RejectReparsePoint(workspace, workingDirectory);
    }

    public static void RequireOwnedTree(string workingDirectory)
    {
        RequireOwned(workingDirectory);
        if (!Directory.Exists(workingDirectory)) return;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workingDirectory));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current, workingDirectory);
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, workingDirectory);
                pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw Refuse(workingDirectory);
        }
    }

    private static string GetExpectedWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
        return Path.GetFullPath(Path.Combine(localData, "Steward", "workspaces", "raft"));
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Refuse(originalPath);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string path)
        => new($"Refusing to use unrecognized or linked Raft Steward workspace '{path}'.");
}
