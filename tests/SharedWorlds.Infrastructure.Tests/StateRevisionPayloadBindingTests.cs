using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StateRevisionPayloadBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-state-payload-binding-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidPayloadFromDifferentRevisionFailsAgainstMetadataBoundDigest()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var first = CreateRevision(worldId, "package-a");
        var second = CreateRevision(worldId, "package-b");
        await StoreAsync(storage, first, "AAAA-canonical-state");
        await StoreAsync(storage, second, "BBBB-canonical-state");

        var firstPayload = Path.Combine(GetRevisionDirectory(worldId, first.Id), "payload.bin");
        var secondPayload = Path.Combine(GetRevisionDirectory(worldId, second.Id), "payload.bin");
        File.Copy(firstPayload, secondPayload, overwrite: true);

        await using var reopened = await storage.OpenRevisionAsync(worldId, second.Id);
        using var sink = new MemoryStream();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => reopened.CopyToAsync(sink));

        Assert.Contains("SHA-256 integrity verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewRevisionPublishesOnlyMetadataAndPayload()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = CreateRevision(worldId, "package");
        await StoreAsync(storage, revision, "canonical-state");

        var directory = GetRevisionDirectory(worldId, revision.Id);
        Assert.True(File.Exists(Path.Combine(directory, "revision.json")));
        Assert.True(File.Exists(Path.Combine(directory, "payload.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "payload.sha256")));
        Assert.Equal(
            ["payload.bin", "revision.json"],
            Directory.EnumerateFiles(directory)
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task StoreAsync(
        LocalWorldStorage storage,
        StateRevision revision,
        string payload)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await storage.StoreRevisionAsync(revision, stream);
    }

    private string GetRevisionDirectory(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            _root,
            "worlds",
            worldId.ToString(),
            "states",
            revisionId.ToString());

    private static StateRevision CreateRevision(WorldId worldId, string packageId)
        => new(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            "factorio",
            packageId);

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
