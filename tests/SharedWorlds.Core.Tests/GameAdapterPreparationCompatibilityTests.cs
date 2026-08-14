using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Tests;

public sealed class GameAdapterPreparationCompatibilityTests
{
    [Fact]
    public async Task ContextAwarePreparationFallsBackToLegacyAdapterDuringMigration()
    {
        IGameAdapter adapter = new LegacyPreparationAdapter();
        var installation = new GameInstallation("install", "game-root", "test");
        var environment = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: "1",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
        var preparation = new PreparedWorldPreparationContext(
            WorkspaceId.New(),
            Path.Combine("managed", "offered-workspace"));

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment,
            preparation);

        Assert.Equal("legacy-runtime-workspace", prepared.WorkingDirectory);
        Assert.Null(prepared.RecoveryLocation);
    }

    private sealed class LegacyPreparationAdapter : IGameAdapter
    {
        public string Id => "legacy";
        public string DisplayName => "Legacy";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

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
            => Task.FromResult(new PreparedWorld(
                installation,
                "legacy-runtime-workspace",
                requiredEnvironment));

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
