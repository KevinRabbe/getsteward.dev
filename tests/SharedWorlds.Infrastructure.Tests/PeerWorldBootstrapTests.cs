using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-bootstrap-{Guid.NewGuid():N}");

    [Fact]
    public async Task Bootstrap_CreatesSameWorldIdentityForCanonicalMember_AndPersistsObservedFence()
    {
        var fixture = await CreateFixtureAsync();
        var installer = Installer(fixture);
        var exchange = new DirectBootstrapExchange(installer);
        var transfer = new PeerWorldBootstrapTransferService(
            fixture.Source,
            exchange);

        await transfer.BootstrapAsync(
            fixture.World.Id,
            fixture.TargetUser);

        var copied = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(copied);
        AssertEquivalentWorld(fixture.World, copied);
        Assert.Contains(
            copied.Members,
            member => member.Provider == fixture.TargetUser.Provider &&
                      member.ExternalId == fixture.TargetUser.ExternalId);

        var revision = await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.State.Id);
        Assert.Equal(fixture.State, revision);

        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
        Assert.Equal(fixture.World.PeerAuthority!.Generation, fence.Generation);
        Assert.Equal(fixture.World.PeerAuthority.Holder.Provider, fence.Holder.Provider);
        Assert.Equal(fixture.World.PeerAuthority.Holder.ExternalId, fence.Holder.ExternalId);
        Assert.Equal(fixture.State.Id, fence.StateRevisionId);

        await using var payload = await fixture.Target.OpenRevisionAsync(
            fixture.World.Id,
            fixture.State.Id);
        using var reader = new StreamReader(payload, Encoding.UTF8);
        Assert.Equal("bootstrap-state", await reader.ReadToEndAsync());
        Assert.Equal(1, exchange.TransferCount);
    }

    [Fact]
    public async Task Bootstrap_SenderRefusesIdentityNotInCanonicalMembership()
    {
        var fixture = await CreateFixtureAsync();
        var stranger = new UserIdentity("steam", "9999", "Stranger");
        var transfer = new PeerWorldBootstrapTransferService(
            fixture.Source,
            new DirectBootstrapExchange(
                new PeerWorldBootstrapInstaller(
                    fixture.Target,
                    stranger,
                    fixture.TargetFences)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.BootstrapAsync(
            fixture.World.Id,
            stranger));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_ReceiverRefusesOfferWhenLocalIdentityIsNotMember()
    {
        var fixture = await CreateFixtureAsync();
        var stranger = new UserIdentity("steam", "9999", "Stranger");
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            stranger,
            fixture.TargetFences);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_ReceiverRefusesOfferWithoutPersistentPeerAuthority()
    {
        var fixture = await CreateFixtureAsync();
        var offer = (await CreateOfferAsync(fixture)) with
        {
            World = fixture.World with { PeerAuthority = null }
        };
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(fixture).InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_CorruptedPayloadNeverPublishesWorldOrAuthorityFence()
    {
        var fixture = await CreateFixtureAsync();
        var installer = Installer(fixture);
        var offer = await CreateOfferAsync(fixture);
        var corrupted = Encoding.UTF8.GetBytes("bootstrap-Xtate");
        await using var payload = new MemoryStream(corrupted, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.State.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_RetryAfterImmutableDataInstalledCanPublishWorldAndFence()
    {
        var fixture = await CreateFixtureAsync();
        var offer = await CreateOfferAsync(fixture);

        await fixture.Target.StoreEnvironmentRevisionAsync(fixture.Environment);
        await using (var payload = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await fixture.Target.StoreRevisionAsync(fixture.State, payload);
        }

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
        await using var retryPayload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        var receipt = await Installer(fixture).InstallAsync(offer, retryPayload);

        Assert.Equal(fixture.World.Id, receipt.WorldId);
        var published = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(published);
        AssertEquivalentWorld(fixture.World, published);
        var fence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Observed, fence.State);
        Assert.Equal(fixture.State.Id, fence.StateRevisionId);
    }

    [Fact]
    public async Task Bootstrap_IsIdempotentWhenExactWorldAndObservedFenceAlreadyInstalled()
    {
        var fixture = await CreateFixtureAsync();
        var installer = Installer(fixture);
        var offer = await CreateOfferAsync(fixture);

        await using (var first = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await installer.InstallAsync(offer, first);
        }

        var originalFence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(originalFence);

        await using var retry = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);
        var receipt = await installer.InstallAsync(offer, retry);

        Assert.Equal(fixture.State.Id, receipt.StateRevisionId);
        var installed = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(installed);
        AssertEquivalentWorld(fixture.World, installed);
        var retryFence = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(retryFence);
        Assert.Equal(originalFence.WorldId, retryFence.WorldId);
        Assert.Equal(originalFence.Generation, retryFence.Generation);
        Assert.Equal(originalFence.StateRevisionId, retryFence.StateRevisionId);
        Assert.Equal(originalFence.State, retryFence.State);
        Assert.Equal(originalFence.Holder.Provider, retryFence.Holder.Provider);
        Assert.Equal(originalFence.Holder.ExternalId, retryFence.Holder.ExternalId);
    }

    [Fact]
    public async Task Bootstrap_ExactWorldRetryRepairsMissingObservedFence()
    {
        var fixture = await CreateFixtureAsync();
        var installer = Installer(fixture);
        var offer = await CreateOfferAsync(fixture);

        await using (var first = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await installer.InstallAsync(offer, first);
        }

        fixture.TargetFences.Records.Remove(fixture.World.Id);
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));

        await using var retry = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);
        await installer.InstallAsync(offer, retry);

        var healed = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(healed);
        Assert.Equal(PeerAuthorityFenceState.Observed, healed.State);
        Assert.Equal(fixture.World.PeerAuthority!.Generation, healed.Generation);
        Assert.Equal(fixture.State.Id, healed.StateRevisionId);
    }

    [Fact]
    public async Task Bootstrap_FenceWriteFailureNeverPublishesWorld()
    {
        var fixture = await CreateFixtureAsync();
        fixture.TargetFences.FailSave = true;
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<IOException>(() => Installer(fixture).InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
        Assert.NotNull(await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.State.Id));
    }

    [Fact]
    public async Task Bootstrap_RefusesDivergentExistingWorldWithSameIdentity()
    {
        var fixture = await CreateFixtureAsync();
        var divergent = fixture.World with { Name = "Different Local World" };
        await fixture.Target.SaveWorldAsync(divergent);
        var installer = Installer(fixture);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
            offer,
            payload));

        var unchanged = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(unchanged);
        AssertEquivalentWorld(divergent, unchanged);
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_RefusesConflictingPersistentAuthorityOnExistingWorld()
    {
        var fixture = await CreateFixtureAsync();
        var conflicting = fixture.World with
        {
            PeerAuthority = new WorldPeerAuthority(fixture.TargetUser, 2)
        };
        await fixture.Target.SaveWorldAsync(conflicting);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(fixture).InstallAsync(
            offer,
            payload));

        var unchanged = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(unchanged);
        AssertEquivalentWorld(conflicting, unchanged);
        Assert.Null(await fixture.TargetFences.LoadAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_RefusesToDowngradeExistingActiveFence()
    {
        var fixture = await CreateFixtureAsync();
        var authority = fixture.World.PeerAuthority!;
        fixture.TargetFences.Records[fixture.World.Id] = new PeerAuthorityFence(
            fixture.World.Id,
            fixture.TargetUser,
            checked(authority.Generation + 1),
            fixture.State.Id,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(fixture).InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
        var unchanged = await fixture.TargetFences.LoadAsync(fixture.World.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PeerAuthorityFenceState.Active, unchanged.State);
        Assert.Equal(checked(authority.Generation + 1), unchanged.Generation);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var source = new LocalWorldStorage(Path.Combine(_root, "source"));
        var target = new LocalWorldStorage(Path.Combine(_root, "target"));
        var targetFences = new TestFenceStore();
        var host = new UserIdentity("steam", "1001", "Host");
        var targetUser = new UserIdentity("steam", "1002", "Target");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
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
            host,
            manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            host,
            "fake",
            "bootstrap-package",
            environmentId);
        var world = new World(
            worldId,
            "Bootstrap World",
            "fake",
            [host, targetUser],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, 1)
        };

        await source.StoreEnvironmentRevisionAsync(environment);
        await using (var payload = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await source.StoreRevisionAsync(state, payload);
        }
        await source.SaveWorldAsync(world);
        return new Fixture(
            source,
            target,
            targetFences,
            world,
            environment,
            state,
            targetUser);
    }

    private static PeerWorldBootstrapInstaller Installer(Fixture fixture)
        => new(
            fixture.Target,
            fixture.TargetUser,
            fixture.TargetFences);

    private static async Task<PeerWorldRevisionOffer> CreateOfferAsync(Fixture fixture)
    {
        await using var payload = await fixture.Source.OpenRevisionAsync(
            fixture.World.Id,
            fixture.State.Id);
        using var memory = new MemoryStream();
        await payload.CopyToAsync(memory);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return new PeerWorldRevisionOffer(
            fixture.World,
            fixture.Environment,
            fixture.State,
            bytes.LongLength,
            hash);
    }

    private static void AssertEquivalentWorld(World expected, World actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.GameAdapterId, actual.GameAdapterId);
        Assert.Equal(expected.CurrentEnvironmentRevisionId, actual.CurrentEnvironmentRevisionId);
        Assert.Equal(expected.CurrentStateRevisionId, actual.CurrentStateRevisionId);
        Assert.Equal(expected.SharingMode, actual.SharingMode);
        Assert.Equal(expected.GameVersionPolicy, actual.GameVersionPolicy);
        Assert.Equal(expected.Visibility, actual.Visibility);
        Assert.Equal(expected.JoinPolicy, actual.JoinPolicy);
        Assert.Equal(expected.StartYourOwnPolicy, actual.StartYourOwnPolicy);
        Assert.Equal(expected.StartedFrom, actual.StartedFrom);
        Assert.Equal(expected.PeerAuthority?.Generation, actual.PeerAuthority?.Generation);
        Assert.Equal(expected.PeerAuthority?.Holder.Provider, actual.PeerAuthority?.Holder.Provider);
        Assert.Equal(expected.PeerAuthority?.Holder.ExternalId, actual.PeerAuthority?.Holder.ExternalId);
        Assert.Equal(expected.Members.Count, actual.Members.Count);
        for (var index = 0; index < expected.Members.Count; index++)
        {
            Assert.Equal(expected.Members[index].Provider, actual.Members[index].Provider);
            Assert.Equal(expected.Members[index].ExternalId, actual.Members[index].ExternalId);
            Assert.Equal(expected.Members[index].DisplayName, actual.Members[index].DisplayName);
        }

        Assert.Equal(expected.Checkpoints.Count, actual.Checkpoints.Count);
        for (var index = 0; index < expected.Checkpoints.Count; index++)
        {
            Assert.Equal(expected.Checkpoints[index].StateRevisionId, actual.Checkpoints[index].StateRevisionId);
            Assert.Equal(expected.Checkpoints[index].Name, actual.Checkpoints[index].Name);
            Assert.Equal(expected.Checkpoints[index].CreatedAt, actual.Checkpoints[index].CreatedAt);
            Assert.Equal(expected.Checkpoints[index].CreatedBy, actual.Checkpoints[index].CreatedBy);
        }
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
        TestFenceStore TargetFences,
        World World,
        EnvironmentRevision Environment,
        StateRevision State,
        UserIdentity TargetUser);

    private sealed class DirectBootstrapExchange : IPeerWorldBootstrapExchange
    {
        private readonly PeerWorldBootstrapInstaller _installer;

        public DirectBootstrapExchange(PeerWorldBootstrapInstaller installer)
            => _installer = installer;

        public int TransferCount { get; private set; }

        public async Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
            UserIdentity targetMember,
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

    private sealed class TestFenceStore : IPeerAuthorityFenceStore
    {
        public Dictionary<WorldId, PeerAuthorityFence> Records { get; } = [];
        public bool FailSave { get; set; }

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
            ArgumentNullException.ThrowIfNull(fence);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave)
            {
                throw new IOException("Synthetic durable fence write failure.");
            }

            Records[fence.WorldId] = fence;
            return Task.CompletedTask;
        }
    }
}
