using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed record WorldHistorySnapshot(
    IReadOnlyList<StateRevision> Revisions,
    bool HasOlderRevisions);

/// <summary>
/// Exposes the immutable canonical state chain as user-facing World History.
/// Restore never rewinds or deletes the chain: it writes a new revision whose payload is copied from
/// the selected ancestor and whose parent is the previously current head. Make My Copy creates an
/// independent local World identity from the selected historical state.
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

            EnsureRevisionMatchesWorld(world, revision);
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

        var currentRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidOperationException(
                $"World '{world.Name}' has no current state to restore from History.");
        if (sourceRevisionId == currentRevisionId)
        {
            throw new InvalidOperationException(
                "The selected History entry is already the current World state.");
        }

        var source = await FindCanonicalRevisionAsync(
            world,
            sourceRevisionId,
            cancellationToken);
        _ = await LoadStableEnvironmentAsync(world, cancellationToken);

        var restoredRevisionId = RevisionId.New();
        var restoredRevision = new StateRevision(
            Id: restoredRevisionId,
            WorldId: world.Id,
            ParentRevisionId: currentRevisionId,
            CreatedAt: DateTimeOffset.UtcNow,
            CreatedBy: restoredBy,
            AdapterId: world.GameAdapterId,
            StatePackageId: source.StatePackageId);

        await using (var package = await _storage.OpenRevisionAsync(
                         world.Id,
                         source.Id,
                         cancellationToken))
        {
            await _storage.StoreRevisionAsync(restoredRevision, package, cancellationToken);
        }

        var updated = world with { CurrentStateRevisionId = restoredRevisionId };
        // Publish the new head last. Failure before this write leaves only an unreferenced immutable
        // candidate; the previous current World remains authoritative and intact.
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
        var sourceEnvironment = await LoadStableEnvironmentAsync(
            sourceWorld,
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
            StatePackageId: sourceState.StatePackageId);
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

    private async Task<EnvironmentRevision> LoadStableEnvironmentAsync(
        World world,
        CancellationToken cancellationToken)
    {
        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidOperationException(
                $"World '{world.Name}' has no current environment for History.");
        var environment = await _storage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{world.Name}' points to missing environment revision '{environmentRevisionId}'.");

        if (environment.WorldId != world.Id)
        {
            throw new InvalidDataException(
                $"World '{world.Name}' points to an environment belonging to a different World.");
        }

        if (!string.Equals(
                environment.Manifest.AdapterId,
                world.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{world.Name}' has an environment for adapter " +
                $"'{environment.Manifest.AdapterId}', not '{world.GameAdapterId}'.");
        }

        // StateRevision does not yet journal the EnvironmentRevision that produced it. Once a World
        // has changed environment, pairing an old state with the current environment would be a guess.
        // Keep History readable, but refuse mutation until that association is made explicit.
        if (environment.ParentRevisionId is not null)
        {
            throw new InvalidOperationException(
                "Safe World cannot Restore or Make My Copy from this History yet because the World's " +
                "environment changed and older saved states do not record which environment created them.");
        }

        return environment;
    }

    private static void EnsureRevisionMatchesWorld(World world, StateRevision revision)
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
}
