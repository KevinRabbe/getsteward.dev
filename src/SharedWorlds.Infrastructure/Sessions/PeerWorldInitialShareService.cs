using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Establishes persistent peer authority for a World that has never been shared remotely. Unlike legacy
/// shared-World migration, no external retirement proof is needed because LocalOnly has no competing
/// remote writer. The durable Active fence is still committed before Shared/PeerAuthority becomes
/// visible so an interruption leaves only a safe resumable marker.
///
/// LocalOnly membership is device-local ownership metadata, not remote access authority. Fresh peer
/// sharing therefore normalizes canonical membership to exactly the current peer identity; additional
/// participants are added afterward through the fenced membership service.
/// </summary>
public sealed class PeerWorldInitialShareService
{
    private const ulong InitialGeneration = 1;

    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;

    public PeerWorldInitialShareService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        _storage = storage;
        _authorityFences = authorityFences;
    }

    public async Task<World> ShareAsync(
        WorldId worldId,
        UserIdentity localOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localOwner);
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot share missing World '{worldId}'.");

        var stateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical state revision to share.");
        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical environment revision to share.");

        await VerifyCanonicalSourceAsync(
            world,
            stateRevisionId,
            environmentRevisionId,
            cancellationToken);

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            if (world.PeerAuthority is not { } existingAuthority ||
                existingAuthority.Generation != InitialGeneration ||
                !SameUser(existingAuthority.Holder, localOwner) ||
                !ContainsStableMember(world.Members, localOwner))
            {
                throw new InvalidOperationException(
                    $"World '{worldId}' is already shared through a different authority model and cannot be initialized as a fresh peer World.");
            }

            var existingFence = await _authorityFences.LoadAsync(worldId, cancellationToken);
            if (!MatchesInitialActiveFence(
                    existingFence,
                    worldId,
                    localOwner,
                    stateRevisionId))
            {
                throw new InvalidDataException(
                    $"World '{worldId}' has peer authority generation 1 but its local durable Active fence is missing or inconsistent.");
            }

            return world;
        }

        if (world.SharingMode != WorldSharingMode.LocalOnly)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' has unsupported sharing mode '{world.SharingMode}'.");
        }

        if (world.PeerAuthority is not null)
        {
            throw new InvalidDataException(
                $"Local-only World '{worldId}' already contains peer authority metadata and requires recovery instead of a new share transaction.");
        }

        var fence = await _authorityFences.LoadAsync(worldId, cancellationToken);
        if (fence is null)
        {
            await _authorityFences.SaveAsync(
                new PeerAuthorityFence(
                    worldId,
                    localOwner,
                    InitialGeneration,
                    stateRevisionId,
                    PeerAuthorityFenceState.Active,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        else if (!MatchesInitialActiveFence(
                     fence,
                     worldId,
                     localOwner,
                     stateRevisionId))
        {
            throw new InvalidDataException(
                $"World '{worldId}' already has a conflicting durable peer-authority fence and cannot be shared automatically.");
        }

        var shared = world with
        {
            SharingMode = WorldSharingMode.Shared,
            Members = [localOwner],
            PeerAuthority = new WorldPeerAuthority(
                localOwner,
                InitialGeneration)
        };
        await _storage.SaveWorldAsync(shared, cancellationToken);

        var verified = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new IOException(
                $"World '{worldId}' disappeared after peer sharing was published.");
        if (verified.SharingMode != WorldSharingMode.Shared ||
            verified.PeerAuthority is not { } verifiedAuthority ||
            verifiedAuthority.Generation != InitialGeneration ||
            !SameUser(verifiedAuthority.Holder, localOwner) ||
            verified.Members.Count != 1 ||
            !SameUser(verified.Members[0], localOwner) ||
            verified.CurrentStateRevisionId != stateRevisionId ||
            verified.CurrentEnvironmentRevisionId != environmentRevisionId)
        {
            throw new IOException(
                $"World '{worldId}' did not persist the exact generation-1 peer sharing state.");
        }

        return verified;
    }

    private async Task VerifyCanonicalSourceAsync(
        World world,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken)
    {
        var environment = await _storage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{world.Id}' cannot be shared because its canonical environment metadata is missing.");
        if (environment.WorldId != world.Id ||
            environment.Id != environmentRevisionId ||
            !string.Equals(
                environment.Manifest.AdapterId,
                world.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{world.Id}' canonical environment does not match the World adapter/identity.");
        }

        var state = await _storage.LoadStateRevisionAsync(
            world.Id,
            stateRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{world.Id}' cannot be shared because its canonical state metadata is missing.");
        if (state.WorldId != world.Id ||
            state.Id != stateRevisionId ||
            state.EnvironmentRevisionId != environmentRevisionId ||
            !string.Equals(state.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{world.Id}' canonical state does not match the World/environment identity.");
        }

        if (!await _storage.IsRevisionPayloadAvailableAsync(
                world.Id,
                stateRevisionId,
                cancellationToken))
        {
            throw new InvalidDataException(
                $"World '{world.Id}' cannot be shared because its canonical state payload is unavailable on this device.");
        }
    }

    private static bool MatchesInitialActiveFence(
        PeerAuthorityFence? fence,
        WorldId worldId,
        UserIdentity holder,
        RevisionId stateRevision)
        => fence is not null &&
           fence.WorldId == worldId &&
           fence.State == PeerAuthorityFenceState.Active &&
           fence.Generation == InitialGeneration &&
           fence.StateRevisionId == stateRevision &&
           SameUser(fence.Holder, holder);

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
