using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

public sealed record PeerWorldRevisionOffer(
    World World,
    EnvironmentRevision EnvironmentRevision,
    StateRevision StateRevision,
    long PayloadLength,
    string PayloadSha256);

public sealed record PeerWorldRevisionReceipt(
    WorldId WorldId,
    RevisionId StateRevisionId,
    long PayloadLength,
    string PayloadSha256);

/// <summary>
/// Moves one exact immutable World revision to another participant and returns only after that
/// participant has durably installed and verified it. Transport implementations own framing,
/// connectivity, authentication, retries, and acknowledgement; they do not decide World authority.
/// </summary>
public interface IPeerWorldRevisionExchange
{
    Task<PeerWorldRevisionReceipt> TransferAsync(
        UserIdentity targetHost,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Concrete IPeerWorldRevisionTransfer implementation. It exports only the exact current committed
/// revision plus the prospective next persistent authority generation, proves the local payload while
/// hashing it, and requires an exact receipt from the peer before live lobby ownership may move.
/// </summary>
public sealed class PeerWorldRevisionTransferService : IPeerWorldRevisionTransfer
{
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly IPeerWorldRevisionExchange _exchange;

    public PeerWorldRevisionTransferService(
        IWorldStorage storage,
        IPeerWorldRevisionExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(exchange);
        _storage = storage;
        _exchange = exchange;
    }

    public async Task EnsureAvailableAsync(
        WorldId worldId,
        RevisionId committedStateRevision,
        UserIdentity targetHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetHost);

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot replicate World '{worldId}' because its canonical metadata is missing.");
        if (world.CurrentStateRevisionId != committedStateRevision)
        {
            throw new InvalidDataException(
                $"World '{worldId}' canonical head is '{world.CurrentStateRevisionId}', not committed handoff revision '{committedStateRevision}'.");
        }

        var authority = world.PeerAuthority
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no persistent peer authority to hand off.");
        if (authority.Generation == 0)
        {
            throw new InvalidDataException(
                $"World '{worldId}' has invalid zero peer-authority generation.");
        }

        if (!ContainsStableMember(world.Members, authority.Holder) ||
            !ContainsStableMember(world.Members, targetHost))
        {
            throw new InvalidDataException(
                $"World '{worldId}' peer authority holder and handoff target must both be canonical members.");
        }

        if (SameUser(authority.Holder, targetHost))
        {
            throw new InvalidOperationException(
                "Peer revision handoff target already owns persistent World authority.");
        }

        var stateRevision = await _storage.LoadStateRevisionAsync(
            worldId,
            committedStateRevision,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' points to missing state revision '{committedStateRevision}'.");
        if (stateRevision.WorldId != worldId ||
            stateRevision.Id != committedStateRevision ||
            !string.Equals(stateRevision.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"State revision '{committedStateRevision}' does not match canonical World '{worldId}'.");
        }

        var environmentRevisionId = stateRevision.EnvironmentRevisionId
            ?? throw new InvalidDataException(
                $"State revision '{committedStateRevision}' does not identify its exact environment revision.");
        if (world.CurrentEnvironmentRevisionId != environmentRevisionId)
        {
            throw new InvalidDataException(
                $"World '{worldId}' current environment does not match state revision '{committedStateRevision}'.");
        }

        var environmentRevision = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' points to missing environment revision '{environmentRevisionId}'.");
        if (environmentRevision.WorldId != worldId ||
            environmentRevision.Id != environmentRevisionId ||
            !string.Equals(
                environmentRevision.Manifest.AdapterId,
                world.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Environment revision '{environmentRevisionId}' does not match canonical World '{worldId}'.");
        }

        var prospectiveWorld = world with
        {
            PeerAuthority = new WorldPeerAuthority(
                targetHost,
                checked(authority.Generation + 1))
        };
        var digest = await ComputePayloadDigestAsync(
            worldId,
            committedStateRevision,
            cancellationToken);
        var offer = new PeerWorldRevisionOffer(
            prospectiveWorld,
            environmentRevision,
            stateRevision,
            digest.Length,
            digest.Sha256);

        await using var payload = await _storage.OpenRevisionAsync(
            worldId,
            committedStateRevision,
            cancellationToken);
        var receipt = await _exchange.TransferAsync(
            targetHost,
            offer,
            payload,
            cancellationToken);

        if (receipt.WorldId != worldId ||
            receipt.StateRevisionId != committedStateRevision ||
            receipt.PayloadLength != digest.Length ||
            !string.Equals(receipt.PayloadSha256, digest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The peer acknowledged a different World revision or payload than Steward offered for handoff.");
        }
    }

    private async Task<(long Length, string Sha256)> ComputePayloadDigestAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        await using var payload = await _storage.OpenRevisionAsync(
            worldId,
            revisionId,
            cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBufferBytes];
        long length = 0;

        while (true)
        {
            var read = await payload.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            checked
            {
                length += read;
            }

            hash.AppendData(buffer, 0, read);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
