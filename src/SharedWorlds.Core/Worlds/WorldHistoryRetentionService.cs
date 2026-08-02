using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed record WorldHistoryRetentionPlan(
    WorldId WorldId,
    int KeepNewestPayloads,
    IReadOnlyList<RevisionId> EvictionCandidates,
    int AlreadyUnavailableCount,
    long PlannedReclaimableBytes,
    bool HasOlderUnscannedHistory);

public sealed record WorldHistoryRetentionResult(
    int EvictedPayloads,
    int AlreadyUnavailablePayloads,
    int NewlyProtectedPayloads,
    long ReclaimedBytes,
    bool HasOlderUnscannedHistory);

/// <summary>
/// Reclaims large local state payloads without deleting immutable History metadata or parent links.
/// Current, recent, and checkpointed revisions are always protected.
/// </summary>
public sealed class WorldHistoryRetentionService
{
    public const int DefaultKeepNewestPayloads = 20;
    public const int MaximumScannedHistoryEntries = 500;

    private readonly IWorldStorage _storage;
    private readonly WorldHistoryService _history;

    public WorldHistoryRetentionService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _history = new WorldHistoryService(storage);
    }

    public async Task<WorldHistoryRetentionPlan> PlanAsync(
        World world,
        int keepNewestPayloads = DefaultKeepNewestPayloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateKeepNewestPayloads(keepNewestPayloads);

        var history = await _history.GetHistoryAsync(
            world,
            MaximumScannedHistoryEntries,
            cancellationToken);
        var checkpointed = world.Checkpoints
            .Select(checkpoint => checkpoint.StateRevisionId)
            .ToHashSet();
        var candidates = new List<RevisionId>();
        var alreadyUnavailable = 0;
        long plannedReclaimableBytes = 0;

        for (var index = 0; index < history.Revisions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = history.Revisions[index];
            var payloadSize = await _storage.GetRevisionPayloadSizeAsync(
                world.Id,
                revision.Id,
                cancellationToken);
            if (payloadSize is null)
            {
                alreadyUnavailable++;
                continue;
            }

            if (payloadSize < 0)
            {
                throw new InvalidDataException(
                    $"Storage reported a negative payload size for state revision '{revision.Id}'.");
            }

            if (index < keepNewestPayloads ||
                revision.Id == world.CurrentStateRevisionId ||
                checkpointed.Contains(revision.Id))
            {
                continue;
            }

            candidates.Add(revision.Id);
            plannedReclaimableBytes = checked(plannedReclaimableBytes + payloadSize.Value);
        }

        return new WorldHistoryRetentionPlan(
            world.Id,
            keepNewestPayloads,
            candidates,
            alreadyUnavailable,
            plannedReclaimableBytes,
            history.HasOlderRevisions);
    }

    public async Task<WorldHistoryRetentionResult> ApplyAsync(
        WorldHistoryRetentionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateKeepNewestPayloads(plan.KeepNewestPayloads);

        var evicted = 0;
        var alreadyUnavailable = 0;
        var newlyProtected = 0;
        long reclaimedBytes = 0;

        foreach (var candidateId in plan.EvictionCandidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveWorld = await _storage.LoadWorldAsync(plan.WorldId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Cannot apply History retention because World '{plan.WorldId}' no longer exists.");
            var liveHistory = await _history.GetHistoryAsync(
                liveWorld,
                MaximumScannedHistoryEntries,
                cancellationToken);
            var liveIndex = FindRevisionIndex(liveHistory.Revisions, candidateId);
            if (liveIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Cannot evict state payload '{candidateId}' because it is no longer in the bounded canonical History of World '{plan.WorldId}'.");
            }

            var isProtected = liveIndex < plan.KeepNewestPayloads ||
                              candidateId == liveWorld.CurrentStateRevisionId ||
                              liveWorld.Checkpoints.Any(
                                  checkpoint => checkpoint.StateRevisionId == candidateId);
            if (isProtected)
            {
                newlyProtected++;
                continue;
            }

            var livePayloadSize = await _storage.GetRevisionPayloadSizeAsync(
                plan.WorldId,
                candidateId,
                cancellationToken);
            if (livePayloadSize is null)
            {
                alreadyUnavailable++;
                continue;
            }

            if (livePayloadSize < 0)
            {
                throw new InvalidDataException(
                    $"Storage reported a negative payload size for state revision '{candidateId}'.");
            }

            if (await _storage.EvictRevisionPayloadAsync(
                    plan.WorldId,
                    candidateId,
                    cancellationToken))
            {
                evicted++;
                reclaimedBytes = checked(reclaimedBytes + livePayloadSize.Value);
            }
            else
            {
                alreadyUnavailable++;
            }
        }

        return new WorldHistoryRetentionResult(
            evicted,
            plan.AlreadyUnavailableCount + alreadyUnavailable,
            newlyProtected,
            reclaimedBytes,
            plan.HasOlderUnscannedHistory);
    }

    private static int FindRevisionIndex(
        IReadOnlyList<StateRevision> revisions,
        RevisionId revisionId)
    {
        for (var index = 0; index < revisions.Count; index++)
        {
            if (revisions[index].Id == revisionId)
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateKeepNewestPayloads(int keepNewestPayloads)
    {
        if (keepNewestPayloads is <= 0 or > MaximumScannedHistoryEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keepNewestPayloads),
                $"History retention must keep between 1 and {MaximumScannedHistoryEntries} newest payloads.");
        }
    }
}
