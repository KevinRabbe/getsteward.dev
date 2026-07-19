using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Core knows what must happen; the adapter knows how a specific game makes it happen.
/// Adapters may internally use Steam, CurseForge, Modrinth, Prism, filesystem rules, or anything else.
/// </summary>
public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    GameAdapterCapabilities Capabilities { get; }

    Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default);

    Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default);

    Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default);

    Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default);

    Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default);

    Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default);

    Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default);
}

[Flags]
public enum GameAdapterCapabilities
{
    None = 0,
    Mods = 1 << 0,
    AutomaticHostLaunch = 1 << 1,
    AutomaticClientJoin = 1 << 2,
    ExactGameVersion = 1 << 3,
    ExactModVersions = 1 << 4,
    EnvironmentIsolation = 1 << 5
}

public sealed record GameInstallation(string Id, string RootPath, string Source);
public sealed record DetectedWorld(string Id, string DisplayName, string SourcePath);
public sealed record PreparedWorld(string WorkingDirectory, EnvironmentManifest Environment);
public sealed record CapturedState(StatePackage Package, DateTimeOffset CapturedAt);
public sealed record StatePackage(string Id, string Path);
public sealed record GameSessionHandle(int ProcessId, DateTimeOffset StartedAt);
public sealed record HostConnection(string Address, int? Port = null, string? JoinToken = null);
