using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Portability;
using SharedWorlds.Infrastructure.Portability;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PortableWorldArchiveTests
{
    [Fact]
    public async Task WriteThenValidateAndExtractRoundTripsOpaqueStateAndMetadata()
    {
        var stateBytes = new byte[] { 0, 1, 2, 3, 4, 250, 251, 252, 253, 254, 255 };
        await using var state = new MemoryStream(stateBytes, writable: false);
        await using var artifact = new MemoryStream();

        var description = CreateDescription();
        var written = await PortableWorldArchive.WriteAsync(artifact, description, state);

        Assert.Equal(PortableWorldArchive.CurrentFormatVersion, written.FormatVersion);
        Assert.Equal(PortableWorldArchive.StateEntryName, written.Payload.EntryName);
        Assert.Equal(stateBytes.Length, written.Payload.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(stateBytes)).ToLowerInvariant(),
            written.Payload.Sha256);

        artifact.Position = 0;
        await using var extracted = new MemoryStream();
        var read = await PortableWorldArchive.ValidateAndExtractStateAsync(artifact, extracted);

        Assert.Equal(written, read);
        Assert.Equal(stateBytes, extracted.ToArray());
        Assert.Equal(0, extracted.Position);
        Assert.Equal("Creator", read.Presentation?.Creator);
        Assert.Equal("source-snapshot", read.StartedFrom?.SnapshotId);
    }

    [Fact]
    public async Task ValidateRejectsUnexpectedArchiveEntries()
    {
        var manifest = CreateManifest(new byte[] { 1, 2, 3 });
        await using var artifact = await CreateArchiveAsync(
            manifest,
            new byte[] { 1, 2, 3 },
            extraEntryName: "extra.txt");
        await using var staging = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging));
        Assert.Equal(0, staging.Length);
    }

    [Fact]
    public async Task ValidateRejectsTraversalInsteadOfExtractingIt()
    {
        var stateBytes = new byte[] { 1, 2, 3 };
        var manifest = CreateManifest(stateBytes);
        await using var artifact = new MemoryStream();
        using (var archive = new ZipArchive(artifact, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonEntryAsync(archive, PortableWorldArchive.ManifestEntryName, manifest);
            var stateEntry = archive.CreateEntry("../state.bin", CompressionLevel.NoCompression);
            await using var output = stateEntry.Open();
            await output.WriteAsync(stateBytes);
        }

        artifact.Position = 0;
        await using var staging = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging));
        Assert.Equal(0, staging.Length);
    }

    [Fact]
    public async Task ValidateRejectsHashMismatchAndClearsStaging()
    {
        var stateBytes = new byte[] { 10, 20, 30, 40 };
        var manifest = CreateManifest(stateBytes) with
        {
            Payload = new PortableWorldPayload(
                PortableWorldArchive.StateEntryName,
                stateBytes.Length,
                new string('0', 64))
        };
        await using var artifact = await CreateArchiveAsync(manifest, stateBytes);
        await using var staging = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging));

        Assert.Equal(0, staging.Length);
        Assert.Equal(0, staging.Position);
    }

    [Fact]
    public async Task WriteRejectsStateBeyondConfiguredBound()
    {
        await using var state = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false);
        await using var artifact = new MemoryStream();
        var limits = new PortableWorldArchiveLimits(
            MaximumStateBytes: 3,
            MaximumManifestBytes: 4096);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.WriteAsync(artifact, CreateDescription(), state, limits));
    }

    [Fact]
    public async Task ValidateRejectsManifestBeyondConfiguredBound()
    {
        var stateBytes = new byte[] { 1 };
        var manifest = CreateManifest(stateBytes) with
        {
            Presentation = new PortableWorldPresentation(Description: new string('x', 1024))
        };
        await using var artifact = await CreateArchiveAsync(manifest, stateBytes);
        await using var staging = new MemoryStream();
        var limits = new PortableWorldArchiveLimits(
            MaximumStateBytes: 1024,
            MaximumManifestBytes: 128);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging, limits));
        Assert.Equal(0, staging.Length);
    }

    [Fact]
    public async Task WriteRejectsNonHttpCreatorSource()
    {
        var description = CreateDescription() with
        {
            Presentation = new PortableWorldPresentation(
                Creator: "Creator",
                SourceUrl: "file:///C:/private.txt")
        };
        await using var state = new MemoryStream(new byte[] { 1 }, writable: false);
        await using var artifact = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.WriteAsync(artifact, description, state));
    }

    [Fact]
    public async Task ValidateRejectsUnknownFormatVersion()
    {
        var stateBytes = new byte[] { 1, 2 };
        var manifest = CreateManifest(stateBytes) with { FormatVersion = 999 };
        await using var artifact = await CreateArchiveAsync(manifest, stateBytes);
        await using var staging = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging));
        Assert.Equal(0, staging.Length);
    }

    private static PortableWorldDescription CreateDescription()
        => new(
            GameAdapterId: "factorio",
            WorldName: "500h Megabase",
            SnapshotId: "snapshot-001",
            CreatedAt: new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
            Environment: CreateEnvironment(),
            Presentation: new PortableWorldPresentation(
                Creator: "Creator",
                Description: "A finished megabase.",
                SourceUrl: "https://example.test/video"),
            StartedFrom: new PortableWorldOrigin(
                SnapshotId: "source-snapshot",
                WorldName: "Original Megabase",
                Creator: "Original Creator"));

    private static PortableWorldManifest CreateManifest(byte[] stateBytes)
    {
        var description = CreateDescription();
        return new PortableWorldManifest(
            FormatVersion: PortableWorldArchive.CurrentFormatVersion,
            GameAdapterId: description.GameAdapterId,
            WorldName: description.WorldName,
            SnapshotId: description.SnapshotId,
            CreatedAt: description.CreatedAt,
            Environment: description.Environment,
            Payload: new PortableWorldPayload(
                EntryName: PortableWorldArchive.StateEntryName,
                Length: stateBytes.Length,
                Sha256: Convert.ToHexString(SHA256.HashData(stateBytes)).ToLowerInvariant()),
            Presentation: description.Presentation,
            StartedFrom: description.StartedFrom);
    }

    private static EnvironmentManifest CreateEnvironment()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.72",
            Components:
            [
                new EnvironmentComponent(
                    Kind: "mod",
                    Id: "base",
                    Version: "2.0.72",
                    Source: "steam")
            ],
            Configuration: new Dictionary<string, string>
            {
                ["quality"] = "vanilla"
            });

    private static async Task<MemoryStream> CreateArchiveAsync(
        PortableWorldManifest manifest,
        byte[] stateBytes,
        string? extraEntryName = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonEntryAsync(archive, PortableWorldArchive.ManifestEntryName, manifest);

            var stateEntry = archive.CreateEntry(
                PortableWorldArchive.StateEntryName,
                CompressionLevel.NoCompression);
            await using (var output = stateEntry.Open())
            {
                await output.WriteAsync(stateBytes);
            }

            if (extraEntryName is not null)
            {
                var extra = archive.CreateEntry(extraEntryName, CompressionLevel.NoCompression);
                await using var output = extra.Open();
                await output.WriteAsync(new byte[] { 9 });
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive,
        string name,
        PortableWorldManifest manifest)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(
            output,
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
