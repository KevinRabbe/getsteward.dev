using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

public sealed class LocalWorkspaceRecoveryStore : IWorkspaceRecoveryStore
{
    private readonly string _directory;

    public LocalWorkspaceRecoveryStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _directory = Path.Combine(Path.GetFullPath(rootPath), "recovery");
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(
        WorkspaceRecoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var destination = GetPath(record.Id);
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
                    StorageDocumentSchemas.WorkspaceRecovery,
                    record,
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

    public Task RemoveAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(workspaceId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var records = new List<WorkspaceRecoveryRecord>();

        foreach (var path in Directory
                     .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);

            var record = await PersistedDocumentCodec.ReadAsync(
                stream,
                StorageDocumentSchemas.WorkspaceRecovery,
                cancellationToken);
            var storageKey = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(storageKey, record.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Persisted recovery record '{record.Id}' is stored under mismatched workspace key '{storageKey}'.");
            }

            records.Add(record);
        }

        return records;
    }

    private string GetPath(WorkspaceId workspaceId)
        => Path.Combine(_directory, $"{workspaceId}.json");

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
