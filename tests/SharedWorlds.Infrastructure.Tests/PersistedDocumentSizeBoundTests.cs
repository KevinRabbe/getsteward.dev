using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PersistedDocumentSizeBoundTests : IDisposable
{
    private const long OversizedDocumentBytes = (16L * 1024 * 1024) + 1;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-document-size-{Guid.NewGuid():N}");

    [Fact]
    public async Task OversizedWorldMetadataIsRejectedBeforeJsonParsing()
    {
        var worldId = WorldId.New();
        var path = Path.Combine(_root, "worlds", worldId.ToString(), "world.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            stream.SetLength(OversizedDocumentBytes);
        }

        var storage = new LocalWorldStorage(_root);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.LoadWorldAsync(worldId));

        Assert.Contains("exceeds Steward's", exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety ceiling", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
