using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldAccessClientTests
{
    [Fact]
    public async Task ListsFlatMembersAndPendingInvitations()
    {
        var worldId = WorldId.New();
        var invitationId = Guid.NewGuid();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/members", StringComparison.Ordinal) => Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "code": "WorldMembersFound",
                  "data": [
                    {
                      "worldId": "{{worldId.Value:D}}",
                      "identity": { "provider": "steam", "externalId": "1" },
                      "status": "Active",
                      "addedAt": "2026-07-22T18:00:00Z"
                    },
                    {
                      "worldId": "{{worldId.Value:D}}",
                      "identity": { "provider": "steam", "externalId": "2" },
                      "status": "RevocationPending",
                      "addedAt": "2026-07-22T18:01:00Z"
                    }
                  ],
                  "retryable": false
                }
                """),
            "/api/v1/invitations" => Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "code": "PendingInvitationsFound",
                  "data": [
                    {
                      "invitationId": "{{invitationId:D}}",
                      "worldId": "{{worldId.Value:D}}",
                      "invitedIdentity": { "provider": "steam", "externalId": "2" },
                      "invitedBy": { "provider": "steam", "externalId": "1" },
                      "status": "Pending",
                      "createdAt": "2026-07-22T18:02:00Z",
                      "respondedAt": null
                    }
                  ],
                  "retryable": false
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });
        using var http = Client(handler);
        var client = new StewardWorldAccessClient(http, new StaticTokenProvider());

        var members = await client.ListMembersAsync(worldId);
        var invitations = await client.ListPendingInvitationsAsync();

        Assert.Equal(2, members.Count);
        Assert.Equal(RemoteWorldMemberStatus.Active, members[0].Status);
        Assert.Equal(RemoteWorldMemberStatus.RevocationPending, members[1].Status);
        var invitation = Assert.Single(invitations);
        Assert.Equal(invitationId, invitation.InvitationId);
        Assert.Equal(RemoteInvitationStatus.Pending, invitation.Status);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer access-token", request.Authorization));
    }

    [Fact]
    public async Task InvitePostsTargetIdentityAndReturnsInvitation()
    {
        var worldId = WorldId.New();
        var invitationId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Created,
            $$"""
            {
              "code": "InvitationCreated",
              "data": {
                "invitationId": "{{invitationId:D}}",
                "worldId": "{{worldId.Value:D}}",
                "invitedIdentity": { "provider": "steam", "externalId": "2" },
                "invitedBy": { "provider": "steam", "externalId": "1" },
                "status": "Pending",
                "createdAt": "2026-07-22T18:00:00Z",
                "respondedAt": null
              },
              "retryable": false
            }
            """));
        using var http = Client(handler);
        var client = new StewardWorldAccessClient(http, new StaticTokenProvider());

        var invitation = await client.InviteAsync(worldId, "steam", "2");

        Assert.Equal(invitationId, invitation.InvitationId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith($"/api/v1/worlds/{worldId.Value:D}/invitations", request.Uri, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"steam\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"externalId\":\"2\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespondRevokeTransferAndLeaveUseStableActionCodes()
    {
        var worldId = WorldId.New();
        var invitationId = Guid.NewGuid();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/accept", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "code": "InvitationAccepted", "retryable": false }"""),
            var path when path.EndsWith("/decline", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "code": "InvitationDeclined", "retryable": false }"""),
            var path when path.EndsWith("/members/revoke", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "code": "MemberRevocationPending", "retryable": false }"""),
            var path when path.EndsWith("/access-manager", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "code": "AccessManagerTransferred", "retryable": false }"""),
            var path when path.EndsWith("/leave", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "code": "WorldLeft", "retryable": false }"""),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });
        using var http = Client(handler);
        var client = new StewardWorldAccessClient(http, new StaticTokenProvider());

        await client.AcceptInvitationAsync(invitationId);
        await client.DeclineInvitationAsync(invitationId);
        var revoke = await client.RevokeMemberAsync(worldId, "steam", "2");
        await client.TransferAccessManagerAsync(worldId, "steam", "2");
        await client.LeaveWorldAsync(worldId);

        Assert.Equal(RemoteMemberRevocationStatus.RevocationPending, revoke);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task DomainConflictSurfacesExistingRemoteApiExceptionContract()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Conflict,
            """{ "code": "MustTransferAccessManager", "retryable": false }"""));
        using var http = Client(handler);
        var client = new StewardWorldAccessClient(http, new StaticTokenProvider());

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.LeaveWorldAsync(WorldId.New()));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("MustTransferAccessManager", exception.Code);
        Assert.False(exception.Retryable);
    }

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);
}
