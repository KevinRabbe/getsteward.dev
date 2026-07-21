using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardApiEndpointsTests
{
    [Fact]
    public async Task WorldListRequiresValidBearerCredential()
    {
        await using var harness = await ApiTestHarness.CreateAsync();

        using var response = await harness.Client.GetAsync("/api/v1/worlds");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AuthenticationRequired", body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task AuthenticatedTransferFlowUsesStableCodesAndDoesNotExposeStorageInternals()
    {
        await using var harness = await ApiTestHarness.CreateAsync();
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            harness.Tokens.AccessToken);

        using (var worldsResponse = await harness.Client.GetAsync("/api/v1/worlds"))
        {
            Assert.Equal(HttpStatusCode.OK, worldsResponse.StatusCode);
            using var worlds = JsonDocument.Parse(await worldsResponse.Content.ReadAsStringAsync());
            Assert.Equal("WorldsListed", worlds.RootElement.GetProperty("code").GetString());
            var world = Assert.Single(worlds.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(harness.WorldId.Value, world.GetProperty("worldId").GetGuid());
        }

        var revisionId = Guid.NewGuid();
        var sha256 = new string('A', 64);
        using var beginResponse = await harness.Client.PostAsJsonAsync(
            $"/api/v1/worlds/{harness.WorldId.Value:D}/transfers",
            new
            {
                revisionId,
                kind = "State",
                expectedByteSize = 1024L,
                expectedSha256 = sha256,
                requiredEnvironmentRevisionId = (Guid?)null
            });

        Assert.Equal(HttpStatusCode.Created, beginResponse.StatusCode);
        var beginText = await beginResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("providerUploadId", beginText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", beginText, StringComparison.OrdinalIgnoreCase);
        using var begin = JsonDocument.Parse(beginText);
        Assert.Equal("TransferStarted", begin.RootElement.GetProperty("code").GetString());
        var transferId = begin.RootElement
            .GetProperty("data")
            .GetProperty("transferId")
            .GetGuid();

        using (var partResponse = await harness.Client.PostAsync(
                   $"/api/v1/transfers/{transferId:D}/parts/1/authorization",
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, partResponse.StatusCode);
            using var part = JsonDocument.Parse(await partResponse.Content.ReadAsStringAsync());
            Assert.Equal("PartAuthorized", part.RootElement.GetProperty("code").GetString());
            var authorization = part.RootElement.GetProperty("data");
            Assert.Equal("PUT", authorization.GetProperty("method").GetString());
            Assert.StartsWith(
                "https://storage.test/upload/",
                authorization.GetProperty("uri").GetString(),
                StringComparison.Ordinal);
        }

        using (var finalizeResponse = await harness.Client.PostAsync(
                   $"/api/v1/transfers/{transferId:D}/finalize",
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, finalizeResponse.StatusCode);
            using var finalized = JsonDocument.Parse(await finalizeResponse.Content.ReadAsStringAsync());
            Assert.Equal("TransferFinalized", finalized.RootElement.GetProperty("code").GetString());
            Assert.Equal(sha256, finalized.RootElement.GetProperty("data").GetProperty("sha256").GetString());
        }

        using (var downloadResponse = await harness.Client.PostAsync(
                   $"/api/v1/worlds/{harness.WorldId.Value:D}/revisions/{revisionId:D}/state/download-authorization",
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
            using var download = JsonDocument.Parse(await downloadResponse.Content.ReadAsStringAsync());
            Assert.Equal("DownloadAuthorized", download.RootElement.GetProperty("code").GetString());
            Assert.Equal(sha256, download.RootElement.GetProperty("data").GetProperty("expectedSha256").GetString());
        }

        using (var currentResponse = await harness.Client.GetAsync(
                   $"/api/v1/worlds/{harness.WorldId.Value:D}/current-revision"))
        {
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            using var current = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
            Assert.Equal("CurrentRevisionFound", current.RootElement.GetProperty("code").GetString());
            var currentWorld = current.RootElement.GetProperty("data").GetProperty("world");
            Assert.Equal(
                harness.InitialStateRevisionId.Value,
                currentWorld.GetProperty("currentStateRevisionId").GetGuid());
        }
    }

    [Fact]
    public async Task InvalidPackageKindReturnsMachineReadableValidationOutcome()
    {
        await using var harness = await ApiTestHarness.CreateAsync();
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            harness.Tokens.AccessToken);

        using var response = await harness.Client.PostAsJsonAsync(
            $"/api/v1/worlds/{harness.WorldId.Value:D}/transfers",
            new
            {
                revisionId = Guid.NewGuid(),
                kind = "Unknown",
                expectedByteSize = 1024L,
                expectedSha256 = new string('B', 64),
                requiredEnvironmentRevisionId = (Guid?)null
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidPackageKind", body.RootElement.GetProperty("code").GetString());
    }
}
