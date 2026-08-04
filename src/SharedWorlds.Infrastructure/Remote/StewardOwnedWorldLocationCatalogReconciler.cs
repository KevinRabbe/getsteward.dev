using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Derives this installation's desired private World-location claims from canonical local storage.
/// The reconciler never treats UI state, lifecycle presentation, timestamps, or directory presence as
/// authority. It records exact state/environment pairs and bounded identifying presentation only after
/// immutable metadata agrees and the state payload is locally available. Network replay is separate.
/// </summary>
public sealed class StewardOwnedWorldLocationCatalogReconciler
{
    private readonly IWorldStorage _localStorage;
    private readonly IOwnedWorldLocationPublicationJournal _journal;
    private readonly StewardOwnedWorldLocationPublicationService _publication;
    private readonly OwnedWorldCanonicalSnapshotResolver _snapshotResolver;

    public StewardOwnedWorldLocationCatalogReconciler(
        IWorldStorage localStorage,
        IOwnedWorldLocationPublicationJournal journal,
        StewardOwnedWorldLocationPublicationService publication)
    {
        ArgumentNullException.ThrowIfNull(localStorage);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(publication);
        _localStorage = localStorage;
        _journal = journal;
        _publication = publication;
        _snapshotResolver = new OwnedWorldCanonicalSnapshotResolver(localStorage);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var worlds = await _localStorage.ListWorldsAsync(cancellationToken);
        var worldsById = new Dictionary<WorldId, World>();
        foreach (var world in worlds)
        {
            if (!worldsById.TryAdd(world.Id, world))
            {
                throw new InvalidDataException(
                    $"Local World storage returned duplicate canonical World ID '{world.Id}'.");
            }
        }

        var journalStates = await _journal.ListAsync(cancellationToken);
        var failures = new List<Exception>();

        foreach (var world in worldsById.Values
                     .OrderBy(world => world.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileWorldAsync(world, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new InvalidOperationException(
                    $"Could not reconcile local World '{world.Id}' into its owned-location publication journal.",
                    exception));
            }
        }

        foreach (var state in journalStates
                     .Where(state => !worldsById.ContainsKey(state.WorldId))
                     .OrderBy(state => state.WorldId.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _publication.RecordRemovalAsync(state.WorldId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new InvalidOperationException(
                    $"Could not record removal for locally absent World '{state.WorldId}'.",
                    exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more local Worlds could not be reconciled into owned-location publication state.",
                failures);
        }
    }

    private async Task ReconcileWorldAsync(
        World world,
        CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotResolver.ResolveAsync(world, cancellationToken);
        if (snapshot is null)
        {
            await _publication.RecordRemovalAsync(world.Id, cancellationToken);
            return;
        }

        await _publication.RecordDesiredWithPresentationAsync(
            snapshot.World.Id,
            snapshot.State.Id,
            snapshot.Environment.Id,
            snapshot.World.Name,
            snapshot.World.GameAdapterId,
            cancellationToken);
    }
}
