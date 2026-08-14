using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorkspaceRecoveryStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-recovery-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RecoveryRecord_RoundTripsAndCanBeRemoved()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecord() with
        {
            RecoveryLocation = PreparedWorldRecoveryLocation.Managed()
        };

        await store.SaveAsync(record);
        var records = await store.ListAsync();

        Assert.Equal(record, Assert.Single(records));

        await store.RemoveAsync(record.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RecoveryRecord_IsStoredInProtectedEnvelope()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecord();

        await store.SaveAsync(record);

        await using var stream = File.OpenRead(GetPath(record.Id));
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal("sharedworlds.workspace-recovery", root.GetProperty("documentType").GetString());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("integrityVersion").GetInt32());
        Assert.Equal(64, root.GetProperty("contentSha256").GetString()!.Length);
    }

    [Fact]
    public async Task LegacySchema1RecoveryRecordWithoutLocationDescriptorRemainsReadable()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecord();
        Directory.CreateDirectory(Path.Combine(_root, "recovery"));

        var payload = JsonSerializer.SerializeToNode(record, CamelCaseJson)!.AsObject();
        Assert.True(payload.Remove("recoveryLocation"));
        await File.WriteAllTextAsync(
            GetPath(record.Id),
            JsonSerializer.Serialize(
                new
                {
                    documentType = "sharedworlds.workspace-recovery",
                    schemaVersion = 1,
                    payload
                },
                CamelCaseJson));

        var loaded = Assert.Single(await store.ListAsync());

        Assert.Equal(record, loaded);
        Assert.Null(loaded.RecoveryLocation);
    }

    [Fact]
    public async Task InvalidRecoveryLocationIsRejectedBeforePersistence()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecord() with
        {
            RecoveryLocation = PreparedWorldRecoveryLocation.Managed() with
            {
                SchemaVersion = 999
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(record));
        Assert.False(File.Exists(GetPath(record.Id)));
    }

    [Fact]
    public async Task ProtectedRecoveryRecordWithoutIntegrityProofFailsClosed()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var record = CreateRecord();
        await store.SaveAsync(record);
        var path = GetPath(record.Id);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("integrityVersion"));
        Assert.True(root.Remove("contentSha256"));
        await File.WriteAllTextAsync(path, root.ToJsonString(CamelCaseJson));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListAsync());

        Assert.Contains("missing its required integrity proof", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryRecord_StatusUpdate_ReplacesMutableRegistryEntry()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var now = DateTimeOffset.UtcNow;
        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "factorio",
            Path.Combine(_root, "workspace"),
            new UserIdentity("local", "tester"),
            now,
            now,
            WorkspaceRecoveryStatus.Active);

        await store.SaveAsync(record);
        await store.SaveAsync(record with
        {
            Status = WorkspaceRecoveryStatus.RecoveryPending,
            UpdatedAt = now.AddMinutes(1),
            Reason = "Injected failure"
        });

        var loaded = Assert.Single(await store.ListAsync());
        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, loaded.Status);
        Assert.Equal("Injected failure", loaded.Reason);
    }

    private WorkspaceRecoveryRecord CreateRecord()
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
            WorkspaceRecoveryStatus.Active);
    }

    private string GetPath(WorkspaceId id)
        => Path.Combine(_root, "recovery", $"{id}.json");

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
