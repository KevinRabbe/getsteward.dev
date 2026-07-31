using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Owns bounded human labels for revisions already present in the canonical World History chain.
/// Checkpoints never copy state bytes or move a World head.
/// </summary>
public sealed class WorldCheckpointService
{
    public const int MaximumCheckpointsPerWorld = 64;
    public const int MaximumCheckpointNameLength = 80;

    private readonly IWorldStorage _storage;

    public WorldCheckpointService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<World> SetAsync(
        World world,
        RevisionId stateRevisionId,
        string name,
        UserIdentity createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(createdBy);
        var normalizedName = NormalizeName(name);

        var history = await new WorldHistoryService(_storage).GetHistoryAsync(
            world,
            WorldHistoryService.DefaultMaximumEntries,
            cancellationToken);
        if (!history.Revisions.Any(revision => revision.Id == stateRevisionId))
        {
            throw new InvalidOperationException(
                "A checkpoint can be attached only to a revision in the visible canonical World History.");
        }

        var checkpoints = world.Checkpoints?.ToList() ?? [];
        var sameRevisionIndex = checkpoints.FindIndex(
            checkpoint => checkpoint.StateRevisionId == stateRevisionId);
        var duplicateName = checkpoints.FirstOrDefault(checkpoint =>
            checkpoint.StateRevisionId != stateRevisionId &&
            string.Equals(checkpoint.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (duplicateName is not null)
        {
            throw new InvalidOperationException(
                $"This World already has a checkpoint named '{normalizedName}'.");
        }

        var checkpoint = new WorldCheckpoint(
            stateRevisionId,
            normalizedName,
            DateTimeOffset.UtcNow,
            createdBy);
        if (sameRevisionIndex >= 0)
        {
            checkpoints[sameRevisionIndex] = checkpoint;
        }
        else
        {
            if (checkpoints.Count >= MaximumCheckpointsPerWorld)
            {
                throw new InvalidOperationException(
                    $"A World can have at most {MaximumCheckpointsPerWorld} named checkpoints.");
            }

            checkpoints.Add(checkpoint);
        }

        var updated = world with
        {
            Checkpoints = checkpoints
                .OrderByDescending(item => item.CreatedAt)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        await _storage.SaveWorldAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<World> RemoveAsync(
        World world,
        RevisionId stateRevisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var checkpoints = world.Checkpoints ?? [];
        var updatedCheckpoints = checkpoints
            .Where(checkpoint => checkpoint.StateRevisionId != stateRevisionId)
            .ToArray();
        if (updatedCheckpoints.Length == checkpoints.Count)
        {
            throw new InvalidOperationException(
                "The selected History entry does not have a named checkpoint.");
        }

        var updated = world with { Checkpoints = updatedCheckpoints };
        await _storage.SaveWorldAsync(updated, cancellationToken);
        return updated;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > MaximumCheckpointNameLength)
        {
            throw new ArgumentException(
                $"Checkpoint names may contain at most {MaximumCheckpointNameLength} characters.",
                nameof(name));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Checkpoint names cannot contain control characters or line breaks.",
                nameof(name));
        }

        return normalized;
    }
}
