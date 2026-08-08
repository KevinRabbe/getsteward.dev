using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldRevisionReplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-replication-{Guid.NewGuid():N}");

    [Fact]
    public async Task Transfer_ReplicatesExactCommittedDelta_AndAdvancesAuthorityOneGeneration()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldRevisionReplicaInstaller(
            fixture.Target,
            fixture.TargetUser);
        var exchange = new DirectExchange(installer);
        var transfer = new PeerWorldRevisionTransferService(fixture.Source, exchange);

        await transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser);

        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld);
        Assert.Equal(fixture.CommittedRevision.Id, targetWorld.CurrentStateRevisionId);
        Assert.NotNull(targetWorld.PeerAuthority);
        Assert.Equal((ulong)2, targetWorld.PeerAuthority.Generation);
        Assert.Equal(fixture.TargetUser.ExternalId, targetWorld.PeerAuthority.Holder.ExternalId);

        var sourceWorld = await fixture.Source.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(sourceWorld?.PeerAuthority);
        Assert.Equal((ulong)1, sourceWorld.PeerAuthority.Generation);
        Assert.Equal(fixture.SourceUser.ExternalId, sourceWorld.PeerAuthority.Holder.ExternalId);

        var targetRevision = await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id);
        Assert.Equal(fixture.CommittedRevision, targetRevision);

        await using var payload = await fixture.Target.OpenRevisionAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id);
        using var reader = new StreamReader(payload, Encoding.UTF8);
        Assert.Equal("committed-state", await reader.ReadToEndAsync());
        Assert.Equal(1, exchange.TransferCount);
    }

    [Fact]
    public async Task Transfer_IsIdempotent_WhenTargetAlreadyHasExactGenerationAndRevision()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldRevisionReplicaInstaller(
            fixture.Target,
            fixture.TargetUser);
        var exchange = new DirectExchange(installer);
        var transfer = new PeerWorldRevisionTransferService(fixture.Source, exchange);

        await transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser);
        await transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser);

        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld?.PeerAuthority);
        Assert.Equal(fixture.CommittedRevision.Id, targetWorld.CurrentStateRevisionId);
        Assert.Equal((ulong)2, targetWorld.PeerAuthority.Generation);
        Assert.Equal(2, exchange.TransferCount);
    }

    [Fact]
    public async Task Transfer_RejectsTargetWithoutActiveWorldBase()
    {
        var fixture = await CreateFixtureAsync(seedTargetBase: false);
        var installer = new PeerWorldRevisionReplicaInstaller(
            fixture.Target,
            fixture.TargetUser);
        var transfer = new PeerWorldRevisionTransferService(
            fixture.Source,
            new DirectExchange(installer));

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id));
    }

    [Fact]
    public async Task Transfer_RejectsCorruptedNetworkPayload_WithoutAdvancingHeadOrAuthority()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldRevisionReplicaInstaller(
            fixture.Target,
            fixture.TargetUser);
        var transfer = new PeerWorldRevisionTransferService(
            fixture.Source,
            new CorruptingExchange(installer));

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser));

        var targetWorld = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(targetWorld?.PeerAuthority);
        Assert.Equal(fixture.BaseRevision.Id, targetWorld.CurrentStateRevisionId);
        Assert.Equal((ulong)1, targetWorld.PeerAuthority.Generation);
        Assert.Equal(fixture.SourceUser.ExternalId, targetWorld.PeerAuthority.Holder.ExternalId);
        Assert.Null(await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id));
    }

    [Fact]
    public async Task Transfer_RejectsDivergentTargetHead_InsteadOfSkippingHistory()
    {
        var fixture = await CreateFixtureAsync();
        var divergent = new StateRevision(
            RevisionId.New(),
            fixture.World.Id,
            fixture.BaseRevision.Id,
            DateTimeOffset.UtcNow,
            fixture.TargetUser,
            fixture.World.GameAdapterId,
            "divergent-package",
            fixture.EnvironmentRevision.Id);
        await using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes("divergent")))
        {
            await fixture.Target.StoreRevisionAsync(divergent, bytes);
        }

        var targetWorld = (await fixture.Target.LoadWorldAsync(fixture.World.Id))! with
        {
            CurrentStateRevisionId = divergent.Id
        };
        await fixture.Target.SaveWorldAsync(targetWorld);

        var transfer = new PeerWorldRevisionTransferService(
            fixture.Source,
            new DirectExchange(new PeerWorldRevisionReplicaInstaller(
                fixture.Target,
                fixture.TargetUser)));

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser));

        var unchanged = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(divergent.Id, unchanged!.CurrentStateRevisionId);
        Assert.Equal((ulong)1, unchanged.PeerAuthority!.Generation);
    }

    [Fact]
    public async Task ReceiverRejectsAuthorityGenerationThatSkipsAhead()
    {
        var fixture = await CreateFixtureAsync();
        var transfer = new PeerWorldRevisionTransferService(
            fixture.Source,
            new MutatingAuthorityExchange(
                new PeerWorldRevisionReplicaInstaller(
                    fixture.Target,
                    fixture.TargetUser),
                generation: 3));

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser));

        var unchanged = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(unchanged?.PeerAuthority);
        Assert.Equal((ulong)1, unchanged.PeerAuthority.Generation);
        Assert.Equal(fixture.BaseRevision.Id, unchanged.CurrentStateRevisionId);
    }

    [Fact]
    public async Task Transfer_RejectsWrongPeerReceipt()
    {
        var fixture = await CreateFixtureAsync();
        var transfer = new PeerWorldRevisionTransferService(
            fixture.Source,
            new WrongReceiptExchange());

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.EnsureAvailableAsync(
            fixture.World.Id,
            fixture.CommittedRevision.Id,
            fixture.TargetUser));
    }

    private async Task<Fixture> CreateFixtureAsync(bool seedTargetBase = true)
    {
        Directory.CreateDirectory(_root);
        var source = new LocalWorldStorage(Path.Combine(_root, "source"));
        var target = new LocalWorldStorage(Path.Combine(_root, "target"));
        var sourceUser = new UserIdentity("steam", "1001", "Source");
        var targetUser = new UserIdentity("steam", "1002", "Target");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var baseRevisionId = RevisionId.New();
        var committedRevisionId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            1,
            "fake",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            sourceUser,
            manifest);
        var baseRevision = new StateRevision(
            baseRevisionId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            sourceUser,
            "fake",
            "base-package",
            environmentId);
        var committedRevision = new StateRevision(
            committedRevisionId,
            worldId,
            baseRevisionId,
            DateTimeOffset.UtcNow.AddSeconds(1),
            sourceUser,
            "fake",
            "committed-package",
            environmentId);
        var baseWorld = new World(
            worldId,
            "Peer World",
            "fake",
            [sourceUser, targetUser],
            environmentId,
            baseRevisionId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(sourceUser, 1)
        };
        var committedWorld = baseWorld with
        {
            CurrentStateRevisionId = committedRevisionId
        };

        await SeedRevisionAsync(source, environment, baseRevision, "base-state");
        await source.SaveWorldAsync(baseWorld);
        await using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes("committed-state")))
        {
            await source.StoreRevisionAsync(committedRevision, bytes);
        }
        await source.SaveWorldAsync(committedWorld);

        if (seedTargetBase)
        {
            await SeedRevisionAsync(target, environment, baseRevision, "base-state");
            await target.SaveWorldAsync(baseWorld);
        }

        return new Fixture(
            source,
            target,
            committedWorld,
            environment,
            baseRevision,
            committedRevision,
            sourceUser,
            targetUser);
    }

    private static async Task SeedRevisionAsync(
        LocalWorldStorage storage,
        EnvironmentRevision environment,
        StateRevision state,
        string payload)
    {
        await storage.StoreEnvironmentRevisionAsync(environment);
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await storage.StoreRevisionAsync(state, bytes);
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
        World World,
        EnvironmentRevision EnvironmentRevision,
        StateRevision BaseRevision,
        StateRevision CommittedRevision,
        UserIdentity SourceUser,
        UserIdentity TargetUser);

    private sealed class DirectExchange : IPeerWorldRevisionExchange
    {
        private readonly PeerWorldRevisionReplicaInstaller _installer;

        public DirectExchange(PeerWorldRevisionReplicaInstaller installer)
            => _installer = installer;

        public int TransferCount { get; private set; }

        public async Task<PeerWorldRevisionReceipt> TransferAsync(
            UserIdentity targetHost,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            TransferCount++;
            return await _installer.InstallAsync(
                offer,
                statePayload,
                cancellationToken);
        }
    }

    private sealed class CorruptingExchange : IPeerWorldRevisionExchange
    {
        private readonly PeerWorldRevisionReplicaInstaller _installer;

        public CorruptingExchange(PeerWorldRevisionReplicaInstaller installer)
            => _installer = installer;

        public async Task<PeerWorldRevisionReceipt> TransferAsync(
            UserIdentity targetHost,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            await statePayload.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            bytes[0] ^= 0x5A;
            await using var corrupted = new MemoryStream(bytes, writable: false);
            return await _installer.InstallAsync(
                offer,
                corrupted,
                cancellationToken);
        }
    }

    private sealed class MutatingAuthorityExchange : IPeerWorldRevisionExchange
    {
        private readonly PeerWorldRevisionReplicaInstaller _installer;
        private readonly ulong _generation;

        public MutatingAuthorityExchange(
            PeerWorldRevisionReplicaInstaller installer,
            ulong generation)
        {
            _installer = installer;
            _generation = generation;
        }

        public Task<PeerWorldRevisionReceipt> TransferAsync(
            UserIdentity targetHost,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            var mutated = offer with
            {
                World = offer.World with
                {
                    PeerAuthority = new WorldPeerAuthority(
                        targetHost,
                        _generation)
                }
            };
            return _installer.InstallAsync(
                mutated,
                statePayload,
                cancellationToken);
        }
    }

    private sealed class WrongReceiptExchange : IPeerWorldRevisionExchange
    {
        public Task<PeerWorldRevisionReceipt> TransferAsync(
            UserIdentity targetHost,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PeerWorldRevisionReceipt(
                offer.World.Id,
                RevisionId.New(),
                offer.PayloadLength,
                offer.PayloadSha256));
    }
}
