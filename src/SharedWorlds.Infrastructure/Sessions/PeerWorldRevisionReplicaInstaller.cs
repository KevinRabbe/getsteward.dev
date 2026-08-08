using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Installs one handoff delta onto a participant that already owns the active World's exact base.
/// The incoming World metadata is authoritative only when it advances persistent peer authority by
/// exactly one generation to this local identity and the state revision is the direct canonical child.
/// A durable per-account fence is activated before the new World head is published.
/// </summary>
public sealed class PeerWorldRevisionReplicaInstaller
{
    public const long DefaultMaximumPayloadBytes = 64L * 1024 * 1024 * 1024;
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly UserIdentity _localUser;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly long _maximumPayloadBytes;

    public PeerWorldRevisionReplicaInstaller(
        IWorldStorage storage,
        UserIdentity localUser,
        IPeerAuthorityFenceStore authorityFences,
        long maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(authorityFences);
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        _storage = storage;
        _localUser = localUser;
        _authorityFences = authorityFences;
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
            throw new ArgumentException(
                "Peer World state payload must be readable.",
                nameof(statePayload));
        }

        ValidateOffer(offer);

        var existingWorld = await _storage.LoadWorldAsync(
            offer.World.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Peer handoff cannot create World '{offer.World.Id}' from nothing. The target must already have the active World base.");
        var idempotent = existingWorld.CurrentStateRevisionId == offer.StateRevision.Id;
        ValidateExistingAuthorityBase(existingWorld, offer, idempotent);
        await RequireReceiverFenceBaseAsync(
            existingWorld,
            offer,
            idempotent,
            cancellationToken);

        var existingEnvironment = await _storage.LoadEnvironmentRevisionAsync(
            offer.World.Id,
            offer.EnvironmentRevision.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Peer handoff target is missing required environment revision '{offer.EnvironmentRevision.Id}'.");
        if (!EquivalentEnvironmentRevision(
                existingEnvironment,
                offer.EnvironmentRevision))
        {
            throw new InvalidDataException(
                $"Peer handoff target has conflicting metadata for environment revision '{offer.EnvironmentRevision.Id}'.");
        }

        var expectedHash = ParseSha256(offer.PayloadSha256);
        var existingCandidate = await _storage.LoadStateRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);

        if (idempotent)
        {
            if (existingCandidate is null ||
                !EquivalentStateRevision(existingCandidate, offer.StateRevision))
            {
                throw new InvalidDataException(
                    "Peer handoff target points at the offered revision but its immutable revision metadata is missing or conflicting.");
            }

            await VerifyIncomingPayloadAsync(
                statePayload,
                offer.PayloadLength,
                expectedHash,
                cancellationToken);
            await VerifyStoredPayloadAsync(offer, cancellationToken);
            await SaveIncomingActiveFenceAsync(offer, cancellationToken);

            // Retry after the target already installed this generation. The confirmed outgoing host
            // may safely resend the same mutable World metadata fenced by the same authority record.
            await _storage.SaveWorldAsync(offer.World, cancellationToken);
            return Receipt(offer);
        }

        if (offer.StateRevision.ParentRevisionId is null ||
            existingWorld.CurrentStateRevisionId != offer.StateRevision.ParentRevisionId)
        {
            throw new InvalidDataException(
                $"Peer handoff target World '{offer.World.Id}' is not on direct parent revision '{offer.StateRevision.ParentRevisionId}'. Refusing to invent or skip canonical history.");
        }

        if (existingCandidate is not null)
        {
            if (!EquivalentStateRevision(existingCandidate, offer.StateRevision))
            {
                throw new InvalidDataException(
                    $"Peer handoff target already contains conflicting immutable state revision '{offer.StateRevision.Id}'.");
            }

            await VerifyIncomingPayloadAsync(
                statePayload,
                offer.PayloadLength,
                expectedHash,
                cancellationToken);
            await VerifyStoredPayloadAsync(offer, cancellationToken);
        }
        else
        {
            await using var verifiedPayload = new DigestVerifyingReadStream(
                statePayload,
                offer.PayloadLength,
                expectedHash);
            await _storage.StoreRevisionAsync(
                offer.StateRevision,
                verifiedPayload,
                cancellationToken);
            verifiedPayload.EnsureCompleted();
            await VerifyStoredPayloadAsync(offer, cancellationToken);
        }

        // The account fence becomes Active N+1 after every immutable byte is verified but before
        // canonical World metadata advances. If the subsequent World write fails, a retry may resume
        // from this exact Active fence; ordinary hosting remains impossible until both agree.
        await SaveIncomingActiveFenceAsync(offer, cancellationToken);
        await _storage.SaveWorldAsync(offer.World, cancellationToken);
        return Receipt(offer);
    }

