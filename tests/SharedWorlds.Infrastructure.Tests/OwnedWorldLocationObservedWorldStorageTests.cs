using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class OwnedWorldLocationObservedWorldStorageTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulCanonicalMutationsSignalExactlyOnce()
    {
        var inner = new StubWorldStorage();
        var signals = 0;
        var storage = new OwnedWorldLocationObservedWorldStorage(
            inner,
            () => signals++);
        var fixture = CreateFixture();

        await storage.SaveWorldAsync(fixture.World);
        await storage.StoreEnvironmentRevisionAsync(fixture.Environment);
        await using (var payload = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false))
        {
            await storage.StoreRevisionAsync(fixture.State, payload);
        }

        inner.DeleteResult = false;
        Assert.False(await storage.DeleteWorldAsync(fixture.World.Id));
        inner.DeleteResult = true;
        Assert.True(await storage.DeleteWorldAsync(fixture.World.Id));

        inner.EvictResult = false;
        Assert.False(await storage.EvictRevisionPayloadAsync(
            fixture.World.Id,
            fixture.State.Id));
        inner.EvictResult = true;
        Assert.True(await storage.EvictRevisionPayloadAsync(
            fixture.World.Id,
            fixture.State.Id));

        Assert.Equal(5, signals);
    }

    [Fact]
    public async Task ReadsNeverSignal()
    {
        var inner = new StubWorldStorage();
        var signals = 0;
        var storage = new OwnedWorldLocationObservedWorldStorage(
            inner,
            () => signals++);
        var fixture = CreateFixture();

        await storage.LoadWorldAsync(fixture.World.Id);
        await storage.ListWorldsAsync();
        await storage.LoadEnvironmentRevisionAsync(
            fixture.World.Id,
            fixture.Environment.Id);
        await storage.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.State.Id);
        await using (await storage.OpenRevisionAsync(
                         fixture.World.Id,
                         fixture.State.Id))
        {
        }

        await storage.IsRevisionPayloadAvailableAsync(
            fixture.World.Id,
            fixture.State.Id);
        await storage.GetRevisionPayloadSizeAsync(
            fixture.World.Id,
            fixture.State.Id);

        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task FailedMutationCannotEmitAFalseChangeSignal()
    {
        var inner = new StubWorldStorage
        {
            SaveWorldException = new IOException("Injected storage failure.")
        };
        var signals = 0;
        var storage = new OwnedWorldLocationObservedWorldStorage(
            inner,
            () => signals++);
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveWorldAsync(fixture.World));

        Assert.Equal(0, signals);
    }

    private static Fixture CreateFixture()
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "test.adapter",
                GameVersion: "1.0.0",
                Components: Array.Empty<EnvironmentComponent>(),
                Configuration: new Dictionary<string, string>()));
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            AdapterId: "test.adapter",
            StatePackageId: $"state:{stateId}",
            EnvironmentRevisionId: environmentId);
        var world = new World(
            worldId,
            "Observed World",
            "test.adapter",
            Array.Empty<UserIdentity>(),
            environmentId,
            stateId);
        return new(world, state, environment);
    }

    private sealed record Fixture(
        World World,
        StateRevision State,
        EnvironmentRevision Environment);

    private sealed class StubWorldStorage : IWorldStorage
    {
        public Exception? SaveWorldException { get; init; }
        public bool DeleteResult { get; set; }
        public bool EvictResult { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
            => SaveWorldException is null
                ? Task.CompletedTask
                : Task.FromException(SaveWorldException);

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(null);

        public Task<IReadOnlyList<World>> ListWorldsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<World>>(Array.Empty<World>());

        public Task<bool> DeleteWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DeleteResult);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(null);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(
                new MemoryStream(new byte[] { 1, 2, 3 }, writable: false));

        public Task<bool> IsRevisionPayloadAvailableAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<long?> GetRevisionPayloadSizeAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(3);

        public Task<bool> EvictRevisionPayloadAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EvictResult);
    }
}
