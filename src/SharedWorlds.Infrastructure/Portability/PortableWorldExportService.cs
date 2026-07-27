using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Portability;

namespace SharedWorlds.Infrastructure.Portability;

/// <summary>
/// Exports only the current immutable, already-committed World state. This service never captures a
/// running game and never infers public-export safety from private capture support.
/// </summary>
public sealed class PortableWorldExportService
{
    private readonly IWorldStorage _storage;

    public PortableWorldExportService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<PortableWorldManifest> ExportCurrentAsync(
        World world,
        IGameAdapter adapter,
        Stream destination,
        PortableWorldPresentation? presentation = null,
        PortableWorldOrigin? startedFrom = null,
        PortableWorldArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException(
                "Portable World export destination must be writable and seekable.",
                nameof(destination));
        }

        if (destination.Length != 0 || destination.Position != 0)
        {
            throw new ArgumentException(
                "Portable World export destination must be empty.",
                nameof(destination));
        }

        if (!string.Equals(world.GameAdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"World '{world.Id}' belongs to adapter '{world.GameAdapterId}', not '{adapter.Id}'.");
        }

        if (adapter is not IPublicWorldExportAdapter publicExportAdapter)
        {
            throw new NotSupportedException(
                $"{adapter.DisplayName} has not qualified its captured World state for public export.");
        }

        if (world.CurrentEnvironmentRevisionId is not { } environmentRevisionId)
        {
            throw new InvalidOperationException(
                $"World '{world.Id}' has no committed environment revision to export.");
        }

        if (world.CurrentStateRevisionId is not { } stateRevisionId)
        {
            throw new InvalidOperationException(
                $"World '{world.Id}' has no committed state revision to export.");
        }

        try
        {
            var environmentRevision = await _storage.LoadEnvironmentRevisionAsync(
                world.Id,
                environmentRevisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"World '{world.Id}' points to missing environment revision '{environmentRevisionId}'.");

            var stateRevision = await _storage.LoadStateRevisionAsync(
                world.Id,
                stateRevisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"World '{world.Id}' points to missing state revision '{stateRevisionId}'.");

            if (!string.Equals(stateRevision.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"World '{world.Id}' state revision '{stateRevision.Id}' belongs to adapter '{stateRevision.AdapterId}'.");
            }

            if (!string.Equals(
                    environmentRevision.Manifest.AdapterId,
                    world.GameAdapterId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"World '{world.Id}' environment revision '{environmentRevision.Id}' belongs to adapter '{environmentRevision.Manifest.AdapterId}'.");
            }

            var readiness = await publicExportAdapter.CheckPublicWorldExportAsync(
                environmentRevision.Manifest,
                cancellationToken);
            if (!readiness.IsSupported)
            {
                throw new NotSupportedException(
                    readiness.Reason ??
                    $"{adapter.DisplayName} did not authorize this World environment for public export.");
            }

            await using var statePackage = await _storage.OpenRevisionAsync(
                world.Id,
                stateRevisionId,
                cancellationToken);

            var description = new PortableWorldDescription(
                GameAdapterId: world.GameAdapterId,
                WorldName: world.Name,
                SnapshotId: stateRevision.Id.ToString(),
                CreatedAt: stateRevision.CreatedAt,
                Environment: environmentRevision.Manifest,
                Presentation: presentation,
                StartedFrom: startedFrom);

            var manifest = await PortableWorldArchive.WriteAsync(
                destination,
                description,
                statePackage,
                limits,
                cancellationToken);

            destination.Position = 0;
            return manifest;
        }
        catch
        {
            destination.SetLength(0);
            destination.Position = 0;
            throw;
        }
    }
}
