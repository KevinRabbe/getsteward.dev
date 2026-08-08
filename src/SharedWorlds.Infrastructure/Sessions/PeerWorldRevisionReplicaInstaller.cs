using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Installs one handoff delta onto a participant that already owns the active World's exact base.
/// This is intentionally not the public portable-copy import path: World/revision identities remain
/// unchanged, and the canonical World head is advanced only after immutable payload verification.
/// </summary>
public sealed class PeerWorldRevisionReplicaInstaller
{
    public const long DefaultMaximumPayloadBytes = 64L * 1024 * 1024 * 1024;
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly long _maximumPayloadBytes;

    public PeerWorldRevisionReplicaInstaller(
        IWorldStorage storage,
        long maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        _storage = storage;
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
            throw new ArgumentException("Peer World state payload must be readable.", nameof(statePayload));
        }

        ValidateOffer(offer);

        var existingWorld = await _storage.LoadWorldAsync(
            offer.World.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Peer handoff cannot create World '{offer.World.Id}' from nothing. The target must already have the active World base.");
        ValidateExistingWorldBase(existingWorld, offer);

        var existingEnvironment = await _storage.LoadEnvironmentRevisionAsync(
            offer.World.Id,
            offer.EnvironmentRevision.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Peer handoff target is missing required base environment revision '{offer.EnvironmentRevision.Id}'.");
        if (!EquivalentEnvironmentRevision(existingEnvironment, offer.EnvironmentRevision))
        {
            throw new InvalidDataException(
                $"Peer handoff target has conflicting metadata for environment revision '{offer.EnvironmentRevision.Id}'.");
        }

        var expectedHash = ParseSha256(offer.PayloadSha256);
        var existingCandidate = await _storage.LoadStateRevisionAsync(
            offer.World.Id,
            offer.StateRevision.Id,
            cancellationToken);

        if (existingWorld.CurrentStateRevisionId == offer.StateRevision.Id)
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

        // Canonical metadata is the transaction boundary and is written last. If any transfer,
        // digest, immutable-revision, or storage verification fails, the target never becomes the
        // new canonical owner of this state revision.
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
    }

    private static void ValidateExistingWorldBase(
        World existing,
        PeerWorldRevisionOffer offer)
    {
        var incoming = offer.World;
        if (existing.Id != incoming.Id ||
            !string.Equals(existing.Name, incoming.Name, StringComparison.Ordinal) ||
            !string.Equals(existing.GameAdapterId, incoming.GameAdapterId, StringComparison.Ordinal) ||
            existing.CurrentEnvironmentRevisionId != incoming.CurrentEnvironmentRevisionId ||
            existing.SharingMode != incoming.SharingMode ||
            existing.GameVersionPolicy != incoming.GameVersionPolicy ||
            existing.Visibility != incoming.Visibility ||
            existing.JoinPolicy != incoming.JoinPolicy ||
            existing.StartYourOwnPolicy != incoming.StartYourOwnPolicy ||
            !EquivalentUsers(existing.Members, incoming.Members) ||
            !EquivalentProvenance(existing.StartedFrom, incoming.StartedFrom) ||
            !EquivalentCheckpoints(existing.Checkpoints, incoming.Checkpoints))
        {
            throw new InvalidDataException(
                $"Peer handoff target has divergent canonical metadata for World '{incoming.Id}'.");
        }
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
            throw new InvalidDataException("Peer World payload SHA-256 verification failed.");
        }
    }

    private static byte[] ParseSha256(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("Peer World payload SHA-256 is missing.");
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString(text);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Peer World payload SHA-256 is malformed.", exception);
        }

        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("Peer World payload SHA-256 has the wrong length.");
        }

        return hash;
    }

    private static PeerWorldRevisionReceipt Receipt(PeerWorldRevisionOffer offer)
        => new(
            offer.World.Id,
            offer.StateRevision.Id,
            offer.PayloadLength,
            offer.PayloadSha256.ToUpperInvariant());

    private static bool EquivalentStateRevision(StateRevision left, StateRevision right)
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
               string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
    }

    private static bool EquivalentProvenance(WorldProvenance? left, WorldProvenance? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null && right is not null && left == right;
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

    private sealed class DigestVerifyingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

        private void Observe(ReadOnlySpan<byte> bytes, bool endOfStream)
        {
            if (_completed && bytes.Length > 0)
            {
                throw new InvalidDataException("Peer World payload produced bytes after end-of-stream verification.");
            }

            if (bytes.Length > 0)
            {
                checked
                {
                    _length += bytes.Length;
                }

                if (_length > _expectedLength)
                {
                    throw new InvalidDataException("Peer World payload exceeded its declared byte length.");
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
                throw new InvalidDataException("Peer World payload SHA-256 verification failed.");
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
