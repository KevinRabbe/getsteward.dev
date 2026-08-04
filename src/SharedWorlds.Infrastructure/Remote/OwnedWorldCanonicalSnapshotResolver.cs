using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Resolves one transferable owner-private snapshot strictly from canonical local storage.
/// A null result means the World currently has no private transferable authority; malformed or
/// contradictory canonical state fails closed.
/// </summary>
public sealed class OwnedWorldCanonicalSnapshotResolver
{
    private readonly IWorldStorage _storage;

    public OwnedWorldCanonicalSnapshotResolver(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<OwnedWorldCanonicalSnapshot?> ResolveAsync(
        World world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.SharingMode != WorldSharingMode.LocalOnly)
        {
            return null;
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
            return null;
        }

        var state = await _storage.LoadStateRevisionAsync(
            world.Id,
            stateRevisionId.Value,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Local World '{world.Id}' points to missing state revision '{stateRevisionId}'.");
        var environment = await _storage.LoadEnvironmentRevisionAsync(
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

        if (!await _storage.IsRevisionPayloadAvailableAsync(
                world.Id,
                state.Id,
                cancellationToken))
        {
            return null;
        }

        return new OwnedWorldCanonicalSnapshot(world, state, environment);
    }
}

public sealed record OwnedWorldCanonicalSnapshot(
    World World,
    StateRevision State,
    EnvironmentRevision Environment);
