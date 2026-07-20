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
}