    private void ValidateOffer(PeerWorldRevisionOffer offer)
    {
        if (offer.PayloadLength < 0 || offer.PayloadLength > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Peer World payload length '{offer.PayloadLength}' exceeds the allowed 0..{_maximumPayloadBytes} byte range.");
        }

        _ = ParseSha256(offer.PayloadSha256);

        if (offer.World.Id != offer.EnvironmentRevision.WorldId ||
            offer.World.Id != offer.StateRevision.WorldId ||
            offer.World.CurrentEnvironmentRevisionId != offer.EnvironmentRevision.Id ||
            offer.World.CurrentStateRevisionId != offer.StateRevision.Id ||
            offer.StateRevision.EnvironmentRevisionId != offer.EnvironmentRevision.Id)
        {
            throw new InvalidDataException(
                "Peer World offer contains mismatched World, environment, or state revision identities.");
        }

        if (!string.Equals(
                offer.World.GameAdapterId,
                offer.EnvironmentRevision.Manifest.AdapterId,
                StringComparison.Ordinal) ||
            !string.Equals(
                offer.World.GameAdapterId,
                offer.StateRevision.AdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Peer World offer contains mismatched game-adapter identities.");
        }

        var authority = offer.World.PeerAuthority
            ?? throw new InvalidDataException(
                "Peer handoff offer is missing persistent World authority.");
        if (authority.Generation == 0 ||
            !SameUser(authority.Holder, _localUser))
        {
            throw new InvalidDataException(
                "Peer handoff offer does not assign a valid persistent authority generation to the local participant.");
        }

        if (!ContainsStableMember(offer.World.Members, _localUser))
        {
            throw new InvalidDataException(
                "Peer handoff offer assigns authority to a local identity outside canonical World membership.");
        }
    }

    private void ValidateExistingAuthorityBase(
        World existing,
        PeerWorldRevisionOffer offer,
        bool idempotent)
    {
        var incoming = offer.World;
        if (existing.Id != incoming.Id ||
            !string.Equals(
                existing.GameAdapterId,
                incoming.GameAdapterId,
                StringComparison.Ordinal) ||
            existing.CurrentEnvironmentRevisionId != incoming.CurrentEnvironmentRevisionId)
        {
            throw new InvalidDataException(
                $"Peer handoff target has incompatible canonical identity/environment metadata for World '{incoming.Id}'.");
        }

        var existingAuthority = existing.PeerAuthority
            ?? throw new InvalidDataException(
                $"Peer handoff target World '{incoming.Id}' has no persistent authority base.");
        var incomingAuthority = incoming.PeerAuthority!;
        if (existingAuthority.Generation == 0)
        {
            throw new InvalidDataException(
                $"Peer handoff target World '{incoming.Id}' has invalid zero authority generation.");
        }

        if (idempotent)
        {
            if (incomingAuthority.Generation != existingAuthority.Generation ||
                !SameUser(incomingAuthority.Holder, existingAuthority.Holder) ||
                !SameUser(incomingAuthority.Holder, _localUser))
            {
                throw new InvalidDataException(
                    "Idempotent handoff retry does not match the already-installed persistent authority generation.");
            }

            return;
        }

        ulong expectedGeneration;
        try
        {
            expectedGeneration = checked(existingAuthority.Generation + 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Peer World authority generation cannot advance beyond UInt64.MaxValue.",
                exception);
        }

        if (incomingAuthority.Generation != expectedGeneration ||
            !SameUser(incomingAuthority.Holder, _localUser))
        {
            throw new InvalidDataException(
                $"Peer handoff must advance authority exactly from generation {existingAuthority.Generation} to {expectedGeneration} and assign it to the local participant.");
        }
    }

    private async Task RequireReceiverFenceBaseAsync(
        World existingWorld,
        PeerWorldRevisionOffer offer,
        bool idempotent,
        CancellationToken cancellationToken)
    {
        var fence = await _authorityFences.LoadAsync(
            offer.World.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Peer handoff target has no durable account fence for its existing World replica.");
        var incomingAuthority = offer.World.PeerAuthority!;

        if (idempotent)
        {
            if (!FenceMatches(
                    fence,
                    PeerAuthorityFenceState.Active,
                    incomingAuthority.Holder,
                    incomingAuthority.Generation,
                    offer.StateRevision.Id))
            {
                throw new InvalidDataException(
                    "Idempotent peer handoff retry does not match the target account's active authority fence.");
            }

            return;
        }

        var existingAuthority = existingWorld.PeerAuthority!;
        var existingState = existingWorld.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                "Peer handoff target has no canonical state revision for its existing authority fence.");
        var normalObservedBase = FenceMatches(
            fence,
            PeerAuthorityFenceState.Observed,
            existingAuthority.Holder,
            existingAuthority.Generation,
            existingState);
        var resumedActiveTarget = FenceMatches(
            fence,
            PeerAuthorityFenceState.Active,
            incomingAuthority.Holder,
            incomingAuthority.Generation,
            offer.StateRevision.Id);
        if (!normalObservedBase && !resumedActiveTarget)
        {
            throw new InvalidDataException(
                "Peer handoff target account fence is stale, conflicting, or belongs to a different authority transition.");
        }
    }

    private async Task SaveIncomingActiveFenceAsync(
        PeerWorldRevisionOffer offer,
        CancellationToken cancellationToken)
    {
        var authority = offer.World.PeerAuthority!;
        var current = await _authorityFences.LoadAsync(
            offer.World.Id,
            cancellationToken);
        if (current is not null &&
            current.Generation > authority.Generation)
        {
            throw new InvalidDataException(
                "Peer handoff would overwrite a newer durable authority fence on the target account.");
        }

        if (current is not null &&
            current.Generation == authority.Generation &&
            current.State == PeerAuthorityFenceState.Active &&
            (!SameUser(current.Holder, authority.Holder) ||
             current.StateRevisionId != offer.StateRevision.Id))
        {
            throw new InvalidDataException(
                "Peer handoff conflicts with an existing active authority fence at the same generation.");
        }

        await _authorityFences.SaveAsync(
            new PeerAuthorityFence(
                offer.World.Id,
                authority.Holder,
                authority.Generation,
                offer.StateRevision.Id,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task VerifyStoredPayloadAsync(
        PeerWorldRevisionOffer offer,
        CancellationToken cancellationToken)
    {
        await using var stored = await _storage.OpenRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);
        var expectedHash = ParseSha256(offer.PayloadSha256);
        await VerifyIncomingPayloadAsync(
            stored,
            offer.PayloadLength,
            expectedHash,
            cancellationToken);
    }

    private static async Task VerifyIncomingPayloadAsync(
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
                throw new InvalidDataException(
                    "Peer World payload exceeded its declared byte length.");
            }

            hash.AppendData(buffer, 0, read);
        }

        if (length != expectedLength)
        {
            throw new InvalidDataException(
                $"Peer World payload length was {length} bytes, expected {expectedLength}.");
        }

        var actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException(
                "Peer World payload SHA-256 verification failed.");
        }
    }

