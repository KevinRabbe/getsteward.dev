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

    /// <summary>
    /// Captures a game-specific detected world into a portable state package during import.
    /// Core must not assume that a world is a single file or directory.
    /// </summary>
    Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default);

    Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether this device can reproduce the exact required environment without launching
    /// the game or advancing any World revision. Adapters own all game-specific verification rules.
    /// </summary>
    Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => Task.FromResult(EnvironmentVerificationReport.Unsupported(
            $"{DisplayName} does not implement environment verification yet."));

    /// <summary>
    /// Attempts only adapter-defined safe local repairs. A repair may provision local artifacts but
    /// must never mutate the World's immutable EnvironmentRevision or StateRevision heads.
    /// </summary>
    async Task<EnvironmentRepairResult> RepairEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);
        return new EnvironmentRepairResult(
            Changed: false,
            Verification: verification,
            Message: $"{DisplayName} does not implement automatic environment repair yet.");
    }

    Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default);

    Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches the prepared World as a local, non-shared session.
    /// This is distinct from hosting and must not expose a World for multiplayer by accident.
    /// </summary>
    Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default);

    Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default);

    Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes how this adapter can join a validated ready host on this device.
    /// The default preserves the existing AutomaticClientJoin capability; adapters may override
    /// this to expose guided manual Join or an explicit blocked/unsupported reason.
    /// </summary>
    Task<JoinCapabilityResult> GetJoinCapabilityAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(host);

        return Task.FromResult(
            Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin)
                ? JoinCapabilityResult.SupportedAutomatic()
                : JoinCapabilityResult.Unsupported(
                    $"{DisplayName} does not expose a validated Join path yet."));
    }

    /// <summary>
    /// Waits until the game-specific session represented by the handle has actually ended.
    /// Adapters own this because launchers may spawn or hand off to other processes.
    /// </summary>
    Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes adapter-owned prepared workspace resources.
    /// Discard means the workspace is no longer needed and should be removed where safe.
    /// PreserveForRecovery means potentially recoverable local state must remain intact.
    /// </summary>
    Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
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
    EnvironmentIsolation = 1 << 5,
    AutomaticLocalLaunch = 1 << 6
}

public enum JoinCapabilityKind
{
    SupportedAutomatic,
    SupportedGuidedManual,
    Unsupported,
    BlockedByEnvironment,
    BlockedByIdentityLimitation
}

public sealed record JoinCapabilityResult(
    JoinCapabilityKind Kind,
    string? Guidance = null,
    string? Reason = null)
{
    public bool IsSupported => Kind is
        JoinCapabilityKind.SupportedAutomatic or
        JoinCapabilityKind.SupportedGuidedManual;

    public static JoinCapabilityResult SupportedAutomatic()
        => new(JoinCapabilityKind.SupportedAutomatic);

    public static JoinCapabilityResult SupportedGuidedManual(string guidance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
        return new(JoinCapabilityKind.SupportedGuidedManual, Guidance: guidance);
    }

    public static JoinCapabilityResult Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(JoinCapabilityKind.Unsupported, Reason: reason);
    }

    public static JoinCapabilityResult BlockedByEnvironment(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(JoinCapabilityKind.BlockedByEnvironment, Reason: reason);
    }

    public static JoinCapabilityResult BlockedByIdentityLimitation(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(JoinCapabilityKind.BlockedByIdentityLimitation, Reason: reason);
    }
}

public enum PreparedWorldDisposition
{
    Discard,
    PreserveForRecovery
}

public sealed record GameInstallation(
    string Id,
    string RootPath,
    string Source,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record DetectedWorld(string Id, string DisplayName, string SourcePath);

public sealed record PreparedWorld(
    GameInstallation Installation,
    string WorkingDirectory,
    EnvironmentManifest Environment,
    string? DisplayName = null);

/// <summary>
/// A captured adapter state package. When <see cref="DeletePackageAfterStore"/> is true,
/// the package path is temporary and Core may delete it after durable storage succeeds or fails.
/// </summary>
public sealed record CapturedState(
    StatePackage Package,
    DateTimeOffset CapturedAt,
    bool DeletePackageAfterStore = false);

public sealed record StatePackage(string Id, string Path);
public sealed record GameSessionHandle(int ProcessId, DateTimeOffset StartedAt);
public sealed record HostConnection(string Address, int? Port = null, string? JoinToken = null);
