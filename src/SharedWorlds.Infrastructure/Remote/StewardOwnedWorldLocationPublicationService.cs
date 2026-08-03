using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public sealed class StewardOwnedWorldLocationPublicationException : InvalidOperationException
{
    public StewardOwnedWorldLocationPublicationException(
        WorldId worldId,
        string code,
        bool retryable,
        string message)
        : base(message)
    {
        WorldId = worldId;
        Code = code;
        Retryable = retryable;
    }

    public WorldId WorldId { get; }
    public string Code { get; }
    public bool Retryable { get; }
}

/// <summary>
/// Stages and replays exact private World location compare-and-swap operations through Steward. The
/// journal is always advanced before network access and only acknowledged after a protocol-valid
/// success, so process death cannot turn an ambiguous remote outcome into a guessed next operation.
/// Every persisted entry is bound to the exact durable installation ID used by the authenticated
/// session; a different installation identity can neither mutate nor replay it.
/// </summary>
public sealed class StewardOwnedWorldLocationPublicationService
{
    private const int GateStripeCount = 64;
    private const int MaxOperationsPerReplay = 4;

    private readonly IOwnedWorldLocationPublicationJournal _journal;
    private readonly StewardOwnedWorldLocationClient _client;
    private readonly string _installationId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim[] _stateGates = CreateGates();
    private readonly SemaphoreSlim[] _replayGates = CreateGates();

