using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardAccessApiEndpoints
{
    public static IEndpointRouteBuilder MapStewardAccessApiV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1");

        group.MapGet("/worlds/{worldId:guid}/members", ListMembersAsync);
        group.MapGet("/invitations", ListInvitationsAsync);
        group.MapPost("/worlds/{worldId:guid}/invitations", CreateInvitationAsync);
        group.MapPost("/invitations/{invitationId:guid}/accept", AcceptInvitationAsync);
        group.MapPost("/invitations/{invitationId:guid}/decline", DeclineInvitationAsync);
        group.MapPost("/worlds/{worldId:guid}/members/revoke", RevokeMemberAsync);
        group.MapPost("/worlds/{worldId:guid}/leave", LeaveWorldAsync);
        group.MapPost("/worlds/{worldId:guid}/access-manager", TransferAccessManagerAsync);

        return endpoints;
    }

    private static async Task<IResult> ListMembersAsync(
        Guid worldId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var members = await access.ListMembersAsync(
            auth.Caller!.Identity,
            new WorldId(worldId),
            cancellationToken);
        return members is null
            ? StewardApiResults.NotFound("WorldNotFoundOrUnauthorized")
            : Results.Ok(new StewardApiResponse(
                "WorldMembersFound",
                members.Select(SharedWorldMemberDto.From).ToArray()));
    }

    private static async Task<IResult> ListInvitationsAsync(
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var invitations = await access.ListPendingInvitationsAsync(
            auth.Caller!.Identity,
            cancellationToken);
        return Results.Ok(new StewardApiResponse(
            "PendingInvitationsFound",
            invitations.Select(WorldAccessInvitationDto.From).ToArray()));
    }

    private static async Task<IResult> CreateInvitationAsync(
        Guid worldId,
        ExternalIdentityRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(request, out var target))
        {
            return StewardApiResults.BadRequest("InvalidTargetIdentity");
        }

        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var result = await access.CreateInvitationAsync(
            auth.Caller!.Identity,
            new WorldId(worldId),
            target!,
            cancellationToken);
        return result.Status switch
        {
            CreateWorldAccessInvitationStatus.Created => Results.Json(
                new StewardApiResponse(
                    "InvitationCreated",
                    WorldAccessInvitationDto.From(result.Invitation!)),
                statusCode: StatusCodes.Status201Created),
            CreateWorldAccessInvitationStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            CreateWorldAccessInvitationStatus.NotAccessManager =>
                StewardApiResults.Forbidden("NotAccessManager"),
            CreateWorldAccessInvitationStatus.CannotInviteSelf =>
                StewardApiResults.Conflict("CannotInviteSelf"),
            CreateWorldAccessInvitationStatus.TargetAlreadyMember =>
                StewardApiResults.Conflict("TargetAlreadyMember"),
            CreateWorldAccessInvitationStatus.AlreadyInvited =>
                StewardApiResults.Conflict("AlreadyInvited"),
            _ => throw new InvalidOperationException("Unexpected invitation result.")
        };
    }

    private static Task<IResult> AcceptInvitationAsync(
        Guid invitationId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
        => RespondToInvitationAsync(
            new WorldAccessInvitationId(invitationId),
            accept: true,
            context,
            sessions,
            access,
            cancellationToken);

    private static Task<IResult> DeclineInvitationAsync(
        Guid invitationId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
        => RespondToInvitationAsync(
            new WorldAccessInvitationId(invitationId),
            accept: false,
            context,
            sessions,
            access,
            cancellationToken);

    private static async Task<IResult> RespondToInvitationAsync(
        WorldAccessInvitationId invitationId,
        bool accept,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var status = accept
            ? await access.AcceptInvitationAsync(auth.Caller!.Identity, invitationId, cancellationToken)
            : await access.DeclineInvitationAsync(auth.Caller!.Identity, invitationId, cancellationToken);
        return status switch
        {
            RespondToWorldAccessInvitationStatus.Accepted =>
                Results.Ok(new StewardApiResponse("InvitationAccepted")),
            RespondToWorldAccessInvitationStatus.Declined =>
                Results.Ok(new StewardApiResponse("InvitationDeclined")),
            RespondToWorldAccessInvitationStatus.InvitationNotFound =>
                StewardApiResults.NotFound("InvitationNotFound"),
            RespondToWorldAccessInvitationStatus.AlreadyResolved =>
                StewardApiResults.Conflict("InvitationAlreadyResolved"),
            _ => throw new InvalidOperationException("Unexpected invitation-response result.")
        };
    }

    private static async Task<IResult> RevokeMemberAsync(
        Guid worldId,
        ExternalIdentityRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(request, out var target))
        {
            return StewardApiResults.BadRequest("InvalidTargetIdentity");
        }

        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var status = await access.RemoveMemberAsync(
            auth.Caller!.Identity,
            new WorldId(worldId),
            target!,
            cancellationToken);
        return status switch
        {
            RemoveWorldMemberStatus.Revoked => Results.Ok(new StewardApiResponse("MemberRevoked")),
            RemoveWorldMemberStatus.RevocationPending => Results.Ok(
                new StewardApiResponse(
                    "MemberRevocationPending",
                    new { reason = "Writable responsibility must resolve before access is removed." })),
            RemoveWorldMemberStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            RemoveWorldMemberStatus.NotAccessManager =>
                StewardApiResults.Forbidden("NotAccessManager"),
            RemoveWorldMemberStatus.TargetNotActiveMember =>
                StewardApiResults.Conflict("TargetNotActiveMember"),
            RemoveWorldMemberStatus.CannotRemoveAccessManager =>
                StewardApiResults.Conflict("CannotRemoveAccessManager"),
            RemoveWorldMemberStatus.ManagerChanged =>
                StewardApiResults.Conflict("AccessManagerChanged"),
            _ => throw new InvalidOperationException("Unexpected member-revocation result.")
        };
    }

    private static async Task<IResult> LeaveWorldAsync(
        Guid worldId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var status = await access.LeaveWorldAsync(
            auth.Caller!.Identity,
            new WorldId(worldId),
            cancellationToken);
        return status switch
        {
            LeaveSharedWorldStatus.Left => Results.Ok(new StewardApiResponse("WorldLeft")),
            LeaveSharedWorldStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            LeaveSharedWorldStatus.MustTransferAccessManager =>
                StewardApiResults.Conflict("MustTransferAccessManager"),
            LeaveSharedWorldStatus.ResponsibilityUnresolved =>
                StewardApiResults.Conflict("WritableResponsibilityUnresolved"),
            _ => throw new InvalidOperationException("Unexpected leave-World result.")
        };
    }

    private static async Task<IResult> TransferAccessManagerAsync(
        Guid worldId,
        ExternalIdentityRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAccessService access,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(request, out var target))
        {
            return StewardApiResults.BadRequest("InvalidTargetIdentity");
        }

        var auth = await AuthenticateAsync(context, sessions, cancellationToken);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        var status = await access.TransferAccessManagerAsync(
            auth.Caller!.Identity,
            new WorldId(worldId),
            target!,
            cancellationToken);
        return status switch
        {
            TransferAccessManagerStatus.Transferred =>
                Results.Ok(new StewardApiResponse("AccessManagerTransferred")),
            TransferAccessManagerStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            TransferAccessManagerStatus.NotAccessManager =>
                StewardApiResults.Forbidden("NotAccessManager"),
            TransferAccessManagerStatus.AlreadyManager =>
                StewardApiResults.Conflict("AlreadyAccessManager"),
            TransferAccessManagerStatus.TargetNotActiveMember =>
                StewardApiResults.Conflict("TargetNotActiveMember"),
            TransferAccessManagerStatus.ManagerChanged =>
                StewardApiResults.Conflict("AccessManagerChanged"),
            _ => throw new InvalidOperationException("Unexpected Access Manager transfer result.")
        };
    }

    private static bool TryIdentity(
        ExternalIdentityRequest? request,
        out ExternalIdentityRef? identity)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Provider) ||
            string.IsNullOrWhiteSpace(request.ExternalId))
        {
            identity = null;
            return false;
        }

        identity = new ExternalIdentityRef(request.Provider.Trim(), request.ExternalId.Trim());
        return true;
    }

    private static async Task<AuthenticationResolution> AuthenticateAsync(
        HttpContext context,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, StewardApiResults.AuthenticationRequired());
        }

        var token = authorization[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(' '))
        {
            return new(null, StewardApiResults.AuthenticationRequired());
        }

        var caller = await sessions.ValidateAccessTokenAsync(token, cancellationToken);
        return caller is null
            ? new(null, StewardApiResults.AuthenticationRequired())
            : new(caller, null);
    }

    private sealed record AuthenticationResolution(
        StewardAuthenticatedCaller? Caller,
        IResult? Error);
}

public sealed record ExternalIdentityRequest(string Provider, string ExternalId);

public sealed record SharedWorldMemberDto(
    Guid WorldId,
    ExternalIdentityDto Identity,
    string Status,
    DateTimeOffset AddedAt)
{
    public static SharedWorldMemberDto From(SharedWorldMember member)
        => new(
            member.WorldId.Value,
            ExternalIdentityDto.From(member.Identity),
            member.Status.ToString(),
            member.AddedAt);
}

public sealed record WorldAccessInvitationDto(
    Guid InvitationId,
    Guid WorldId,
    ExternalIdentityDto InvitedIdentity,
    ExternalIdentityDto InvitedBy,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RespondedAt)
{
    public static WorldAccessInvitationDto From(WorldAccessInvitation invitation)
        => new(
            invitation.Id.Value,
            invitation.WorldId.Value,
            ExternalIdentityDto.From(invitation.InvitedIdentity),
            ExternalIdentityDto.From(invitation.InvitedBy),
            invitation.Status.ToString(),
            invitation.CreatedAt,
            invitation.RespondedAt);
}
