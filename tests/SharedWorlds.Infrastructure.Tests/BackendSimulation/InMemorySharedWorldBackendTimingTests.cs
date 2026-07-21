using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemorySharedWorldBackendTimingTests
{
    [Fact]
    public void ReclaimGraceIsMeasuredFromHeartbeatDeadlineNotFirstObservation()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var backend = new InMemorySharedWorldBackend(() => clock.UtcNow);
        backend.CreateWorld(
            "world-1",
            "test-adapter",
            "S0",
            Encoding.UTF8.GetBytes("initial"),
            "steam-a",
            new[] { "steam-b" });

        var acquired = backend.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var reservation = Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);

        clock.Advance(
            InMemorySharedWorldBackend.UncertaintyAfter +
            InMemorySharedWorldBackend.ReclaimAfterUncertain +
            TimeSpan.FromSeconds(1));

        // No intermediate status read occurs. Reclaim still becomes eligible because uncertainty
        // began when the heartbeat deadline expired, not when this request first observed it.
        var reclaim = backend.ReclaimUncertainReservation("world-1", "steam-b");

        Assert.Equal(ReclaimReservationStatus.Reclaimed, reclaim.Status);
        Assert.Equal(reservation.Generation, reclaim.InvalidatedGeneration);
        Assert.Null(backend.GetReservation("world-1", "steam-a"));
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan amount)
        {
            UtcNow += amount;
        }
    }
}
