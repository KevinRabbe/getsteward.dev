using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorldStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharedworlds-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WorldMetadata_RoundTrips()
    {
        var storage = new LocalWorldStorage(_root);
        var world = new World(
            WorldId.New(),
            "Test World",
            "factorio",
            [new UserIdentity("local", "tester", "Tester")],
            null,
            null);

        await storage.SaveWorldAsync(world);
        var loaded = await storage.LoadWorldAsync(world.Id);

        Assert.NotNull(loaded);
        Assert.Equal(world.Id, loaded.Id);
        Assert.Equal(world.Name, loaded.Name);
        Assert.Equal(world.GameAdapterId, loaded.GameAdapterId);
        Assert.Equal(world.CurrentEnvironmentRevisionId, loaded.CurrentEnvironmentRevisionId);
        Assert.Equal(world.CurrentStateRevisionId, loaded.CurrentStateRevisionId);
        Assert.Equal(world.Members, loaded.Members);
    }

    [Fact]
    public async Task StateRevisionPayload_IsStoredAndReopened()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = new StateRevision(
            RevisionId.New(),
            worldId,
            null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            "factorio",
            "package-1");

        var expected = Encoding.UTF8.GetBytes("state-payload");
        await using var input = new MemoryStream(expected);

        await storage.StoreRevisionAsync(revision, input);
        await using var reopened = await storage.OpenRevisionAsync(worldId, revision.Id);
        using var output = new MemoryStream();
        await reopened.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
    }

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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
