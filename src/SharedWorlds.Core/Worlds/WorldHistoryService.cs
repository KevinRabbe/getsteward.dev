using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed record WorldHistorySnapshot(
    IReadOnlyList<StateRevision> Revisions,
    bool HasOlderRevisions);

/// <summary>
/// Exposes the immutable canonical state chain as user-facing World History.
/// Restore never rewinds or deletes either chain: it writes a new state revision and, when needed,
/// a new environment revision derived from the selected historical state's exact environment.
/// Make My Copy creates an independent local World identity from that same state/environment pair.
/// </summary>
public sealed class WorldHistoryService
{
    public const int DefaultMaximumEntries = 100;
    private const int MaximumSupportedEntries = 500;

    private readonly IWorldStorage _storage;

    public WorldHistoryService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<WorldHistorySnapshot> GetHistoryAsync(
        World world,
        int maximumEntries = DefaultMaximumEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (maximumEntries is <= 0 or > MaximumSupportedEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"World History supports between 1 and {MaximumSupportedEntries} entries per view.");
        }

        if (world.CurrentStateRevisionId is not { } currentRevisionId)
        {
            return new WorldHistorySnapshot([], HasOlderRevisions: false);
        }

        var revisions = new List<StateRevision>(Math.Min(maximumEntries, 32));
        var visited = new HashSet<RevisionId>();
        RevisionId? nextRevisionId = currentRevisionId;

        while (nextRevisionId is { } revisionId && revisions.Count < maximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(revisionId))
            {
                throw new InvalidDataException(
                    $"World '{world.Name}' has a cycle in its state history at revision '{revisionId}'.");
            }

            var revision = await _storage.LoadStateRevisionAsync(
                world.Id,
                revisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"World '{world.Name}' points to missing state-history revision '{revisionId}'.");

