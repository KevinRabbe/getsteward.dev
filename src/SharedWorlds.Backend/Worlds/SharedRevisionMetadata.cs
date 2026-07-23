using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Backend.Worlds;

public sealed record SharedStateRevisionMetadata(
    WorldId WorldId,
    RevisionId RevisionId,
    string AdapterId,
    string PackageObjectKey,
    long ByteSize,
    string Sha256,
    RevisionId? RequiredEnvironmentRevisionId,
    ExternalIdentityRef PublishedBy,
    DateTimeOffset PublishedAt);

/// <summary>
/// Environment revisions may point either to a Steward-hosted immutable package or to an opaque
/// reproducible/native artifact reference. The structured manifest is the game-agnostic runtime
/// contract used by adapters to reproduce the exact environment. Legacy rows may not have a manifest;
/// new remote-runtime environment revisions publish one explicitly.
/// </summary>
public sealed record SharedEnvironmentRevisionMetadata(
    WorldId WorldId,
    RevisionId RevisionId,
    string AdapterId,
    string ArtifactReference,
    long? ByteSize,
    string? Sha256,
    ExternalIdentityRef PublishedBy,
    DateTimeOffset PublishedAt,
    EnvironmentManifest? Manifest = null);

public enum StoreRevisionMetadataStatus
{
    Recorded,
    AlreadyRecorded,
    Conflict
}

public interface ISharedRevisionMetadataStore
{
    Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
        SharedStateRevisionMetadata revision,
        CancellationToken cancellationToken = default);

    Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
        SharedEnvironmentRevisionMetadata revision,
        CancellationToken cancellationToken = default);

    Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default);
}

public enum RecordRevisionMetadataStatus
{
    Recorded,
    AlreadyRecorded,
    WorldNotFound,
    AdapterMismatch,
    RequiredEnvironmentMissing,
    Conflict
}

public enum PublishEnvironmentManifestStatus
{
    Published,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidManifest,
    AdapterMismatch,
    Conflict
}

public sealed record SharedCurrentRevisionMetadata(
    SharedWorldMetadata World,
    SharedStateRevisionMetadata? State,
    SharedEnvironmentRevisionMetadata? Environment);

/// <summary>
/// BE-2 metadata catalog for already-verified immutable artifacts. Recording is an internal backend
/// operation called only after the upstream transfer/session layer has authorized the caller and
/// verified bytes/reference integrity. Normal queries require active World membership. The explicit
/// environment-manifest publication method is the authenticated desktop boundary for native/reproducible
/// environments that have no hosted package bytes.
/// </summary>
public sealed class SharedRevisionMetadataService
{
    private readonly ISharedWorldMetadataStore _worldStore;
    private readonly ISharedRevisionMetadataStore _revisionStore;

    public SharedRevisionMetadataService(
        ISharedWorldMetadataStore worldStore,
        ISharedRevisionMetadataStore revisionStore)
    {
        ArgumentNullException.ThrowIfNull(worldStore);
        ArgumentNullException.ThrowIfNull(revisionStore);
        _worldStore = worldStore;
        _revisionStore = revisionStore;
    }

    public async Task<RecordRevisionMetadataStatus> RecordVerifiedEnvironmentRevisionAsync(
        SharedEnvironmentRevisionMetadata revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateEnvironmentRevision(revision);

        var world = await _worldStore.LoadWorldAsync(revision.WorldId, cancellationToken);
        if (world is null)
        {
            return RecordRevisionMetadataStatus.WorldNotFound;
        }

        if (!string.Equals(world.AdapterId, revision.AdapterId, StringComparison.Ordinal))
        {
            return RecordRevisionMetadataStatus.AdapterMismatch;
        }

        return MapStoreResult(await _revisionStore.TryRecordEnvironmentRevisionAsync(
            revision,
            cancellationToken));
    }

