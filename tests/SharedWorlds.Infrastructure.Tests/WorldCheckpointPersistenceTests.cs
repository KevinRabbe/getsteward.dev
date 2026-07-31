using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class WorldCheckpointPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safe-world-checkpoint-persistence",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckpointsRoundTripInsideWorldSchemaThree()
    {
        var storage = new LocalWorldStorage(_root);
        var revisionId = RevisionId.New();
        var owner = new UserIdentity("local", "owner", "Owner");
        var checkpoint = new WorldCheckpoint(
            revisionId,
            "Before the boss",
            DateTimeOffset.UtcNow,
            owner);
        var world = new World(
            WorldId.New(),
            "Checkpoint World",
            "factorio",
            [owner],
            RevisionId.New(),
            revisionId)
        {
            Checkpoints = [checkpoint]
        };

        await storage.SaveWorldAsync(world);
        var loaded = await storage.LoadWorldAsync(world.Id);

        Assert.NotNull(loaded);
        Assert.Equal(checkpoint, Assert.Single(loaded.Checkpoints));

        var path = Path.Combine(_root, "worlds", world.Id.ToString(), "world.json");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var persistedCheckpoint = document.RootElement
            .GetProperty("payload")
            .GetProperty("checkpoints")[0];
        Assert.Equal("Before the boss", persistedCheckpoint.GetProperty("name").GetString());
        Assert.Equal(
            revisionId.Value,
            persistedCheckpoint
                .GetProperty("stateRevisionId")
                .GetProperty("value")
                .GetGuid());
    }

    [Fact]
    public async Task SchemaOneWorldWithoutCheckpointPropertyLoadsEmpty()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var directory = Path.Combine(_root, "worlds", worldId.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "world.json");
        var json = JsonSerializer.Serialize(new
        {
            documentType = "sharedworlds.world",
            schemaVersion = 1,
            payload = new
            {
                id = worldId,
                name = "Legacy World",
                gameAdapterId = "factorio",
                members = Array.Empty<UserIdentity>(),
                currentEnvironmentRevisionId = (RevisionId?)null,
                currentStateRevisionId = (RevisionId?)null,
                sharingMode = WorldSharingMode.LocalOnly,
                gameVersionPolicy = WorldGameVersionPolicy.KeepExact,
                visibility = WorldVisibility.Private,
                joinPolicy = WorldJoinPolicy.InviteOrCodeOnly,
                startYourOwnPolicy = StartYourOwnPolicy.Disabled,
                startedFrom = (WorldProvenance?)null
            }
        });
        await File.WriteAllTextAsync(path, json);

        var loaded = await storage.LoadWorldAsync(worldId);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Checkpoints);
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
            // Test cleanup must not hide the assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the assertion result.
        }
    }
}
