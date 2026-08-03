using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Api;

public static class StewardOwnedWorldLocationApi
{
    public static IEndpointRouteBuilder MapStewardOwnedWorldLocationApiV1(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut("/api/v1/installations/current", RegisterInstallationAsync);
        endpoints.MapGet("/api/v1/private-worlds", ListPrivateWorldCatalogAsync);
        endpoints.MapPut(
            "/api/v1/private-worlds/{worldId:guid}/location",
            PublishLocationAsync);
        endpoints.MapDelete(
            "/api/v1/private-worlds/{worldId:guid}/location",
            RemoveLocationAsync);
        endpoints.MapGet(
            "/api/v1/private-worlds/{worldId:guid}/bring-here",
            ResolveBringHereAsync);

        return endpoints;
    }

    private static async Task<IResult> RegisterInstallationAsync(
        RegisterInstallationRequest body,
        HttpRequest request,
        StewardSessionService sessions,
        OwnedWorldLocationApplicationService locations,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var registration = await locations.RegisterCurrentInstallationAsync(
            caller,
            body.DisplayName,
            cancellationToken);
        return Results.Ok(new OwnedWorldLocationResponse(
            "InstallationRegistered",
            Retryable: false,
            Data: new InstallationData(
                registration.InstallationId,
                registration.DisplayName,
                registration.RegisteredAt,
                registration.LastSeenAt)));
    }

    private static async Task<IResult> ListPrivateWorldCatalogAsync(
        HttpRequest request,
        StewardSessionService sessions,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var service = new OwnedPrivateWorldCatalogApplicationService(
            new PostgreSqlOwnedWorldLocationCatalogStore(dataSource));
        var entries = await service.ListAsync(caller, cancellationToken);
        return Results.Ok(new OwnedWorldLocationResponse(
            "OwnedPrivateWorldCatalog",
            Retryable: false,
            Data: new OwnedPrivateWorldCatalogData(
                entries.Select(ToCatalogEntryData).ToArray())));
    }

    private static async Task<IResult> PublishLocationAsync(
        Guid worldId,
        PublishOwnedWorldLocationRequest body,
        HttpRequest request,
        StewardSessionService sessions,
        OwnedWorldLocationApplicationService locations,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        EnsureExpectedPair(body.ExpectedStateRevisionId, body.ExpectedEnvironmentRevisionId);
        EnsurePresentationPair(body.WorldName, body.GameAdapterId);

        RevisionId? expectedState = body.ExpectedStateRevisionId is null
            ? null
            : new RevisionId(body.ExpectedStateRevisionId.Value);
        RevisionId? expectedEnvironment = body.ExpectedEnvironmentRevisionId is null
            ? null
            : new RevisionId(body.ExpectedEnvironmentRevisionId.Value);
        var decision = body.WorldName is null
            ? await locations.PublishCurrentLocationAsync(
                caller,
                new WorldId(worldId),
                new RevisionId(body.StateRevisionId),
                new RevisionId(body.EnvironmentRevisionId),
                expectedState,
                expectedEnvironment,
                cancellationToken)
            : await locations.PublishCurrentLocationWithPresentationAsync(
                caller,
                new WorldId(worldId),
                new RevisionId(body.StateRevisionId),
                new RevisionId(body.EnvironmentRevisionId),
                body.WorldName,
                body.GameAdapterId!,
                expectedState,
                expectedEnvironment,
                cancellationToken);

        var response = WriteDecisionResponse(decision);
        return decision.Result == OwnedWorldLocationWriteResult.Conflict
            ? Results.Conflict(response)
            : Results.Ok(response);
    }

    private static async Task<IResult> RemoveLocationAsync(
        Guid worldId,
        Guid expectedStateRevisionId,
        Guid expectedEnvironmentRevisionId,
        HttpRequest request,
        StewardSessionService sessions,
        OwnedWorldLocationApplicationService locations,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var decision = await locations.RemoveCurrentLocationAsync(
            caller,
            new WorldId(worldId),
            new RevisionId(expectedStateRevisionId),
            new RevisionId(expectedEnvironmentRevisionId),
            cancellationToken);
        var response = WriteDecisionResponse(decision);
        return decision.Result == OwnedWorldLocationWriteResult.Conflict
            ? Results.Conflict(response)
            : Results.Ok(response);
    }

