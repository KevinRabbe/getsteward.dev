using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PersistedDocumentIntegrityTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-metadata-integrity-{Guid.NewGuid():N}");

    [Fact]
    public async Task NewWorldMetadataCarriesRequiredInEnvelopeIntegrityProof()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();

        await storage.SaveWorldAsync(world);

        await using var stream = File.OpenRead(GetWorldMetadataPath(world.Id));
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("integrityVersion").GetInt32());
        var digest = root.GetProperty("contentSha256").GetString();
        Assert.NotNull(digest);
        Assert.Equal(SHA256.HashSizeInBytes * 2, digest.Length);
        Assert.All(digest, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public async Task ValidLookingCanonicalWorldPointerTamperingFailsIntegrityVerification()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        await storage.SaveWorldAsync(world);
        var path = GetWorldMetadataPath(world.Id);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["payload"]!.AsObject()["currentStateRevisionId"] =
            JsonSerializer.SerializeToNode(RevisionId.New(), CamelCaseJson);
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(world.Id));

        Assert.Contains("SHA-256 integrity verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaVersionTamperingFailsIntegrityVerificationBeforeMigration()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        await storage.SaveWorldAsync(world);
        var path = GetWorldMetadataPath(world.Id);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["schemaVersion"] = 1;
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(world.Id));

        Assert.Contains("SHA-256 integrity verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedEnvelopeMissingDigestFailsClosed()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        await storage.SaveWorldAsync(world);
        var path = GetWorldMetadataPath(world.Id);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("contentSha256"));
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(world.Id));

        Assert.Contains("integrity proof is incomplete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedEnvelopeWithEntireIntegrityProofRemovedFailsClosed()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        await storage.SaveWorldAsync(world);
        var path = GetWorldMetadataPath(world.Id);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("integrityVersion"));
        Assert.True(root.Remove("contentSha256"));
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(world.Id));

        Assert.Contains("missing its required integrity proof", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingVersionedWorldEnvelopeWithoutIntegrityProofRemainsReadable()
    {
        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        var path = GetWorldMetadataPath(world.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    documentType = "sharedworlds.world",
                    schemaVersion = 1,
                    payload = world
                },
                CamelCaseJson));

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
    public async Task StateRevisionMetadataTamperingFailsIntegrityVerification()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = CreateStateRevision(worldId);
        await using (var package = new MemoryStream(Encoding.UTF8.GetBytes("canonical-state")))
        {
            await storage.StoreRevisionAsync(revision, package);
        }

        var path = Path.Combine(GetStateRevisionDirectory(worldId, revision.Id), "revision.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["payload"]!.AsObject()["statePackageId"] = "different-package";
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadStateRevisionAsync(worldId, revision.Id));

        Assert.Contains("SHA-256 integrity verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentRevisionMetadataTamperingFailsIntegrityVerification()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = new EnvironmentRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            new UserIdentity("local", "tester"),
            CreateManifest("1.0.0"));
        await storage.StoreEnvironmentRevisionAsync(revision);

        var path = Path.Combine(
            _root,
            "worlds",
            worldId.ToString(),
            "environments",
            $"{revision.Id}.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["payload"]!.AsObject()["manifest"]!.AsObject()["gameVersion"] = "2.0.0";
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadEnvironmentRevisionAsync(worldId, revision.Id));

        Assert.Contains("SHA-256 integrity verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingSchema2StateRevisionWithoutMetadataProofRemainsReadable()
    {
        var storage = new LocalWorldStorage(_root);
        var worldId = WorldId.New();
        var revision = CreateStateRevision(worldId);
        var revisionDirectory = GetStateRevisionDirectory(worldId, revision.Id);
        Directory.CreateDirectory(revisionDirectory);
        var payload = Encoding.UTF8.GetBytes("schema-2-state");

        await File.WriteAllTextAsync(
            Path.Combine(revisionDirectory, "revision.json"),
            JsonSerializer.Serialize(
                new
                {
                    documentType = "sharedworlds.state-revision",
                    schemaVersion = 2,
                    payload = revision
                },
                CamelCaseJson));
        await File.WriteAllBytesAsync(Path.Combine(revisionDirectory, "payload.bin"), payload);
        await File.WriteAllTextAsync(
            Path.Combine(revisionDirectory, "payload.sha256"),
            Convert.ToHexString(SHA256.HashData(payload)));

        Assert.Equal(revision, await storage.LoadStateRevisionAsync(worldId, revision.Id));

        await using var reopened = await storage.OpenRevisionAsync(worldId, revision.Id);
        using var copy = new MemoryStream();
        await reopened.CopyToAsync(copy);
        Assert.Equal(payload, copy.ToArray());
    }

    private string GetWorldMetadataPath(WorldId worldId)
        => Path.Combine(_root, "worlds", worldId.ToString(), "world.json");

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
            "Integrity World",
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
