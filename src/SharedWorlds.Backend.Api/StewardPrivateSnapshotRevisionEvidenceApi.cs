using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardPrivateSnapshotRevisionEvidenceApi
{
    public static IEndpointRouteBuilder MapStewardPrivateSnapshotRevisionEvidenceApiV1(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(
            "/api/v1/private-worlds/{worldId:guid}/snapshot-revision-evidence",
            PublishAsync);
        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        Guid worldId,
        PublishPrivateSnapshotRevisionEvidenceRequest body,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotRevisionEvidenceService evidence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (body.StateRevision is null || body.EnvironmentRevision is null)
        {
            return Results.BadRequest(Response(
                "PrivateSnapshotRevisionEvidenceInvalid",
                retryable: false));
        }

        var result = await evidence.PublishAsync(
            caller,
            new PublishPrivateSnapshotRevisionEvidenceCommand(
                new WorldId(worldId),
                body.StateRevision,
                body.EnvironmentRevision),
            cancellationToken);
        return result.Status switch
        {
            PublishPrivateSnapshotRevisionEvidenceStatus.Published => Results.Ok(Response(
                "PrivateSnapshotRevisionEvidencePublished",
                retryable: false,
                ToData(result.Evidence
                    ?? throw new InvalidDataException(
                        "Private revision evidence was published without current evidence.")))),
            PublishPrivateSnapshotRevisionEvidenceStatus.AlreadyPublished => Results.Ok(Response(
                "PrivateSnapshotRevisionEvidenceAlreadyPublished",
                retryable: false,
                ToData(result.Evidence
                    ?? throw new InvalidDataException(
                        "Private revision evidence was already published without current evidence.")))),
            PublishPrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized => Results.NotFound(Response(
                "PrivateSnapshotNotFound",
                retryable: false)),
            PublishPrivateSnapshotRevisionEvidenceStatus.InvalidRequest => Results.BadRequest(Response(
                "PrivateSnapshotRevisionEvidenceInvalid",
                retryable: false)),
            PublishPrivateSnapshotRevisionEvidenceStatus.Conflict => Results.Conflict(Response(
                "PrivateSnapshotRevisionEvidenceConflict",
                retryable: false)),
            _ => throw new InvalidOperationException(
                $"Unhandled private revision evidence status {result.Status}.")
        };
    }

    private static PrivateSnapshotRevisionEvidenceData ToData(
        Core.Worlds.OwnedWorldSnapshotRevisionEvidence evidence)
        => new(
            evidence.WorldId.Value,
            evidence.StateRevision.Id.Value,
            evidence.EnvironmentRevision.Id.Value,
            evidence.RecordedAt);

    private static PrivateSnapshotRevisionEvidenceResponse Response(
        string code,
        bool retryable,
        object? data = null)
        => new(code, retryable, data);

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

    public sealed record PublishPrivateSnapshotRevisionEvidenceRequest(
        StateRevision? StateRevision,
        EnvironmentRevision? EnvironmentRevision);

    public sealed record PrivateSnapshotRevisionEvidenceData(
        Guid WorldId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        DateTimeOffset RecordedAt);

    public sealed record PrivateSnapshotRevisionEvidenceResponse(
        string Code,
        bool Retryable,
        object? Data = null);
}
