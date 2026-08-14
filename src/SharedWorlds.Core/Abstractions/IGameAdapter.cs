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

    /// <summary>
    /// Captures a game-specific detected world into a portable state package during import.
    /// Core must not assume that a world is a single file or directory.
    /// </summary>
    Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the game/adapter to create a new native World and return its initial exact environment
    /// plus a portable captured state. The opaque settings bag is adapter-owned; Core never interprets
    /// game-specific creation fields or writes native save formats itself.
    /// </summary>
    Task<NativeWorldCreationResult> CreateWorldAsync(
        GameInstallation installation,
        WorldCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated native World creation path.");
    }

    /// <summary>
    /// Legacy preparation boundary retained while adapters migrate to stable workspace identity.
    /// New Core lifecycle code calls the context-aware overload below. The default implementation of
    /// that overload delegates here, so an adapter can migrate without a product-wide flag day.
    /// </summary>
    Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares a writable runtime using an identity allocated by Core before any filesystem state is
    /// created. ManagedWorkingDirectory is the one SafeWorld-managed location offered for this
    /// workspace. An adapter that deliberately uses a native game location may ignore that path, but
    /// must return a native <see cref="PreparedWorld.RecoveryLocation"/> once migrated.
    /// </summary>
    Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        return PrepareEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);
    }

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
    /// Unsupported launch modes need no adapter stub; capability absence is the product contract.
    /// </summary>
    Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated Steward-managed local launch path.");
    }

    Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated Steward-managed host launch path.");
    }

    /// <summary>
    /// Requests the adapter-defined safe end of an already-running managed host session. Core never
    /// assumes that stopping a server means killing a process; an adapter may need to save, issue a
    /// game-specific shutdown command, wait for child processes, or restore temporary runtime inputs.
    /// The adapter must not return successfully until its own safe-stop boundary has completed.
    /// </summary>
    Task RequestHostStopAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated managed host-stop path.");
    }

    Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated Steward-managed Join launch path.");
    }

    /// <summary>
    /// Describes whether this adapter can automatically join a validated ready host on this device,
    /// including any environment/identity condition that blocks that otherwise-automatic path.
    /// First-release Join does not expose a separate manual-session lifecycle.
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
    /// Adapters own this because launchers may spawn or hand off to other processes. Adapters with
    /// no launch capability need no unreachable wait stub.
    /// </summary>
    Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"{DisplayName} does not expose a validated managed session observation path.");
    }

    /// <summary>
    /// Finalizes game-specific prepared-runtime resources. Filesystem ownership is separate from
    /// game finalization: an adapter may delete only storage that it owns itself. In particular, an
    /// adapter must never delete the root of a <see cref="PreparedWorldRecoveryLocationKind.SafeWorldManaged"/>
    /// runtime; Core proves the durable WorkspaceId and performs that deletion after adapter finalization.
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
    AutomaticLocalLaunch = 1 << 6,
    AutomaticHostStop = 1 << 7,
    NativeWorldCreation = 1 << 8
}

public enum JoinCapabilityKind
{
    SupportedAutomatic,
    Unsupported,
    BlockedByEnvironment,
    BlockedByIdentityLimitation
}

public sealed record JoinCapabilityResult(
    JoinCapabilityKind Kind,
    string? Reason = null)
{
    public bool IsSupported => Kind == JoinCapabilityKind.SupportedAutomatic;

    public static JoinCapabilityResult SupportedAutomatic()
        => new(JoinCapabilityKind.SupportedAutomatic);

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
    /// <summary>
    /// Finalize and discard adapter-owned runtime storage. This is used for legacy scratch or native
    /// locations whose deletion policy belongs to the adapter, not for a SafeWorld-managed root.
    /// </summary>
    Discard,

    /// <summary>
    /// Finalize game-specific resources for a runtime whose SafeWorld-managed root will be deleted by
    /// Core immediately after the adapter returns. The adapter must not delete that managed root.
    /// </summary>
    ReleaseForCoreManagedDiscard,

    /// <summary>
    /// Leave potentially recoverable runtime state intact.
    /// </summary>
    PreserveForRecovery
}

public sealed record GameInstallation(
    string Id,
    string RootPath,
    string Source,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record DetectedWorld(string Id, string DisplayName, string SourcePath);

/// <summary>
/// Generic creation request. Only DisplayName has cross-game meaning. Settings are deliberately
/// opaque adapter-owned key/value inputs so Core never accumulates game-specific creation rules.
/// </summary>
public sealed record WorldCreationRequest(
    string DisplayName,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Initial native World materialized by an adapter. The environment and state are persisted as the
/// first immutable Steward revisions; the captured state follows the same disposal contract as import.
/// </summary>
public sealed record NativeWorldCreationResult(
    EnvironmentManifest Environment,
    CapturedState State);

/// <summary>
/// Stable preparation identity allocated by Core before an adapter creates writable state.
/// ManagedWorkingDirectory is a runtime location derived from WorkspaceId and the current SafeWorld
/// storage layout; the directory itself is not durable identity.
/// </summary>
public sealed record PreparedWorldPreparationContext(
    WorkspaceId WorkspaceId,
    string ManagedWorkingDirectory);

public sealed record PreparedWorld(
    GameInstallation Installation,
    string WorkingDirectory,
    EnvironmentManifest Environment,
    string? DisplayName = null,
    PreparedWorldRecoveryLocation? RecoveryLocation = null);

/// <summary>
/// A captured adapter state package. Captured packages are adapter-owned disposable artifacts by
/// default, so Core deletes them after durable storage succeeds or fails. An adapter that deliberately
/// returns a borrowed or persistent path must opt out with <see cref="DeletePackageAfterStore"/> false.
/// </summary>
public sealed record CapturedState(
    StatePackage Package,
    DateTimeOffset CapturedAt,
    bool DeletePackageAfterStore = true);

public sealed record StatePackage(string Id, string Path);
public sealed record GameSessionHandle(int ProcessId, DateTimeOffset StartedAt);
public sealed record HostConnection(string Address, int? Port = null, string? JoinToken = null);
