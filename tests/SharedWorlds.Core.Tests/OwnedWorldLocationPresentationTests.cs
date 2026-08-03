using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class OwnedWorldLocationPresentationTests
{
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");

    [Fact]
    public async Task PresentedPublicationCarriesBoundedWorldIdentity()
    {
        var store = new CaptureStore();
        var registry = await CreateRegistryAsync(store);

        var decision = await registry.PublishLocationWithPresentationAsync(
            Owner,
            "pc-a",
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);

        var presentation = Assert.IsType<OwnedWorldPresentation>(
            decision.Current!.Presentation);
        Assert.Equal("Factory World", presentation.Name);
        Assert.Equal("factorio", presentation.GameAdapterId);
    }

    [Fact]
    public async Task LegacyPublicationRemainsValidWithoutInventingPresentation()
    {
        var store = new CaptureStore();
        var registry = await CreateRegistryAsync(store);

        var decision = await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow);

        Assert.Null(decision.Current!.Presentation);
    }

    [Fact]
    public async Task InvalidPresentationFailsBeforeStoreMutation()
    {
        var store = new CaptureStore();
        var registry = await CreateRegistryAsync(store);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.PublishLocationWithPresentationAsync(
                Owner,
                "pc-a",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                "World\nName",
                "factorio",
                DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            registry.PublishLocationWithPresentationAsync(
                Owner,
                "pc-a",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                new string('w', 201),
                "factorio",
                DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            registry.PublishLocationWithPresentationAsync(
                Owner,
                "pc-a",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                "World",
                new string('a', 129),
                DateTimeOffset.UtcNow));

        Assert.Null(store.LastDesired);
    }

    private static async Task<OwnedWorldLocationRegistry> CreateRegistryAsync(
        CaptureStore store)
    {
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            Owner,
            "pc-a",
            "Gaming PC",
            DateTimeOffset.UtcNow);
        store.LastDesired = null;
        return registry;
    }

    private sealed class CaptureStore : IOwnedWorldLocationStore
    {
        private OwnedInstallationRegistration? _installation;

        public OwnedWorldLocationClaim? LastDesired { get; set; }

        public Task<OwnedInstallationRegistration?> GetInstallationAsync(
            string installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                string.Equals(
                    _installation?.InstallationId,
                    installationId,
                    StringComparison.Ordinal)
                    ? _installation
                    : null);

        public Task RegisterInstallationAsync(
            OwnedInstallationRegistration registration,
            CancellationToken cancellationToken = default)
        {
            _installation = registration;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldLocationClaim>>([]);

        public Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
            OwnedWorldLocationClaim desired,
            RevisionId? expectedStateRevisionId,
            RevisionId? expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            LastDesired = desired;
            return Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Created,
                desired,
                "Created."));
        }

        public Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            string installationId,
            RevisionId expectedStateRevisionId,
            RevisionId expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
