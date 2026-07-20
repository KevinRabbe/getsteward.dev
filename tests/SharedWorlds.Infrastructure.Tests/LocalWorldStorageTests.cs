using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorldStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharedworlds-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WorldMetadata_RoundTrips()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();

        await storage.SaveWorldAsync(world);
        var loaded = await storage.LoadWorldAsync(world.Id);

        Assert.NotNull(loaded);
        Assert.Equal(world.Id, loaded.Id);
        Assert.Equal(world.Name, loaded.Name);
        Assert.Equal(world.GameAdapterId, loaded.GameAdapterId);
        Assert.Equal(world.CurrentEnvironmentRevisionId, loaded.CurrentEnvironmentRevisionId);
        Assert.Equal(world.CurrentStateRevisionId, loaded.CurrentStateRevisionId);
        Assert.Equal(world.Members.ToArray(), loaded.Members.ToArray());
    }

    [Fact]
    public async Task ListWorlds_ReturnsStoredWorldsSortedByName_AndSkipsIncompleteDirectories()
    {
        var storage = new LocalWorldStorage(_root);
        var zeta = CreateWorld("Zeta World");
        var alpha = CreateWorld("Alpha World");

        await storage.SaveWorldAsync(zeta);
        await storage.SaveWorldAsync(alpha);
        Directory.CreateDirectory(Path.Combine(_root, "worlds", "incomplete-import"));

        var worlds = await storage.ListWorldsAsync();

        Assert.Equal([alpha.Id, zeta.Id], worlds.Select(world => world.Id).ToArray());
    }

    [Fact]
    public async Task WorldMetadata_IsStoredInVersionedEnvelope()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();

        await storage.SaveWorldAsync(world);

        var path = Path.Combine(_root, "worlds", world.Id.ToString(), "world.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);

        Assert.Equal("sharedworlds.world", document.RootElement.GetProperty("documentType").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(world.Name, document.RootElement.GetProperty("payload").GetProperty("name").GetString());
    }

    [Fact]
    public async Task LegacyUnwrappedWorldMetadata_IsMigratedOnRead()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        var directory = Path.Combine(_root, "worlds", world.Id.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "world.json");

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(world));

        var loaded = await storage.LoadWorldAsync(world.Id);

        Assert.NotNull(loaded);
        Assert.Equal(world.Id, loaded.Id);
        Assert.Equal(world.Name, loaded.Name);
    }

    [Fact]
    public async Task UnsupportedFutureWorldSchema_ThrowsTypedCompatibilityFailure()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        var directory = Path.Combine(_root, "worlds", world.Id.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "world.json");

        var json = JsonSerializer.Serialize(new
        {
            documentType = "sharedworlds.world",
            schemaVersion = 999,
            payload = world
        });
        await File.WriteAllTextAsync(path, json);

        var exception = await Assert.ThrowsAsync<PersistedDataCompatibilityException>(
            () => storage.LoadWorldAsync(world.Id));

        Assert.Equal("sharedworlds.world", exception.DocumentType);
        Assert.Equal(999, exception.EncounteredSchemaVersion);
        Assert.Equal(1, exception.CurrentSchemaVersion);
    }

    [Fact]
    public async Task StateRevisionAndPayload_RoundTrip()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = CreateStateRevision(worldId);

        var expected = Encoding.UTF8.GetBytes("state-payload");
        await using var input = new MemoryStream(expected);

        await storage.StoreRevisionAsync(revision, input);

        var loadedRevision = await storage.LoadStateRevisionAsync(worldId, revision.Id);
        Assert.Equal(revision, loadedRevision);

        await using var reopened = await storage.OpenRevisionAsync(worldId, revision.Id);
        using var output = new MemoryStream();
        await reopened.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public async Task StateRevision_CannotBeOverwritten()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = CreateStateRevision(worldId);
        var originalPayload = Encoding.UTF8.GetBytes("original");

        await using (var original = new MemoryStream(originalPayload))
        {
            await storage.StoreRevisionAsync(revision, original);
        }

        await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
        await Assert.ThrowsAsync<IOException>(
            () => storage.StoreRevisionAsync(revision, replacement));

        await using var reopened = await storage.OpenRevisionAsync(worldId, revision.Id);
        using var output = new MemoryStream();
        await reopened.CopyToAsync(output);

        Assert.Equal(originalPayload, output.ToArray());
    }

    [Fact]
    public async Task EnvironmentRevision_CannotBeOverwritten()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var user = new UserIdentity("local", "tester");
        var original = new EnvironmentRevision(
            revisionId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            user,
            CreateManifest("1.0.0"));
        var replacement = original with { Manifest = CreateManifest("2.0.0") };

        await storage.StoreEnvironmentRevisionAsync(original);
        await Assert.ThrowsAsync<IOException>(
            () => storage.StoreEnvironmentRevisionAsync(replacement));

        var loaded = await storage.LoadEnvironmentRevisionAsync(worldId, revisionId);
        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded.Manifest.GameVersion);
    }

    private static World CreateWorld(string name = "Test World")
        => new(
            WorldId.New(),
            name,
            "factorio",
            [new UserIdentity("local", "tester", "Tester")],
            null,
            null);

    private static StateRevision CreateStateRevision(WorldId worldId)
        => new(
            RevisionId.New(),
            worldId,
            null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            "factorio",
            "package-1");

    private static EnvironmentManifest CreateManifest(string gameVersion)
        => new(
            1,
            "factorio",
            gameVersion,
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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
