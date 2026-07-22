using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardWorldStorageOptions
{
    public StewardWorldStorageOptions(int commitTransportAttempts)
    {
        if (commitTransportAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(commitTransportAttempts));
        }

        CommitTransportAttempts = commitTransportAttempts;
    }

    public int CommitTransportAttempts { get; }

    public static StewardWorldStorageOptions FirstReleaseDefaults { get; } = new(
        commitTransportAttempts: 2);
}

public sealed class StewardWorldStorageException : InvalidOperationException
{
    public StewardWorldStorageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class StewardCommitOutcomeUnknownException : IOException
{
    public StewardCommitOutcomeUnknownException(
        WorldId worldId,
        RevisionId candidateRevisionId,
        Exception innerException)
        : base(
            $"Steward could not determine whether candidate revision '{candidateRevisionId}' became canonical for World '{worldId}'. " +
            "The writable reservation and recovery evidence remain unresolved; retry the same commit after connectivity returns.",
            innerException)
    {
        WorldId = worldId;
        CandidateRevisionId = candidateRevisionId;
    }

    public WorldId WorldId { get; }
    public RevisionId CandidateRevisionId { get; }
}

/// <summary>
/// Remote IWorldStorage implementation for already-created shared Worlds. Immutable state bytes use
/// the verified BE-3 transfer/cache path; environment manifests use immutable backend metadata; and
/// SaveWorldAsync maps the Core "write canonical head last" boundary onto the exact BE-4 reservation
/// generation registered by <see cref="StewardWorldSessionCoordinator"/>.
///
/// Before any candidate bytes are published, the candidate revision ID is written into the local
/// workspace recovery journal. This is the write-ahead record that lets Waiting to sync distinguish
/// an interrupted upload, a lost commit response, and an already-successful canonical commit.
/// </summary>
public sealed class StewardWorldStorage : IWorldStorage
{
    private readonly StewardWorldMetadataClient _metadata;
    private readonly StewardVerifiedPackageSource _packages;
    private readonly StewardPackageUploadClient _uploads;
    private readonly StewardAuthorityClient _authority;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly StewardWorldStorageOptions _options;

    public StewardWorldStorage(
        StewardWorldMetadataClient metadata,
        StewardVerifiedPackageSource packages,
        StewardPackageUploadClient uploads,
        StewardAuthorityClient authority,
        IStewardAccessTokenProvider accessTokens,
        StewardWritableReservationRegistry reservations,
        IWorkspaceRecoveryStore recovery,
        StewardWorldStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(recovery);

        _metadata = metadata;
        _packages = packages;
        _uploads = uploads;
        _authority = authority;
        _accessTokens = accessTokens;
        _reservations = reservations;
        _recovery = recovery;
        _options = options ?? StewardWorldStorageOptions.FirstReleaseDefaults;
    }

    public async Task<World?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var remote = await _metadata.GetWorldAsync(worldId, accessToken, cancellationToken);
        return remote is null ? null : ToCoreWorld(remote);
    }

