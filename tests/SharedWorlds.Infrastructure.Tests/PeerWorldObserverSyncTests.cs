using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldObserverSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-observer-sync-{Guid.NewGuid():N}");

    [Fact]
    public async Task Synchronize_ReplaysMissingRevisionsOldestToNewest_WithoutGrantingAuthority()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 1,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0);
        var exchange = new DirectObserverExchange(Installer(fixture));
        var service = Service(fixture, exchange);

        var result = await service.SynchronizeAsync(
            fixture.World.Id,
            fixture.TargetUser,
            fixture.Revisions[0].Id);

        Assert.Equal(fixture.World.CurrentStateRevisionId, result.CurrentStateRevisionId);
        Assert.Equal(2, exchange.TransferCount);
        Assert.Equal(
            [fixture.Revisions[1].Id, fixture.Revisions[2].Id],
            exchange.TransferredRevisions);

        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld);
        Assert.Equal(fixture.Revisions[2].Id, targetWorld.CurrentStateRevisionId);
        Assert.NotNull(targetWorld.PeerAuthority);
        Assert.Equal(fixture.HostUser.ExternalId, targetWorld.PeerAuthority.Holder.ExternalId);
        Assert.Equal((ulong)1, targetWorld.PeerAuthority.Generation);
        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
        Assert.Equal(fixture.HostUser.ExternalId, fence.Holder.ExternalId);
        Assert.Equal((ulong)1, fence.Generation);
        Assert.Equal(fixture.Revisions[2].Id, fence.StateRevisionId);
    }

    [Fact]
    public async Task Synchronize_ReturningMemberCanCatchUpAcrossPastHostGenerations()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 3,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0,
            targetObservedHolderIsCurrentHost: false);
        var exchange = new DirectObserverExchange(Installer(fixture));
        var service = Service(fixture, exchange);

        await service.SynchronizeAsync(
            fixture.World.Id,
            fixture.TargetUser,
            fixture.Revisions[0].Id);

        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld?.PeerAuthority);
        Assert.Equal((ulong)3, targetWorld.PeerAuthority.Generation);
        Assert.Equal(fixture.HostUser.ExternalId, targetWorld.PeerAuthority.Holder.ExternalId);
        Assert.Equal(fixture.Revisions[2].Id, targetWorld.CurrentStateRevisionId);
        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
        Assert.Equal((ulong)3, fence.Generation);
        Assert.Equal(fixture.HostUser.ExternalId, fence.Holder.ExternalId);
    }

    [Fact]
    public async Task Synchronize_SameStateStillRefreshesObservedAuthorityGenerationAndMetadata()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 4,
            targetAuthorityGeneration: 2,
            targetRevisionIndex: 2,
            targetObservedHolderIsCurrentHost: false);
        var exchange = new DirectObserverExchange(Installer(fixture));
        var service = Service(fixture, exchange);

        await service.SynchronizeAsync(
            fixture.World.Id,
            fixture.TargetUser,
            fixture.Revisions[2].Id);

        Assert.Equal(1, exchange.TransferCount);
        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld?.PeerAuthority);
        Assert.Equal((ulong)4, targetWorld.PeerAuthority.Generation);
        Assert.Equal(fixture.HostUser.ExternalId, targetWorld.PeerAuthority.Holder.ExternalId);
        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal((ulong)4, fence.Generation);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
    }

    [Fact]
    public async Task Synchronize_RejectsDivergentTargetHeadInsteadOfMerging()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 1,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0);
        var divergent = RevisionId.New();
        var service = Service(
            fixture,
            new DirectObserverExchange(Installer(fixture)));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SynchronizeAsync(
            fixture.World.Id,
            fixture.TargetUser,
            divergent));

        var target = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(fixture.Revisions[0].Id, target!.CurrentStateRevisionId);
    }

    [Fact]
    public async Task Synchronize_RejectsUnboundedHistoryWalk()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 1,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0);
        var service = new PeerWorldObserverSyncService(
            fixture.Source,
            fixture.HostUser,
            fixture.SourceFences,
            new DirectObserverExchange(Installer(fixture)),
            maximumRevisionSteps: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SynchronizeAsync(
            fixture.World.Id,
            fixture.TargetUser,
            fixture.Revisions[0].Id));
    }

    [Fact]
    public async Task Installer_CorruptedPayloadDoesNotAdvanceObservedFenceOrWorldHead()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 1,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0);
        var offer = await OfferForAsync(fixture, fixture.Revisions[1]);
        var bytes = Encoding.UTF8.GetBytes("state-1");
        bytes[0] ^= 0x7F;
        await using var corrupted = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Installer(fixture).InstallAsync(offer, corrupted));

        var target = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(fixture.Revisions[0].Id, target!.CurrentStateRevisionId);
        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(fixture.Revisions[0].Id, fence.StateRevisionId);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
    }

    [Fact]
    public async Task Installer_RejectsActiveLocalFenceBecauseObserverPathCannotCreateWriter()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 1,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0);
        fixture.TargetFences.Records[fixture.World.Id] = new PeerAuthorityFence(
            fixture.World.Id,
            fixture.TargetUser,
            1,
            fixture.Revisions[0].Id,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);
        var offer = await OfferForAsync(fixture, fixture.Revisions[1]);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("state-1"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Installer(fixture).InstallAsync(offer, payload));
    }

    [Fact]
    public async Task Installer_RetryResumesFromObservedFenceWrittenBeforeWorldHead()
    {
        var fixture = await CreateFixtureAsync(
            sourceAuthorityGeneration: 2,
            targetAuthorityGeneration: 1,
            targetRevisionIndex: 0,
            targetObservedHolderIsCurrentHost: false);
        var offer = await OfferForAsync(fixture, fixture.Revisions[1]);

        await fixture.TargetFences.SaveAsync(new PeerAuthorityFence(
            fixture.World.Id,
            fixture.HostUser,
            2,
            fixture.Revisions[1].Id,
            PeerAuthorityFenceState.Observed,
            DateTimeOffset.UtcNow));
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("state-1"),
            writable: false);

        await Installer(fixture).InstallAsync(offer, payload);

        var target = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(fixture.Revisions[1].Id, target!.CurrentStateRevisionId);
        Assert.Equal((ulong)2, target.PeerAuthority!.Generation);
    }

    private static PeerWorldObserverSyncService Service(
        Fixture fixture,
        IPeerWorldObserverSyncExchange exchange)
        => new(
            fixture.Source,
            fixture.HostUser,
            fixture.SourceFences,
            exchange);

    private static PeerWorldObserverSyncInstaller Installer(Fixture fixture)
        => new(
            fixture.Target,
            fixture.TargetUser,
            fixture.TargetFences);

    private async Task<Fixture> CreateFixtureAsync(
        ulong sourceAuthorityGeneration,
        ulong targetAuthorityGeneration,
        int targetRevisionIndex,
        bool targetObservedHolderIsCurrentHost = true)
    {
        Directory.CreateDirectory(_root);
        var source = new LocalWorldStorage(Path.Combine(_root, Guid.NewGuid().ToString("N"), "source"));
        var target = new LocalWorldStorage(Path.Combine(_root, Guid.NewGuid().ToString("N"), "target"));
        var sourceFences = new InMemoryFenceStore();
        var targetFences = new InMemoryFenceStore();
        var oldHost = new UserIdentity("steam", "1000", "Old Host");
        var host = new UserIdentity("steam", "1001", "Current Host");
        var targetUser = new UserIdentity("steam", "1002", "Returning Member");
        var worldId = WorldId.New();
        var environment = new EnvironmentRevision(
            RevisionId.New(),
            worldId,
            null,
            DateTimeOffset.UtcNow,
            host,
            new EnvironmentManifest(
                1,
                "fake",
                "1.0.0",
                [],
                new Dictionary<string, string>()));

        var revisions = new List<StateRevision>();
        RevisionId? parent = null;
        for (var index = 0; index < 3; index++)
        {
            var revision = new StateRevision(
                RevisionId.New(),
                worldId,
                parent,
                DateTimeOffset.UtcNow.AddSeconds(index),
                index == 0 ? oldHost : host,
                "fake",
                $"package-{index}",
                environment.Id);
            revisions.Add(revision);
            parent = revision.Id;
        }

        var sourceWorld = new World(
            worldId,
            "Observer World",
            "fake",
            [oldHost, host, targetUser],
            environment.Id,
            revisions[^1].Id)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, sourceAuthorityGeneration)
        };
        await source.StoreEnvironmentRevisionAsync(environment);
        foreach (var (revision, index) in revisions.Select((revision, index) => (revision, index)))
        {
            await using var payload = new MemoryStream(
                Encoding.UTF8.GetBytes($"state-{index}"),
                writable: false);
            await source.StoreRevisionAsync(revision, payload);
        }
        await source.SaveWorldAsync(sourceWorld);
        await sourceFences.SaveAsync(new PeerAuthorityFence(
            worldId,
            host,
            sourceAuthorityGeneration,
            revisions[^1].Id,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow));

        var targetAuthorityHolder = targetObservedHolderIsCurrentHost ? host : oldHost;
        var targetWorld = sourceWorld with
        {
            CurrentStateRevisionId = revisions[targetRevisionIndex].Id,
            PeerAuthority = new WorldPeerAuthority(
                targetAuthorityHolder,
                targetAuthorityGeneration)
        };
        await target.StoreEnvironmentRevisionAsync(environment);
        for (var index = 0; index <= targetRevisionIndex; index++)
        {
            await using var payload = new MemoryStream(
                Encoding.UTF8.GetBytes($"state-{index}"),
                writable: false);
            await target.StoreRevisionAsync(revisions[index], payload);
        }
        await target.SaveWorldAsync(targetWorld);
        await targetFences.SaveAsync(new PeerAuthorityFence(
            worldId,
            targetAuthorityHolder,
            targetAuthorityGeneration,
            revisions[targetRevisionIndex].Id,
            PeerAuthorityFenceState.Observed,
            DateTimeOffset.UtcNow));

        return new Fixture(
            source,
            target,
            sourceFences,
            targetFences,
            sourceWorld,
            environment,
            revisions,
            oldHost,
            host,
            targetUser);
    }

    private static async Task<PeerWorldRevisionOffer> OfferForAsync(
        Fixture fixture,
        StateRevision revision)
    {
        var stepWorld = fixture.World with
        {
            CurrentStateRevisionId = revision.Id,
            CurrentEnvironmentRevisionId = revision.EnvironmentRevisionId
        };
        await using var payload = await fixture.Source.OpenRevisionAsync(
            fixture.World.Id,
            revision.Id);
        using var memory = new MemoryStream();
        await payload.CopyToAsync(memory);
        var bytes = memory.ToArray();
        return new PeerWorldRevisionOffer(
            stepWorld,
            fixture.Environment,
            revision,
            bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Fixture(
        LocalWorldStorage Source,
        LocalWorldStorage Target,
        InMemoryFenceStore SourceFences,
        InMemoryFenceStore TargetFences,
        World World,
        EnvironmentRevision Environment,
        List<StateRevision> Revisions,
        UserIdentity OldHost,
        UserIdentity HostUser,
        UserIdentity TargetUser);

    private sealed class InMemoryFenceStore : IPeerAuthorityFenceStore
    {
        public Dictionary<WorldId, PeerAuthorityFence> Records { get; } = [];

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.TryGetValue(worldId, out var fence);
            return Task.FromResult(fence);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records[fence.WorldId] = fence;
            return Task.CompletedTask;
        }
    }

    private sealed class DirectObserverExchange : IPeerWorldObserverSyncExchange
    {
        private readonly PeerWorldObserverSyncInstaller _installer;

        public DirectObserverExchange(PeerWorldObserverSyncInstaller installer)
            => _installer = installer;

        public int TransferCount { get; private set; }
        public List<RevisionId> TransferredRevisions { get; } = [];

        public async Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
            UserIdentity targetMember,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            TransferCount++;
            TransferredRevisions.Add(offer.StateRevision.Id);
            return await _installer.InstallAsync(
                offer,
                statePayload,
                cancellationToken);
        }
    }
}
