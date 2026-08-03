using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.Tests;

public sealed class OwnedWorldLocationPresentationApplicationTests
{
    [Fact]
    public async Task PresentedPublicationUsesOnlyAuthenticatedSessionOwnerAndInstallation()
    {
        var store = new CaptureStore();
        var observedAt = DateTimeOffset.UtcNow;
        var service = new OwnedWorldLocationApplicationService(store, () => observedAt);
        var caller = new StewardAuthenticatedCaller(
            new VerifiedExternalIdentity(
                new ExternalIdentityRef("steam", "owner-1"),
                "Owner"),
            "pc-a",
            StewardSessionId.New());
        await service.RegisterCurrentInstallationAsync(caller, "Gaming PC");

        var decision = await service.PublishCurrentLocationWithPresentationAsync(
            caller,
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            "Factory World",
            "factorio");

        Assert.Equal(OwnedWorldLocationWriteResult.Created, decision.Result);
        Assert.Equal("owner-1", decision.Current!.OwnerExternalId);
        Assert.Equal("pc-a", decision.Current.InstallationId);
        Assert.Equal(observedAt, decision.Current.ObservedAt);
        var presentation = Assert.IsType<OwnedWorldPresentation>(
            decision.Current.Presentation);
        Assert.Equal("Factory World", presentation.Name);
        Assert.Equal("factorio", presentation.GameAdapterId);
    }

    private sealed class CaptureStore : IOwnedWorldLocationStore
    {
        private OwnedInstallationRegistration? _installation;

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
            => Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Created,
                desired,
                "Created."));

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
