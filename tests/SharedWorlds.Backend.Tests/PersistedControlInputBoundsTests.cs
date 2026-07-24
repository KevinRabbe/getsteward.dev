using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Backend.Tests;

public sealed class PersistedControlInputBoundsTests
{
    [Fact]
    public async Task OversizedWorldAdapterIdIsRejectedBeforeStoreUse()
    {
        var store = new RecordingMetadataStore();
        var service = new SharedWorldMetadataService(
            store,
            () => DateTimeOffset.UnixEpoch);
        var command = CreateWorldCommand(adapterId: new string('a', 257));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSharedWorldAsync(Caller(), command));

        Assert.Contains("Adapter ID", exception.Message, StringComparison.Ordinal);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task OversizedWorldDisplayNameIsRejectedBeforeStoreUse()
    {
        var store = new RecordingMetadataStore();
        var service = new SharedWorldMetadataService(
            store,
            () => DateTimeOffset.UnixEpoch);
        var command = CreateWorldCommand(displayName: new string('w', 513));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSharedWorldAsync(Caller(), command));

        Assert.Contains("World display name", exception.Message, StringComparison.Ordinal);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task WorldDisplayNameWithControlCharactersIsRejectedBeforeStoreUse()
    {
        var store = new RecordingMetadataStore();
        var service = new SharedWorldMetadataService(
            store,
            () => DateTimeOffset.UnixEpoch);
        var command = CreateWorldCommand(displayName: "World\nInjected");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSharedWorldAsync(Caller(), command));

        Assert.Contains("control characters", exception.Message, StringComparison.Ordinal);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task OversizedEnvironmentManifestIsRejectedBeforeStoreLookup()
    {
        var metadataStore = new ThrowingMetadataStore();
        var revisionStore = new ThrowingRevisionStore();
        var service = new SharedRevisionMetadataService(metadataStore, revisionStore);
        var manifest = new EnvironmentManifest(
            1,
            "factorio",
            new string('v', (4 * 1024 * 1024) + 1),
            [],
            new Dictionary<string, string>());

        var status = await service.PublishEnvironmentManifestAsync(
            Caller(),
            WorldId.New(),
            RevisionId.New(),
            manifest);

        Assert.Equal(PublishEnvironmentManifestStatus.InvalidManifest, status);
    }

    [Fact]
    public void OversizedIdentityProviderIsRejectedAtValueObjectBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ExternalIdentityRef(new string('p', 129), "external-id"));

        Assert.Contains("Identity provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedExternalIdentityIdIsRejectedAtValueObjectBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ExternalIdentityRef("steam", new string('i', 513)));

        Assert.Contains("External identity ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityControlCharactersAreRejectedAtValueObjectBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ExternalIdentityRef("steam", "player\ninjected"));

        Assert.Contains("control characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedVerifiedDisplayNameIsRejectedAtValueObjectBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new VerifiedExternalIdentity(
                new ExternalIdentityRef("steam", "76561198000000000"),
                new string('n', 513)));

        Assert.Contains("Verified display name", exception.Message, StringComparison.Ordinal);
    }

    private static CreateSharedWorldCommand CreateWorldCommand(
        string adapterId = "factorio",
        string displayName = "Bounded World")
        => new(
            WorldId.New(),
            adapterId,
            displayName,
            RevisionId.New(),
            CurrentEnvironmentRevisionId: null);

    private static VerifiedExternalIdentity Caller()
        => new(new ExternalIdentityRef("steam", "76561198000000000"), "Tester");

    private sealed class RecordingMetadataStore : ISharedWorldMetadataStore
    {
        public bool CreateCalled { get; private set; }

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            return Task.FromResult(true);
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingMetadataStore : ISharedWorldMetadataStore
    {
        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before World lookup.");

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before membership lookup.");

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");
    }

    private sealed class ThrowingRevisionStore : ISharedRevisionMetadataStore
    {
        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Invalid manifest must fail before persistence.");
    }
}
