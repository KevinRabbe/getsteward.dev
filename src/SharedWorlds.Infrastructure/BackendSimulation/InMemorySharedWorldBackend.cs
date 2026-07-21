using System.Security.Cryptography;

namespace SharedWorlds.Infrastructure.BackendSimulation;

public enum SimulatedReservationState
{
    Active,
    Uncertain
}

public enum AcquireReservationStatus
{
    Acquired,
    AlreadyReserved,
    WorldUncertain,
    HeadChanged,
    Unauthorized
}

public enum HeartbeatStatus
{
    Accepted,
    ReservationMismatch,
    Unauthorized
}

public enum PublishCandidateStatus
{
    Published,
    InvalidCandidate,
    Unauthorized
}

public enum CommitCandidateStatus
{
    Committed,
    Unchanged,
    HeadChanged,
    ReservationMismatch,
    InvalidCandidate,
    Unauthorized
}

public enum ReclaimReservationStatus
{
    Reclaimed,
    GracePeriodRequired,
    ReservationMismatch,
    Unauthorized
}

public sealed record SimulatedWorldSnapshot(
    string WorldId,
    string AdapterId,
    string CurrentStateRevisionId,
    string? CurrentEnvironmentRevisionId,
    string AccessManagerIdentityId);

public sealed record SimulatedReservationSnapshot(
    string WorldId,
    string SessionId,
    long Generation,
    string HolderIdentityId,
    string DeviceId,
    string StartingStateRevisionId,
    SimulatedReservationState State,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? BecameUncertainAt);

public sealed record AcquireReservationResult(
    AcquireReservationStatus Status,
    SimulatedReservationSnapshot? Reservation,
    string? ObservedHeadRevisionId);

public sealed record PublishCandidateResult(
    PublishCandidateStatus Status,
    string? RevisionId);

public sealed record CommitCandidateResult(
    CommitCandidateStatus Status,
    string CurrentHeadRevisionId,
    string? CandidateRevisionId);

public sealed record ReclaimReservationResult(
    ReclaimReservationStatus Status,
    long? InvalidatedGeneration);

/// <summary>
/// Provider-free deterministic simulation of the shared backend authority contract.
/// It intentionally owns no HTTP, Steam, database, object-storage, UI, or game-specific behavior.
/// </summary>
public sealed class InMemorySharedWorldBackend
{
    public static readonly TimeSpan UncertaintyAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReclaimAfterUncertain = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, WorldRecord> _worlds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RevisionRecord> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReservationRecord> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generationCounters = new(StringComparer.Ordinal);

    public InMemorySharedWorldBackend(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public void CreateWorld(
        string worldId,
        string adapterId,
        string initialStateRevisionId,
        ReadOnlySpan<byte> initialStateBytes,
        string accessManagerIdentityId,
        IEnumerable<string>? additionalMembers = null,
        string? environmentRevisionId = null)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(adapterId, nameof(adapterId));
        ValidateRequired(initialStateRevisionId, nameof(initialStateRevisionId));
        ValidateRequired(accessManagerIdentityId, nameof(accessManagerIdentityId));

        lock (_gate)
        {
            if (_worlds.ContainsKey(worldId))
            {
                throw new InvalidOperationException($"World '{worldId}' already exists.");
            }

            if (_revisions.ContainsKey(initialStateRevisionId))
            {
                throw new InvalidOperationException($"Revision '{initialStateRevisionId}' already exists.");
            }

            var members = new HashSet<string>(StringComparer.Ordinal)
            {
                accessManagerIdentityId
            };

            if (additionalMembers is not null)
            {
                foreach (var member in additionalMembers)
                {
                    ValidateRequired(member, nameof(additionalMembers));
                    members.Add(member);
                }
            }

            var bytes = initialStateBytes.ToArray();
            _revisions.Add(
                initialStateRevisionId,
                new RevisionRecord(
                    initialStateRevisionId,
                    worldId,
                    ComputeSha256(bytes),
                    bytes,
                    published: true));

            _worlds.Add(
                worldId,
                new WorldRecord(
                    worldId,
                    adapterId,
                    initialStateRevisionId,
                    environmentRevisionId,
                    accessManagerIdentityId,
                    members));
        }
    }

