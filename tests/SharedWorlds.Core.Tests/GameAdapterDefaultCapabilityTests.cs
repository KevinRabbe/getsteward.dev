using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Tests;

public sealed class GameAdapterDefaultCapabilityTests
{
    [Fact]
    public async Task UnsupportedLaunchOperationsFailClosedWithoutAdapterStubs()
    {
        IGameAdapter adapter = new ImportOnlyAdapter();
        var installation = new GameInstallation("test", Path.GetTempPath(), "test");
        var environment = new EnvironmentManifest(
            1,
            adapter.Id,
            "1",
            [],
            new Dictionary<string, string>());
        var prepared = new PreparedWorld(installation, Path.GetTempPath(), environment);
        var session = new GameSessionHandle(1, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.LaunchLocalAsync(prepared));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.LaunchHostAsync(prepared));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.LaunchClientAsync(prepared, new HostConnection("127.0.0.1")));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.WaitForSessionEndAsync(session));

        var join = await adapter.GetJoinCapabilityAsync(
            prepared,
            new HostConnection("127.0.0.1"));
        Assert.Equal(JoinCapabilityKind.Unsupported, join.Kind);
        Assert.False(join.IsSupported);
    }

    private sealed class ImportOnlyAdapter : IGameAdapter
    {
        public string Id => "import-only";
        public string DisplayName => "Import Only";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

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

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