            EnsureStateMatchesWorld(world, revision);
            revisions.Add(revision);
            nextRevisionId = revision.ParentRevisionId;
        }

        return new WorldHistorySnapshot(
            revisions,
            HasOlderRevisions: nextRevisionId is not null);
    }

    public async Task<World> RestoreAsync(
        World world,
        RevisionId sourceRevisionId,
        UserIdentity restoredBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(restoredBy);

        var currentStateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidOperationException(
                $"World '{world.Name}' has no current state to restore from History.");
        var currentEnvironmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidOperationException(
                $"World '{world.Name}' has no current environment for History.");
        if (sourceRevisionId == currentStateRevisionId)
        {
            throw new InvalidOperationException(
                "The selected History entry is already the current World state.");
        }

        var sourceState = await FindCanonicalRevisionAsync(
            world,
            sourceRevisionId,
            cancellationToken);
        var sourceEnvironment = await ResolveEnvironmentForStateAsync(
            world,
            sourceState,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var restoredEnvironmentRevisionId = currentEnvironmentRevisionId;
        EnvironmentRevision? restoredEnvironment = null;
        if (sourceEnvironment.Id != currentEnvironmentRevisionId)
        {
            restoredEnvironmentRevisionId = RevisionId.New();
            restoredEnvironment = new EnvironmentRevision(
                Id: restoredEnvironmentRevisionId,
                WorldId: world.Id,
                ParentRevisionId: currentEnvironmentRevisionId,
                CreatedAt: now,
                CreatedBy: restoredBy,
                Manifest: sourceEnvironment.Manifest);
        }

        var restoredStateRevisionId = RevisionId.New();
        var restoredState = new StateRevision(
            Id: restoredStateRevisionId,
            WorldId: world.Id,
            ParentRevisionId: currentStateRevisionId,
            CreatedAt: now,
            CreatedBy: restoredBy,
            AdapterId: world.GameAdapterId,
            StatePackageId: sourceState.StatePackageId,
            EnvironmentRevisionId: restoredEnvironmentRevisionId);

        if (restoredEnvironment is not null)
        {
            await _storage.StoreEnvironmentRevisionAsync(restoredEnvironment, cancellationToken);
        }

        await using (var package = await _storage.OpenRevisionAsync(
                         world.Id,
                         sourceState.Id,
                         cancellationToken))
        {
            await _storage.StoreRevisionAsync(restoredState, package, cancellationToken);
        }

        var updated = world with
        {
            CurrentEnvironmentRevisionId = restoredEnvironmentRevisionId,
            CurrentStateRevisionId = restoredStateRevisionId
        };
        // Publish both new heads last. A failure before this write leaves only unreferenced immutable
        // candidates; the previous current state/environment pair remains authoritative and intact.
        await _storage.SaveWorldAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<World> MakeIndependentCopyAsync(
        World sourceWorld,
        RevisionId sourceRevisionId,
        string copyName,
        UserIdentity owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyName);

        var sourceState = await FindCanonicalRevisionAsync(
            sourceWorld,
            sourceRevisionId,
            cancellationToken);
        var sourceEnvironment = await ResolveEnvironmentForStateAsync(
            sourceWorld,
            sourceState,
            cancellationToken);

        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var now = DateTimeOffset.UtcNow;

        var copiedEnvironment = new EnvironmentRevision(
            Id: environmentId,
            WorldId: worldId,
            ParentRevisionId: null,
            CreatedAt: now,
            CreatedBy: owner,
            Manifest: sourceEnvironment.Manifest);
        var copiedState = new StateRevision(
            Id: stateId,
            WorldId: worldId,
            ParentRevisionId: null,
            CreatedAt: now,
            CreatedBy: owner,
            AdapterId: sourceWorld.GameAdapterId,
            StatePackageId: sourceState.StatePackageId,
            EnvironmentRevisionId: environmentId);
        var copiedWorld = new World(
            Id: worldId,
            Name: copyName.Trim(),
            GameAdapterId: sourceWorld.GameAdapterId,
            Members: [owner],
            CurrentEnvironmentRevisionId: environmentId,
            CurrentStateRevisionId: stateId)
        {
            SharingMode = WorldSharingMode.LocalOnly,
            GameVersionPolicy = sourceWorld.GameVersionPolicy,
            Visibility = WorldVisibility.Private,
            JoinPolicy = WorldJoinPolicy.InviteOrCodeOnly,
            StartYourOwnPolicy = StartYourOwnPolicy.Disabled,
            StartedFrom = new WorldProvenance(
                SnapshotId: sourceState.Id.ToString(),
                WorldName: sourceWorld.Name,
                SnapshotCreatedAt: sourceState.CreatedAt,
                Creator: sourceState.CreatedBy?.DisplayName,
                Description: "Created from World History.")
        };

        await _storage.StoreEnvironmentRevisionAsync(copiedEnvironment, cancellationToken);
        await using (var package = await _storage.OpenRevisionAsync(
                         sourceWorld.Id,
                         sourceState.Id,
                         cancellationToken))
        {
            await _storage.StoreRevisionAsync(copiedState, package, cancellationToken);
        }

        // As with import, the independent World head is written only after both immutable inputs exist.
        await _storage.SaveWorldAsync(copiedWorld, cancellationToken);
        return copiedWorld;
    }

    private async Task<StateRevision> FindCanonicalRevisionAsync(
        World world,
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        var history = await GetHistoryAsync(
            world,
            DefaultMaximumEntries,
            cancellationToken);
        return history.Revisions.FirstOrDefault(candidate => candidate.Id == revisionId)
               ?? throw new InvalidOperationException(
                   "The selected revision is not in the bounded current World History view.");
    }

    private async Task<EnvironmentRevision> ResolveEnvironmentForStateAsync(
        World world,
        StateRevision state,
        CancellationToken cancellationToken)
    {
        if (state.EnvironmentRevisionId is { } linkedEnvironmentRevisionId)
        {
            var linked = await _storage.LoadEnvironmentRevisionAsync(
                world.Id,
                linkedEnvironmentRevisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"History state '{state.Id}' points to missing environment revision " +
                    $"'{linkedEnvironmentRevisionId}'.");
            EnsureEnvironmentMatchesWorld(world, linked);
            return linked;
        }

        // Compatibility for Worlds created before state/environment association existed. A legacy
        // state can use the current environment only while the World still has its one original
        // environment revision. Once environment history exists, inferring an association would be a
        // guess, so mutation remains fail-closed while History itself stays readable.
        var currentEnvironmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidOperationException(
                $"World '{world.Name}' has no current environment for History.");
        var current = await _storage.LoadEnvironmentRevisionAsync(
            world.Id,
            currentEnvironmentRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{world.Name}' points to missing environment revision " +
                $"'{currentEnvironmentRevisionId}'.");
        EnsureEnvironmentMatchesWorld(world, current);
        if (current.ParentRevisionId is not null)
        {
            throw new InvalidOperationException(
                "Safe World cannot Restore or Make My Copy from this legacy History entry because " +
                "the World's environment changed and that saved state does not record which " +
                "environment created it.");
        }

        return current;
    }

    private static void EnsureStateMatchesWorld(World world, StateRevision revision)
    {
        if (revision.WorldId != world.Id)
        {
            throw new InvalidDataException(
                $"History revision '{revision.Id}' belongs to a different World.");
        }

        if (!string.Equals(revision.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"History revision '{revision.Id}' belongs to adapter '{revision.AdapterId}', " +
                $"not '{world.GameAdapterId}'.");
        }
    }

    private static void EnsureEnvironmentMatchesWorld(
        World world,
        EnvironmentRevision environment)
    {
        if (environment.WorldId != world.Id)
        {
            throw new InvalidDataException(
                $"History environment '{environment.Id}' belongs to a different World.");
        }

        if (!string.Equals(
                environment.Manifest.AdapterId,
                world.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"History environment '{environment.Id}' belongs to adapter " +
                $"'{environment.Manifest.AdapterId}', not '{world.GameAdapterId}'.");
        }
    }
}
