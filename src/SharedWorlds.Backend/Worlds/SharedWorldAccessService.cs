using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public enum CreateWorldAccessInvitationStatus
{
    Created,
    NotFoundOrUnauthorized,
    NotAccessManager,
    CannotInviteSelf,
    TargetAlreadyMember,
    AlreadyInvited
}

public sealed record CreateWorldAccessInvitationResult(
    CreateWorldAccessInvitationStatus Status,
    WorldAccessInvitation? Invitation);

public enum RespondToWorldAccessInvitationStatus
{
    Accepted,
    Declined,
    InvitationNotFound,
    AlreadyResolved
}

public enum RemoveWorldMemberStatus
{
    Revoked,
    RevocationPending,
    NotFoundOrUnauthorized,
    NotAccessManager,
    TargetNotActiveMember,
    CannotRemoveAccessManager,
    ManagerChanged
}

public enum LeaveSharedWorldStatus
{
    Left,
    NotFoundOrUnauthorized,
    MustTransferAccessManager,
    ResponsibilityUnresolved
}

public enum CompletePendingRevocationStatus
{
    Completed,
    StillUnresolved,
    NotPending
}

public enum TransferAccessManagerStatus
{
    Transferred,
    NotFoundOrUnauthorized,
    NotAccessManager,
    AlreadyManager,
    TargetNotActiveMember,
    ManagerChanged
}

/// <summary>
/// BE-2 application service for flat World membership, invitations, revocation, and the single
/// Access Manager responsibility. This layer never grants gameplay/reservation priority.
/// </summary>
public sealed class SharedWorldAccessService
{
    private readonly ISharedWorldMetadataStore _metadataStore;
    private readonly ISharedWorldAccessStore _accessStore;
    private readonly ISharedWorldResponsibilityInspector _responsibilityInspector;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<WorldAccessInvitationId> _newInvitationId;

    public SharedWorldAccessService(
        ISharedWorldMetadataStore metadataStore,
        ISharedWorldAccessStore accessStore,
        ISharedWorldResponsibilityInspector responsibilityInspector,
        Func<DateTimeOffset> utcNow,
        Func<WorldAccessInvitationId>? newInvitationId = null)
    {
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(accessStore);
        ArgumentNullException.ThrowIfNull(responsibilityInspector);
        ArgumentNullException.ThrowIfNull(utcNow);
        _metadataStore = metadataStore;
        _accessStore = accessStore;
        _responsibilityInspector = responsibilityInspector;
        _utcNow = utcNow;
        _newInvitationId = newInvitationId ?? WorldAccessInvitationId.New;
    }

