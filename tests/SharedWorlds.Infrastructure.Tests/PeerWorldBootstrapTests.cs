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
    public async Task Bootstrap_CreatesSameWorldIdentityForCanonicalMember()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            fixture.TargetUser);
        var exchange = new DirectBootstrapExchange(installer);
        var transfer = new PeerWorldBootstrapTransferService(
            fixture.Source,
            exchange);

        await transfer.BootstrapAsync(
            fixture.World.Id,
            fixture.TargetUser);

        var copied = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(copied);
        Assert.Equal(fixture.World.Id, copied.Id);
        Assert.Equal(fixture.World.CurrentStateRevisionId, copied.CurrentStateRevisionId);
        Assert.Equal(fixture.World.CurrentEnvironmentRevisionId, copied.CurrentEnvironmentRevisionId);
        Assert.Contains(
            copied.Members,
            member => member.Provider == fixture.TargetUser.Provider &&
                      member.ExternalId == fixture.TargetUser.ExternalId);

        var revision = await fixture.Target.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.State.Id);
        Assert.Equal(fixture.State, revision);

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
                new PeerWorldBootstrapInstaller(fixture.Target, stranger)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.BootstrapAsync(
            fixture.World.Id,
            stranger));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_ReceiverRefusesOfferWhenLocalIdentityIsNotMember()
    {
        var fixture = await CreateFixtureAsync();
        var stranger = new UserIdentity("steam", "9999", "Stranger");
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            stranger);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            offer,
            payload));

        Assert.Null(await fixture.Target.LoadWorldAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_CorruptedPayloadNeverPublishesWorld()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            fixture.TargetUser);
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
    }

    [Fact]
    public async Task Bootstrap_RetryAfterImmutableDataInstalledCanPublishWorld()
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
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            fixture.TargetUser);
        await using var retryPayload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        var receipt = await installer.InstallAsync(offer, retryPayload);

        Assert.Equal(fixture.World.Id, receipt.WorldId);
        var published = await fixture.Target.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(fixture.World, published);
    }

    [Fact]
    public async Task Bootstrap_IsIdempotentWhenExactWorldAlreadyInstalled()
    {
        var fixture = await CreateFixtureAsync();
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            fixture.TargetUser);
        var offer = await CreateOfferAsync(fixture);

        await using (var first = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await installer.InstallAsync(offer, first);
        }

        await using var retry = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);
        var receipt = await installer.InstallAsync(offer, retry);

        Assert.Equal(fixture.State.Id, receipt.StateRevisionId);
        Assert.Equal(fixture.World, await fixture.Target.LoadWorldAsync(fixture.World.Id));
    }

    [Fact]
    public async Task Bootstrap_RefusesDivergentExistingWorldWithSameIdentity()
    {
        var fixture = await CreateFixtureAsync();
        var divergent = fixture.World with { Name = "Different Local World" };
        await fixture.Target.SaveWorldAsync(divergent);
        var installer = new PeerWorldBootstrapInstaller(
            fixture.Target,
            fixture.TargetUser);
        var offer = await CreateOfferAsync(fixture);
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes("bootstrap-state"),
            writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
            offer,
            payload));

        Assert.Equal(divergent, await fixture.Target.LoadWorldAsync(fixture.World.Id));
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var source = new LocalWorldStorage(Path.Combine(_root, "source"));
        var target = new LocalWorldStorage(Path.Combine(_root, "target"));
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
            SharingMode = WorldSharingMode.Shared
        };

        await source.StoreEnvironmentRevisionAsync(environment);
        await using (var payload = new MemoryStream(
                         Encoding.UTF8.GetBytes("bootstrap-state"),
                         writable: false))
        {
            await source.StoreRevisionAsync(state, payload);
        }
        await source.SaveWorldAsync(world);
        return new Fixture(source, target, world, environment, state, targetUser);
    }

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
}