    public async Task<IReadOnlyList<World>> ListWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var worlds = await _metadata.ListWorldsAsync(accessToken, cancellationToken);
        return worlds.Select(ToCoreWorld).ToArray();
    }

    public async Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Id.Value == Guid.Empty || revision.WorldId.Value == Guid.Empty)
        {
            throw new ArgumentException("Environment revision identity is required.", nameof(revision));
        }

        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var result = await _metadata.PublishEnvironmentRevisionAsync(
            revision.WorldId,
            revision.Id,
            revision.Manifest,
            accessToken,
            cancellationToken);
        if (result is RemoteEnvironmentPublishStatus.Published or
            RemoteEnvironmentPublishStatus.AlreadyPublished)
        {
            return;
        }

        throw result switch
        {
            RemoteEnvironmentPublishStatus.NotFoundOrUnauthorized => new StewardWorldStorageException(
                "WorldNotFoundOrUnauthorized",
                "The shared World must already exist and the caller must be an active member before an environment revision can be published."),
            RemoteEnvironmentPublishStatus.InvalidManifest => new StewardWorldStorageException(
                "InvalidEnvironmentManifest",
                "The adapter returned an invalid environment manifest."),
            RemoteEnvironmentPublishStatus.AdapterMismatch => new StewardWorldStorageException(
                "AdapterMismatch",
                "The environment manifest belongs to a different game adapter than the shared World."),
            RemoteEnvironmentPublishStatus.RevisionConflict => new StewardWorldStorageException(
                "RevisionConflict",
                "This environment revision ID already refers to different immutable metadata."),
            _ => new StewardWorldStorageException(
                "EnvironmentPublicationFailed",
                "Steward rejected the environment revision publication.")
        };
    }

    public async Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var remote = await _metadata.GetEnvironmentRevisionAsync(
            worldId,
            revisionId,
            accessToken,
            cancellationToken);
        if (remote is null)
        {
            return null;
        }

        if (remote.Manifest is null)
        {
            throw new StewardWorldStorageException(
                "EnvironmentManifestUnavailable",
                "This legacy environment revision does not contain the structured manifest required for remote play.");
        }

        return new EnvironmentRevision(
            remote.RevisionId,
            worldId,
            ParentRevisionId: null,
            remote.PublishedAt,
            CreatedBy: null,
            remote.Manifest);
    }

    public async Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(package);

        var lease = RequireLease(revision.WorldId);
        await JournalCandidateAsync(revision, lease, cancellationToken);

        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var result = await _uploads.UploadAsync(
            revision.WorldId,
            revision.Id,
            RemotePackageKind.State,
            package,
            lease.StartingHead.EnvironmentRevisionId,
            accessToken,
            cancellationToken);
        if (result.Status is RemotePackageUploadStatus.Published or
            RemotePackageUploadStatus.AlreadyPublished)
        {
            return;
        }

        throw new StewardWorldStorageException(
            result.Status.ToString(),
            $"Steward rejected candidate state revision '{revision.Id}' with status '{result.Status}'.");
    }

    public async Task<StateRevision?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var remote = await _metadata.GetStateRevisionAsync(
            worldId,
            revisionId,
            accessToken,
            cancellationToken);
        if (remote is null)
        {
            return null;
        }

        var world = await _metadata.GetWorldAsync(worldId, accessToken, cancellationToken);
        if (world is null)
        {
            return null;
        }

        return new StateRevision(
            remote.RevisionId,
            worldId,
            ParentRevisionId: null,
            remote.PublishedAt,
            CreatedBy: null,
            world.AdapterId,
            remote.RevisionId.ToString());
    }

    public async Task<Stream> OpenRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        return await _packages.OpenStateRevisionAsync(
            worldId,
            revisionId,
            accessToken,
            cancellationToken);
    }

    public async Task SaveWorldAsync(
        World world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var candidateStateRevisionId = world.CurrentStateRevisionId
            ?? throw new StewardWorldStorageException(
                "MissingStateRevision",
                "A shared World cannot commit without a candidate state revision.");
        var lease = RequireLease(world.Id);

        if (world.CurrentEnvironmentRevisionId != lease.StartingHead.EnvironmentRevisionId)
        {
            throw new StewardWorldStorageException(
                "EnvironmentHeadChangeUnsupported",
                "The current E4 runtime path commits gameplay state against the reserved environment. Environment upgrades require a separate reviewed transition.");
        }

        var idempotencyKey = CreateCommitIdempotencyKey(
            lease,
            candidateStateRevisionId,
            world.CurrentEnvironmentRevisionId);
        RemoteWorldCommitResult? result = null;
        Exception? lastTransportFailure = null;

        for (var attempt = 0; attempt < _options.CommitTransportAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
                result = await _authority.CommitAsync(
                    world.Id,
                    lease.InstallationId,
                    lease.SessionId,
                    lease.Generation,
                    lease.StartingHead,
                    candidateStateRevisionId,
                    world.CurrentEnvironmentRevisionId,
                    accessToken,
                    idempotencyKey,
                    cancellationToken);
                lastTransportFailure = null;
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastTransportFailure = new TimeoutException(
                    "Steward canonical commit timed out before the outcome was known.");
            }
            catch (StewardRemoteApiException exception) when (exception.Retryable)
            {
                lastTransportFailure = exception;
            }
            catch (HttpRequestException exception)
            {
                lastTransportFailure = exception;
            }
            catch (IOException exception)
            {
                lastTransportFailure = exception;
            }
        }

        if (result is null)
        {
            throw new StewardCommitOutcomeUnknownException(
                world.Id,
                candidateStateRevisionId,
                lastTransportFailure ?? new IOException("Canonical commit outcome is unknown."));
        }

        switch (result.Status)
        {
            case RemoteWorldCommitStatus.Committed:
            case RemoteWorldCommitStatus.Unchanged:
                _reservations.TryResolve(world.Id, lease.SessionId, lease.Generation);
                return;

            case RemoteWorldCommitStatus.HeadChanged:
                throw new StewardWorldStorageException(
                    "HeadChanged",
                    "The canonical World head changed before this candidate could commit. The candidate remains immutable recovery evidence.");

            case RemoteWorldCommitStatus.ReservationMismatch:
                throw new StewardWorldStorageException(
                    "ReservationMismatch",
                    "The exact writable reservation generation is no longer valid. The candidate was not made canonical by this request.");

            case RemoteWorldCommitStatus.InvalidCandidate:
                throw new StewardWorldStorageException(
                    "InvalidCandidate",
                    "The uploaded candidate does not satisfy the reserved World/environment invariants.");

            case RemoteWorldCommitStatus.IdempotencyKeyConflict:
                throw new InvalidDataException(
                    "Steward reported an idempotency conflict for a deterministic canonical commit key.");

            default:
                throw new InvalidOperationException("Unexpected canonical commit status.");
        }
    }

    private async Task JournalCandidateAsync(
        StateRevision revision,
        StewardWritableReservationLease lease,
        CancellationToken cancellationToken)
    {
        var records = await _recovery.ListAsync(cancellationToken);
        var record = records
            .Where(candidate =>
                candidate.WorldId == revision.WorldId &&
                candidate.BaseStateRevisionId == lease.StartingHead.StateRevisionId &&
                candidate.Status is WorkspaceRecoveryStatus.Active or WorkspaceRecoveryStatus.RecoveryPending)
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (record is null)
        {
            throw new StewardWorldStorageException(
                "RecoveryJournalMissing",
                "Candidate publication was blocked because no matching writable workspace recovery journal exists.");
        }

        if (record.CandidateStateRevisionId is { } existingCandidate)
        {
            if (existingCandidate != revision.Id)
            {
                throw new StewardWorldStorageException(
                    "RecoveryCandidateConflict",
                    $"Workspace recovery already tracks candidate '{existingCandidate}', so candidate '{revision.Id}' cannot replace it.");
            }

            return;
        }

        await _recovery.SaveAsync(
            record with
            {
                CandidateStateRevisionId = revision.Id,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private StewardWritableReservationLease RequireLease(WorldId worldId)
        => _reservations.Get(worldId)
           ?? throw new StewardWorldStorageException(
               "WritableReservationRequired",
               "A remote shared World can be modified only while this Steward installation owns the exact writable reservation generation.");

    private static World ToCoreWorld(StewardRemoteWorldMetadata remote)
    {
        var manager = new UserIdentity(
            remote.AccessManager.Provider,
            remote.AccessManager.ExternalId,
            remote.AccessManager.ExternalId);
        return new World(
            remote.WorldId,
            remote.DisplayName,
            remote.AdapterId,
            [manager],
            remote.CurrentEnvironmentRevisionId,
            remote.CurrentStateRevisionId)
        {
            SharingMode = WorldSharingMode.Shared
        };
    }

    private static string CreateCommitIdempotencyKey(
        StewardWritableReservationLease lease,
        RevisionId candidateStateRevisionId,
        RevisionId? candidateEnvironmentRevisionId)
        => $"commit-{lease.SessionId:N}-{lease.Generation}-{candidateStateRevisionId.Value:N}-" +
           (candidateEnvironmentRevisionId?.Value.ToString("N") ?? "none");
}
