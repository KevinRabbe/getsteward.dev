using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Backend.Api;

public static class StewardPrivateSnapshotTransferApi
{
    public static IEndpointRouteBuilder MapStewardPrivateSnapshotTransferApiV1(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
            "/api/v1/private-worlds/{worldId:guid}/snapshot-upload",
            BeginUploadAsync);
        endpoints.MapGet(
            "/api/v1/private-snapshot-transfers/{transferId:guid}",
            GetProgressAsync);
        endpoints.MapPost(
            "/api/v1/private-snapshot-transfers/{transferId:guid}/parts/{partNumber:int}/authorization",
            AuthorizePartAsync);
        endpoints.MapPost(
            "/api/v1/private-snapshot-transfers/{transferId:guid}/finalize",
            FinalizeAsync);
        endpoints.MapGet(
            "/api/v1/private-worlds/{worldId:guid}/snapshot-download",
            AuthorizeDownloadAsync);

        return endpoints;
    }

    private static async Task<IResult> BeginUploadAsync(
        Guid worldId,
        BeginPrivateSnapshotUploadRequest body,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotTransferService transfers,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await transfers.BeginUploadAsync(
            caller,
            new BeginPrivateSnapshotUploadCommand(
                new WorldId(worldId),
                new RevisionId(body.StateRevisionId),
                new RevisionId(body.EnvironmentRevisionId),
                body.GameAdapterId,
                body.ExpectedByteSize,
                body.ExpectedSha256,
                body.EnvironmentManifest),
            cancellationToken);
        return result.Status switch
        {
            BeginPrivateSnapshotUploadStatus.Started => Results.Ok(Response(
                "PrivateSnapshotUploadStarted",
                retryable: false,
                ToTransferData(result.Transfer
                    ?? throw new InvalidDataException(
                        "Private snapshot upload started without transfer state.")))),
            BeginPrivateSnapshotUploadStatus.AlreadyPublished => Results.Ok(Response(
                "PrivateSnapshotAlreadyPublished",
                retryable: false)),
            BeginPrivateSnapshotUploadStatus.NotFoundOrUnauthorized => Results.NotFound(Response(
                "PrivateSnapshotNotFound",
                retryable: false)),
            BeginPrivateSnapshotUploadStatus.InvalidRequest => Results.BadRequest(Response(
                "PrivateSnapshotInvalidRequest",
                retryable: false)),
            BeginPrivateSnapshotUploadStatus.Conflict => Results.Conflict(Response(
                "PrivateSnapshotConflict",
                retryable: false)),
            BeginPrivateSnapshotUploadStatus.StorageIntegrityFailure => Results.Conflict(Response(
                "PrivateSnapshotStorageIntegrityFailure",
                retryable: false)),
            _ => throw new InvalidOperationException(
                $"Unhandled private snapshot begin status {result.Status}.")
        };
    }

    private static async Task<IResult> GetProgressAsync(
        Guid transferId,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotTransferService transfers,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (transferId == Guid.Empty)
        {
            return Results.BadRequest(Response(
                "PrivateSnapshotTransferInvalid",
                retryable: false));
        }

        var progress = await transfers.GetProgressAsync(
            caller,
            new PrivateSnapshotTransferId(transferId),
            cancellationToken);
        return progress is null
            ? Results.NotFound(Response(
                "PrivateSnapshotTransferNotFound",
                retryable: false))
            : Results.Ok(Response(
                "PrivateSnapshotTransferProgress",
                retryable: false,
                ToTransferData(
                    progress.Transfer,
                    progress.CompletedParts,
                    progress.ProviderUploadCompleted)));
    }

    private static async Task<IResult> AuthorizePartAsync(
        Guid transferId,
        int partNumber,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotTransferService transfers,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (transferId == Guid.Empty)
        {
            return Results.BadRequest(Response(
                "PrivateSnapshotTransferInvalid",
                retryable: false));
        }

        var result = await transfers.AuthorizePartAsync(
            caller,
            new PrivateSnapshotTransferId(transferId),
            partNumber,
            cancellationToken);
        return result.Status switch
        {
            AuthorizePrivateSnapshotPartStatus.Authorized => Results.Ok(Response(
                "PrivateSnapshotPartAuthorized",
                retryable: false,
                new PrivateSnapshotPartAuthorizationData(
                    result.Authorization
                        ?? throw new InvalidDataException(
                            "Private snapshot part was authorized without transfer authorization.")))),
            AuthorizePrivateSnapshotPartStatus.TransferNotFound => Results.NotFound(Response(
                "PrivateSnapshotTransferNotFound",
                retryable: false)),
            AuthorizePrivateSnapshotPartStatus.TransferNotActive => Results.Conflict(Response(
                "PrivateSnapshotTransferNotActive",
                retryable: false)),
            AuthorizePrivateSnapshotPartStatus.Expired => Results.Conflict(Response(
                "PrivateSnapshotTransferExpired",
                retryable: true)),
            AuthorizePrivateSnapshotPartStatus.InvalidPart => Results.BadRequest(Response(
                "PrivateSnapshotPartInvalid",
                retryable: false)),
            _ => throw new InvalidOperationException(
                $"Unhandled private snapshot part status {result.Status}.")
        };
    }

    private static async Task<IResult> FinalizeAsync(
        Guid transferId,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotTransferService transfers,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (transferId == Guid.Empty)
        {
            return Results.BadRequest(Response(
                "PrivateSnapshotTransferInvalid",
                retryable: false));
        }

        var result = await transfers.FinalizeAsync(
            caller,
            new PrivateSnapshotTransferId(transferId),
            cancellationToken);
        return result.Status switch
        {
            FinalizePrivateSnapshotUploadStatus.Finalized => Results.Ok(Response(
                "PrivateSnapshotFinalized",
                retryable: false,
                result.Snapshot is null ? null : ToSnapshotData(result.Snapshot))),
            FinalizePrivateSnapshotUploadStatus.AlreadyFinalized => Results.Ok(Response(
                "PrivateSnapshotAlreadyFinalized",
                retryable: false,
                result.Snapshot is null ? null : ToSnapshotData(result.Snapshot))),
            FinalizePrivateSnapshotUploadStatus.TransferNotFound => Results.NotFound(Response(
                "PrivateSnapshotTransferNotFound",
                retryable: false)),
            FinalizePrivateSnapshotUploadStatus.TransferNotActive => Results.Conflict(Response(
                "PrivateSnapshotTransferNotActive",
                retryable: false)),
            FinalizePrivateSnapshotUploadStatus.Expired => Results.Conflict(Response(
                "PrivateSnapshotTransferExpired",
                retryable: true)),
            FinalizePrivateSnapshotUploadStatus.IntegrityMismatch => Results.Conflict(Response(
                "PrivateSnapshotIntegrityMismatch",
                retryable: false)),
            FinalizePrivateSnapshotUploadStatus.PublicationBlocked => Results.Conflict(Response(
                "PrivateSnapshotPublicationBlocked",
                retryable: false)),
            FinalizePrivateSnapshotUploadStatus.SnapshotConflict => Results.Conflict(Response(
                "PrivateSnapshotConflict",
                retryable: false,
                result.Snapshot is null ? null : ToSnapshotData(result.Snapshot))),
            _ => throw new InvalidOperationException(
                $"Unhandled private snapshot finalize status {result.Status}.")
        };
    }

    private static async Task<IResult> AuthorizeDownloadAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        PrivateSnapshotTransferService transfers,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await transfers.AuthorizeDownloadAsync(
            caller,
            new WorldId(worldId),
            cancellationToken);
        return result.Status switch
        {
            AuthorizePrivateSnapshotDownloadStatus.Authorized => Results.Ok(Response(
                "PrivateSnapshotDownloadAuthorized",
                retryable: false,
                ToDownloadData(result.Plan
                    ?? throw new InvalidDataException(
                        "Private snapshot download was authorized without a transfer plan.")))),
            AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized => Results.NotFound(Response(
                "PrivateSnapshotNotFound",
                retryable: false)),
            AuthorizePrivateSnapshotDownloadStatus.Unavailable => Results.Conflict(Response(
                "PrivateSnapshotUnavailable",
                retryable: true,
                new PrivateSnapshotReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.AlreadyHere => Results.Conflict(Response(
                "PrivateSnapshotAlreadyHere",
                retryable: false,
                new PrivateSnapshotReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.Conflict => Results.Conflict(Response(
                "PrivateSnapshotHeadConflict",
                retryable: false,
                new PrivateSnapshotReasonData(result.Reason))),
            AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure => Results.Conflict(Response(
                "PrivateSnapshotStorageIntegrityFailure",
                retryable: false,
                new PrivateSnapshotReasonData(result.Reason))),
            _ => throw new InvalidOperationException(
                $"Unhandled private snapshot download status {result.Status}.")
        };
    }

    private static PrivateSnapshotTransferData ToTransferData(
        PrivateSnapshotTransferRecord transfer,
        IReadOnlyList<ImmutableUploadedPart>? completedParts = null,
        bool providerUploadCompleted = false)
        => new(
            transfer.Id.Value,
            transfer.WorldId.Value,
            transfer.StateRevisionId.Value,
            transfer.EnvironmentRevisionId.Value,
            transfer.GameAdapterId,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.PartSizeBytes,
            transfer.PartCount,
            transfer.CreatedAt,
            transfer.ExpiresAt,
            (int)transfer.State,
            completedParts ?? Array.Empty<ImmutableUploadedPart>(),
            providerUploadCompleted);

    private static PrivateSnapshotData ToSnapshotData(OwnedWorldSnapshot snapshot)
        => new(
            snapshot.WorldId.Value,
            snapshot.InstallationId,
            snapshot.StateRevisionId.Value,
            snapshot.EnvironmentRevisionId.Value,
            snapshot.GameAdapterId,
            snapshot.StatePackageByteSize,
            snapshot.StatePackageSha256,
            snapshot.EnvironmentManifest,
            snapshot.PublishedAt);

    private static PrivateSnapshotDownloadData ToDownloadData(
        PrivateSnapshotDownloadPlan plan)
        => new(
            plan.WorldId.Value,
            plan.SourceInstallationId,
            plan.StateRevisionId.Value,
            plan.EnvironmentRevisionId.Value,
            plan.GameAdapterId,
            plan.ExpectedByteSize,
            plan.ExpectedSha256,
            plan.EnvironmentManifest,
            plan.Authorization);

    private static PrivateSnapshotTransferResponse Response(
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

    public sealed record BeginPrivateSnapshotUploadRequest(
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        EnvironmentManifest EnvironmentManifest);

    public sealed record PrivateSnapshotTransferData(
        Guid TransferId,
        Guid WorldId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        int PartSizeBytes,
        int PartCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int State,
        IReadOnlyList<ImmutableUploadedPart> CompletedParts,
        bool ProviderUploadCompleted);

    public sealed record PrivateSnapshotPartAuthorizationData(
        DirectObjectTransferAuthorization Authorization);

    public sealed record PrivateSnapshotData(
        Guid WorldId,
        string SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        EnvironmentManifest EnvironmentManifest,
        DateTimeOffset PublishedAt);

    public sealed record PrivateSnapshotDownloadData(
        Guid WorldId,
        string SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        EnvironmentManifest EnvironmentManifest,
        DirectObjectTransferAuthorization Authorization);

    public sealed record PrivateSnapshotReasonData(string Reason);

    public sealed record PrivateSnapshotTransferResponse(
        string Code,
        bool Retryable,
        object? Data = null);
}
