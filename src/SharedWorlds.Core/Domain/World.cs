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
}
