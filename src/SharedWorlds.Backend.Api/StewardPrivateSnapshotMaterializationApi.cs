using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Backend.Api;

public static class StewardPrivateSnapshotMaterializationApi
{
    public static IEndpointRouteBuilder MapStewardPrivateSnapshotMaterializationApiV1(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
            "/api/v1/private-worlds/{worldId:guid}/materialization-download",
            AuthorizeAsync);
        return endpoints;
    }

    private static async Task<IResult> AuthorizeAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotMaterializationDownloadService materialization,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await materialization.AuthorizeAsync(
            caller,
            new WorldId(worldId),
            cancellationToken);
        return result.Status switch
        {
            AuthorizePrivateSnapshotDownloadStatus.Authorized => Results.Ok(Response(
                "PrivateSnapshotMaterializationDownloadAuthorized",
                retryable: false,
                ToData(result.Plan
                    ?? throw new InvalidDataException(
                        "Materialization authorization omitted its exact plan.")))),
            AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized => Results.NotFound(Response(
                "PrivateSnapshotNotFound",
                retryable: false)),
            AuthorizePrivateSnapshotDownloadStatus.Unavailable => Results.Conflict(Response(
                "PrivateSnapshotMaterializationUnavailable",
                retryable: true,
                new ReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.AlreadyHere => Results.Conflict(Response(
                "PrivateSnapshotAlreadyHere",
                retryable: false,
                new ReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.Conflict => Results.Conflict(Response(
                "PrivateSnapshotHeadConflict",
                retryable: false,
                new ReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure => Results.Conflict(Response(
                "PrivateSnapshotStorageIntegrityFailure",
                retryable: true,
                new ReasonData(result.Reason))),
            _ => throw new InvalidOperationException(
                $"Unhandled private materialization status {result.Status}.")
        };
    }

    private static PrivateSnapshotMaterializationData ToData(
        PrivateSnapshotMaterializationDownloadPlan plan)
        => new(
            plan.WorldId.Value,
            plan.SourceInstallationId,
            plan.StateRevisionId.Value,
            plan.EnvironmentRevisionId.Value,
            plan.GameAdapterId,
            plan.ExpectedByteSize,
            plan.ExpectedSha256,
            plan.EnvironmentManifest,
            plan.StateRevision,
            plan.EnvironmentRevision,
            new DirectTransferAuthorizationData(
                plan.Authorization.Uri.AbsoluteUri,
                plan.Authorization.Method,
                plan.Authorization.RequiredHeaders,
                plan.Authorization.ExpiresAt,
                plan.Authorization.ExpectedByteSize));

    private static PrivateSnapshotMaterializationResponse Response(
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

    public sealed record PrivateSnapshotMaterializationData(
        Guid WorldId,
        string SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        EnvironmentManifest EnvironmentManifest,
        StateRevision StateRevision,
        EnvironmentRevision EnvironmentRevision,
        DirectTransferAuthorizationData Authorization);

    public sealed record DirectTransferAuthorizationData(
        string Uri,
        string Method,
        IReadOnlyDictionary<string, string> RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize);

    public sealed record ReasonData(string Reason);

    public sealed record PrivateSnapshotMaterializationResponse(
        string Code,
        bool Retryable,
        object? Data = null);
}
