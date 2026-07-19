namespace SharedWorlds.Core.Domain;

public sealed record World(
    WorldId Id,
    string Name,
    string GameAdapterId,
    IReadOnlyList<UserIdentity> Members,
    RevisionId? CurrentEnvironmentRevisionId,
    RevisionId? CurrentStateRevisionId);
