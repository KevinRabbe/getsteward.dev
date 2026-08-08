using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

/// <summary>
/// Temporary one-way migration endpoint used only while legacy Backend.Api authority is being
/// retired in favor of peer authority. The authenticated session supplies identity and installation;
/// callers can submit only the exact reservation session/generation they hold.
/// </summary>
public static class StewardLegacyAuthorityRetirementApi
{
    public static IEndpointRouteBuilder MapStewardLegacyAuthorityRetirementApiV1(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/api/v1/worlds/{worldId:guid}/reservation/retire-peer-authority",
            GetAsync);
        endpoints.MapPost(
            "/api/v1/worlds/{worldId:guid}/reservation/retire-peer-authority",
            RetireAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        LegacySharedWorldAuthorityRetirementService retirement,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await retirement.GetAsync(
            caller.Identity,
            new WorldId(worldId),
            cancellationToken);
        return result is null
            ? Results.NotFound(new LegacyAuthorityRetirementResponse(
                "LegacyAuthorityNotRetired",
                Retryable: false))
            : Results.Ok(MapSuccess("LegacyAuthorityAlreadyRetired", result));
    }

    private static async Task<IResult> RetireAsync(
        Guid worldId,
        RetireLegacyAuthorityRequest body,
        HttpRequest request,
        StewardSessionService sessions,
        LegacySharedWorldAuthorityRetirementService retirement,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (body.SessionId == Guid.Empty || body.Generation <= 0)
        {
            return Results.UnprocessableEntity(new LegacyAuthorityRetirementResponse(
                "InvalidReservation",
                Retryable: false));
        }

        var result = await retirement.RetireAsync(
            caller.Identity,
            new WorldId(worldId),
            caller.InstallationId,
            body.SessionId,
            body.Generation,
            cancellationToken);

        return result.Status switch
        {
            LegacySharedWorldAuthorityRetirementStatus.Retired => Results.Ok(
                MapSuccess("LegacyAuthorityRetired", result)),
            LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired => Results.Ok(
                MapSuccess("LegacyAuthorityAlreadyRetired", result)),
            LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized => Results.NotFound(
                new LegacyAuthorityRetirementResponse(
                    "WorldNotFoundOrUnauthorized",
                    Retryable: false)),
            LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch => Results.Conflict(
                new LegacyAuthorityRetirementResponse(
                    "ReservationMismatch",
                    Retryable: false)),
            LegacySharedWorldAuthorityRetirementStatus.AccessStateNotReady => Results.Conflict(
                new LegacyAuthorityRetirementResponse(
                    "LegacyAccessStateNotReady",
                    Retryable: false)),
            _ => throw new InvalidOperationException(
                $"Unhandled legacy authority retirement status {result.Status}.")
        };
    }

    private static LegacyAuthorityRetirementResponse MapSuccess(
        string code,
        LegacySharedWorldAuthorityRetirementResult result)
    {
        if (result.RetiredSessionId is null ||
            result.RetiredGeneration is null ||
            result.RetiredStateRevisionId is null ||
            !StableIdentitySetFingerprint.IsCanonicalFingerprint(
                result.RetiredActiveMembersFingerprint) ||
            result.RetiredAt is null)
        {
            throw new InvalidDataException(
                "Successful legacy authority retirement did not include durable retirement evidence.");
        }

        return new LegacyAuthorityRetirementResponse(
            code,
            Retryable: false,
            Data: new LegacyAuthorityRetirementData(
                result.WorldId.Value,
                result.RetiredSessionId.Value,
                result.RetiredGeneration.Value,
                result.RetiredStateRevisionId.Value.Value,
                result.RetiredEnvironmentRevisionId?.Value,
                result.RetiredActiveMembersFingerprint!,
                result.RetiredAt.Value));
    }

    private static async Task<StewardAuthenticatedCaller?> AuthenticateAsync(
        HttpRequest request,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var accessToken = authorization[bearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return await sessions.ValidateAccessTokenAsync(accessToken, cancellationToken);
    }

    public sealed record RetireLegacyAuthorityRequest(
        Guid SessionId,
        long Generation);

    public sealed record LegacyAuthorityRetirementData(
        Guid WorldId,
        Guid SessionId,
        long Generation,
        Guid StateRevisionId,
        Guid? EnvironmentRevisionId,
        string ActiveMembersFingerprint,
        DateTimeOffset RetiredAt);

    public sealed record LegacyAuthorityRetirementResponse(
        string Code,
        bool Retryable,
        LegacyAuthorityRetirementData? Data = null);
}
