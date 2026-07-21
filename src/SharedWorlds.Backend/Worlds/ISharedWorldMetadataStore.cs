using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

/// <summary>
/// Persistence boundary for BE-2 World metadata/access. Implementations must make
/// TryCreateWorldWithManagerAsync atomic: the World and its first active Access Manager membership
/// either both become durable or neither does.
/// </summary>
public interface ISharedWorldMetadataStore
{
    Task<bool> TryCreateWorldWithManagerAsync(
        SharedWorldMetadata world,
        SharedWorldMember accessManager,
        CancellationToken cancellationToken = default);

    Task<SharedWorldMetadata?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<SharedWorldMember?> LoadMemberAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);
}
