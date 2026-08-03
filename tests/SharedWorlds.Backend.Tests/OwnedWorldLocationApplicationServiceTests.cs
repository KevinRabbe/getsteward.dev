using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Tests;

public sealed class OwnedWorldLocationApplicationServiceTests
{
    [Fact]
    public async Task RegistrationAndPublicationUseOnlySessionIdentityAndInstallation()
    {
        var store = new MemoryStore();
        var now = DateTimeOffset.UtcNow;
        var service = new OwnedWorldLocationApplicationService(store, () => now);
        var caller = Caller("steam", "owner-1", "pc-a");
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();

        var registration = await service.RegisterCurrentInstallationAsync(caller, "Gaming PC");
        var published = await service.PublishCurrentLocationAsync(
            caller,
            worldId,
            stateId,
            environmentId);

        Assert.Equal("steam", registration.OwnerProvider);
        Assert.Equal("owner-1", registration.OwnerExternalId);
        Assert.Equal("pc-a", registration.InstallationId);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, published.Result);
        Assert.Equal("owner-1", published.Current?.OwnerExternalId);
        Assert.Equal("pc-a", published.Current?.InstallationId);
    }

    [Fact]
    public async Task BringHereResolutionUsesCurrentSessionInstallationAsTarget()
    {
        var store = new MemoryStore();
        var service = new OwnedWorldLocationApplicationService(store, () => DateTimeOffset.UtcNow);
        var remote = Caller("steam", "owner-1", "pc-a");
        var target = Caller("steam", "owner-1", "pc-b");
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();

        await service.RegisterCurrentInstallationAsync(remote, "Remote PC");
        await service.PublishCurrentLocationAsync(remote, worldId, stateId, environmentId);
        await service.RegisterCurrentInstallationAsync(target, "Target PC");

        var decision = await service.ResolveBringHereAsync(target, worldId);

        Assert.Equal(BringHereAvailability.Available, decision.Availability);
        Assert.Equal("pc-a", decision.Source?.InstallationId);
    }

    [Fact]
    public async Task DifferentAuthenticatedOwnerCannotSeeAnotherOwnersLocation()
    {
        var store = new MemoryStore();
        var service = new OwnedWorldLocationApplicationService(store, () => DateTimeOffset.UtcNow);
        var owner = Caller("steam", "owner-1", "pc-a");
        var other = Caller("steam", "owner-2", "pc-b");
        var worldId = WorldId.New();

        await service.RegisterCurrentInstallationAsync(owner, "Owner PC");
        await service.PublishCurrentLocationAsync(
            owner,
            worldId,
            RevisionId.New(),
            RevisionId.New());
        await service.RegisterCurrentInstallationAsync(other, "Other PC");

        var decision = await service.ResolveBringHereAsync(other, worldId);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Null(decision.Source);
    }

    private static StewardAuthenticatedCaller Caller(
        string provider,
        string externalId,
        string installationId)
        => new(
            new VerifiedExternalIdentity(
                new ExternalIdentityRef(provider, externalId),
                externalId),
            installationId,
            StewardSessionId.New());

    private sealed class MemoryStore : IOwnedWorldLocationStore
    {
        private readonly Dictionary<string, OwnedInstallationRegistration> _installations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(WorldId, string, string, string), OwnedWorldLocationClaim> _locations = [];

        public Task<OwnedInstallationRegistration?> GetInstallationAsync(
            string installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_installations.GetValueOrDefault(installationId));

        public Task RegisterInstallationAsync(
            OwnedInstallationRegistration registration,
            CancellationToken cancellationToken = default)
        {
            _installations[registration.InstallationId] = registration;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldLocationClaim>>(
                _locations.Values
                    .Where(location => location.WorldId == worldId)
                    .Where(location => location.OwnerProvider == ownerProvider)
                    .Where(location => location.OwnerExternalId == ownerExternalId)
                    .ToArray());

        public Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
            OwnedWorldLocationClaim desired,
            RevisionId? expectedStateRevisionId,
            RevisionId? expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            var key = (
                desired.WorldId,
                desired.OwnerProvider,
                desired.OwnerExternalId,
                desired.InstallationId);
            _locations.TryGetValue(key, out var current);
            if (current is null)
            {
                if (expectedStateRevisionId is not null)
                {
                    return Task.FromResult(new OwnedWorldLocationWriteDecision(
                        OwnedWorldLocationWriteResult.Conflict,
                        null,
                        "Expected prior claim was absent."));
                }

                _locations[key] = desired;
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Created,
                    desired,
                    "Created."));
            }

            if (current.StateRevisionId == desired.StateRevisionId &&
                current.EnvironmentRevisionId == desired.EnvironmentRevisionId)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.NoChange,
                    current,
                    "No change."));
            }

            if (expectedStateRevisionId != current.StateRevisionId ||
                expectedEnvironmentRevisionId != current.EnvironmentRevisionId)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Conflict,
                    current,
                    "Conflict."));
            }

            _locations[key] = desired;
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
            if (!_locations.TryGetValue(key, out var current))
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.NoChange,
                    null,
                    "Absent."));
            }

            if (current.StateRevisionId != expectedStateRevisionId ||
                current.EnvironmentRevisionId != expectedEnvironmentRevisionId)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Conflict,
                    current,
                    "Conflict."));
            }

            _locations.Remove(key);
            return Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Updated,
                null,
                "Removed."));
        }
    }
}
