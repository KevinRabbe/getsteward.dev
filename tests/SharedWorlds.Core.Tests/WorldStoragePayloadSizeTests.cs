using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Tests;

public sealed class WorldStoragePayloadSizeTests
{
    [Fact]
    public async Task DefaultSizeCapabilityUsesLengthWithoutReadingOrSeeking()
    {
        var concrete = new LengthOnlyStorage(payloadLength: 987_654_321);
        IWorldStorage storage = concrete;

        var size = await storage.GetRevisionPayloadSizeAsync(WorldId.New(), RevisionId.New());

        Assert.Equal(987_654_321, size);
        var opened = Assert.IsType<LengthOnlyStream>(concrete.Stream);
        Assert.Equal(0, opened.ReadCalls);
    }

    [Fact]
    public async Task DefaultSizeCapabilityReturnsNullWhenPayloadIsAbsent()
    {
        var concrete = new LengthOnlyStorage(payloadLength: null);
        IWorldStorage storage = concrete;

        var size = await storage.GetRevisionPayloadSizeAsync(WorldId.New(), RevisionId.New());

        Assert.Null(size);
        Assert.Null(concrete.Stream);
    }

    private sealed class LengthOnlyStorage(long? payloadLength) : IWorldStorage
    {
        public LengthOnlyStream? Stream { get; private set; }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(null);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(null);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            Stream = new LengthOnlyStream(payloadLength ?? throw new FileNotFoundException());
            return Task.FromResult<Stream>(Stream);
        }

        public Task<bool> IsRevisionPayloadAvailableAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(payloadLength is not null);
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
