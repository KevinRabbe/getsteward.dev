using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Storage;

/// <summary>
/// Integrity-protected local write-ahead journal for private World location publication. One file is
/// owned by one World ID, so a crash can leave at most the previous complete entry or the next complete
/// entry; temporary files are never treated as journal authority.
/// </summary>
public sealed class LocalOwnedWorldLocationPublicationJournal :
    IOwnedWorldLocationPublicationJournal
{
    private readonly string _directory;

    public LocalOwnedWorldLocationPublicationJournal(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _directory = Path.Combine(
            Path.GetFullPath(rootPath),
            "owned-world-location-publication");
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(
        OwnedWorldLocationPublicationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();

        var destination = GetPath(state.WorldId);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                await PersistedDocumentCodec.WriteAsync(
                    stream,
                    StorageDocumentSchemas.OwnedWorldLocationPublication,
                    state,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<OwnedWorldLocationPublicationState?> LoadAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(worldId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadAsync(path, worldId, cancellationToken);
    }

    public async Task<IReadOnlyList<OwnedWorldLocationPublicationState>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var states = new List<OwnedWorldLocationPublicationState>();
        foreach (var path in Directory
                     .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storageKey = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(storageKey, "N", out var worldGuid))
            {
                throw new InvalidDataException(
                    $"Owned-World location journal entry '{storageKey}' is not a valid World ID key.");
            }

            states.Add(await ReadAsync(
                path,
                new WorldId(worldGuid),
                cancellationToken));
        }

        return states;
    }

    public Task RemoveAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(worldId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private async Task<OwnedWorldLocationPublicationState> ReadAsync(
        string path,
        WorldId storageWorldId,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var state = await PersistedDocumentCodec.ReadAsync(
            stream,
            StorageDocumentSchemas.OwnedWorldLocationPublication,
            cancellationToken);
        state.Validate();
        if (state.WorldId != storageWorldId)
        {
            throw new InvalidDataException(
                $"Owned-World location publication state '{state.WorldId}' is stored under mismatched World key '{storageWorldId}'.");
        }

        return state;
    }

    private string GetPath(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID must not be empty.", nameof(worldId));
        }

        return Path.Combine(_directory, $"{worldId}.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
