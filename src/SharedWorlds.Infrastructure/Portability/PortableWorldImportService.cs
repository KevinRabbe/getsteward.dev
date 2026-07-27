using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Portability;

namespace SharedWorlds.Infrastructure.Portability;

/// <summary>
/// Materializes one validated portable snapshot as a new, independent Safe World World.
/// The imported World receives fresh World/revision identities and never becomes linked to,
/// synchronized with, or writable back into the source snapshot.
/// </summary>
public sealed class PortableWorldImportService
{
    private readonly IWorldStorage _storage;

    public PortableWorldImportService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<PortableWorldImportResult> ImportAsync(
        Stream source,
        IGameAdapter adapter,
        Stream stateStaging,
        PortableWorldArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(stateStaging);
        cancellationToken.ThrowIfCancellationRequested();

        PortableWorldManifest? manifest = null;
        try
        {
            manifest = await PortableWorldArchive.ValidateAndExtractStateAsync(
                source,
                stateStaging,
                limits,
                cancellationToken);

            if (!string.Equals(manifest.GameAdapterId, adapter.Id, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"This portable World belongs to adapter '{manifest.GameAdapterId}', not '{adapter.Id}'.");
            }

            if (!string.Equals(manifest.Environment.AdapterId, adapter.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Portable World environment adapter does not match the selected game adapter.");
            }

            var importedAt = DateTimeOffset.UtcNow;
            var worldId = WorldId.New();
            var environmentRevisionId = RevisionId.New();
            var stateRevisionId = RevisionId.New();

            var environmentRevision = new EnvironmentRevision(
                environmentRevisionId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: importedAt,
                CreatedBy: null,
                Manifest: manifest.Environment);

            var stateRevision = new StateRevision(
                stateRevisionId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: importedAt,
                CreatedBy: null,
                AdapterId: manifest.GameAdapterId,
                StatePackageId: stateRevisionId.ToString());

            var world = new World(
                worldId,
                manifest.WorldName,
                manifest.GameAdapterId,
                Members: [],
                CurrentEnvironmentRevisionId: environmentRevisionId,
                CurrentStateRevisionId: stateRevisionId)
            {
                StartedFrom = new WorldProvenance(
                    SnapshotId: manifest.SnapshotId,
                    WorldName: manifest.WorldName,
                    SnapshotCreatedAt: manifest.CreatedAt,
                    Creator: manifest.Presentation?.Creator,
                    Description: manifest.Presentation?.Description,
                    SourceUrl: manifest.Presentation?.SourceUrl)
            };

            // Publish the discoverable World metadata last. Environment/state writes are immutable and
            // individually atomic; if an earlier write fails, no half-imported World enters ListWorlds.
            await _storage.StoreEnvironmentRevisionAsync(environmentRevision, cancellationToken);
            stateStaging.Position = 0;
            await _storage.StoreRevisionAsync(stateRevision, stateStaging, cancellationToken);
            await _storage.SaveWorldAsync(world, cancellationToken);

            return new PortableWorldImportResult(world, manifest);
        }
        finally
        {
            if (stateStaging.CanSeek && stateStaging.CanWrite)
            {
                stateStaging.SetLength(0);
                stateStaging.Position = 0;
            }
        }
    }
}

public sealed record PortableWorldImportResult(
    World World,
    PortableWorldManifest SourceManifest);
