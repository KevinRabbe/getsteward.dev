using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardAuthorityApiEndpoints
{
    public static IEndpointRouteBuilder MapStewardAuthorityApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/v1");

        api.MapPost("/worlds/{worldId:guid}/reservation/acquire", AcquireAsync);
        api.MapGet("/worlds/{worldId:guid}/reservation", GetAsync);
        api.MapPost("/worlds/{worldId:guid}/reservation/heartbeat", HeartbeatAsync);
        api.MapPost("/worlds/{worldId:guid}/reservation/reclaim", ReclaimAsync);
        api.MapPost("/worlds/{worldId:guid}/reservation/commit", CommitAsync);

        return endpoints;
    }

    private static async Task<IResult> AcquireAsync(
        Guid worldId,
        AcquireWorldReservationRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var expectedHead = new SharedWorldHead(
            new RevisionId(request.ExpectedStateRevisionId),
            request.ExpectedEnvironmentRevisionId is { } environmentId
                ? new RevisionId(environmentId)
                : null);
        var result = await authority.AcquireAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            request.InstallationId,
            expectedHead,
            cancellationToken);

        return result.Status switch
        {
            AcquireSharedWorldReservationStatus.Acquired => Results.Ok(new StewardApiResponse(
                "ReservationAcquired",
                SharedWorldReservationDto.From(result.Reservation!))),
            AcquireSharedWorldReservationStatus.AlreadyHeldByCaller => Results.Ok(new StewardApiResponse(
                "ReservationAlreadyHeldByCaller",
                SharedWorldReservationDto.From(result.Reservation!))),
            AcquireSharedWorldReservationStatus.WorldBusy => StewardApiResults.DomainConflict(
                "WorldBusy",
                SharedWorldReservationDto.From(result.Reservation!)),
            AcquireSharedWorldReservationStatus.WorldUncertain => StewardApiResults.DomainConflict(
                "WorldUncertain",
                SharedWorldReservationDto.From(result.Reservation!)),
            AcquireSharedWorldReservationStatus.HeadChanged => StewardApiResults.DomainConflict(
                "HeadChanged",
                result.CurrentHead is null ? null : SharedWorldHeadDto.From(result.CurrentHead)),
            AcquireSharedWorldReservationStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            _ => throw new InvalidOperationException("Unexpected reservation-acquire result.")
        };
    }

    private static async Task<IResult> GetAsync(
        Guid worldId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var reservation = await authority.GetReservationAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            cancellationToken);
        return reservation is null
            ? StewardApiResults.NotFound("ReservationNotFoundOrUnauthorized")
            : Results.Ok(new StewardApiResponse(
                "ReservationFound",
                SharedWorldReservationDto.From(reservation)));
    }

    private static async Task<IResult> HeartbeatAsync(
        Guid worldId,
        WorldReservationHeartbeatRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var result = await authority.HeartbeatAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            request.InstallationId,
            request.SessionId,
            request.Generation,
            cancellationToken);
        return result switch
        {
            SharedWorldHeartbeatStatus.Accepted => Results.Ok(new StewardApiResponse("HeartbeatAccepted")),
            SharedWorldHeartbeatStatus.ReservationMismatch =>
                StewardApiResults.DomainConflict("ReservationMismatch"),
            _ => throw new InvalidOperationException("Unexpected heartbeat result.")
        };
    }

    private static async Task<IResult> ReclaimAsync(
        Guid worldId,
        ReclaimWorldReservationRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var result = await authority.ReclaimAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            request.ExpectedSessionId,
            request.ExpectedGeneration,
            cancellationToken);
        return result.Status switch
        {
            ReclaimSharedWorldReservationStatus.Reclaimed => Results.Ok(new StewardApiResponse(
                "ReservationReclaimed",
                new ReclaimedReservationDto(result.InvalidatedGeneration!.Value))),
            ReclaimSharedWorldReservationStatus.GracePeriodRequired =>
                StewardApiResults.DomainConflict("GracePeriodRequired"),
            ReclaimSharedWorldReservationStatus.ReservationMismatch =>
                StewardApiResults.DomainConflict("ReservationMismatch"),
            ReclaimSharedWorldReservationStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            _ => throw new InvalidOperationException("Unexpected reservation-reclaim result.")
        };
    }

    private static async Task<IResult> CommitAsync(
        Guid worldId,
        CommitWorldReservationRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var result = await authority.CommitAsync(
            authentication.Caller!.Identity,
            new CommitSharedWorldCommand(
                new WorldId(worldId),
                request.SessionId,
                request.Generation,
                request.InstallationId,
                new SharedWorldHead(
                    new RevisionId(request.ExpectedStateRevisionId),
                    request.ExpectedEnvironmentRevisionId is { } expectedEnvironment
                        ? new RevisionId(expectedEnvironment)
                        : null),
                new RevisionId(request.CandidateStateRevisionId),
                request.CandidateEnvironmentRevisionId is { } candidateEnvironment
                    ? new RevisionId(candidateEnvironment)
                    : null),
            cancellationToken);

        var data = new CommitWorldReservationDto(
            SharedWorldHeadDto.From(result.CurrentHead),
            result.CandidateHead is null ? null : SharedWorldHeadDto.From(result.CandidateHead));
        return result.Status switch
        {
            CommitSharedWorldStatus.Committed => Results.Ok(new StewardApiResponse("Committed", data)),
            CommitSharedWorldStatus.Unchanged => Results.Ok(new StewardApiResponse("Unchanged", data)),
            CommitSharedWorldStatus.HeadChanged => StewardApiResults.DomainConflict("HeadChanged", data),
            CommitSharedWorldStatus.ReservationMismatch =>
                StewardApiResults.DomainConflict("ReservationMismatch", data),
            CommitSharedWorldStatus.InvalidCandidate =>
                StewardApiResults.DomainConflict("InvalidCandidate", data),
            _ => throw new InvalidOperationException("Unexpected World-commit result.")
        };
    }

    private static async Task<AuthorityAuthenticationResolution> AuthenticateAsync(
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

    private sealed record AuthorityAuthenticationResolution(
        StewardAuthenticatedCaller? Caller,
        IResult? Error);
}

