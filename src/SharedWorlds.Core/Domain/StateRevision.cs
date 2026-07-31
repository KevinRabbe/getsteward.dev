namespace SharedWorlds.Core.Domain;

public sealed record StateRevision(
    RevisionId Id,
    WorldId WorldId,
    RevisionId? ParentRevisionId,
    DateTimeOffset CreatedAt,
    UserIdentity? CreatedBy,
    string AdapterId,
    string StatePackageId,
    RevisionId? EnvironmentRevisionId = null);
