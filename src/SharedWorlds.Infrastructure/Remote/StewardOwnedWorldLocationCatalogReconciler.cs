using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Derives this installation's desired private World-location claims from canonical local storage.
/// The reconciler never treats UI state, lifecycle presentation, timestamps, or directory presence as
/// authority. It records exact state/environment pairs only after their immutable metadata agrees and
/// the state payload is locally available. Network replay remains a separate operation.
/// </summary>
public sealed class StewardOwnedWorldLocationCatalogReconciler
{
    private readonly IWorldStorage _localStorage;
    private readonly IOwnedWorldLocationPublicationJournal _journal;
    private readonly StewardOwnedWorldLocationPublicationService _publication;

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
                // Canonical local deletion is definitive. RecordRemovalAsync still preserves any
                // ambiguous in-flight publish and will force exact reconciliation before deletion.
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
        if (world.SharingMode != WorldSharingMode.LocalOnly)
        {
            // Local shared records are publication/recovery shadows, not this installation's private
            // writable authority. A prior private location must therefore be removed exactly.
            await _publication.RecordRemovalAsync(world.Id, cancellationToken);
            return;
        }

        var stateRevisionId = world.CurrentStateRevisionId;
        var environmentRevisionId = world.CurrentEnvironmentRevisionId;
        if (stateRevisionId.HasValue != environmentRevisionId.HasValue)
        {
            throw new InvalidDataException(
                $"Local World '{world.Id}' has only one side of its canonical state/environment head.");
        }

        if (stateRevisionId is null || environmentRevisionId is null)
        {
            await _publication.RecordRemovalAsync(world.Id, cancellationToken);
            return;
        }

        var state = await _localStorage.LoadStateRevisionAsync(
            world.Id,
            stateRevisionId.Value,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Local World '{world.Id}' points to missing state revision '{stateRevisionId}'.");
        var environment = await _localStorage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentRevisionId.Value,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Local World '{world.Id}' points to missing environment revision '{environmentRevisionId}'.");

        if (state.EnvironmentRevisionId != environmentRevisionId)
        {
            throw new InvalidDataException(
                $"State revision '{state.Id}' for World '{world.Id}' is not bound to canonical environment revision '{environmentRevisionId}'.");
        }

        if (!string.Equals(world.GameAdapterId, state.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(
                world.GameAdapterId,
                environment.Manifest.AdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{world.Id}', state revision '{state.Id}', and environment revision '{environment.Id}' do not agree on one exact adapter ID.");
        }

        if (!await _localStorage.IsRevisionPayloadAvailableAsync(
                world.Id,
                state.Id,
                cancellationToken))
        {
            // Metadata without payload bytes cannot satisfy Bring Here. This is a definitive local
            // absence, not corruption, so remove any previously confirmed location claim.
            await _publication.RecordRemovalAsync(world.Id, cancellationToken);
            return;
        }

        await _publication.RecordDesiredAsync(
            world.Id,
            state.Id,
            environment.Id,
            cancellationToken);
    }
}
