using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public enum SharedWorldMemberStatus
{
    Active,
    RevocationPending
}

public sealed record SharedWorldMetadata(
    WorldId WorldId,
    string AdapterId,
    string DisplayName,
    RevisionId CurrentStateRevisionId,
    RevisionId? CurrentEnvironmentRevisionId,
    ExternalIdentityRef AccessManager,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SharedWorldMember(
    WorldId WorldId,
    ExternalIdentityRef Identity,
    SharedWorldMemberStatus Status,
    DateTimeOffset AddedAt);
