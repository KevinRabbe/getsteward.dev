using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Enforces the first-release rule that one Steward desktop process supervises at most one
/// writable managed World lifecycle at a time. Remote coordination still protects each World
/// globally; this gate protects the local desktop across different Worlds.
/// </summary>
public sealed class ManagedWritableSessionGate
{
    private readonly object _gate = new();
    private LeaseState? _active;

    public WorldId? ActiveWorldId
    {
        get
        {
            lock (_gate)
            {
                return _active?.WorldId;
            }
        }
    }

    public ManagedWritableSessionLease Acquire(WorldId worldId)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    $"A writable Steward session is already active for World '{_active.WorldId}'.");
            }

            var token = Guid.NewGuid();
            _active = new LeaseState(worldId, token);
            return new ManagedWritableSessionLease(this, worldId, token);
        }
    }

    private void Release(WorldId worldId, Guid token)
    {
        lock (_gate)
        {
            if (_active is null ||
                _active.WorldId != worldId ||
                _active.Token != token)
            {
                return;
            }

            _active = null;
        }
    }

    private sealed record LeaseState(WorldId WorldId, Guid Token);

    public sealed class ManagedWritableSessionLease : IDisposable
    {
        private ManagedWritableSessionGate? _owner;
        private readonly WorldId _worldId;
        private readonly Guid _token;

        internal ManagedWritableSessionLease(
            ManagedWritableSessionGate owner,
            WorldId worldId,
            Guid token)
        {
            _owner = owner;
            _worldId = worldId;
            _token = token;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_worldId, _token);
        }
    }
}
