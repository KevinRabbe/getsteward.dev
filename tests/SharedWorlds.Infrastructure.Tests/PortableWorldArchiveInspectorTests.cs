using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Portability;
using SharedWorlds.Infrastructure.Portability;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PortableWorldArchiveInspectorTests
{
    [Fact]
    public async Task InspectReturnsRoutingMetadataWithoutClaimingStateIntegrity()
    {
        var state = "opaque-state-that-is-not-validated-by-preflight"u8.ToArray();
        var manifest = CreateManifest(state) with
        {
            Payload = new PortableWorldPayload(
                PortableWorldArchive.StateEntryName,
                state.Length,
                new string('0', 64))
        };
        await using var artifact = await CreateArchiveAsync(manifest, state);

        var preflight = await PortableWorldArchiveInspector.InspectAsync(artifact);

        Assert.Equal("factorio", preflight.GameAdapterId);
        Assert.Equal("500h Megabase", preflight.WorldName);
        Assert.Equal("snapshot-001", preflight.SnapshotId);
        Assert.Equal("2.0.72", preflight.Environment.GameVersion);
        Assert.Equal(state.Length, preflight.StateBytes);
        Assert.Equal("Creator", preflight.Presentation?.Creator);
        Assert.Equal(0, artifact.Position);

        await using var staging = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchive.ValidateAndExtractStateAsync(artifact, staging));
        Assert.Equal(0, staging.Length);
    }

    [Fact]
    public async Task InspectRejectsUnknownFormatBeforeAdapterRouting()
    {
        var state = new byte[] { 1, 2, 3 };
        var manifest = CreateManifest(state) with { FormatVersion = 999 };
        await using var artifact = await CreateArchiveAsync(manifest, state);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchiveInspector.InspectAsync(artifact));
        Assert.Equal(0, artifact.Position);
    }

    [Fact]
    public async Task InspectRejectsAdapterEnvironmentMismatch()
    {
        var state = new byte[] { 1, 2, 3 };
        var manifest = CreateManifest(state) with
        {
            Environment = CreateEnvironment() with { AdapterId = "other-game" }
        };
        await using var artifact = await CreateArchiveAsync(manifest, state);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchiveInspector.InspectAsync(artifact));
        Assert.Equal(0, artifact.Position);
    }

    [Fact]
    public async Task InspectRejectsUnexpectedArchiveEntry()
    {
        var state = new byte[] { 1, 2, 3 };
        var manifest = CreateManifest(state);
        await using var artifact = await CreateArchiveAsync(
            manifest,
            state,
            extraEntryName: "surprise.txt");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchiveInspector.InspectAsync(artifact));
        Assert.Equal(0, artifact.Position);
    }

    [Fact]
    public async Task InspectRejectsStateBeyondConfiguredBoundFromArchiveMetadata()
    {
        var state = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifest(state);
        await using var artifact = await CreateArchiveAsync(manifest, state);
        var limits = new PortableWorldArchiveLimits(
            MaximumStateBytes: 3,
            MaximumManifestBytes: 4096);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortableWorldArchiveInspector.InspectAsync(artifact, limits));
        Assert.Equal(0, artifact.Position);
    }

    private static PortableWorldManifest CreateManifest(byte[] state)
        => new(
            FormatVersion: PortableWorldArchive.CurrentFormatVersion,
            GameAdapterId: "factorio",
            WorldName: "500h Megabase",
            SnapshotId: "snapshot-001",
            CreatedAt: new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
            Environment: CreateEnvironment(),
            Payload: new PortableWorldPayload(
                PortableWorldArchive.StateEntryName,
                state.Length,
                Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant()),
            Presentation: new PortableWorldPresentation(
                Creator: "Creator",
                Description: "A finished megabase.",
                SourceUrl: "https://example.test/world"));

    private static EnvironmentManifest CreateEnvironment()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.72",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private static async Task<MemoryStream> CreateArchiveAsync(
        PortableWorldManifest manifest,
        byte[] state,
        string? extraEntryName = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(
                PortableWorldArchive.ManifestEntryName,
                CompressionLevel.Optimal);
            await using (var output = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    output,
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }

            var stateEntry = archive.CreateEntry(
                PortableWorldArchive.StateEntryName,
                CompressionLevel.NoCompression);
            await using (var output = stateEntry.Open())
            {
                await output.WriteAsync(state);
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
}