public sealed record AcquireWorldReservationRequest(
    string InstallationId,
    Guid ExpectedStateRevisionId,
    Guid? ExpectedEnvironmentRevisionId);

public sealed record WorldReservationHeartbeatRequest(
    string InstallationId,
    Guid SessionId,
    long Generation);

public sealed record ReclaimWorldReservationRequest(
    Guid ExpectedSessionId,
    long ExpectedGeneration);

public sealed record CommitWorldReservationRequest(
    string InstallationId,
    Guid SessionId,
    long Generation,
    Guid ExpectedStateRevisionId,
    Guid? ExpectedEnvironmentRevisionId,
    Guid CandidateStateRevisionId,
    Guid? CandidateEnvironmentRevisionId);

public sealed record SharedWorldHeadDto(
    Guid StateRevisionId,
    Guid? EnvironmentRevisionId)
{
    public static SharedWorldHeadDto From(SharedWorldHead head)
        => new(head.StateRevisionId.Value, head.EnvironmentRevisionId?.Value);
}

public sealed record SharedWorldReservationDto(
    Guid WorldId,
    Guid SessionId,
    long Generation,
    ExternalIdentityDto Holder,
    string InstallationId,
    SharedWorldHeadDto StartingHead,
    string State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? BecameUncertainAt)
{
    public static SharedWorldReservationDto From(SharedWorldReservation reservation)
        => new(
            reservation.WorldId.Value,
            reservation.SessionId,
            reservation.Generation,
            ExternalIdentityDto.From(reservation.Holder),
            reservation.InstallationId,
            SharedWorldHeadDto.From(reservation.StartingHead),
            reservation.State.ToString(),
            reservation.AcquiredAt,
            reservation.LastHeartbeatAt,
            reservation.BecameUncertainAt);
}

public sealed record ReclaimedReservationDto(long InvalidatedGeneration);

public sealed record CommitWorldReservationDto(
    SharedWorldHeadDto CurrentHead,
    SharedWorldHeadDto? CandidateHead);