    private static byte[] ParseSha256(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException(
                "Peer World payload SHA-256 is missing.");
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString(text);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Peer World payload SHA-256 is malformed.",
                exception);
        }

        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                "Peer World payload SHA-256 has the wrong length.");
        }

        return hash;
    }

    private static PeerWorldRevisionReceipt Receipt(PeerWorldRevisionOffer offer)
        => new(
            offer.World.Id,
            offer.StateRevision.Id,
            offer.PayloadLength,
            offer.PayloadSha256.ToUpperInvariant());

    private static bool EquivalentStateRevision(
        StateRevision left,
        StateRevision right)
        => left.Id == right.Id &&
           left.WorldId == right.WorldId &&
           left.ParentRevisionId == right.ParentRevisionId &&
           left.CreatedAt == right.CreatedAt &&
           EquivalentUser(left.CreatedBy, right.CreatedBy) &&
           string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) &&
           string.Equals(left.StatePackageId, right.StatePackageId, StringComparison.Ordinal) &&
           left.EnvironmentRevisionId == right.EnvironmentRevisionId;

    private static bool EquivalentEnvironmentRevision(
        EnvironmentRevision left,
        EnvironmentRevision right)
        => left.Id == right.Id &&
           left.WorldId == right.WorldId &&
           left.ParentRevisionId == right.ParentRevisionId &&
           left.CreatedAt == right.CreatedAt &&
           EquivalentUser(left.CreatedBy, right.CreatedBy) &&
           EquivalentManifest(left.Manifest, right.Manifest);

    private static bool EquivalentManifest(
        EnvironmentManifest left,
        EnvironmentManifest right)
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

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool FenceMatches(
        PeerAuthorityFence fence,
        PeerAuthorityFenceState state,
        UserIdentity holder,
        ulong generation,
        RevisionId stateRevisionId)
        => fence.State == state &&
           fence.Generation == generation &&
           fence.StateRevisionId == stateRevisionId &&
           SameUser(fence.Holder, holder);

    private static bool EquivalentUser(
        UserIdentity? left,
        UserIdentity? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null &&
               right is not null &&
               SameUser(left, right);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private sealed class DigestVerifyingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        private long _length;
        private bool _completed;
        private bool _disposed;

        public DigestVerifyingReadStream(
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
                    "World storage did not consume the complete peer payload before reporting success.");
            }
        }

        private void Observe(
            ReadOnlySpan<byte> bytes,
            bool endOfStream)
        {
            if (_completed && bytes.Length > 0)
            {
                throw new InvalidDataException(
                    "Peer World payload produced bytes after end-of-stream verification.");
            }

            if (bytes.Length > 0)
            {
                checked
                {
                    _length += bytes.Length;
                }

                if (_length > _expectedLength)
                {
                    throw new InvalidDataException(
                        "Peer World payload exceeded its declared byte length.");
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
                    $"Peer World payload length was {_length} bytes, expected {_expectedLength}.");
            }

            var actualHash = _hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash))
            {
                throw new InvalidDataException(
                    "Peer World payload SHA-256 verification failed.");
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
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
