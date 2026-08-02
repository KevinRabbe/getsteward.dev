using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorldStorageRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safe-world-retention-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EvictRevisionPayloadPreservesMetadataAndParentLink()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var user = new UserIdentity("local", "tester", "Tester");
        var parent = new StateRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            user,
            "fake",
            "parent-package",
            environmentId);
        var child = new StateRevision(
            RevisionId.New(),
            worldId,
            parent.Id,
            DateTimeOffset.UtcNow,
            user,
            "fake",
            "child-package",
            environmentId);

        await StoreAsync(storage, parent, "parent-state");
        await StoreAsync(storage, child, "child-state");

        Assert.True(await storage.IsRevisionPayloadAvailableAsync(worldId, parent.Id));
        Assert.True(await storage.EvictRevisionPayloadAsync(worldId, parent.Id));
        Assert.False(await storage.IsRevisionPayloadAvailableAsync(worldId, parent.Id));

        var persistedParent = await storage.LoadStateRevisionAsync(worldId, parent.Id);
        var persistedChild = await storage.LoadStateRevisionAsync(worldId, child.Id);
        Assert.Equal(parent, persistedParent);
        Assert.Equal(parent.Id, persistedChild?.ParentRevisionId);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            storage.OpenRevisionAsync(worldId, parent.Id));

        Assert.True(await storage.IsRevisionPayloadAvailableAsync(worldId, child.Id));
        await using var childPayload = await storage.OpenRevisionAsync(worldId, child.Id);
        using var reader = new StreamReader(childPayload, Encoding.UTF8);
        Assert.Equal("child-state", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task EvictRevisionPayloadIsIdempotent()
    {
        var storage = new LocalWorldStorage(_root);
        var revision = new StateRevision(
            RevisionId.New(),
            WorldId.New(),
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester", "Tester"),
            "fake",
            "package",
            RevisionId.New());
        await StoreAsync(storage, revision, "state");

        Assert.True(await storage.EvictRevisionPayloadAsync(revision.WorldId, revision.Id));
        Assert.False(await storage.EvictRevisionPayloadAsync(revision.WorldId, revision.Id));
        Assert.NotNull(await storage.LoadStateRevisionAsync(revision.WorldId, revision.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task StoreAsync(
        LocalWorldStorage storage,
        StateRevision revision,
        string payload)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await storage.StoreRevisionAsync(revision, stream);
    }
}
