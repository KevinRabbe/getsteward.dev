using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Domain;

public sealed record EnvironmentRevision(
    RevisionId Id,
    WorldId WorldId,
    RevisionId? ParentRevisionId,
    DateTimeOffset CreatedAt,
    UserIdentity? CreatedBy,
    EnvironmentManifest Manifest);
