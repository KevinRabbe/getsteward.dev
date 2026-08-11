using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

/// <summary>
/// Test wrappers sometimes hold LocalWorldStorage concretely while forwarding the optional IWorldStorage
/// capabilities. C# default interface implementations are dispatchable through the interface, not as
/// concrete-class members, so keep that language detail in one test-only forwarding helper.
/// </summary>
internal static class WorldStorageDefaultInterfaceTestExtensions
{
    public static Task<long?> GetRevisionPayloadSizeAsync(
        this LocalWorldStorage storage,
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return ((IWorldStorage)storage).GetRevisionPayloadSizeAsync(
            worldId,
            revisionId,
            cancellationToken);
    }
}