    public SimulatedWorldSnapshot? GetWorld(string worldId, string callerIdentityId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            if (!_worlds.TryGetValue(worldId, out var world) || !world.Members.Contains(callerIdentityId))
            {
                return null;
            }

            return ToSnapshot(world);
        }
    }

    public AcquireReservationResult AcquireReservation(
        string worldId,
        string callerIdentityId,
        string deviceId,
        string expectedHeadRevisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(deviceId, nameof(deviceId));
        ValidateRequired(expectedHeadRevisionId, nameof(expectedHeadRevisionId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out var world))
            {
                return new(AcquireReservationStatus.Unauthorized, null, null);
            }

            RefreshUncertainty(worldId);

            if (!string.Equals(world.CurrentStateRevisionId, expectedHeadRevisionId, StringComparison.Ordinal))
            {
                return new(AcquireReservationStatus.HeadChanged, null, world.CurrentStateRevisionId);
            }

            if (_reservations.TryGetValue(worldId, out var existing))
            {
                return new(
                    existing.State == SimulatedReservationState.Uncertain
                        ? AcquireReservationStatus.WorldUncertain
                        : AcquireReservationStatus.AlreadyReserved,
                    ToSnapshot(existing),
                    world.CurrentStateRevisionId);
            }

            var generation = _generationCounters.TryGetValue(worldId, out var currentGeneration)
                ? currentGeneration + 1
                : 1;
            _generationCounters[worldId] = generation;

            var now = _utcNow();
            var reservation = new ReservationRecord(
                worldId,
                $"{worldId}-session-{generation}",
                generation,
                callerIdentityId,
                deviceId,
                world.CurrentStateRevisionId,
                SimulatedReservationState.Active,
                now,
                becameUncertainAt: null);

            _reservations.Add(worldId, reservation);

            return new(
                AcquireReservationStatus.Acquired,
                ToSnapshot(reservation),
                world.CurrentStateRevisionId);
        }
    }

    public HeartbeatStatus Heartbeat(
        string worldId,
        string callerIdentityId,
        string deviceId,
        long generation)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(deviceId, nameof(deviceId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return HeartbeatStatus.Unauthorized;
            }

            RefreshUncertainty(worldId);

            if (!_reservations.TryGetValue(worldId, out var reservation) ||
                reservation.Generation != generation ||
                !string.Equals(reservation.HolderIdentityId, callerIdentityId, StringComparison.Ordinal) ||
                !string.Equals(reservation.DeviceId, deviceId, StringComparison.Ordinal))
            {
                return HeartbeatStatus.ReservationMismatch;
            }

            reservation.LastHeartbeatAt = _utcNow();
            reservation.State = SimulatedReservationState.Active;
            reservation.BecameUncertainAt = null;
            return HeartbeatStatus.Accepted;
        }
    }

    public SimulatedReservationSnapshot? GetReservation(string worldId, string callerIdentityId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return null;
            }

            RefreshUncertainty(worldId);
            return _reservations.TryGetValue(worldId, out var reservation)
                ? ToSnapshot(reservation)
                : null;
        }
    }

    public PublishCandidateResult PublishCandidate(
        string worldId,
        string callerIdentityId,
        string revisionId,
        ReadOnlySpan<byte> packageBytes,
        long expectedByteSize,
        string expectedSha256)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(revisionId, nameof(revisionId));
        ValidateRequired(expectedSha256, nameof(expectedSha256));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return new(PublishCandidateStatus.Unauthorized, null);
            }

            if (_revisions.TryGetValue(revisionId, out var existing))
            {
                var sameCandidate = existing.WorldId == worldId &&
                    existing.ByteSize == expectedByteSize &&
                    string.Equals(existing.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

                return sameCandidate
                    ? new(PublishCandidateStatus.Published, revisionId)
                    : new(PublishCandidateStatus.InvalidCandidate, null);
            }

            var bytes = packageBytes.ToArray();
            var actualHash = ComputeSha256(bytes);

            if (bytes.LongLength != expectedByteSize ||
                !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(PublishCandidateStatus.InvalidCandidate, null);
            }

            _revisions.Add(
                revisionId,
                new RevisionRecord(revisionId, worldId, actualHash, bytes, published: true));

            return new(PublishCandidateStatus.Published, revisionId);
        }
    }

    public CommitCandidateResult CommitCandidate(
        string worldId,
        string callerIdentityId,
        long generation,
        string expectedHeadRevisionId,
        string candidateRevisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(expectedHeadRevisionId, nameof(expectedHeadRevisionId));
        ValidateRequired(candidateRevisionId, nameof(candidateRevisionId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out var world))
            {
                return new(CommitCandidateStatus.Unauthorized, string.Empty, candidateRevisionId);
            }

            RefreshUncertainty(worldId);

            if (!_reservations.TryGetValue(worldId, out var reservation) ||
                reservation.Generation != generation ||
                !string.Equals(reservation.HolderIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return new(CommitCandidateStatus.ReservationMismatch, world.CurrentStateRevisionId, candidateRevisionId);
            }

            if (!string.Equals(world.CurrentStateRevisionId, expectedHeadRevisionId, StringComparison.Ordinal) ||
                !string.Equals(reservation.StartingStateRevisionId, expectedHeadRevisionId, StringComparison.Ordinal))
            {
                return new(CommitCandidateStatus.HeadChanged, world.CurrentStateRevisionId, candidateRevisionId);
            }

            if (!_revisions.TryGetValue(candidateRevisionId, out var candidate) ||
                !candidate.Published ||
                !string.Equals(candidate.WorldId, worldId, StringComparison.Ordinal))
            {
                return new(CommitCandidateStatus.InvalidCandidate, world.CurrentStateRevisionId, candidateRevisionId);
            }

            var currentRevision = _revisions[world.CurrentStateRevisionId];
            if (string.Equals(currentRevision.Sha256, candidate.Sha256, StringComparison.Ordinal))
            {
                return new(CommitCandidateStatus.Unchanged, world.CurrentStateRevisionId, candidateRevisionId);
            }

            world.CurrentStateRevisionId = candidateRevisionId;
            return new(CommitCandidateStatus.Committed, world.CurrentStateRevisionId, candidateRevisionId);
        }
    }

    public bool ReleaseReservation(
        string worldId,
        string callerIdentityId,
        long generation)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return false;
            }

            if (!_reservations.TryGetValue(worldId, out var reservation) ||
                reservation.Generation != generation ||
                !string.Equals(reservation.HolderIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return false;
            }

            _reservations.Remove(worldId);
            return true;
        }
    }

    public ReclaimReservationResult ReclaimUncertainReservation(
        string worldId,
        string callerIdentityId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return new(ReclaimReservationStatus.Unauthorized, null);
            }

            RefreshUncertainty(worldId);

            if (!_reservations.TryGetValue(worldId, out var reservation) ||
                reservation.State != SimulatedReservationState.Uncertain ||
                reservation.BecameUncertainAt is null)
            {
                return new(ReclaimReservationStatus.ReservationMismatch, null);
            }

            if (_utcNow() - reservation.BecameUncertainAt.Value < ReclaimAfterUncertain)
            {
                return new(ReclaimReservationStatus.GracePeriodRequired, null);
            }

            var invalidatedGeneration = reservation.Generation;
            _reservations.Remove(worldId);
            return new(ReclaimReservationStatus.Reclaimed, invalidatedGeneration);
        }
    }

    public byte[]? DownloadRevision(
        string worldId,
        string callerIdentityId,
        string revisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(revisionId, nameof(revisionId));

        lock (_gate)
        {
            if (!TryGetAuthorizedWorld(worldId, callerIdentityId, out _))
            {
                return null;
            }

            if (!_revisions.TryGetValue(revisionId, out var revision) ||
                !revision.Published ||
                !string.Equals(revision.WorldId, worldId, StringComparison.Ordinal))
            {
                return null;
            }

            return revision.Bytes.ToArray();
        }
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private void RefreshUncertainty(string worldId)
    {
        if (!_reservations.TryGetValue(worldId, out var reservation) ||
            reservation.State != SimulatedReservationState.Active)
        {
            return;
        }

        var now = _utcNow();
        if (now - reservation.LastHeartbeatAt < UncertaintyAfter)
        {
            return;
        }

        reservation.State = SimulatedReservationState.Uncertain;
        reservation.BecameUncertainAt = now;
    }

    private bool TryGetAuthorizedWorld(
        string worldId,
        string callerIdentityId,
        out WorldRecord world)
    {
        if (_worlds.TryGetValue(worldId, out var found) && found.Members.Contains(callerIdentityId))
        {
            world = found;
            return true;
        }

        world = null!;
        return false;
    }

    private static SimulatedWorldSnapshot ToSnapshot(WorldRecord world)
        => new(
            world.WorldId,
            world.AdapterId,
            world.CurrentStateRevisionId,
            world.CurrentEnvironmentRevisionId,
            world.AccessManagerIdentityId);

    private static SimulatedReservationSnapshot ToSnapshot(ReservationRecord reservation)
        => new(
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            reservation.HolderIdentityId,
            reservation.DeviceId,
            reservation.StartingStateRevisionId,
            reservation.State,
            reservation.LastHeartbeatAt,
            reservation.BecameUncertainAt);

    private static void ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }
    }

    private sealed class WorldRecord
    {
        public WorldRecord(
            string worldId,
            string adapterId,
            string currentStateRevisionId,
            string? currentEnvironmentRevisionId,
            string accessManagerIdentityId,
            HashSet<string> members)
        {
            WorldId = worldId;
            AdapterId = adapterId;
            CurrentStateRevisionId = currentStateRevisionId;
            CurrentEnvironmentRevisionId = currentEnvironmentRevisionId;
            AccessManagerIdentityId = accessManagerIdentityId;
            Members = members;
        }

        public string WorldId { get; }
        public string AdapterId { get; }
        public string CurrentStateRevisionId { get; set; }
        public string? CurrentEnvironmentRevisionId { get; }
        public string AccessManagerIdentityId { get; }
        public HashSet<string> Members { get; }
    }

    private sealed class RevisionRecord
    {
        public RevisionRecord(
            string revisionId,
            string worldId,
            string sha256,
            byte[] bytes,
            bool published)
        {
            RevisionId = revisionId;
            WorldId = worldId;
            Sha256 = sha256;
            Bytes = bytes;
            Published = published;
        }

        public string RevisionId { get; }
        public string WorldId { get; }
        public string Sha256 { get; }
        public byte[] Bytes { get; }
        public long ByteSize => Bytes.LongLength;
        public bool Published { get; }
    }

    private sealed class ReservationRecord
    {
        public ReservationRecord(
            string worldId,
            string sessionId,
            long generation,
            string holderIdentityId,
            string deviceId,
            string startingStateRevisionId,
            SimulatedReservationState state,
            DateTimeOffset lastHeartbeatAt,
            DateTimeOffset? becameUncertainAt)
        {
            WorldId = worldId;
            SessionId = sessionId;
            Generation = generation;
            HolderIdentityId = holderIdentityId;
            DeviceId = deviceId;
            StartingStateRevisionId = startingStateRevisionId;
            State = state;
            LastHeartbeatAt = lastHeartbeatAt;
            BecameUncertainAt = becameUncertainAt;
        }

        public string WorldId { get; }
        public string SessionId { get; }
        public long Generation { get; }
        public string HolderIdentityId { get; }
        public string DeviceId { get; }
        public string StartingStateRevisionId { get; }
        public SimulatedReservationState State { get; set; }
        public DateTimeOffset LastHeartbeatAt { get; set; }
        public DateTimeOffset? BecameUncertainAt { get; set; }
    }
}
