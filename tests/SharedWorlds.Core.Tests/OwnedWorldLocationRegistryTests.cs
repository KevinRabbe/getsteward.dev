using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class OwnedWorldLocationRegistryTests
{
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");
    private static readonly UserIdentity OtherOwner = new("steam", "owner-2", "Other");

    [Fact]
    public async Task InstallationCannotBeReassignedToAnotherOwner()
    {
        var store = new MemoryStore();
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            Owner,
            "pc-a",
            "Gaming PC",
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterInstallationAsync(
                OtherOwner,
                "pc-a",
                "Stolen name",
                DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Contains("different authenticated owner", exception.Message, StringComparison.Ordinal);
        Assert.Equal("owner-1", store.Installations["pc-a"].OwnerExternalId);
    }

    [Fact]
    public async Task RegisteredOwnerCanCreateLocationWithoutExpectedHead()
    {
        var store = new MemoryStore();
        var registry = await CreateRegistryAsync(store);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();

        var result = await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            DateTimeOffset.UtcNow);

        Assert.Equal(OwnedWorldLocationWriteResult.Created, result.Result);
        Assert.Equal(stateId, result.Current?.StateRevisionId);
        Assert.Equal(environmentId, result.Current?.EnvironmentRevisionId);
    }

    [Fact]
    public async Task ExistingLocationRequiresExactExpectedStateAndEnvironmentPair()
    {
        var store = new MemoryStore();
        var registry = await CreateRegistryAsync(store);
        var worldId = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            firstState,
            firstEnvironment,
            DateTimeOffset.UtcNow);

        var nextState = RevisionId.New();
        var nextEnvironment = RevisionId.New();
        var conflict = await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            nextState,
            nextEnvironment,
            DateTimeOffset.UtcNow.AddMinutes(1),
            expectedStateRevisionId: RevisionId.New(),
            expectedEnvironmentRevisionId: firstEnvironment);

        Assert.Equal(OwnedWorldLocationWriteResult.Conflict, conflict.Result);
        Assert.Equal(firstState, conflict.Current?.StateRevisionId);

        var updated = await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            nextState,
            nextEnvironment,
            DateTimeOffset.UtcNow.AddMinutes(2),
            expectedStateRevisionId: firstState,
            expectedEnvironmentRevisionId: firstEnvironment);

        Assert.Equal(OwnedWorldLocationWriteResult.Updated, updated.Result);
        Assert.Equal(nextState, updated.Current?.StateRevisionId);
    }

    [Fact]
    public async Task NewerTimestampCannotOverrideDifferentHeadWithoutExpectedMatch()
    {
        var store = new MemoryStore();
        var registry = await CreateRegistryAsync(store);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            DateTimeOffset.UtcNow);

        var result = await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow.AddYears(10));

        Assert.Equal(OwnedWorldLocationWriteResult.Conflict, result.Result);
        Assert.Equal(stateId, result.Current?.StateRevisionId);
    }

    [Fact]
    public async Task RemovingLocationRequiresExactExpectedHead()
    {
        var store = new MemoryStore();
        var registry = await CreateRegistryAsync(store);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await registry.PublishLocationAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            DateTimeOffset.UtcNow);

        var conflict = await registry.RemoveLocationAsync(
            Owner,
            "pc-a",
            worldId,
            RevisionId.New(),
            environmentId);
        Assert.Equal(OwnedWorldLocationWriteResult.Conflict, conflict.Result);

        var removed = await registry.RemoveLocationAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId);
        Assert.Equal(OwnedWorldLocationWriteResult.Updated, removed.Result);
        Assert.Null(removed.Current);
    }

    [Fact]
    public async Task UnregisteredInstallationCannotPublishLocation()
    {
        var registry = new OwnedWorldLocationRegistry(new MemoryStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishLocationAsync(
                Owner,
                "unknown-pc",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                DateTimeOffset.UtcNow));

        Assert.Contains("must be registered", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<OwnedWorldLocationRegistry> CreateRegistryAsync(MemoryStore store)
    {
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            Owner,
            "pc-a",
            "Gaming PC",
            DateTimeOffset.UtcNow);
        return registry;
    }

    private sealed class MemoryStore : IOwnedWorldLocationStore
    {
        public Dictionary<string, OwnedInstallationRegistration> Installations { get; } =
            new(StringComparer.Ordinal);

        private Dictionary<(WorldId WorldId, string Provider, string ExternalId, string InstallationId), OwnedWorldLocationClaim> Locations { get; } = [];

        public Task<OwnedInstallationRegistration?> GetInstallationAsync(
            string installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Installations.GetValueOrDefault(installationId));

        public Task RegisterInstallationAsync(
            OwnedInstallationRegistration registration,
            CancellationToken cancellationToken = default)
        {
            Installations[registration.InstallationId] = registration;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldLocationClaim>>(
                Locations.Values
                    .Where(location => location.WorldId == worldId)
                    .Where(location => string.Equals(location.OwnerProvider, ownerProvider, StringComparison.Ordinal))
                    .Where(location => string.Equals(location.OwnerExternalId, ownerExternalId, StringComparison.Ordinal))
                    .ToArray());

        public Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
            OwnedWorldLocationClaim desired,
            RevisionId? expectedStateRevisionId,
            RevisionId? expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            var key = (desired.WorldId, desired.OwnerProvider, desired.OwnerExternalId, desired.InstallationId);
            Locations.TryGetValue(key, out var current);

            if (current is null)
            {
                if (expectedStateRevisionId is not null || expectedEnvironmentRevisionId is not null)
                {
                    return Task.FromResult(new OwnedWorldLocationWriteDecision(
                        OwnedWorldLocationWriteResult.Conflict,
                        Current: null,
                        "The expected previous location no longer exists."));
                }

                Locations[key] = desired;
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Created,
                    desired,
                    "Created."));
            }

            if (current.StateRevisionId == desired.StateRevisionId &&
                current.EnvironmentRevisionId == desired.EnvironmentRevisionId)
            {
                Locations[key] = desired;
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.NoChange,
                    desired,
                    "The same immutable head was observed again."));
            }

            if (expectedStateRevisionId is null ||
                expectedEnvironmentRevisionId is null ||
                current.StateRevisionId != expectedStateRevisionId ||
                current.EnvironmentRevisionId != expectedEnvironmentRevisionId)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Conflict,
                    current,
                    "The persisted location changed before this update."));
            }

            Locations[key] = desired;
            return Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Updated,
                desired,
                "Updated."));
        }

        public Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            string installationId,
            RevisionId expectedStateRevisionId,
            RevisionId expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            var key = (worldId, ownerProvider, ownerExternalId, installationId);
            if (!Locations.TryGetValue(key, out var current))
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.NoChange,
                    Current: null,
                    "The location is already absent."));
            }

            if (current.StateRevisionId != expectedStateRevisionId ||
                current.EnvironmentRevisionId != expectedEnvironmentRevisionId)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Conflict,
                    current,
                    "The persisted location changed before removal."));
            }

            Locations.Remove(key);
            return Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Updated,
                Current: null,
                "Removed."));
        }
    }
}
