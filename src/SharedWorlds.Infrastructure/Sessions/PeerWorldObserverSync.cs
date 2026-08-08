using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// P2P transport boundary for a non-authoritative participant catching an existing replica up to the
/// confirmed host's current canonical head. Each offer is one immutable revision step; authority
/// remains observed and never becomes writable through this path.
/// </summary>
public interface IPeerWorldObserverSyncExchange
{
    Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
        UserIdentity targetMember,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Replays the missing canonical state chain from an existing participant replica to the current host
/// head. Revisions are sent oldest to newest and every step retains the host's current persistent peer
/// authority generation. A returning member may therefore catch up across one or more past host
/// handoffs without ever acquiring writable authority.
/// </summary>
public sealed class PeerWorldObserverSyncService
{
    public const int DefaultMaximumRevisionSteps = 512;
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly UserIdentity _localUser;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly IPeerWorldObserverSyncExchange _exchange;
    private readonly int _maximumRevisionSteps;

    public PeerWorldObserverSyncService(
        IWorldStorage storage,
        UserIdentity localUser,
        IPeerAuthorityFenceStore authorityFences,
        IPeerWorldObserverSyncExchange exchange,
        int maximumRevisionSteps = DefaultMaximumRevisionSteps)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(exchange);
        if (maximumRevisionSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRevisionSteps));
        }

        _storage = storage;
        _localUser = localUser;
        _authorityFences = authorityFences;
        _exchange = exchange;
        _maximumRevisionSteps = maximumRevisionSteps;
    }

    public async Task<World> SynchronizeAsync(
        WorldId worldId,
        UserIdentity targetMember,
        RevisionId targetCurrentStateRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetMember);
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot synchronize missing canonical World '{worldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' is local-only and cannot synchronize a peer observer.");
        }

        var authority = world.PeerAuthority
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no persistent peer authority.");
        if (authority.Generation == 0 ||
            !SameUser(authority.Holder, _localUser) ||
            !ContainsStableMember(world.Members, _localUser))
        {
            throw new InvalidOperationException(
                $"Local identity '{_localUser.ExternalId}' is not the canonical peer authority holder for World '{worldId}'.");
        }

        if (!ContainsStableMember(world.Members, targetMember))
        {
            throw new InvalidOperationException(
                $"Identity '{targetMember.ExternalId}' is not a canonical member of World '{worldId}'.");
        }

        if (SameUser(targetMember, _localUser))
        {
            throw new InvalidOperationException(
                "Steward cannot observer-sync the active World back to its own authority account.");
        }

        var currentStateRevision = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical state head to synchronize.");
        await RequireActiveSourceFenceAsync(
            world,
            currentStateRevision,
            cancellationToken);

        var revisions = await BuildForwardChainAsync(
            world,
            targetCurrentStateRevision,
            cancellationToken);
        if (revisions.Count == 0)
        {
            var current = await RequireStateRevisionAsync(
                worldId,
                currentStateRevision,
                cancellationToken);
            revisions.Add(current);
        }

        foreach (var state in revisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var environmentId = state.EnvironmentRevisionId
                ?? throw new InvalidDataException(
                    $"State revision '{state.Id}' does not identify its exact environment revision.");
            var environment = await _storage.LoadEnvironmentRevisionAsync(
                worldId,
                environmentId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"World '{worldId}' is missing environment revision '{environmentId}' required by state '{state.Id}'.");
            ValidateRevisionIdentity(world, environment, state);

            var stepWorld = world with
            {
                CurrentEnvironmentRevisionId = environment.Id,
                CurrentStateRevisionId = state.Id
            };
            var digest = await ComputeDigestAsync(
                worldId,
                state.Id,
                cancellationToken);
            var offer = new PeerWorldRevisionOffer(
                stepWorld,
                environment,
                state,
                digest.Length,
                digest.Sha256);

            await using var payload = await _storage.OpenRevisionAsync(
                worldId,
                state.Id,
                cancellationToken);
            var receipt = await _exchange.TransferObserverRevisionAsync(
                targetMember,
                offer,
                payload,
                cancellationToken);
            if (receipt.WorldId != worldId ||
                receipt.StateRevisionId != state.Id ||
                receipt.PayloadLength != digest.Length ||
                !string.Equals(
                    receipt.PayloadSha256,
                    digest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The peer observer acknowledged a different World revision or payload than Steward synchronized.");
            }
        }

        return world;
    }

    private async Task<List<StateRevision>> BuildForwardChainAsync(
        World world,
        RevisionId targetCurrentStateRevision,
        CancellationToken cancellationToken)
    {
        var head = world.CurrentStateRevisionId!.Value;
        if (head == targetCurrentStateRevision)
        {
            return [];
        }

        var reverse = new List<StateRevision>();
        var visited = new HashSet<RevisionId>();
        var cursor = head;
        for (var step = 0; step < _maximumRevisionSteps; step++)
        {
            if (!visited.Add(cursor))
            {
                throw new InvalidDataException(
                    $"World '{world.Id}' state history contains a cycle at revision '{cursor}'.");
            }

            var revision = await RequireStateRevisionAsync(
                world.Id,
                cursor,
                cancellationToken);
            if (revision.Id == targetCurrentStateRevision)
            {
                reverse.Reverse();
                return reverse;
            }

            reverse.Add(revision);
            if (revision.ParentRevisionId is not { } parent)
            {
                break;
            }

            cursor = parent;
        }

        if (reverse.Count >= _maximumRevisionSteps)
        {
            throw new InvalidDataException(
                $"World '{world.Id}' needs more than {_maximumRevisionSteps} revision steps for observer catch-up. Refusing an unbounded history walk.");
        }

        throw new InvalidDataException(
            $"Target revision '{targetCurrentStateRevision}' is not an ancestor of World '{world.Id}' canonical head '{head}'. Refusing to merge divergent history.");
    }

    private async Task<StateRevision> RequireStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken)
        => await _storage.LoadStateRevisionAsync(
               worldId,
               revisionId,
               cancellationToken)
           ?? throw new InvalidDataException(
               $"World '{worldId}' is missing state revision metadata '{revisionId}'.");

    private async Task RequireActiveSourceFenceAsync(
        World world,
        RevisionId stateRevisionId,
        CancellationToken cancellationToken)
    {
        var authority = world.PeerAuthority!;
        var fence = await _authorityFences.LoadAsync(
            world.Id,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Local account has no durable authority fence for World '{world.Id}'.");
        if (fence.State != PeerAuthorityFenceState.Active ||
            fence.Generation != authority.Generation ||
            fence.StateRevisionId != stateRevisionId ||
            !SameUser(fence.Holder, _localUser))
        {
            throw new InvalidOperationException(
                $"Local account authority fence does not confirm active peer authority for World '{world.Id}'.");
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

    private static void ValidateRevisionIdentity(
        World world,
        EnvironmentRevision environment,
        StateRevision state)
    {
        if (environment.WorldId != world.Id ||
            state.WorldId != world.Id ||
            state.EnvironmentRevisionId != environment.Id ||
            !string.Equals(state.AdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(
                environment.Manifest.AdapterId,
                world.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Observer synchronization encountered mismatched World, revision, or adapter identities.");
        }
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    internal static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}

/// <summary>
/// Applies one canonical observer step. It may advance a stale observer across newer authority
/// generations, but the local account always remains `Observed`; this path can never create Active
/// writable authority. State history must advance by one direct child at a time or retry the exact head.
/// </summary>
public sealed class PeerWorldObserverSyncInstaller
{
    private const int HashBufferBytes = 128 * 1024;

    private readonly IWorldStorage _storage;
    private readonly UserIdentity _localUser;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly long _maximumPayloadBytes;

    public PeerWorldObserverSyncInstaller(
        IWorldStorage storage,
        UserIdentity localUser,
        IPeerAuthorityFenceStore authorityFences,
        long maximumPayloadBytes = PeerWorldRevisionReplicaInstaller.DefaultMaximumPayloadBytes)
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
                "Peer observer payload must be readable.",
                nameof(statePayload));
        }

        ValidateOffer(offer);
        var existing = await _storage.LoadWorldAsync(
            offer.World.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Observer synchronization cannot create World '{offer.World.Id}'. Use peer bootstrap first.");
        var existingAuthority = existing.PeerAuthority
            ?? throw new InvalidDataException(
                $"Existing observer World '{existing.Id}' has no persistent peer authority.");
        var incomingAuthority = offer.World.PeerAuthority!;
        ValidateAuthorityAdvance(
            existingAuthority,
            incomingAuthority,
            offer.World.Id);

        var idempotentState = existing.CurrentStateRevisionId == offer.StateRevision.Id;
        if (!idempotentState &&
            (offer.StateRevision.ParentRevisionId is null ||
             existing.CurrentStateRevisionId != offer.StateRevision.ParentRevisionId))
        {
            throw new InvalidDataException(
                $"Observer World '{existing.Id}' is not on direct parent revision '{offer.StateRevision.ParentRevisionId}'. Refusing to skip or merge history.");
        }

        await RequireObservedFenceBaseAsync(
            existing,
            offer,
            idempotentState,
            cancellationToken);
        await EnsureEnvironmentAsync(offer, cancellationToken);

        var expectedHash = ParseSha256(offer.PayloadSha256);
        await EnsureStateAsync(
            offer,
            statePayload,
            expectedHash,
            cancellationToken);

        // The observer fence advances before the local World head. A failed metadata write therefore
        // leaves a safe resumable mismatch that cannot ever be mistaken for writable authority.
        await _authorityFences.SaveAsync(
            new PeerAuthorityFence(
                offer.World.Id,
                incomingAuthority.Holder,
                incomingAuthority.Generation,
                offer.StateRevision.Id,
                PeerAuthorityFenceState.Observed,
                DateTimeOffset.UtcNow),
            cancellationToken);
        await _storage.SaveWorldAsync(offer.World, cancellationToken);
        return new PeerWorldRevisionReceipt(
            offer.World.Id,
            offer.StateRevision.Id,
            offer.PayloadLength,
            offer.PayloadSha256.ToUpperInvariant());
    }

    private void ValidateOffer(PeerWorldRevisionOffer offer)
    {
        if (offer.PayloadLength < 0 || offer.PayloadLength > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Peer observer payload length '{offer.PayloadLength}' exceeds the allowed range.");
        }

        _ = ParseSha256(offer.PayloadSha256);
        if (offer.World.Id != offer.EnvironmentRevision.WorldId ||
            offer.World.Id != offer.StateRevision.WorldId ||
            offer.World.CurrentEnvironmentRevisionId != offer.EnvironmentRevision.Id ||
            offer.World.CurrentStateRevisionId != offer.StateRevision.Id ||
            offer.StateRevision.EnvironmentRevisionId != offer.EnvironmentRevision.Id ||
            !string.Equals(
                offer.World.GameAdapterId,
                offer.StateRevision.AdapterId,
                StringComparison.Ordinal) ||
            !string.Equals(
                offer.World.GameAdapterId,
                offer.EnvironmentRevision.Manifest.AdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Peer observer offer contains mismatched World, revision, environment, or adapter identities.");
        }

        var authority = offer.World.PeerAuthority
            ?? throw new InvalidDataException(
                "Peer observer offer is missing persistent World authority.");
        if (authority.Generation == 0 ||
            SameUser(authority.Holder, _localUser) ||
            !ContainsStableMember(offer.World.Members, authority.Holder) ||
            !ContainsStableMember(offer.World.Members, _localUser))
        {
            throw new InvalidDataException(
                "Peer observer offer contains invalid authority or canonical membership.");
        }
    }

    private static void ValidateAuthorityAdvance(
        WorldPeerAuthority existing,
        WorldPeerAuthority incoming,
        WorldId worldId)
    {
        if (existing.Generation == 0)
        {
            throw new InvalidDataException(
                $"Observer World '{worldId}' has invalid zero authority generation.");
        }

        if (incoming.Generation < existing.Generation)
        {
            throw new InvalidDataException(
                $"Observer synchronization cannot move authority backward from generation {existing.Generation} to {incoming.Generation}.");
        }

        if (incoming.Generation == existing.Generation &&
            !SameUser(incoming.Holder, existing.Holder))
        {
            throw new InvalidDataException(
                "Observer synchronization conflicts with the existing holder at the same authority generation.");
        }
    }

    private async Task RequireObservedFenceBaseAsync(
        World existing,
        PeerWorldRevisionOffer offer,
        bool idempotentState,
        CancellationToken cancellationToken)
    {
        var current = await _authorityFences.LoadAsync(
            existing.Id,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Observer World has no durable account fence for its existing replica.");
        var existingAuthority = existing.PeerAuthority!;
        var incomingAuthority = offer.World.PeerAuthority!;
        var existingState = existing.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                "Observer World has no canonical state revision for its durable fence.");

        var normalBase = current.State == PeerAuthorityFenceState.Observed &&
                         current.Generation == existingAuthority.Generation &&
                         current.StateRevisionId == existingState &&
                         SameUser(current.Holder, existingAuthority.Holder);
        var resumedIncoming = current.State == PeerAuthorityFenceState.Observed &&
                              current.Generation == incomingAuthority.Generation &&
                              current.StateRevisionId == offer.StateRevision.Id &&
                              SameUser(current.Holder, incomingAuthority.Holder);
        if (!normalBase && !resumedIncoming)
        {
            throw new InvalidDataException(
                "Observer account fence is stale, writable, or belongs to a different synchronization transition.");
        }

        if (idempotentState && !normalBase && !resumedIncoming)
        {
            throw new InvalidDataException(
                "Idempotent observer synchronization does not match the durable account fence.");
        }
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

        await using var verified = new ObserverDigestVerifyingReadStream(
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
                throw new InvalidDataException(
                    "Peer observer payload exceeded its declared length.");
            }

            hash.AppendData(buffer, 0, read);
        }

        if (length != expectedLength)
        {
            throw new InvalidDataException(
                $"Peer observer payload length was {length}, expected {expectedLength}.");
        }

        var actual = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedHash))
        {
            throw new InvalidDataException(
                "Peer observer payload SHA-256 verification failed.");
        }
    }

    private static byte[] ParseSha256(string text)
    {
        byte[] hash;
        try
        {
            hash = Convert.FromHexString(text);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException(
                "Peer observer payload SHA-256 is malformed.",
                exception);
        }

        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                "Peer observer payload SHA-256 has the wrong length.");
        }

        return hash;
    }

    private static bool EquivalentState(
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

    private static bool EquivalentEnvironment(
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
        => PeerWorldObserverSyncService.SameUser(left, right);

    private sealed class ObserverDigestVerifyingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        private long _length;
        private bool _completed;
        private bool _disposed;

        public ObserverDigestVerifyingReadStream(
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
                    "World storage did not consume the complete observer payload.");
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
                    throw new InvalidDataException(
                        "Peer observer payload exceeded its declared length.");
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
                    $"Peer observer payload length was {_length}, expected {_expectedLength}.");
            }

            var actual = _hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actual, _expectedHash))
            {
                throw new InvalidDataException(
                    "Peer observer payload SHA-256 verification failed.");
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
