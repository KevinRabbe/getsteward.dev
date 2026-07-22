using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    /// <summary>
    /// The product contract uses a real dedicated Factorio server for hosted Worlds. This explicit
    /// interface implementation keeps every Core/Desktop IGameAdapter path on the readiness-proven
    /// server flow while the older concrete convenience method remains source-compatible.
    /// </summary>
    Task<GameSessionHandle> IGameAdapter.LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
        => LaunchTrackedAsync(
            world,
            FactorioDedicatedServer.LaunchReadyAsync,
            cancellationToken);
}
