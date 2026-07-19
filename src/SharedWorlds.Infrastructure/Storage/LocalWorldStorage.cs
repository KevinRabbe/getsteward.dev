using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

public sealed class LocalWorldStorage : IWorldStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootPath;

    public LocalWorldStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync(GetWorldMetadataPath(world.Id), world, cancellationToken);

    public async Task<World?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var path = GetWorldMetadataPath(worldId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await JsonSerializer.DeserializeAsync<World>(stream, JsonOptions, cancellationToken);
    }

    public Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync(
            GetEnvironmentRevisionPath(revision.WorldId, revision.Id),
            revision,
            cancellationToken);

    public async Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var path = GetEnvironmentRevisionPath(worldId, revisionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EnvironmentRevision>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    public async Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var directory = GetStateRevisionDirectory(revision.WorldId, revision.Id);
        Directory.CreateDirectory(directory);

        await WriteJsonAtomicAsync(
            Path.Combine(directory, "revision.json"),
            revision,
            cancellationToken);

        var destination = Path.Combine(directory, "payload.bin");
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 128,
                             useAsync: true))
            {
                await package.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<StateRevision?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetStateRevisionDirectory(worldId, revisionId), "revision.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StateRevision>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    public Task<Stream> OpenRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.Combine(GetStateRevisionDirectory(worldId, revisionId), "payload.bin");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"State revision '{revisionId}' for World '{worldId}' does not exist.",
                path);
        }

        Stream stream = OpenRead(path);
        return Task.FromResult(stream);
    }

    private async Task WriteJsonAtomicAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Cannot resolve directory for '{destination}'.");

        Directory.CreateDirectory(directory);
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private string GetWorldDirectory(WorldId worldId)
        => Path.Combine(_rootPath, "worlds", worldId.ToString());

    private string GetWorldMetadataPath(WorldId worldId)
        => Path.Combine(GetWorldDirectory(worldId), "world.json");

    private string GetEnvironmentRevisionPath(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            GetWorldDirectory(worldId),
            "environments",
            $"{revisionId}.json");

    private string GetStateRevisionDirectory(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            GetWorldDirectory(worldId),
            "states",
            revisionId.ToString());

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

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