    public async Task<IReadOnlyList<SharedWorldMember>?> ListMembersAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);

        if (!await IsActiveMemberAsync(worldId, caller.Subject, cancellationToken))
        {
            return null;
        }

        return await _accessStore.ListMembersAsync(worldId, cancellationToken);
    }

    public Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsAsync(
        VerifiedExternalIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _accessStore.ListPendingInvitationsForIdentityAsync(caller.Subject, cancellationToken);
    }

    public async Task<CreateWorldAccessInvitationResult> CreateInvitationAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        ExternalIdentityRef targetIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targetIdentity);
        ValidateWorldId(worldId);

        var authorization = await AuthorizeManagerAsync(worldId, caller.Subject, cancellationToken);
        if (authorization == ManagerAuthorization.NotFoundOrUnauthorized)
        {
            return new(CreateWorldAccessInvitationStatus.NotFoundOrUnauthorized, null);
        }

        if (authorization == ManagerAuthorization.NotManager)
        {
            return new(CreateWorldAccessInvitationStatus.NotAccessManager, null);
        }

        if (caller.Subject == targetIdentity)
        {
            return new(CreateWorldAccessInvitationStatus.CannotInviteSelf, null);
        }

        var invitation = new WorldAccessInvitation(
            _newInvitationId(),
            worldId,
            targetIdentity,
            caller.Subject,
            WorldAccessInvitationStatus.Pending,
            _utcNow());

        if (invitation.Id.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Invitation ID generator returned an empty ID.");
        }

        var status = await _accessStore.TryCreateInvitationAsync(invitation, cancellationToken);
        return status switch
        {
            StoreCreateInvitationStatus.Created =>
                new(CreateWorldAccessInvitationStatus.Created, invitation),
            StoreCreateInvitationStatus.AlreadyInvited =>
                new(CreateWorldAccessInvitationStatus.AlreadyInvited, null),
            StoreCreateInvitationStatus.TargetAlreadyMember =>
                new(CreateWorldAccessInvitationStatus.TargetAlreadyMember, null),
            _ => throw new InvalidOperationException("Unexpected invitation-store result.")
        };
    }

    public async Task<RespondToWorldAccessInvitationStatus> AcceptInvitationAsync(
        VerifiedExternalIdentity caller,
        WorldAccessInvitationId invitationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateInvitationId(invitationId);

        var status = await _accessStore.TryAcceptInvitationAsync(
            invitationId,
            caller.Subject,
            _utcNow(),
            cancellationToken);

        return status switch
        {
            StoreInvitationResponseStatus.Completed => RespondToWorldAccessInvitationStatus.Accepted,
            StoreInvitationResponseStatus.NotFoundOrNotInvited => RespondToWorldAccessInvitationStatus.InvitationNotFound,
            StoreInvitationResponseStatus.AlreadyResolved => RespondToWorldAccessInvitationStatus.AlreadyResolved,
            _ => throw new InvalidOperationException("Unexpected invitation response result.")
        };
    }

    public async Task<RespondToWorldAccessInvitationStatus> DeclineInvitationAsync(
        VerifiedExternalIdentity caller,
        WorldAccessInvitationId invitationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateInvitationId(invitationId);

        var status = await _accessStore.TryDeclineInvitationAsync(
            invitationId,
            caller.Subject,
            _utcNow(),
            cancellationToken);

        return status switch
        {
            StoreInvitationResponseStatus.Completed => RespondToWorldAccessInvitationStatus.Declined,
            StoreInvitationResponseStatus.NotFoundOrNotInvited => RespondToWorldAccessInvitationStatus.InvitationNotFound,
            StoreInvitationResponseStatus.AlreadyResolved => RespondToWorldAccessInvitationStatus.AlreadyResolved,
            _ => throw new InvalidOperationException("Unexpected invitation response result.")
        };
    }

    public async Task<RemoveWorldMemberStatus> RemoveMemberAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        ExternalIdentityRef targetIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targetIdentity);
        ValidateWorldId(worldId);

        var authorization = await AuthorizeManagerAsync(worldId, caller.Subject, cancellationToken);
        if (authorization == ManagerAuthorization.NotFoundOrUnauthorized)
        {
            return RemoveWorldMemberStatus.NotFoundOrUnauthorized;
        }

        if (authorization == ManagerAuthorization.NotManager)
        {
            return RemoveWorldMemberStatus.NotAccessManager;
        }

        if (caller.Subject == targetIdentity)
        {
            return RemoveWorldMemberStatus.CannotRemoveAccessManager;
        }

        var defer = await _responsibilityInspector.HasUnresolvedWritableResponsibilityAsync(
            worldId,
            targetIdentity,
            cancellationToken);
        var result = await _accessStore.TryRevokeMemberAsync(
            worldId,
            caller.Subject,
            targetIdentity,
            defer,
            _utcNow(),
            cancellationToken);

        return result switch
        {
            StoreMemberRevocationStatus.Revoked => RemoveWorldMemberStatus.Revoked,
            StoreMemberRevocationStatus.RevocationPending => RemoveWorldMemberStatus.RevocationPending,
            StoreMemberRevocationStatus.TargetNotActiveMember => RemoveWorldMemberStatus.TargetNotActiveMember,
            StoreMemberRevocationStatus.ManagerChanged => RemoveWorldMemberStatus.ManagerChanged,
            _ => throw new InvalidOperationException("Unexpected member-revocation result.")
        };
    }

    public async Task<LeaveSharedWorldStatus> LeaveWorldAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);

        var membership = await _metadataStore.LoadMemberAsync(worldId, caller.Subject, cancellationToken);
        if (membership?.Status != SharedWorldMemberStatus.Active)
        {
            return LeaveSharedWorldStatus.NotFoundOrUnauthorized;
        }

        var world = await _metadataStore.LoadWorldAsync(worldId, cancellationToken);
        if (world is null)
        {
            return LeaveSharedWorldStatus.NotFoundOrUnauthorized;
        }

        if (world.AccessManager == caller.Subject)
        {
            return LeaveSharedWorldStatus.MustTransferAccessManager;
        }

        if (await _responsibilityInspector.HasUnresolvedWritableResponsibilityAsync(
                worldId,
                caller.Subject,
                cancellationToken))
        {
            return LeaveSharedWorldStatus.ResponsibilityUnresolved;
        }

        var result = await _accessStore.TryLeaveWorldAsync(
            worldId,
            caller.Subject,
            _utcNow(),
            cancellationToken);

        return result switch
        {
            StoreLeaveMemberStatus.Left => LeaveSharedWorldStatus.Left,
            StoreLeaveMemberStatus.IsAccessManager => LeaveSharedWorldStatus.MustTransferAccessManager,
            StoreLeaveMemberStatus.TargetNotActiveMember => LeaveSharedWorldStatus.NotFoundOrUnauthorized,
            _ => throw new InvalidOperationException("Unexpected leave-World result.")
        };
    }

    public async Task<CompletePendingRevocationStatus> CompletePendingRevocationAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateWorldId(worldId);

        if (await _responsibilityInspector.HasUnresolvedWritableResponsibilityAsync(
                worldId,
                identity,
                cancellationToken))
        {
            return CompletePendingRevocationStatus.StillUnresolved;
        }

        var result = await _accessStore.TryCompletePendingRevocationAsync(
            worldId,
            identity,
            _utcNow(),
            cancellationToken);

        return result switch
        {
            StoreCompletePendingRevocationStatus.Completed => CompletePendingRevocationStatus.Completed,
            StoreCompletePendingRevocationStatus.NotPending => CompletePendingRevocationStatus.NotPending,
            _ => throw new InvalidOperationException("Unexpected pending-revocation result.")
        };
    }

    public async Task<TransferAccessManagerStatus> TransferAccessManagerAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        ExternalIdentityRef targetIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targetIdentity);
        ValidateWorldId(worldId);

        var authorization = await AuthorizeManagerAsync(worldId, caller.Subject, cancellationToken);
        if (authorization == ManagerAuthorization.NotFoundOrUnauthorized)
        {
            return TransferAccessManagerStatus.NotFoundOrUnauthorized;
        }

        if (authorization == ManagerAuthorization.NotManager)
        {
            return TransferAccessManagerStatus.NotAccessManager;
        }

        if (caller.Subject == targetIdentity)
        {
            return TransferAccessManagerStatus.AlreadyManager;
        }

        var result = await _accessStore.TryTransferAccessManagerAsync(
            worldId,
            caller.Subject,
            targetIdentity,
            _utcNow(),
            cancellationToken);

        return result switch
        {
            StoreTransferAccessManagerStatus.Transferred => TransferAccessManagerStatus.Transferred,
            StoreTransferAccessManagerStatus.AlreadyManager => TransferAccessManagerStatus.AlreadyManager,
            StoreTransferAccessManagerStatus.TargetNotActiveMember => TransferAccessManagerStatus.TargetNotActiveMember,
            StoreTransferAccessManagerStatus.ManagerChanged => TransferAccessManagerStatus.ManagerChanged,
            _ => throw new InvalidOperationException("Unexpected Access Manager transfer result.")
        };
    }

    private async Task<ManagerAuthorization> AuthorizeManagerAsync(
        WorldId worldId,
        ExternalIdentityRef caller,
        CancellationToken cancellationToken)
    {
        if (!await IsActiveMemberAsync(worldId, caller, cancellationToken))
        {
            return ManagerAuthorization.NotFoundOrUnauthorized;
        }

        var world = await _metadataStore.LoadWorldAsync(worldId, cancellationToken);
        if (world is null)
        {
            return ManagerAuthorization.NotFoundOrUnauthorized;
        }

        return world.AccessManager == caller
            ? ManagerAuthorization.Manager
            : ManagerAuthorization.NotManager;
    }

    private async Task<bool> IsActiveMemberAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken)
    {
        var membership = await _metadataStore.LoadMemberAsync(worldId, identity, cancellationToken);
        return membership?.Status == SharedWorldMemberStatus.Active;
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateInvitationId(WorldAccessInvitationId invitationId)
    {
        if (invitationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Invitation ID is required.", nameof(invitationId));
        }
    }

    private enum ManagerAuthorization
    {
        Manager,
        NotManager,
        NotFoundOrUnauthorized
    }
}
