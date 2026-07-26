using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Persists a game-native newly created World into the same immutable Steward revision model used by
/// imported Worlds. Creation happens before any writable session exists, so no host reservation or
/// session-recovery state is introduced here.
/// </summary>
public sealed class WorldCreationService
{
    private readonly IWorldStorage _storage;

    public WorldCreationService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<World> CreateAsync(
        IGameAdapter adapter,
        GameInstallation installation,
        WorldCreationRequest request,
        UserIdentity owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.NativeWorldCreation))
        {
            throw new NotSupportedException(
                $"{adapter.DisplayName} does not expose a validated native World creation path.");
        }

        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        NativeWorldCreationResult? created = null;

        try
        {
            created = await adapter.CreateWorldAsync(
                installation,
                request,
                cancellationToken);

            if (!string.Equals(created.Environment.AdapterId, adapter.Id, StringComparison.Ordinal))
            {
                throw new AdapterMismatchException(
                    adapter.Id,
                    created.Environment.AdapterId,
                    "native World creation environment");
            }

            var environmentRevision = new EnvironmentRevision(
                Id: environmentId,
                WorldId: worldId,
                ParentRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: owner,
                Manifest: created.Environment);

            var stateRevision = new StateRevision(
                Id: stateId,
                WorldId: worldId,
                ParentRevisionId: null,
                CreatedAt: created.State.CapturedAt,
                CreatedBy: owner,
                AdapterId: adapter.Id,
                StatePackageId: created.State.Package.Id);

            var world = new World(
                Id: worldId,
                Name: request.DisplayName,
                GameAdapterId: adapter.Id,
                Members: [owner],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: stateId)
            {
                SharingMode = WorldSharingMode.LocalOnly
            };

            await _storage.StoreEnvironmentRevisionAsync(environmentRevision, cancellationToken);
            await using (var package = File.OpenRead(created.State.Package.Path))
            {
                await _storage.StoreRevisionAsync(stateRevision, package, cancellationToken);
            }

            // Creation follows the same commit boundary as import: the canonical World head is
            // written only after both immutable initial revisions exist durably.
            await _storage.SaveWorldAsync(world, cancellationToken);
            return world;
        }
        finally
        {
            CleanupCapturedState(created?.State);
        }
    }

    private static void CleanupCapturedState(CapturedState? captured)
    {
        if (captured?.DeletePackageAfterStore != true)
        {
            return;
        }

        try
        {
            if (File.Exists(captured.Package.Path))
            {
                File.Delete(captured.Package.Path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only. The immutable persisted revision is already authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only. The immutable persisted revision is already authoritative.
        }
    }
}
