namespace SharedWorlds.Infrastructure.BackendSimulation;

public sealed record SimulatedCanonicalRetentionSnapshot(
    string WorldId,
    string? CurrentStateRevisionId,
    IReadOnlyList<string> RetainedStateRevisionIds,
    IReadOnlyList<string> CleanupEligibleStateRevisionIds,
    IReadOnlyList<string> RetainedEnvironmentRevisionIds);

/// <summary>
/// Deterministic BE-D010 retention model. It computes retention and cleanup eligibility only;
/// it does not physically delete revision bytes.
/// </summary>
public sealed class InMemoryCanonicalRetentionPolicy
{
    public const int NormalCanonicalStateCount = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, WorldRetentionRecord> _worlds = new(StringComparer.Ordinal);

    public void RecordCanonicalState(
        string worldId,
        string stateRevisionId,
        string? environmentRevisionId = null)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(stateRevisionId, nameof(stateRevisionId));

        lock (_gate)
        {
            var world = GetOrCreateWorld(worldId);
            if (world.States.Any(state => string.Equals(
                    state.StateRevisionId,
                    stateRevisionId,
                    StringComparison.Ordinal)))
            {
                return;
            }

            world.States.Add(new StateReference(stateRevisionId, environmentRevisionId));
        }
    }

    public bool PinState(string worldId, string stateRevisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(stateRevisionId, nameof(stateRevisionId));

        lock (_gate)
        {
            if (!_worlds.TryGetValue(worldId, out var world) ||
                !world.States.Any(state => string.Equals(
                    state.StateRevisionId,
                    stateRevisionId,
                    StringComparison.Ordinal)))
            {
                return false;
            }

            world.PinnedStateRevisionIds.Add(stateRevisionId);
            return true;
        }
    }

    public bool UnpinState(string worldId, string stateRevisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(stateRevisionId, nameof(stateRevisionId));

        lock (_gate)
        {
            return _worlds.TryGetValue(worldId, out var world) &&
                   world.PinnedStateRevisionIds.Remove(stateRevisionId);
        }
    }

    public SimulatedCanonicalRetentionSnapshot GetSnapshot(string worldId)
    {
        ValidateRequired(worldId, nameof(worldId));

        lock (_gate)
        {
            if (!_worlds.TryGetValue(worldId, out var world) || world.States.Count == 0)
            {
                return new(
                    worldId,
                    CurrentStateRevisionId: null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            var normalStartIndex = Math.Max(0, world.States.Count - NormalCanonicalStateCount);
            var retained = new HashSet<string>(StringComparer.Ordinal);

            for (var index = normalStartIndex; index < world.States.Count; index++)
            {
                retained.Add(world.States[index].StateRevisionId);
            }

            retained.UnionWith(world.PinnedStateRevisionIds);

            var retainedStates = world.States
                .Where(state => retained.Contains(state.StateRevisionId))
                .Select(state => state.StateRevisionId)
                .ToArray();
            var cleanupEligible = world.States
                .Where(state => !retained.Contains(state.StateRevisionId))
                .Select(state => state.StateRevisionId)
                .ToArray();
            var retainedEnvironments = world.States
                .Where(state => retained.Contains(state.StateRevisionId))
                .Select(state => state.EnvironmentRevisionId)
                .Where(environment => !string.IsNullOrWhiteSpace(environment))
                .Select(environment => environment!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new(
                worldId,
                world.States[^1].StateRevisionId,
                retainedStates,
                cleanupEligible,
                retainedEnvironments);
        }
    }

    private WorldRetentionRecord GetOrCreateWorld(string worldId)
    {
        if (_worlds.TryGetValue(worldId, out var existing))
        {
            return existing;
        }

        var created = new WorldRetentionRecord();
        _worlds.Add(worldId, created);
        return created;
    }

    private static void ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }
    }

    private sealed class WorldRetentionRecord
    {
        public List<StateReference> States { get; } = new();
        public HashSet<string> PinnedStateRevisionIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record StateReference(string StateRevisionId, string? EnvironmentRevisionId);
}
