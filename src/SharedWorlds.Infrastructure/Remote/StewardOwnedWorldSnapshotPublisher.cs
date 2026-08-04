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

/// <summary>
/// Publishes immutable bytes for current owner-private canonical heads. The backend transfer intent
/// owns resume state; this scanner owns no queue and never changes local World authority. Confirmed
/// immutable heads are remembered for this runtime so unrelated local mutations do not re-hash large
/// unchanged packages. A new runtime safely repairs that optimization state from backend idempotence.
/// </summary>
public sealed class StewardOwnedWorldSnapshotPublisher
{
    public const int MaximumWorldsPerPass = 10_000;

    private readonly IWorldStorage _storage;
    private readonly OwnedWorldCanonicalSnapshotResolver _resolver;
    private readonly OwnedWorldSnapshotUploadAsync _uploadAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<WorldId, ConfirmedHead> _confirmed = [];

    public StewardOwnedWorldSnapshotPublisher(
        IWorldStorage storage,
        StewardPrivateSnapshotTransferClient transfers)
        : this(storage, Bind(transfers))
    {
    }

    public StewardOwnedWorldSnapshotPublisher(
        IWorldStorage storage,
        OwnedWorldSnapshotUploadAsync uploadAsync)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(uploadAsync);
        _storage = storage;
        _resolver = new OwnedWorldCanonicalSnapshotResolver(storage);
        _uploadAsync = uploadAsync;
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

        foreach (var absentWorldId in _confirmed.Keys
                     .Where(worldId => !worldsById.ContainsKey(worldId))
                     .ToArray())
        {
            _confirmed.Remove(absentWorldId);
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
                    $"Could not publish the current private snapshot for local World '{world.Id}'.",
                    exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more current local private snapshots could not be published.",
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
            _confirmed.Remove(world.Id);
            return;
        }

        var currentHead = new ConfirmedHead(
            snapshot.State.Id,
            snapshot.Environment.Id,
            snapshot.World.GameAdapterId);
        if (_confirmed.TryGetValue(world.Id, out var confirmed) &&
            confirmed == currentHead)
        {
            return;
        }

        await using var package = await _storage.OpenRevisionAsync(
            snapshot.World.Id,
            snapshot.State.Id,
            cancellationToken);
        var result = await _uploadAsync(
            snapshot.World.Id,
            snapshot.State.Id,
            snapshot.Environment.Id,
            snapshot.World.GameAdapterId,
            snapshot.Environment.Manifest,
            package,
            cancellationToken);
        if (result.Status is not (
                RemotePrivateSnapshotUploadStatus.Published or
                RemotePrivateSnapshotUploadStatus.AlreadyPublished))
        {
            throw new IOException(
                $"Steward private snapshot publication ended with status '{result.Status}'.");
        }

        _confirmed[world.Id] = currentHead;
    }

    private static OwnedWorldSnapshotUploadAsync Bind(
        StewardPrivateSnapshotTransferClient transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        return transfers.UploadAsync;
    }

    private sealed record ConfirmedHead(
        RevisionId StateRevisionId,
        RevisionId EnvironmentRevisionId,
        string GameAdapterId);
}
