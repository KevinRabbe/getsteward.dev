using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalStorageIdentityBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-storage-identity-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidWorldDocumentCopiedToDifferentWorldKeyIsRejected()
    {
        var storage = new LocalWorldStorage(_root);
        var source = CreateWorld();
        var requestedId = WorldId.New();
        await storage.SaveWorldAsync(source);

        var requestedPath = GetWorldMetadataPath(requestedId);
        Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);
        File.Copy(GetWorldMetadataPath(source.Id), requestedPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(requestedId));

        Assert.Contains("does not match storage key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorldListingRejectsValidDocumentStoredUnderDifferentDirectoryKey()
    {
        var storage = new LocalWorldStorage(_root);
        var source = CreateWorld();
        await storage.SaveWorldAsync(source);

        var mismatchedDirectory = Path.Combine(
            _root,
            "worlds",
            WorldId.New().ToString());
        Directory.CreateDirectory(mismatchedDirectory);
        File.Copy(
            GetWorldMetadataPath(source.Id),
            Path.Combine(mismatchedDirectory, "world.json"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.ListWorldsAsync());

        Assert.Contains("mismatched World key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidEnvironmentRevisionCopiedToDifferentRevisionKeyIsRejected()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var source = new EnvironmentRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            CreateManifest());
        await storage.StoreEnvironmentRevisionAsync(source);

        var requestedRevisionId = RevisionId.New();
        var requestedPath = GetEnvironmentRevisionPath(worldId, requestedRevisionId);
        File.Copy(GetEnvironmentRevisionPath(worldId, source.Id), requestedPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadEnvironmentRevisionAsync(worldId, requestedRevisionId));

        Assert.Contains("does not match storage key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidStateRevisionMetadataCopiedToDifferentStorageKeyIsRejected()
    {
        var storage = new LocalWorldStorage(_root);
        var sourceWorldId = WorldId.New();
        var source = CreateStateRevision(sourceWorldId);
        await StoreStateAsync(storage, source, "state");

        var requestedWorldId = WorldId.New();
        var requestedRevisionId = RevisionId.New();
        var requestedDirectory = GetStateRevisionDirectory(requestedWorldId, requestedRevisionId);
        Directory.CreateDirectory(requestedDirectory);
        File.Copy(
            Path.Combine(GetStateRevisionDirectory(sourceWorldId, source.Id), "revision.json"),
            Path.Combine(requestedDirectory, "revision.json"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadStateRevisionAsync(requestedWorldId, requestedRevisionId));

        Assert.Contains("does not match storage key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidStateRevisionDirectoryCopiedToDifferentStorageKeyCannotBeOpened()
    {
        var storage = new LocalWorldStorage(_root);
        var sourceWorldId = WorldId.New();
        var source = CreateStateRevision(sourceWorldId);
        await StoreStateAsync(storage, source, "canonical-state");

        var requestedWorldId = WorldId.New();
        var requestedRevisionId = RevisionId.New();
        CopyStateRevisionDirectory(
            GetStateRevisionDirectory(sourceWorldId, source.Id),
            GetStateRevisionDirectory(requestedWorldId, requestedRevisionId));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.OpenRevisionAsync(requestedWorldId, requestedRevisionId));

        Assert.Contains("does not match storage key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadWithoutRevisionMetadataCannotBeOpened()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var directory = GetStateRevisionDirectory(worldId, revisionId);
        Directory.CreateDirectory(directory);
        var payload = Encoding.UTF8.GetBytes("orphaned-payload");
        await File.WriteAllBytesAsync(Path.Combine(directory, "payload.bin"), payload);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "payload.sha256"),
            Convert.ToHexString(SHA256.HashData(payload)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.OpenRevisionAsync(worldId, revisionId));

        Assert.Contains("payload bytes but no revision metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidRecoveryRecordCopiedToDifferentWorkspaceKeyIsRejected()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecoveryRecord();
        await store.SaveAsync(record);

        var recoveryDirectory = Path.Combine(_root, "recovery");
        var mismatchedId = WorkspaceId.New();
        File.Copy(
            Path.Combine(recoveryDirectory, $"{record.Id}.json"),
            Path.Combine(recoveryDirectory, $"{mismatchedId}.json"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListAsync());

        Assert.Contains("mismatched workspace key", exception.Message, StringComparison.Ordinal);
    }

    private async Task StoreStateAsync(
        LocalWorldStorage storage,
        StateRevision revision,
        string payload)
    {
        await using var package = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await storage.StoreRevisionAsync(revision, package);
    }

    private static void CopyStateRevisionDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source))
        {
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        }
    }

    private string GetWorldMetadataPath(WorldId worldId)
        => Path.Combine(_root, "worlds", worldId.ToString(), "world.json");

    private string GetEnvironmentRevisionPath(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            _root,
            "worlds",
            worldId.ToString(),
            "environments",
            $"{revisionId}.json");

    private string GetStateRevisionDirectory(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            _root,
            "worlds",
            worldId.ToString(),
            "states",
            revisionId.ToString());

    private static World CreateWorld()
        => new(
            WorldId.New(),
            "Identity World",
            "factorio",
            [new UserIdentity("local", "tester", "Tester")],
            RevisionId.New(),
            RevisionId.New());

    private static StateRevision CreateStateRevision(WorldId worldId)
        => new(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            "factorio",
            "package-1");

    private WorkspaceRecoveryRecord CreateRecoveryRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "factorio",
            Path.Combine(_root, "workspace"),
            new UserIdentity("local", "tester"),
            now,
            now,
            WorkspaceRecoveryStatus.RecoveryPending);
    }

    private static EnvironmentManifest CreateManifest()
        => new(
            1,
            "factorio",
            "1.0.0",
            [],
            new Dictionary<string, string>());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
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
