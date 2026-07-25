using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Runs the read-only multiplayer Join lifecycle.
///
/// Join deliberately does not acquire writable World authority, materialize the canonical state
/// package, register recovery responsibility, capture state, or commit a revision. The active host
/// already owns the writable state. A joining device only needs the exact environment plus a proven
/// host connection.
/// </summary>
public sealed class WorldJoinService
{
    private readonly IWorldStorage _storage;

    public WorldJoinService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task JoinAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        HostConnection host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Address);

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            throw new NotSupportedException(
                $"{adapter.DisplayName} does not advertise a validated automatic Join path.");
        }

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldNotFoundException(worldId);
        EnsureAdapterMatches(adapter.Id, world.GameAdapterId, "World");

        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new WorldIntegrityException(
                worldId,
                "The canonical environment revision is missing.");
        var environmentRevision = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new RevisionNotFoundException(
                worldId,
                environmentRevisionId,
                "Environment");
        EnsureAdapterMatches(
            adapter.Id,
            environmentRevision.Manifest.AdapterId,
            "environment revision");

        var verification = await adapter.VerifyEnvironmentAsync(
            installation,
            environmentRevision.Manifest,
            cancellationToken);
        if (!verification.IsReady)
        {
            var detail = verification.Issues.Count == 0
                ? "The adapter could not verify the exact World environment."
                : string.Join(
                    " ",
                    verification.Issues
                        .Take(3)
                        .Select(static issue => issue.Message));
            throw new EnvironmentReproductionException(
                adapter.Id,
                $"World '{world.Name}' is not verified ready for Join. {detail}");
        }

        PreparedWorld? prepared = null;
        Exception? operationFailure = null;
        try
        {
            prepared = (await adapter.PrepareEnvironmentAsync(
                installation,
                environmentRevision.Manifest,
                cancellationToken)) with
            {
                DisplayName = world.Name
            };

            var capability = await adapter.GetJoinCapabilityAsync(
                prepared,
                host,
                cancellationToken);
            if (capability.Kind != JoinCapabilityKind.SupportedAutomatic)
            {
                throw new InvalidOperationException(
                    capability.Reason ??
                    $"{adapter.DisplayName} cannot automatically Join this host.");
            }

            var session = await adapter.LaunchClientAsync(
                prepared,
                host,
                cancellationToken);
            await adapter.WaitForSessionEndAsync(session, cancellationToken);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (prepared is not null)
            {
                try
                {
                    await adapter.FinalizePreparedWorldAsync(
                        prepared,
                        PreparedWorldDisposition.Discard,
                        CancellationToken.None);
                }
                catch when (operationFailure is not null)
                {
                    // A read-only Join workspace contains no canonical candidate state. Cleanup
                    // failure must not replace the actual Join failure with false recovery semantics.
                }
            }
        }
    }

    private static void EnsureAdapterMatches(
        string expectedAdapterId,
        string actualAdapterId,
        string source)
    {
        if (!string.Equals(expectedAdapterId, actualAdapterId, StringComparison.Ordinal))
        {
            throw new AdapterMismatchException(
                expectedAdapterId,
                actualAdapterId,
                source);
        }
    }
}