    public async Task<PublishEnvironmentManifestStatus> PublishEnvironmentManifestAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        RevisionId revisionId,
        EnvironmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifest);
        if (worldId.Value == Guid.Empty || revisionId.Value == Guid.Empty || !IsValidManifest(manifest))
        {
            return PublishEnvironmentManifestStatus.InvalidManifest;
        }

        if (!await IsActiveMemberAsync(worldId, caller.Subject, cancellationToken))
        {
            return PublishEnvironmentManifestStatus.NotFoundOrUnauthorized;
        }

        var world = await _worldStore.LoadWorldAsync(worldId, cancellationToken);
        if (world is null)
        {
            return PublishEnvironmentManifestStatus.NotFoundOrUnauthorized;
        }

        if (!string.Equals(world.AdapterId, manifest.AdapterId, StringComparison.Ordinal))
        {
            return PublishEnvironmentManifestStatus.AdapterMismatch;
        }

        var revision = new SharedEnvironmentRevisionMetadata(
            worldId,
            revisionId,
            world.AdapterId,
            $"manifest:{revisionId.Value:N}",
            ByteSize: null,
            Sha256: null,
            caller.Subject,
            DateTimeOffset.UtcNow,
            manifest);
        return await _revisionStore.TryRecordEnvironmentRevisionAsync(revision, cancellationToken) switch
        {
            StoreRevisionMetadataStatus.Recorded => PublishEnvironmentManifestStatus.Published,
            StoreRevisionMetadataStatus.AlreadyRecorded => PublishEnvironmentManifestStatus.AlreadyPublished,
            StoreRevisionMetadataStatus.Conflict => PublishEnvironmentManifestStatus.Conflict,
            _ => throw new InvalidOperationException("Unexpected environment-manifest store result.")
        };
    }

    public async Task<RecordRevisionMetadataStatus> RecordVerifiedStateRevisionAsync(
        SharedStateRevisionMetadata revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateStateRevision(revision);

        var world = await _worldStore.LoadWorldAsync(revision.WorldId, cancellationToken);
        if (world is null)
        {
            return RecordRevisionMetadataStatus.WorldNotFound;
        }

        if (!string.Equals(world.AdapterId, revision.AdapterId, StringComparison.Ordinal))
        {
            return RecordRevisionMetadataStatus.AdapterMismatch;
        }

        if (revision.RequiredEnvironmentRevisionId is { } requiredEnvironment &&
            await _revisionStore.LoadEnvironmentRevisionAsync(
                revision.WorldId,
                requiredEnvironment,
                cancellationToken) is null)
        {
            return RecordRevisionMetadataStatus.RequiredEnvironmentMissing;
        }

        return MapStoreResult(await _revisionStore.TryRecordStateRevisionAsync(
            revision,
            cancellationToken));
    }

    public async Task<SharedCurrentRevisionMetadata?> GetCurrentRevisionMetadataAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);

        var membership = await _worldStore.LoadMemberAsync(worldId, caller.Subject, cancellationToken);
        if (membership?.Status != SharedWorldMemberStatus.Active)
        {
            return null;
        }

        var world = await _worldStore.LoadWorldAsync(worldId, cancellationToken);
        if (world is null)
        {
            return null;
        }

        var state = await _revisionStore.LoadStateRevisionAsync(
            worldId,
            world.CurrentStateRevisionId,
            cancellationToken);
        SharedEnvironmentRevisionMetadata? environment = null;
        if (world.CurrentEnvironmentRevisionId is { } environmentRevisionId)
        {
            environment = await _revisionStore.LoadEnvironmentRevisionAsync(
                worldId,
                environmentRevisionId,
                cancellationToken);
        }

        return new SharedCurrentRevisionMetadata(world, state, environment);
    }

    public async Task<SharedStateRevisionMetadata?> GetStateRevisionMetadataAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);
        ValidateRevisionId(revisionId, nameof(revisionId));

        if (!await IsActiveMemberAsync(worldId, caller.Subject, cancellationToken))
        {
            return null;
        }

        return await _revisionStore.LoadStateRevisionAsync(worldId, revisionId, cancellationToken);
    }

    public async Task<SharedEnvironmentRevisionMetadata?> GetEnvironmentRevisionMetadataAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);
        ValidateRevisionId(revisionId, nameof(revisionId));

        if (!await IsActiveMemberAsync(worldId, caller.Subject, cancellationToken))
        {
            return null;
        }

        return await _revisionStore.LoadEnvironmentRevisionAsync(worldId, revisionId, cancellationToken);
    }

    private async Task<bool> IsActiveMemberAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken)
    {
        var membership = await _worldStore.LoadMemberAsync(worldId, identity, cancellationToken);
        return membership?.Status == SharedWorldMemberStatus.Active;
    }

    private static RecordRevisionMetadataStatus MapStoreResult(StoreRevisionMetadataStatus status)
        => status switch
        {
            StoreRevisionMetadataStatus.Recorded => RecordRevisionMetadataStatus.Recorded,
            StoreRevisionMetadataStatus.AlreadyRecorded => RecordRevisionMetadataStatus.AlreadyRecorded,
            StoreRevisionMetadataStatus.Conflict => RecordRevisionMetadataStatus.Conflict,
            _ => throw new InvalidOperationException("Unexpected revision metadata store result.")
        };

    private static void ValidateStateRevision(SharedStateRevisionMetadata revision)
    {
        ValidateWorldId(revision.WorldId);
        ValidateRevisionId(revision.RevisionId, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.PackageObjectKey);
        ValidatePackageIntegrity(revision.ByteSize, revision.Sha256, nameof(revision));

        if (revision.RequiredEnvironmentRevisionId is { } environmentRevisionId)
        {
            ValidateRevisionId(environmentRevisionId, nameof(revision));
        }
    }

    private static void ValidateEnvironmentRevision(SharedEnvironmentRevisionMetadata revision)
    {
        ValidateWorldId(revision.WorldId);
        ValidateRevisionId(revision.RevisionId, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.ArtifactReference);

        if (revision.ByteSize.HasValue != (revision.Sha256 is not null))
        {
            throw new ArgumentException(
                "Environment byte size and SHA-256 must be supplied together.",
                nameof(revision));
        }

        if (revision.ByteSize is { } byteSize && revision.Sha256 is { } sha256)
        {
            ValidatePackageIntegrity(byteSize, sha256, nameof(revision));
        }

        if (revision.Manifest is not null &&
            (!IsValidManifest(revision.Manifest) ||
             !string.Equals(revision.Manifest.AdapterId, revision.AdapterId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Environment manifest is invalid or belongs to a different adapter.",
                nameof(revision));
        }
    }

    private static bool IsValidManifest(EnvironmentManifest manifest)
        => manifest.SchemaVersion > 0 &&
           !string.IsNullOrWhiteSpace(manifest.AdapterId) &&
           !string.IsNullOrWhiteSpace(manifest.GameVersion) &&
           manifest.Components is not null &&
           manifest.Configuration is not null &&
           manifest.Components.All(component =>
               component is not null &&
               !string.IsNullOrWhiteSpace(component.Kind) &&
               !string.IsNullOrWhiteSpace(component.Id));

    private static void ValidatePackageIntegrity(long byteSize, string sha256, string parameterName)
    {
        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Package byte size must be positive.");
        }

        if (sha256.Length != 64 || sha256.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new ArgumentException("SHA-256 must be exactly 64 hexadecimal characters.", parameterName);
        }
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateRevisionId(RevisionId revisionId, string parameterName)
    {
        if (revisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", parameterName);
        }
    }
}