    private static async Task<IResult> ResolveBringHereAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        OwnedWorldLocationApplicationService locations,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var decision = await locations.ResolveBringHereAsync(
            caller,
            new WorldId(worldId),
            cancellationToken);
        return Results.Ok(new OwnedWorldLocationResponse(
            "BringHereAvailability",
            Retryable: false,
            Data: new BringHereData(
                (int)decision.Availability,
                decision.Source is null ? null : ToLocationData(decision.Source),
                decision.ConflictingClaims.Select(ToLocationData).ToArray(),
                decision.Reason)));
    }

    private static OwnedPrivateWorldCatalogEntryData ToCatalogEntryData(
        OwnedPrivateWorldCatalogEntry entry)
        => new(
            entry.WorldId.Value,
            entry.Name,
            entry.GameAdapterId,
            (int)entry.Availability,
            entry.Source is null ? null : ToLocationData(entry.Source),
            entry.ConflictingClaims.Select(ToLocationData).ToArray(),
            entry.Reason);

    private static OwnedWorldLocationResponse WriteDecisionResponse(
        OwnedWorldLocationWriteDecision decision)
        => new(
            decision.Result switch
            {
                OwnedWorldLocationWriteResult.Created => "WorldLocationCreated",
                OwnedWorldLocationWriteResult.Updated => "WorldLocationUpdated",
                OwnedWorldLocationWriteResult.NoChange => "WorldLocationUnchanged",
                OwnedWorldLocationWriteResult.Conflict => "WorldLocationConflict",
                _ => throw new InvalidOperationException(
                    $"Unhandled World location write result {decision.Result}.")
            },
            Retryable: false,
            Data: new WorldLocationWriteData(
                decision.Result,
                decision.Current is null ? null : ToLocationData(decision.Current),
                decision.Reason));

    private static OwnedWorldLocationData ToLocationData(OwnedWorldLocationClaim claim)
        => new(
            claim.WorldId.Value,
            claim.InstallationId,
            claim.StateRevisionId.Value,
            claim.EnvironmentRevisionId.Value,
            claim.ObservedAt,
            claim.Presentation?.Name,
            claim.Presentation?.GameAdapterId);

    private static void EnsureExpectedPair(Guid? state, Guid? environment)
    {
        if (state.HasValue != environment.HasValue)
        {
            throw new ArgumentException(
                "Expected state and environment revisions must both be supplied or both be absent.");
        }
    }

    private static void EnsurePresentationPair(string? worldName, string? gameAdapterId)
    {
        if ((worldName is null) != (gameAdapterId is null))
        {
            throw new ArgumentException(
                "World name and game adapter ID must both be supplied or both be absent.");
        }
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
        return string.IsNullOrWhiteSpace(accessToken)
            ? null
            : await sessions.ValidateAccessTokenAsync(accessToken, cancellationToken);
    }

    public sealed record RegisterInstallationRequest(string DisplayName);

    public sealed record PublishOwnedWorldLocationRequest(
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        Guid? ExpectedStateRevisionId = null,
        Guid? ExpectedEnvironmentRevisionId = null,
        string? WorldName = null,
        string? GameAdapterId = null);

    public sealed record InstallationData(
        string InstallationId,
        string DisplayName,
        DateTimeOffset RegisteredAt,
        DateTimeOffset LastSeenAt);

    public sealed record OwnedWorldLocationData(
        Guid WorldId,
        string InstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        DateTimeOffset ObservedAt,
        string? WorldName = null,
        string? GameAdapterId = null);

    public sealed record OwnedPrivateWorldCatalogEntryData(
        Guid WorldId,
        string Name,
        string? GameAdapterId,
        int Availability,
        OwnedWorldLocationData? Source,
        IReadOnlyList<OwnedWorldLocationData> ConflictingClaims,
        string Reason);

    public sealed record OwnedPrivateWorldCatalogData(
        IReadOnlyList<OwnedPrivateWorldCatalogEntryData> Worlds);

    public sealed record WorldLocationWriteData(
        OwnedWorldLocationWriteResult Result,
        OwnedWorldLocationData? Current,
        string Reason);

    public sealed record BringHereData(
        int Availability,
        OwnedWorldLocationData? Source,
        IReadOnlyList<OwnedWorldLocationData> ConflictingClaims,
        string Reason);

    public sealed record OwnedWorldLocationResponse(
        string Code,
        bool Retryable,
        object? Data = null);
}
