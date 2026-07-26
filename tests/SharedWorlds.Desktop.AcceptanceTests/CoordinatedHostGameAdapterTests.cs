using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class CoordinatedHostGameAdapterTests
{
    [Fact]
    public async Task ManagedHostPublishesStartingReadyAndEndedWithoutChangingGameHandle()
    {
        var worldId = WorldId.New();
        var inner = new EndpointAdapter();
        var coordinator = new RecordingCoordinator();
        var adapter = new CoordinatedHostGameAdapter(inner, inner, coordinator, worldId);
        var prepared = new PreparedWorld(
            new GameInstallation("test", "C:\\Game", "test"),
            "C:\\Workspace",
            new EnvironmentManifest(1, "test", "1", [], new Dictionary<string, string>()));

        var session = await adapter.LaunchHostAsync(prepared);
        await adapter.WaitForSessionEndAsync(session);

        Assert.Equal(42, session.ProcessId);
        Assert.Equal(
            ["starting", "ready:34197:session-secret", "ended"],
            coordinator.Events);
    }

    [Fact]
    public async Task PresenceFailureNeverTurnsSuccessfulGameLaunchIntoFailure()
    {
        var worldId = WorldId.New();
        var inner = new EndpointAdapter();
        var coordinator = new ThrowingPresenceCoordinator();
        var adapter = new CoordinatedHostGameAdapter(inner, inner, coordinator, worldId);
        var prepared = new PreparedWorld(
            new GameInstallation("test", "C:\\Game", "test"),
            "C:\\Workspace",
            new EnvironmentManifest(1, "test", "1", [], new Dictionary<string, string>()));

        var session = await adapter.LaunchHostAsync(prepared);
        await adapter.WaitForSessionEndAsync(session);

        Assert.Equal(42, session.ProcessId);
        Assert.True(inner.Waited);
    }

    private sealed class EndpointAdapter : IGameAdapter, IManagedHostEndpointProvider
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.AutomaticClientJoin;
        public bool Waited { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GameSessionHandle(42, DateTimeOffset.UtcNow));

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Waited = true;
            return Task.CompletedTask;
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ManagedHostEndpoint? GetManagedHostEndpoint(GameSessionHandle session)
            => new(34197, "session-secret");
    }

    private sealed class RecordingCoordinator : IWorldSessionCoordinator
    {
        public List<string> Events { get; } = [];

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkHostStartingAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            Events.Add("starting");
            return Task.CompletedTask;
        }

        public Task MarkHostReadyAsync(
            WorldId worldId,
            ManagedHostEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"ready:{endpoint.Port}:{endpoint.JoinToken}");
            return Task.CompletedTask;
        }

        public Task EndHostPresenceAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            Events.Add("ended");
            return Task.CompletedTask;
        }

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingPresenceCoordinator : IWorldSessionCoordinator
    {
        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkHostStartingAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new IOException("presence unavailable");

        public Task MarkHostReadyAsync(
            WorldId worldId,
            ManagedHostEndpoint endpoint,
            CancellationToken cancellationToken = default)
            => throw new IOException("presence unavailable");

        public Task EndHostPresenceAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new IOException("presence unavailable");

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
