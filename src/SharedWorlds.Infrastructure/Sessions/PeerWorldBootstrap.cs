using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Transport boundary for first-time active-World bootstrap. The payload/metadata format is the same
/// exact canonical revision offer used by handoff replication, but receiver semantics are different:
/// bootstrap may create the same World identity locally for an already-authorized member.
/// </summary>
public interface IPeerWorldBootstrapExchange
{
    Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
        UserIdentity targetMember,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default);
}

public sealed class PeerWorldBootstrapTransferService
{
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly IPeerWorldBootstrapExchange _exchange;

    public PeerWorldBootstrapTransferService(
        IWorldStorage storage,
        IPeerWorldBootstrapExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(exchange);
        _storage = storage;
        _exchange = exchange;
    }

    public async Task BootstrapAsync(
        WorldId worldId,
        UserIdentity targetMember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetMember);
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot bootstrap missing canonical World '{worldId}'.");
        if (!ContainsStableMember(world.Members, targetMember))
        {
            throw new InvalidOperationException(
                $"Identity '{targetMember.ExternalId}' is not a canonical member of World '{worldId}'.");
        }

        var stateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no committed state revision to bootstrap.");
        var state = await _storage.LoadStateRevisionAsync(
            worldId,
            stateRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' points to missing state revision '{stateRevisionId}'.");
        var environmentRevisionId = state.EnvironmentRevisionId
            ?? throw new InvalidDataException(
                $"State revision '{stateRevisionId}' does not identify its exact environment.");
        if (world.CurrentEnvironmentRevisionId != environmentRevisionId)
        {
            throw new InvalidDataException(
                $"World '{worldId}' current environment does not match state revision '{stateRevisionId}'.");
        }

        var environment = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' points to missing environment revision '{environmentRevisionId}'.");
        ValidateCanonicalOfferParts(world, environment, state);

        var digest = await ComputeDigestAsync(worldId, stateRevisionId, cancellationToken);
        var offer = new PeerWorldRevisionOffer(
            world,
            environment,
            state,
            digest.Length,
            digest.Sha256);

        await using var payload = await _storage.OpenRevisionAsync(
            worldId,
            stateRevisionId,
            cancellationToken);
        var receipt = await _exchange.TransferBootstrapAsync(
            targetMember,
            offer,
            payload,
            cancellationToken);
        if (receipt.WorldId != worldId ||
            receipt.StateRevisionId != stateRevisionId ||
            receipt.PayloadLength != digest.Length ||
            !string.Equals(receipt.PayloadSha256, digest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The joining peer acknowledged a different World revision or payload than Steward bootstrapped.");
        }
    }

    private async Task<(long Length, string Sha256)> ComputeDigestAsync(
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

    internal static void ValidateCanonicalOfferParts(
        World world,
        EnvironmentRevision environment,
        StateRevision state)
    {
        if (world.Id != environment.WorldId ||
            world.Id != state.WorldId ||
            world.CurrentEnvironmentRevisionId != environment.Id ||
            world.CurrentStateRevisionId != state.Id ||
            state.EnvironmentRevisionId != environment.Id ||
            !string.Equals(world.GameAdapterId, state.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(world.GameAdapterId, environment.Manifest.AdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Canonical World bootstrap metadata contains mismatched World, revision, or adapter identities.");
        }
    }

    internal static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    internal static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}

/// <summary>
/// Installs a first canonical snapshot for a user who has already been admitted to the active Steam
/// lobby and appears in the host's canonical World membership. This preserves World/revision identity;
/// it is not the public independent-copy import path.
/// </summary>
public sealed class PeerWorldBootstrapInstaller
{
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly UserIdentity _localUser;
    private readonly long _maximumPayloadBytes;

    public PeerWorldBootstrapInstaller(
        IWorldStorage storage,
        UserIdentity localUser,
        long maximumPayloadBytes = PeerWorldRevisionReplicaInstaller.DefaultMaximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(localUser);
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        _storage = storage;
        _localUser = localUser;
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public async Task<PeerWorldRevisionReceipt> InstallAsync(
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(statePayload);
        if (!statePayload.CanRead)
        {
            throw new ArgumentException("Peer bootstrap payload must be readable.", nameof(statePayload));
        }

        ValidateOffer(offer);
        if (!PeerWorldBootstrapTransferService.ContainsStableMember(
                offer.World.Members,
                _localUser))
        {
            throw new InvalidOperationException(
                $"Local identity '{_localUser.ExternalId}' is not a canonical member of incoming World '{offer.World.Id}'.");
        }

        var expectedHash = ParseSha256(offer.PayloadSha256);
        var existingWorld = await _storage.LoadWorldAsync(
            offer.World.Id,
            cancellationToken);
        if (existingWorld is not null)
        {
            await InstallIdempotentRetryAsync(
                existingWorld,
                offer,
                statePayload,
                expectedHash,
                cancellationToken);
            return Receipt(offer);
        }

        await EnsureEnvironmentAsync(offer, cancellationToken);
        await EnsureStateAsync(
            offer,
            statePayload,
            expectedHash,
            cancellationToken);

        // Publishing the World catalog entry is the bootstrap transaction boundary. Immutable
        // environment/state data may be safely orphaned after interruption; no partially received
        // World becomes visible until every byte has been verified locally.
        await _storage.SaveWorldAsync(offer.World, cancellationToken);
        return Receipt(offer);
    }

    private async Task InstallIdempotentRetryAsync(
        World existingWorld,
        PeerWorldRevisionOffer offer,
        Stream incoming,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        if (!EquivalentWorld(existingWorld, offer.World))
        {
            throw new InvalidDataException(
                $"A divergent local World already uses incoming World ID '{offer.World.Id}'.");
        }

        var environment = await _storage.LoadEnvironmentRevisionAsync(
            offer.World.Id,
            offer.EnvironmentRevision.Id,
            cancellationToken);
        var state = await _storage.LoadStateRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);
        if (environment is null || state is null ||
            !EquivalentEnvironment(environment, offer.EnvironmentRevision) ||
            !EquivalentState(state, offer.StateRevision))
        {
            throw new InvalidDataException(
                "Existing bootstrapped World has missing or conflicting immutable revision metadata.");
        }

        await VerifyPayloadAsync(
            incoming,
            offer.PayloadLength,
            expectedHash,
            cancellationToken);
        await VerifyStoredPayloadAsync(offer, expectedHash, cancellationToken);
    }

    private async Task EnsureEnvironmentAsync(
        PeerWorldRevisionOffer offer,
        CancellationToken cancellationToken)
    {
        var existing = await _storage.LoadEnvironmentRevisionAsync(
            offer.World.Id,
            offer.EnvironmentRevision.Id,
            cancellationToken);
        if (existing is null)
        {
            await _storage.StoreEnvironmentRevisionAsync(
                offer.EnvironmentRevision,
                cancellationToken);
            return;
        }

        if (!EquivalentEnvironment(existing, offer.EnvironmentRevision))
        {
            throw new InvalidDataException(
                $"Conflicting environment revision '{offer.EnvironmentRevision.Id}' already exists locally.");
        }
    }

    private async Task EnsureStateAsync(
        PeerWorldRevisionOffer offer,
        Stream incoming,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        var existing = await _storage.LoadStateRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);
        if (existing is not null)
        {
            if (!EquivalentState(existing, offer.StateRevision))
            {
                throw new InvalidDataException(
                    $"Conflicting state revision '{offer.StateRevision.Id}' already exists locally.");
            }

            await VerifyPayloadAsync(
                incoming,
                offer.PayloadLength,
                expectedHash,
                cancellationToken);
            await VerifyStoredPayloadAsync(offer, expectedHash, cancellationToken);
            return;
        }

        await using var verified = new BootstrapDigestVerifyingReadStream(
            incoming,
            offer.PayloadLength,
            expectedHash);
        await _storage.StoreRevisionAsync(
            offer.StateRevision,
            verified,
            cancellationToken);
        verified.EnsureCompleted();
        await VerifyStoredPayloadAsync(offer, expectedHash, cancellationToken);
    }

    private void ValidateOffer(PeerWorldRevisionOffer offer)
    {
        if (offer.PayloadLength < 0 || offer.PayloadLength > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Peer bootstrap payload length '{offer.PayloadLength}' exceeds the allowed range.");
        }

        _ = ParseSha256(offer.PayloadSha256);
        PeerWorldBootstrapTransferService.ValidateCanonicalOfferParts(
            offer.World,
            offer.EnvironmentRevision,
            offer.StateRevision);
    }

    private async Task VerifyStoredPayloadAsync(
        PeerWorldRevisionOffer offer,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stored = await _storage.OpenRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);
        await VerifyPayloadAsync(
            stored,
            offer.PayloadLength,
            expectedHash,
            cancellationToken);
    }

    private static async Task VerifyPayloadAsync(
        Stream payload,
        long expectedLength,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
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

            if (length > expectedLength)
            {
                throw new InvalidDataException("Peer bootstrap payload exceeded its declared length.");
            }

            hash.AppendData(buffer, 0, read);
        }

        if (length != expectedLength)
        {
            throw new InvalidDataException(
                $"Peer bootstrap payload length was {length}, expected {expectedLength}.");
        }

        var actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("Peer bootstrap payload SHA-256 verification failed.");
        }
    }

