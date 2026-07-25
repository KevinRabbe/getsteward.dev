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
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task AdapterWithoutAutomaticJoinMapsToUnsupported()
    {
        IGameAdapter adapter = new UnsupportedJoinAdapter();
        var result = await adapter.GetJoinCapabilityAsync(
            CreatePreparedWorld(),
            new HostConnection("127.0.0.1", 8211));

        Assert.Equal(JoinCapabilityKind.Unsupported, result.Kind);
        Assert.False(result.IsSupported);
        Assert.Contains("does not expose a validated Join path", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JoinCapabilityKind.Unsupported, "unsupported")]
    [InlineData(JoinCapabilityKind.BlockedByEnvironment, "environment")]
    [InlineData(JoinCapabilityKind.BlockedByIdentityLimitation, "identity")]
    public void BlockedAndUnsupportedResultsAreNotSupportedAndPreserveReason(
        JoinCapabilityKind kind,
        string reason)
    {
        var result = kind switch
        {
            JoinCapabilityKind.Unsupported => JoinCapabilityResult.Unsupported(reason),
            JoinCapabilityKind.BlockedByEnvironment => JoinCapabilityResult.BlockedByEnvironment(reason),
            JoinCapabilityKind.BlockedByIdentityLimitation => JoinCapabilityResult.BlockedByIdentityLimitation(reason),
            _ => throw new InvalidOperationException("Unexpected test case.")
        };

        Assert.False(result.IsSupported);
        Assert.Equal(reason, result.Reason);
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

    private sealed class UnsupportedJoinAdapter : BaseAdapter
    {
        public override GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
    }
}
