using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.State;

namespace SharedWorlds.Infrastructure.State;

/// <summary>
/// A local content-addressed canonical state store. Packages are immutable and the small head file
/// is replaced only after the complete package and revision metadata have been flushed to disk.
/// </summary>
public sealed class FileSystemCanonicalWorldStateStore : ICanonicalWorldStateStore
{
    private const string RevisionsDirectoryName = "revisions";
    private const string HeadFileName = "head.json";
    private const string LockFileName = ".commit.lock";
    private const int BufferSize = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootPath;

    public FileSystemCanonicalWorldStateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath => _rootPath;

    public async Task<CanonicalWorldStateHead?> ReadHeadAsync(
        string worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);

        var worldRoot = GetWorldRoot(worldId);
        Directory.CreateDirectory(worldRoot);

        await using var commitLock = await AcquireCommitLockAsync(worldRoot, cancellationToken);
        return await ReadHeadUnlockedAsync(worldRoot, cancellationToken);
    }

    public async Task<CanonicalWorldStateCommitResult> TryCommitAsync(
        string worldId,
        string? expectedHeadRevisionId,
        CapturedState candidate,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePackagePath = Path.GetFullPath(candidate.Package.Path);
        if (!File.Exists(sourcePackagePath))
        {
            throw new FileNotFoundException(
                "The captured state package does not exist.",
                sourcePackagePath);
        }

        var worldRoot = GetWorldRoot(worldId);
        var revisionsRoot = Path.Combine(worldRoot, RevisionsDirectoryName);
        Directory.CreateDirectory(revisionsRoot);

        await using var commitLock = await AcquireCommitLockAsync(worldRoot, cancellationToken);
        var currentHead = await ReadHeadUnlockedAsync(worldRoot, cancellationToken);
        var observedRevisionId = currentHead?.RevisionId;

        if (!RevisionIdsEqual(expectedHeadRevisionId, observedRevisionId))
        {
            return new CanonicalWorldStateCommitResult(
                CanonicalWorldStateCommitStatus.HeadChanged,
                currentHead,
                expectedHeadRevisionId,
                observedRevisionId);
        }

        var stagingDirectory = Path.Combine(
            revisionsRoot,
            $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var extension = Path.GetExtension(sourcePackagePath);
            var packageFileName = string.IsNullOrWhiteSpace(extension)
                ? "state.package"
                : $"state{extension}";
            var stagingPackagePath = Path.Combine(stagingDirectory, packageFileName);
            var revisionId = await CopyPackageAndHashAsync(
                sourcePackagePath,
                stagingPackagePath,
                cancellationToken);

            if (currentHead is not null && RevisionIdsEqual(currentHead.RevisionId, revisionId))
            {
                TryDeleteDirectory(stagingDirectory);
                TryDeleteTemporarySource(candidate, sourcePackagePath, currentHead.PackagePath);

                return new CanonicalWorldStateCommitResult(
                    CanonicalWorldStateCommitStatus.Unchanged,
                    currentHead,
                    expectedHeadRevisionId,
                    currentHead.RevisionId);
            }

            var revisionDirectory = Path.Combine(revisionsRoot, revisionId);
            var storedPackagePath = Path.Combine(revisionDirectory, packageFileName);

            if (!Directory.Exists(revisionDirectory))
            {
                var revisionDocument = new RevisionDocument(
                    WorldId: worldId,
                    RevisionId: revisionId,
                    PackageFileName: packageFileName,
                    ParentRevisionId: expectedHeadRevisionId,
                    SourcePackageId: candidate.Package.Id,
                    CapturedAt: candidate.CapturedAt,
                    StoredAt: DateTimeOffset.UtcNow);

                await WriteJsonDurablyAsync(
                    Path.Combine(stagingDirectory, "revision.json"),
                    revisionDocument,
                    cancellationToken);

                Directory.Move(stagingDirectory, revisionDirectory);
            }
            else
            {
                TryDeleteDirectory(stagingDirectory);
                if (!File.Exists(storedPackagePath))
                {
                    throw new InvalidDataException(
                        $"Canonical revision '{revisionId}' exists without its package: {storedPackagePath}");
                }
            }

            var newHead = new CanonicalWorldStateHead(
                WorldId: worldId,
                RevisionId: revisionId,
                PackagePath: storedPackagePath,
                ParentRevisionId: expectedHeadRevisionId,
                CapturedAt: candidate.CapturedAt,
                CommittedAt: DateTimeOffset.UtcNow);

            await ReplaceHeadAsync(worldRoot, newHead, cancellationToken);
            TryDeleteTemporarySource(candidate, sourcePackagePath, storedPackagePath);

            return new CanonicalWorldStateCommitResult(
                CanonicalWorldStateCommitStatus.Committed,
                newHead,
                expectedHeadRevisionId,
                revisionId);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private async Task<CanonicalWorldStateHead?> ReadHeadUnlockedAsync(
        string worldRoot,
        CancellationToken cancellationToken)
    {
        var headPath = Path.Combine(worldRoot, HeadFileName);
        if (!File.Exists(headPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            headPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<CanonicalWorldStateHead>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidDataException($"Canonical state head is empty: {headPath}");
    }

    private static async Task ReplaceHeadAsync(
        string worldRoot,
        CanonicalWorldStateHead head,
        CancellationToken cancellationToken)
    {
        var headPath = Path.Combine(worldRoot, HeadFileName);
        var temporaryHeadPath = Path.Combine(
            worldRoot,
            $".{HeadFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WriteJsonDurablyAsync(temporaryHeadPath, head, cancellationToken);
            File.Move(temporaryHeadPath, headPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryHeadPath);
        }
    }

    private static async Task<string> CopyPackageAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferSize];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, bytesRead);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task WriteJsonDurablyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<FileStream> AcquireCommitLockAsync(
        string worldRoot,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(worldRoot, LockFileName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private string GetWorldRoot(string worldId)
    {
        var worldKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(worldId)))
            .ToLowerInvariant();
        return Path.Combine(_rootPath, worldKey);
    }

    private static bool RevisionIdsEqual(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteTemporarySource(
        CapturedState candidate,
        string sourcePackagePath,
        string durablePackagePath)
    {
        if (!candidate.DeletePackageAfterStore)
        {
            return;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (comparer.Equals(
                Path.GetFullPath(sourcePackagePath),
                Path.GetFullPath(durablePackagePath)))
        {
            return;
        }

        TryDeleteFile(sourcePackagePath);
    }

    private static void ValidateWorldId(string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new ArgumentException("World id is required.", nameof(worldId));
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
            // Cleanup is best-effort after the canonical result is already known.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best-effort after the canonical result is already known.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // An orphaned staging directory is safe and can be collected later.
        }
        catch (UnauthorizedAccessException)
        {
            // An orphaned staging directory is safe and can be collected later.
        }
    }

    private sealed record RevisionDocument(
        string WorldId,
        string RevisionId,
        string PackageFileName,
        string? ParentRevisionId,
        string SourcePackageId,
        DateTimeOffset CapturedAt,
        DateTimeOffset StoredAt);
}
