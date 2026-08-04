using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public delegate Task<RemotePrivateSnapshotUploadResult> OwnedWorldSnapshotUploadAsync(
    WorldId worldId,
    RevisionId stateRevisionId,
    RevisionId environmentRevisionId,
    string gameAdapterId,
    EnvironmentManifest environmentManifest,
    Stream statePackage,
    CancellationToken cancellationToken);

public delegate Task<RemotePrivateSnapshotRevisionEvidenceResult>
    OwnedWorldSnapshotRevisionEvidencePublishAsync(
        WorldId worldId,
        StateRevision stateRevision,
        EnvironmentRevision environmentRevision,
        CancellationToken cancellationToken);

/// <summary>
/// Publishes immutable bytes and exact revision-record evidence for current owner-private canonical
/// heads. Backend transfer intent owns byte resume state. Two bounded runtime-local confirmation maps
/// prevent unrelated mutations from re-hashing unchanged large packages and allow interrupted evidence
/// publication to retry metadata only after bytes are known to exist.
/// </summary>
public sealed class StewardOwnedWorldSnapshotPublisher
{
    public const int MaximumWorldsPerPass = 10_000;

    private readonly IWorldStorage _storage;
    private readonly OwnedWorldCanonicalSnapshotResolver _resolver;
    private readonly OwnedWorldSnapshotUploadAsync _uploadAsync;
    private readonly OwnedWorldSnapshotRevisionEvidencePublishAsync _publishEvidenceAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<WorldId, ConfirmedHead> _byteConfirmed = [];
    private readonly Dictionary<WorldId, ConfirmedHead> _fullyConfirmed = [];

    public StewardOwnedWorldSnapshotPublisher(
        IWorldStorage storage,
        StewardPrivateSnapshotTransferClient transfers,
        StewardPrivateSnapshotRevisionEvidenceClient revisionEvidence)
        : this(
            storage,
            BindTransfers(transfers),
            BindRevisionEvidence(revisionEvidence))
    {
    }

