using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public readonly record struct WorldAccessInvitationId(Guid Value)
{
    public static WorldAccessInvitationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum WorldAccessInvitationStatus
{
    Pending,
    Accepted,
    Declined
}

public sealed record WorldAccessInvitation(
    WorldAccessInvitationId Id,
    WorldId WorldId,
    ExternalIdentityRef InvitedIdentity,
    ExternalIdentityRef InvitedBy,
    WorldAccessInvitationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RespondedAt = null);

public enum StoreCreateInvitationStatus
{
    Created,
    AlreadyInvited,
    TargetAlreadyMember
}

public enum StoreInvitationResponseStatus
{
    Completed,
    NotFoundOrNotInvited,
    AlreadyResolved
}

public enum StoreMemberRevocationStatus
{
    Revoked,
    RevocationPending,
    TargetNotActiveMember,
    ManagerChanged
}

public enum StoreLeaveMemberStatus
{
    Left,
    TargetNotActiveMember,
    IsAccessManager
}

public enum StoreCompletePendingRevocationStatus
{
    Completed,
    NotPending
}

public enum StoreTransferAccessManagerStatus
{
    Transferred,
    AlreadyManager,
    TargetNotActiveMember,
    ManagerChanged
}

/// <summary>
/// Transactional persistence boundary for BE-2 access administration. Implementations must keep
/// invitation acceptance + membership creation, revocation state changes, and Access Manager
/// transfer atomic within each operation.
/// </summary>
public interface ISharedWorldAccessStore
{
    Task<IReadOnlyList<SharedWorldMember>> ListMembersAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsForIdentityAsync(
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);

    Task<StoreCreateInvitationStatus> TryCreateInvitationAsync(
        WorldAccessInvitation invitation,
        CancellationToken cancellationToken = default);

    Task<StoreInvitationResponseStatus> TryAcceptInvitationAsync(
        WorldAccessInvitationId invitationId,
        ExternalIdentityRef invitedIdentity,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default);

    Task<StoreInvitationResponseStatus> TryDeclineInvitationAsync(
        WorldAccessInvitationId invitationId,
        ExternalIdentityRef invitedIdentity,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default);

    Task<StoreMemberRevocationStatus> TryRevokeMemberAsync(
        WorldId worldId,
        ExternalIdentityRef expectedAccessManager,
        ExternalIdentityRef targetIdentity,
        bool deferForUnresolvedResponsibility,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task<StoreLeaveMemberStatus> TryLeaveWorldAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task<StoreCompletePendingRevocationStatus> TryCompletePendingRevocationAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task<StoreTransferAccessManagerStatus> TryTransferAccessManagerAsync(
        WorldId worldId,
        ExternalIdentityRef expectedAccessManager,
        ExternalIdentityRef targetIdentity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bridges BE-2 access administration to the later distributed reservation/recovery authority.
/// A pending revocation must never terminate or strand an unresolved writable transaction.
/// </summary>
public interface ISharedWorldResponsibilityInspector
{
    Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);
}
