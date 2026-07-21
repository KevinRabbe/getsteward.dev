using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.Core.Tests.Abstractions;

public sealed class JoinCapabilityResultTests
{
    [Fact]
    public async Task ExistingAutomaticClientJoinFlagMapsToSupportedAutomatic()
    {
        IGameAdapter adapter = new AutomaticJoinAdapter();
        var result = await adapter.GetJoinCapabilityAsync(
            CreatePreparedWorld(),
            new HostConnection("127.0.0.1", 1234));

        Assert.Equal(JoinCapabilityKind.SupportedAutomatic, result.Kind);
        Assert.True(result.IsSupported);
        Assert.Null(result.Guidance);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task AdapterCanExposeGuidedManualJoinWithoutAddingGameSpecificCoreState()
    {
        IGameAdapter adapter = new GuidedJoinAdapter();
        var result = await adapter.GetJoinCapabilityAsync(
            CreatePreparedWorld(),
            new HostConnection("127.0.0.1", 8211));

        Assert.Equal(JoinCapabilityKind.SupportedGuidedManual, result.Kind);
        Assert.True(result.IsSupported);
        Assert.Equal("Connect to 127.0.0.1:8211 through the game server browser.", result.Guidance);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(JoinCapabilityKind.Unsupported)]
    [InlineData(JoinCapabilityKind.BlockedByEnvironment)]
    [InlineData(JoinCapabilityKind.BlockedByIdentityLimitation)]
    public void BlockedAndUnsupportedResultsAreNotSupported(JoinCapabilityKind kind)
    {
        var result = kind switch
        {
            JoinCapabilityKind.Unsupported => JoinCapabilityResult.Unsupported("unsupported"),
            JoinCapabilityKind.BlockedByEnvironment => JoinCapabilityResult.BlockedByEnvironment("environment"),
            JoinCapabilityKind.BlockedByIdentityLimitation => JoinCapabilityResult.BlockedByIdentityLimitation("identity"),
            _ => throw new InvalidOperationException("Unexpected test case.")
        };

        Assert.False(result.IsSupported);
    }

    private static PreparedWorld CreatePreparedWorld()
    {
        var installation = new GameInstallation("install", "C:/Game", "test");
        var manifest = new EnvironmentManifest(
            1,
            "test-adapter",
            "1.0",
            Array.Empty<EnvironmentComponent>(),
            new Dictionary<string, string>());
        return new PreparedWorld(installation, "C:/Work", manifest);
    }

    private abstract class BaseAdapter : IGameAdapter
    {
        public abstract GameAdapterCapabilities Capabilities { get; }
        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>(Array.Empty<GameInstallation>());

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>(Array.Empty<DetectedWorld>());

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreatePreparedWorld().Environment);

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PreparedWorld(installation, "C:/Work", requiredEnvironment));

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AutomaticJoinAdapter : BaseAdapter
    {
        public override GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticClientJoin;
    }

    private sealed class GuidedJoinAdapter : BaseAdapter
    {
        public override GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

        public Task<JoinCapabilityResult> GetJoinCapabilityAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => Task.FromResult(JoinCapabilityResult.SupportedGuidedManual(
                $"Connect to {host.Address}:{host.Port} through the game server browser."));
    }
}