    public StewardOwnedWorldSnapshotPublisher(
        IWorldStorage storage,
        OwnedWorldSnapshotUploadAsync uploadAsync,
        OwnedWorldSnapshotRevisionEvidencePublishAsync publishEvidenceAsync)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(uploadAsync);
        ArgumentNullException.ThrowIfNull(publishEvidenceAsync);
        _storage = storage;
        _resolver = new OwnedWorldCanonicalSnapshotResolver(storage);
        _uploadAsync = uploadAsync;
        _publishEvidenceAsync = publishEvidenceAsync;
    }

    public async Task PublishAllCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PublishAllCurrentLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishAllCurrentLockedAsync(
        CancellationToken cancellationToken)
    {
        var worlds = await _storage.ListWorldsAsync(cancellationToken);
        if (worlds.Count > MaximumWorldsPerPass)
        {
            throw new InvalidDataException(
                $"Local World storage returned more than {MaximumWorldsPerPass} Worlds for one private snapshot publication pass.");
        }

        var worldsById = new Dictionary<WorldId, World>();
        foreach (var world in worlds)
        {
            if (!worldsById.TryAdd(world.Id, world))
            {
                throw new InvalidDataException(
                    $"Local World storage returned duplicate canonical World ID '{world.Id}'.");
            }
        }

        foreach (var absentWorldId in _byteConfirmed.Keys
                     .Concat(_fullyConfirmed.Keys)
                     .Where(worldId => !worldsById.ContainsKey(worldId))
                     .Distinct()
                     .ToArray())
        {
            _byteConfirmed.Remove(absentWorldId);
            _fullyConfirmed.Remove(absentWorldId);
        }

        var failures = new List<Exception>();
        foreach (var world in worldsById.Values
                     .OrderBy(world => world.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PublishWorldAsync(world, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new InvalidOperationException(
                    $"Could not publish the current private snapshot and exact revision evidence for local World '{world.Id}'.",
                    exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more current local private snapshots or revision-evidence records could not be published.",
                failures);
        }
    }

    private async Task PublishWorldAsync(
        World world,
        CancellationToken cancellationToken)
    {
        var snapshot = await _resolver.ResolveAsync(world, cancellationToken);
        if (snapshot is null)
        {
            _byteConfirmed.Remove(world.Id);
            _fullyConfirmed.Remove(world.Id);
            return;
        }

        var currentHead = new ConfirmedHead(
            snapshot.State.Id,
            snapshot.Environment.Id,
            snapshot.World.GameAdapterId);
        if (_fullyConfirmed.TryGetValue(world.Id, out var fullyConfirmed) &&
            fullyConfirmed == currentHead)
        {
            return;
        }

        if (!_byteConfirmed.TryGetValue(world.Id, out var byteConfirmed) ||
            byteConfirmed != currentHead)
        {
            await using var package = await _storage.OpenRevisionAsync(
                snapshot.World.Id,
                snapshot.State.Id,
                cancellationToken);
            var upload = await _uploadAsync(
                snapshot.World.Id,
                snapshot.State.Id,
                snapshot.Environment.Id,
                snapshot.World.GameAdapterId,
                snapshot.Environment.Manifest,
                package,
                cancellationToken);
            if (upload.Status is not (
                    RemotePrivateSnapshotUploadStatus.Published or
                    RemotePrivateSnapshotUploadStatus.AlreadyPublished))
            {
                throw new IOException(
                    $"Steward private snapshot publication ended with status '{upload.Status}'.");
            }

            ValidateUploadResult(upload, snapshot);
            _byteConfirmed[world.Id] = currentHead;
        }

        var evidence = await _publishEvidenceAsync(
            snapshot.World.Id,
            snapshot.State,
            snapshot.Environment,
            cancellationToken);
        if (evidence.Status is not (
                RemotePrivateSnapshotRevisionEvidenceStatus.Published or
                RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished))
        {
            throw new IOException(
                $"Steward private revision evidence publication ended with status '{evidence.Status}'.");
        }

        ValidateEvidenceResult(evidence, snapshot);
        _fullyConfirmed[world.Id] = currentHead;
    }

    private static void ValidateUploadResult(
        RemotePrivateSnapshotUploadResult result,
        OwnedWorldCanonicalSnapshot snapshot)
    {
        if (result.WorldId != snapshot.World.Id ||
            result.StateRevisionId != snapshot.State.Id ||
            result.EnvironmentRevisionId != snapshot.Environment.Id)
        {
            throw new InvalidDataException(
                "Steward private snapshot success result disagrees with the requested exact canonical head.");
        }
    }

    private static void ValidateEvidenceResult(
        RemotePrivateSnapshotRevisionEvidenceResult result,
        OwnedWorldCanonicalSnapshot snapshot)
    {
        if (result.WorldId != snapshot.World.Id ||
            result.StateRevisionId != snapshot.State.Id ||
            result.EnvironmentRevisionId != snapshot.Environment.Id ||
            result.RecordedAt is null ||
            result.RecordedAt.Value == default)
        {
            throw new InvalidDataException(
                "Steward private revision evidence success result disagrees with the requested exact canonical head.");
        }
    }

    private static OwnedWorldSnapshotUploadAsync BindTransfers(
        StewardPrivateSnapshotTransferClient transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        return transfers.UploadAsync;
    }

    private static OwnedWorldSnapshotRevisionEvidencePublishAsync BindRevisionEvidence(
        StewardPrivateSnapshotRevisionEvidenceClient revisionEvidence)
    {
        ArgumentNullException.ThrowIfNull(revisionEvidence);
        return revisionEvidence.PublishAsync;
    }

    private sealed record ConfirmedHead(
        RevisionId StateRevisionId,
        RevisionId EnvironmentRevisionId,
        string GameAdapterId);
}