    private static byte[] ParseSha256(string text)
    {
        byte[] hash;
        try
        {
            hash = Convert.FromHexString(text);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("Peer bootstrap payload SHA-256 is malformed.", exception);
        }

        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("Peer bootstrap payload SHA-256 has the wrong length.");
        }

        return hash;
    }

    private static PeerWorldRevisionReceipt Receipt(PeerWorldRevisionOffer offer)
        => new(
            offer.World.Id,
            offer.StateRevision.Id,
            offer.PayloadLength,
            offer.PayloadSha256.ToUpperInvariant());

    private static bool EquivalentWorld(World left, World right)
        => left.Id == right.Id &&
           string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
           string.Equals(left.GameAdapterId, right.GameAdapterId, StringComparison.Ordinal) &&
           left.CurrentEnvironmentRevisionId == right.CurrentEnvironmentRevisionId &&
           left.CurrentStateRevisionId == right.CurrentStateRevisionId &&
           left.SharingMode == right.SharingMode &&
           left.GameVersionPolicy == right.GameVersionPolicy &&
           left.Visibility == right.Visibility &&
           left.JoinPolicy == right.JoinPolicy &&
           left.StartYourOwnPolicy == right.StartYourOwnPolicy &&
           EquivalentUsers(left.Members, right.Members) &&
           left.StartedFrom == right.StartedFrom &&
           EquivalentCheckpoints(left.Checkpoints, right.Checkpoints);

    private static bool EquivalentEnvironment(
        EnvironmentRevision left,
        EnvironmentRevision right)
        => left.Id == right.Id &&
           left.WorldId == right.WorldId &&
           left.ParentRevisionId == right.ParentRevisionId &&
           left.CreatedAt == right.CreatedAt &&
           EquivalentUser(left.CreatedBy, right.CreatedBy) &&
           EquivalentManifest(left.Manifest, right.Manifest);

    private static bool EquivalentState(StateRevision left, StateRevision right)
        => left.Id == right.Id &&
           left.WorldId == right.WorldId &&
           left.ParentRevisionId == right.ParentRevisionId &&
           left.CreatedAt == right.CreatedAt &&
           EquivalentUser(left.CreatedBy, right.CreatedBy) &&
           string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) &&
           string.Equals(left.StatePackageId, right.StatePackageId, StringComparison.Ordinal) &&
           left.EnvironmentRevisionId == right.EnvironmentRevisionId;

    private static bool EquivalentManifest(EnvironmentManifest left, EnvironmentManifest right)
    {
        if (left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal) ||
            left.Components.Count != right.Components.Count ||
            !EquivalentDictionary(left.Configuration, right.Configuration))
        {
            return false;
        }

        for (var index = 0; index < left.Components.Count; index++)
        {
            var a = left.Components[index];
            var b = right.Components[index];
            if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) ||
                !string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Version, b.Version, StringComparison.Ordinal) ||
                !string.Equals(a.Source, b.Source, StringComparison.Ordinal) ||
                !EquivalentDictionary(a.Metadata, b.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EquivalentDictionary(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EquivalentUsers(
        IReadOnlyList<UserIdentity> left,
        IReadOnlyList<UserIdentity> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!EquivalentUser(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EquivalentUser(UserIdentity? left, UserIdentity? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null &&
               right is not null &&
               PeerWorldBootstrapTransferService.SameUser(left, right);
    }

    private static bool EquivalentCheckpoints(
        IReadOnlyList<WorldCheckpoint> left,
        IReadOnlyList<WorldCheckpoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.StateRevisionId != b.StateRevisionId ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                a.CreatedAt != b.CreatedAt ||
                !EquivalentUser(a.CreatedBy, b.CreatedBy))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class BootstrapDigestVerifyingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private bool _completed;
        private bool _disposed;

        public BootstrapDigestVerifyingReadStream(
            Stream inner,
            long expectedLength,
            byte[] expectedHash)
        {
            _inner = inner;
            _expectedLength = expectedLength;
            _expectedHash = expectedHash;
        }

        public override bool CanRead => !_disposed && _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var read = _inner.Read(buffer, offset, count);
            Observe(buffer.AsSpan(offset, read), read == 0);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            Observe(buffer.Span[..read], read == 0);
            return read;
        }

        public void EnsureCompleted()
        {
            if (!_completed)
            {
                throw new InvalidDataException(
                    "World storage did not consume the complete bootstrap payload.");
            }
        }

        private void Observe(ReadOnlySpan<byte> bytes, bool endOfStream)
        {
            if (bytes.Length > 0)
            {
                checked
                {
                    _length += bytes.Length;
                }

                if (_length > _expectedLength)
                {
                    throw new InvalidDataException("Peer bootstrap payload exceeded its declared length.");
                }

                _hash.AppendData(bytes);
            }

            if (!endOfStream || _completed)
            {
                return;
            }

            if (_length != _expectedLength)
            {
                throw new InvalidDataException(
                    $"Peer bootstrap payload length was {_length}, expected {_expectedLength}.");
            }

            var actualHash = _hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash))
            {
                throw new InvalidDataException("Peer bootstrap payload SHA-256 verification failed.");
            }

            _completed = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