    public StewardOwnedWorldLocationPublicationService(
        IOwnedWorldLocationPublicationJournal journal,
        StewardOwnedWorldLocationClient client,
        string installationId,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(client);
        OwnedWorldLocationPublicationState.ValidateInstallationId(installationId);
        _journal = journal;
        _client = client;
        _installationId = installationId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task RecordDesiredAsync(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(worldId, stateRevisionId, environmentRevisionId);
        var gate = GetGate(_stateGates, worldId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _journal.LoadAsync(worldId, cancellationToken);
            EnsureCurrentInstallation(current, worldId);
            if (current is not null &&
                current.DesiredStateRevisionId == stateRevisionId &&
                current.DesiredEnvironmentRevisionId == environmentRevisionId)
            {
                return;
            }

            var updated = current is null
                ? new OwnedWorldLocationPublicationState(
                    worldId,
                    _installationId,
                    stateRevisionId,
                    environmentRevisionId,
                    ConfirmedStateRevisionId: null,
                    ConfirmedEnvironmentRevisionId: null,
                    InFlight: null,
                    _utcNow())
                : current with
                {
                    DesiredStateRevisionId = stateRevisionId,
                    DesiredEnvironmentRevisionId = environmentRevisionId,
                    UpdatedAt = _utcNow()
                };
            await _journal.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RecordRemovalAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(worldId);
        var gate = GetGate(_stateGates, worldId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _journal.LoadAsync(worldId, cancellationToken);
            EnsureCurrentInstallation(current, worldId);
            if (current is null || current.DesiredStateRevisionId is null)
            {
                return;
            }

            if (current.ConfirmedStateRevisionId is null && current.InFlight is null)
            {
                // No operation was ever staged, so no remote location can exist from this journal.
                await _journal.RemoveAsync(worldId, cancellationToken);
                return;
            }

            await _journal.SaveAsync(
                current with
                {
                    DesiredStateRevisionId = null,
                    DesiredEnvironmentRevisionId = null,
                    UpdatedAt = _utcNow()
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReplayAllAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var state in await _journal.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureCurrentInstallation(state, state.WorldId);
                await ReplayAsync(state.WorldId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more owned-World location publications remain pending.",
                failures);
        }
    }

    public async Task ReplayAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(worldId);
        var replayGate = GetGate(_replayGates, worldId);
        await replayGate.WaitAsync(cancellationToken);
        try
        {
            for (var operationCount = 0;
                 operationCount < MaxOperationsPerReplay;
                 operationCount++)
            {
                var operation = await GetOrStageNextAsync(worldId, cancellationToken);
                if (operation is null)
                {
                    return;
                }

                var response = await ExecuteAsync(worldId, operation, cancellationToken);
                EnsureSuccessfulResponse(worldId, operation, response);
                await AcknowledgeAsync(worldId, operation, cancellationToken);
            }

            var remaining = await GetOrStageNextAsync(worldId, cancellationToken);
            if (remaining is not null)
            {
                throw new StewardOwnedWorldLocationPublicationException(
                    worldId,
                    "ReplayLimitReached",
                    retryable: true,
                    $"Owned-World location publication changed more than {MaxOperationsPerReplay} times during one bounded replay pass. The next exact CAS operation remains journaled.");
            }
        }
        finally
        {
            replayGate.Release();
        }
    }

    private async Task<OwnedWorldLocationPublicationOperation?> GetOrStageNextAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var gate = GetGate(_stateGates, worldId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _journal.LoadAsync(worldId, cancellationToken);
            EnsureCurrentInstallation(current, worldId);
            if (current is null)
            {
                return null;
            }

            if (current.InFlight is not null)
            {
                return current.InFlight;
            }

            if (current.DesiredStateRevisionId == current.ConfirmedStateRevisionId &&
                current.DesiredEnvironmentRevisionId == current.ConfirmedEnvironmentRevisionId)
            {
                return null;
            }

            OwnedWorldLocationPublicationOperation next;
            if (current.DesiredStateRevisionId is { } desiredState &&
                current.DesiredEnvironmentRevisionId is { } desiredEnvironment)
            {
                next = OwnedWorldLocationPublicationOperation.Publish(
                    desiredState,
                    desiredEnvironment,
                    current.ConfirmedStateRevisionId,
                    current.ConfirmedEnvironmentRevisionId);
            }
            else if (current.ConfirmedStateRevisionId is { } confirmedState &&
                     current.ConfirmedEnvironmentRevisionId is { } confirmedEnvironment)
            {
                next = OwnedWorldLocationPublicationOperation.Remove(
                    confirmedState,
                    confirmedEnvironment);
            }
            else
            {
                await _journal.RemoveAsync(worldId, cancellationToken);
                return null;
            }

            await _journal.SaveAsync(
                current with
                {
                    InFlight = next,
                    UpdatedAt = _utcNow()
                },
                cancellationToken);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<OwnedWorldLocationTransportResponse> ExecuteAsync(
        WorldId worldId,
        OwnedWorldLocationPublicationOperation operation,
        CancellationToken cancellationToken)
    {
        operation.Validate();
        return operation.Kind switch
        {
            OwnedWorldLocationPublicationOperationKind.Publish =>
                _client.PublishCurrentLocationAsync(
                    worldId,
                    operation.StateRevisionId!.Value,
                    operation.EnvironmentRevisionId!.Value,
                    operation.ExpectedStateRevisionId,
                    operation.ExpectedEnvironmentRevisionId,
                    cancellationToken),
            OwnedWorldLocationPublicationOperationKind.Remove =>
                _client.RemoveCurrentLocationAsync(
                    worldId,
                    operation.ExpectedStateRevisionId!.Value,
                    operation.ExpectedEnvironmentRevisionId!.Value,
                    cancellationToken),
            _ => throw new InvalidDataException(
                $"Unsupported owned-World location publication operation '{operation.Kind}'.")
        };
    }

    private static void EnsureSuccessfulResponse(
        WorldId worldId,
        OwnedWorldLocationPublicationOperation operation,
        OwnedWorldLocationTransportResponse response)
    {
        if (response.IsConflict)
        {
            throw new StewardOwnedWorldLocationPublicationException(
                worldId,
                response.Code,
                response.Retryable,
                "The backend preserved a divergent owned-World location instead of overwriting it.");
        }

        var valid = operation.Kind switch
        {
            OwnedWorldLocationPublicationOperationKind.Publish => response.Code is
                "WorldLocationCreated" or
                "WorldLocationUpdated" or
                "WorldLocationUnchanged",
            OwnedWorldLocationPublicationOperationKind.Remove => response.Code is
                "WorldLocationUpdated" or
                "WorldLocationUnchanged",
            _ => false
        };
        if (!valid)
        {
            throw new StewardOwnedWorldLocationPublicationException(
                worldId,
                response.Code,
                response.Retryable,
                $"Steward returned unexpected publication response '{response.Code}'.");
        }
    }

    private async Task AcknowledgeAsync(
        WorldId worldId,
        OwnedWorldLocationPublicationOperation completed,
        CancellationToken cancellationToken)
    {
        var gate = GetGate(_stateGates, worldId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _journal.LoadAsync(worldId, cancellationToken)
                ?? throw new InvalidDataException(
                    $"Owned-World location journal entry '{worldId}' disappeared during publication.");
            EnsureCurrentInstallation(current, worldId);
            if (current.InFlight != completed)
            {
                throw new InvalidDataException(
                    $"Owned-World location journal entry '{worldId}' changed its in-flight CAS operation before acknowledgement.");
            }

            if (completed.Kind == OwnedWorldLocationPublicationOperationKind.Remove)
            {
                if (current.DesiredStateRevisionId is null)
                {
                    await _journal.RemoveAsync(worldId, cancellationToken);
                    return;
                }

                await _journal.SaveAsync(
                    current with
                    {
                        ConfirmedStateRevisionId = null,
                        ConfirmedEnvironmentRevisionId = null,
                        InFlight = null,
                        UpdatedAt = _utcNow()
                    },
                    cancellationToken);
                return;
            }

            await _journal.SaveAsync(
                current with
                {
                    ConfirmedStateRevisionId = completed.StateRevisionId,
                    ConfirmedEnvironmentRevisionId = completed.EnvironmentRevisionId,
                    InFlight = null,
                    UpdatedAt = _utcNow()
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureCurrentInstallation(
        OwnedWorldLocationPublicationState? state,
        WorldId worldId)
    {
        if (state is not null &&
            !string.Equals(
                state.InstallationId,
                _installationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Owned-World location journal entry '{worldId}' belongs to installation '{state.InstallationId}', not the current installation '{_installationId}'.");
        }
    }

    private static SemaphoreSlim[] CreateGates()
        => Enumerable.Range(0, GateStripeCount)
            .Select(static _ => new SemaphoreSlim(1, 1))
            .ToArray();

    private static SemaphoreSlim GetGate(
        SemaphoreSlim[] gates,
        WorldId worldId)
        => gates[(int)((uint)worldId.Value.GetHashCode() % GateStripeCount)];

    private static void EnsureNonEmpty(
        WorldId worldId,
        RevisionId? stateRevisionId = null,
        RevisionId? environmentRevisionId = null)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID must not be empty.", nameof(worldId));
        }

        if (stateRevisionId.HasValue != environmentRevisionId.HasValue)
        {
            throw new ArgumentException(
                "State and environment revisions must both be supplied or both be absent.");
        }

        if (stateRevisionId.HasValue && stateRevisionId.Value.Value == Guid.Empty)
        {
            throw new ArgumentException("State revision ID must not be empty.", nameof(stateRevisionId));
        }

        if (environmentRevisionId.HasValue &&
            environmentRevisionId.Value.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Environment revision ID must not be empty.",
                nameof(environmentRevisionId));
        }
    }
}
