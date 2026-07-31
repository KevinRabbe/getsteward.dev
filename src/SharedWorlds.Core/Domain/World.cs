namespace SharedWorlds.Core.Domain;

/// <summary>
/// Controls whether a managed World is local-only or explicitly eligible for sharing.
/// LocalOnly is intentionally zero so older persisted Worlds and newly imported Worlds
/// default to the privacy-preserving state when the property is absent.
/// </summary>
public enum WorldSharingMode
{
    LocalOnly = 0,
    Shared = 1
}

/// <summary>
/// Controls whether SharedWorlds should keep a World on its current exact game version or
/// allow newer versions to be considered through an explicit review-and-test workflow.
/// The current EnvironmentRevision always remains exact and immutable either way.
/// </summary>
public enum WorldGameVersionPolicy
{
    KeepExact = 0,
    AllowUpdateCandidates = 1
}

/// <summary>
/// Controls whether a shared World is discoverable. This is independent from whether
/// sharing itself is enabled and defaults to Private for persisted-data compatibility.
/// </summary>
public enum WorldVisibility
{
    Private = 0,
    Unlisted = 1,
    Public = 2
}

/// <summary>
/// Controls how a player may enter a shared World once they can reach it.
/// InviteOrCodeOnly is intentionally the privacy-preserving default.
/// </summary>
public enum WorldJoinPolicy
{
    InviteOrCodeOnly = 0,
    RequestApproval = 1,
    Open = 2
}

/// <summary>
/// Controls whether other players may create an independent fresh World from the
/// published starting seed. The resulting World has its own ID and canonical history.
/// </summary>
public enum StartYourOwnPolicy
{
    Disabled = 0,
    SeedOnly = 1
}

/// <summary>
/// Lightweight attribution for a World that began as an independent copy of a published snapshot.
/// It never creates synchronization, ancestry, merge, or ownership semantics between Worlds.
/// </summary>
public sealed record WorldProvenance(
    string SnapshotId,
    string WorldName,
    DateTimeOffset SnapshotCreatedAt,
    string? Creator = null,
    string? Description = null,
    string? SourceUrl = null);

/// <summary>
/// A human label attached to one immutable state revision. Checkpoints contain no state bytes and do
/// not create a second history chain; they only mark revisions that already belong to the World.
/// </summary>
public sealed record WorldCheckpoint(
    RevisionId StateRevisionId,
    string Name,
    DateTimeOffset CreatedAt,
    UserIdentity? CreatedBy = null);

public sealed record World(
    WorldId Id,
    string Name,
    string GameAdapterId,
    IReadOnlyList<UserIdentity> Members,
    RevisionId? CurrentEnvironmentRevisionId,
    RevisionId? CurrentStateRevisionId)
{
    /// <summary>
    /// Sharing is opt-in. Discovery and import never make a World shareable automatically.
    /// </summary>
    public WorldSharingMode SharingMode { get; init; } = WorldSharingMode.LocalOnly;

    /// <summary>
    /// Existing and newly imported Worlds keep their exact known-good game version by default.
    /// Allowing update candidates never mutates the current EnvironmentRevision automatically.
    /// </summary>
    public WorldGameVersionPolicy GameVersionPolicy { get; init; } = WorldGameVersionPolicy.KeepExact;

    /// <summary>
    /// Visibility is a separate concern from sharing. A newly shared World remains private
    /// until the owner explicitly chooses Unlisted or Public.
    /// </summary>
    public WorldVisibility Visibility { get; init; } = WorldVisibility.Private;

    /// <summary>
    /// Sharing does not imply open access. New and older Worlds default to invite/code-only.
    /// </summary>
    public WorldJoinPolicy JoinPolicy { get; init; } = WorldJoinPolicy.InviteOrCodeOnly;

    /// <summary>
    /// Start Your Own is opt-in and creates a new independent World rather than joining or
    /// writing to this World's canonical history.
    /// </summary>
    public StartYourOwnPolicy StartYourOwnPolicy { get; init; } = StartYourOwnPolicy.Disabled;

    /// <summary>
    /// Optional source attribution for a copied/published World. This is presentation metadata only;
    /// the local World has its own identity and immutable revision history.
    /// </summary>
    public WorldProvenance? StartedFrom { get; init; }

    /// <summary>
    /// Bounded human labels for immutable state revisions. Older persisted Worlds default to no
    /// checkpoints when this property is absent.
    /// </summary>
    public IReadOnlyList<WorldCheckpoint> Checkpoints { get; init; } = [];
}
