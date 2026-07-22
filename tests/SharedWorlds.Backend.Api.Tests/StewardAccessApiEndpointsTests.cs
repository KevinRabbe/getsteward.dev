using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardAccessApiEndpointsTests
{
    [Fact]
    public async Task InvitationAcceptanceCreatesActiveFlatMembership()
    {
        await using var harness = await AccessHarness.CreateAsync();
        var worldId = await harness.CreateWorldAsync();
        var invitationId = await harness.InviteMemberAsync(worldId);

        harness.UseMember();
        using (var invitations = await harness.Client.GetAsync("/api/v1/invitations"))
        {
            Assert.Equal(HttpStatusCode.OK, invitations.StatusCode);
            using var body = JsonDocument.Parse(await invitations.Content.ReadAsStringAsync());
            var invitation = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(invitationId.Value, invitation.GetProperty("invitationId").GetGuid());
        }

        using (var accept = await harness.Client.PostAsync(
                   $"/api/v1/invitations/{invitationId.Value:D}/accept",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
            Assert.Equal("InvitationAccepted", await ReadCodeAsync(accept));
        }

        using (var members = await harness.Client.GetAsync($"/api/v1/worlds/{worldId.Value:D}/members"))
        {
            Assert.Equal(HttpStatusCode.OK, members.StatusCode);
            using var body = JsonDocument.Parse(await members.Content.ReadAsStringAsync());
            var rows = body.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Contains(
                rows,
                row => row.GetProperty("identity").GetProperty("externalId").GetString() == harness.Member.ExternalId &&
                       row.GetProperty("status").GetString() == "Active");
        }

        Assert.Equal(worldId, Assert.Single(
            await harness.Store.ListWorldsForActiveMemberAsync(harness.Member)).WorldId);
    }

    [Fact]
    public async Task NonManagerCannotInviteAnotherIdentity()
    {
        await using var harness = await AccessHarness.CreateAsync();
        var worldId = await harness.CreateWorldWithAcceptedMemberAsync();

        harness.UseMember();
        using var response = await harness.Client.PostAsJsonAsync(
            $"/api/v1/worlds/{worldId.Value:D}/invitations",
            new { provider = "steam", externalId = "76561198000000003" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("NotAccessManager", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task RevocationWaitsForWritableResponsibilityThenCanComplete()
    {
        await using var harness = await AccessHarness.CreateAsync();
        var worldId = await harness.CreateWorldWithAcceptedMemberAsync();
        harness.Responsibility.SetUnresolved(worldId, harness.Member, unresolved: true);

        harness.UseManager();
        using (var response = await harness.Client.PostAsJsonAsync(
                   $"/api/v1/worlds/{worldId.Value:D}/members/revoke",
                   new { provider = harness.Member.Provider, externalId = harness.Member.ExternalId }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("MemberRevocationPending", await ReadCodeAsync(response));
        }

        var pending = await harness.Store.LoadMemberAsync(worldId, harness.Member);
        Assert.NotNull(pending);
        Assert.Equal(SharedWorldMemberStatus.RevocationPending, pending.Status);
        Assert.Empty(await harness.Store.ListWorldsForActiveMemberAsync(harness.Member));

        harness.Responsibility.SetUnresolved(worldId, harness.Member, unresolved: false);
        Assert.Equal(
            CompletePendingRevocationStatus.Completed,
            await harness.AccessService.CompletePendingRevocationAsync(worldId, harness.Member));
        Assert.Null(await harness.Store.LoadMemberAsync(worldId, harness.Member));
    }

    [Fact]
    public async Task AccessManagerMustTransferBeforeLeavingWorld()
    {
        await using var harness = await AccessHarness.CreateAsync();
        var worldId = await harness.CreateWorldWithAcceptedMemberAsync();

        harness.UseManager();
        using (var blocked = await harness.Client.PostAsync(
                   $"/api/v1/worlds/{worldId.Value:D}/leave",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("MustTransferAccessManager", await ReadCodeAsync(blocked));
        }

        using (var transfer = await harness.Client.PostAsJsonAsync(
                   $"/api/v1/worlds/{worldId.Value:D}/access-manager",
                   new { provider = harness.Member.Provider, externalId = harness.Member.ExternalId }))
        {
            Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
            Assert.Equal("AccessManagerTransferred", await ReadCodeAsync(transfer));
        }

        using (var leave = await harness.Client.PostAsync(
                   $"/api/v1/worlds/{worldId.Value:D}/leave",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.OK, leave.StatusCode);
            Assert.Equal("WorldLeft", await ReadCodeAsync(leave));
        }

        var world = await harness.Store.LoadWorldAsync(worldId);
        Assert.NotNull(world);
        Assert.Equal(harness.Member, world.AccessManager);
        Assert.Null(await harness.Store.LoadMemberAsync(worldId, harness.Manager));
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private sealed class AccessHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _managerToken;
        private readonly string _memberToken;

        private AccessHarness(
            WebApplication app,
            HttpClient client,
            AccessStore store,
            ResponsibilityInspector responsibility,
            SharedWorldAccessService accessService,
            ExternalIdentityRef manager,
            ExternalIdentityRef member,
            string managerToken,
            string memberToken)
        {
            _app = app;
            Client = client;
            Store = store;
            Responsibility = responsibility;
            AccessService = accessService;
            Manager = manager;
            Member = member;
            _managerToken = managerToken;
            _memberToken = memberToken;
        }

        public HttpClient Client { get; }
        public AccessStore Store { get; }
        public ResponsibilityInspector Responsibility { get; }
        public SharedWorldAccessService AccessService { get; }
        public ExternalIdentityRef Manager { get; }
        public ExternalIdentityRef Member { get; }

        public static async Task<AccessHarness> CreateAsync()
        {
            var now = new DateTimeOffset(2026, 7, 22, 18, 0, 0, TimeSpan.Zero);
            var manager = new ExternalIdentityRef("steam", "76561198000000001");
            var member = new ExternalIdentityRef("steam", "76561198000000002");
            var store = new AccessStore();
            var responsibility = new ResponsibilityInspector();
            var sessionStore = new MultiSessionStore();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<ISharedWorldMetadataStore>(store);
            builder.Services.AddSingleton<ISharedWorldAccessStore>(store);
            builder.Services.AddSingleton<ISharedWorldResponsibilityInspector>(responsibility);
            builder.Services.AddSingleton(new StewardSessionService(sessionStore, () => now));
            builder.Services.AddSingleton(services => new SharedWorldAccessService(
                services.GetRequiredService<ISharedWorldMetadataStore>(),
                services.GetRequiredService<ISharedWorldAccessStore>(),
                services.GetRequiredService<ISharedWorldResponsibilityInspector>(),
                () => now));

            var app = builder.Build();
            app.UseStewardApiProblemHandling();
            app.MapStewardAccessApiV1();
            await app.StartAsync();

            var sessions = app.Services.GetRequiredService<StewardSessionService>();
            var managerTokens = await sessions.CreateSessionAsync(
                new VerifiedExternalIdentity(manager, "Manager"),
                "manager-device");
            var memberTokens = await sessions.CreateSessionAsync(
                new VerifiedExternalIdentity(member, "Member"),
                "member-device");

            var harness = new AccessHarness(
                app,
                app.GetTestClient(),
                store,
                responsibility,
                app.Services.GetRequiredService<SharedWorldAccessService>(),
                manager,
                member,
                managerTokens.AccessToken,
                memberTokens.AccessToken);
            harness.UseManager();
            return harness;
        }

        public async Task<WorldId> CreateWorldAsync()
        {
            var now = new DateTimeOffset(2026, 7, 22, 18, 0, 0, TimeSpan.Zero);
            var worldId = WorldId.New();
            var metadata = new SharedWorldMetadata(
                worldId,
                "factorio",
                "Access API Test World",
                RevisionId.New(),
                RevisionId.New(),
                Manager,
                now,
                now);
            Assert.True(await Store.TryCreateWorldWithManagerAsync(
                metadata,
                new SharedWorldMember(worldId, Manager, SharedWorldMemberStatus.Active, now)));
            return worldId;
        }

        public async Task<WorldId> CreateWorldWithAcceptedMemberAsync()
        {
            var worldId = await CreateWorldAsync();
            var invitationId = await InviteMemberAsync(worldId);
            UseMember();
            using var response = await Client.PostAsync(
                $"/api/v1/invitations/{invitationId.Value:D}/accept",
                content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return worldId;
        }

        public async Task<WorldAccessInvitationId> InviteMemberAsync(WorldId worldId)
        {
            UseManager();
            using var response = await Client.PostAsJsonAsync(
                $"/api/v1/worlds/{worldId.Value:D}/invitations",
                new { provider = Member.Provider, externalId = Member.ExternalId });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return new WorldAccessInvitationId(
                body.RootElement.GetProperty("data").GetProperty("invitationId").GetGuid());
        }

        public void UseManager()
            => Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _managerToken);

        public void UseMember()
            => Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _memberToken);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class MultiSessionStore : IStewardSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<StewardSessionId, StewardSessionRecord> _sessions = [];
        private readonly Dictionary<string, StewardAccessCredentialRecord> _access = new(StringComparer.Ordinal);

        public Task ReplaceInstallationSessionAsync(
            StewardSessionRecord session,
            StewardAccessCredentialRecord accessCredential,
            DateTimeOffset replacedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var replacedIds = _sessions.Values
                    .Where(existing =>
                        !existing.Revoked &&
                        existing.Identity == session.Identity &&
                        string.Equals(existing.InstallationId, session.InstallationId, StringComparison.Ordinal))
                    .Select(existing => existing.Id)
                    .ToHashSet();
                foreach (var replacedId in replacedIds)
                {
                    var existing = _sessions[replacedId];
                    _sessions[replacedId] = existing with { Revoked = true, RevokedAt = replacedAt };
                }

                foreach (var hash in _access
                             .Where(pair => replacedIds.Contains(pair.Value.SessionId))
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _access.Remove(hash);
                }

                _sessions[session.Id] = session;
                _access[accessCredential.AccessTokenHash] = accessCredential;
                return Task.CompletedTask;
            }
        }

        public Task<StewardAccessContext?> LoadAccessContextAsync(
            string accessTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_access.TryGetValue(accessTokenHash, out var credential) ||
                    !_sessions.TryGetValue(credential.SessionId, out var session))
                {
                    return Task.FromResult<StewardAccessContext?>(null);
                }

                return Task.FromResult<StewardAccessContext?>(new(session, credential));
            }
        }

        public Task<StewardSessionRecord?> LoadRefreshSessionAsync(
            string refreshTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<StewardSessionRecord?>(
                    _sessions.Values.FirstOrDefault(session =>
                        string.Equals(session.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal)));
            }
        }

        public Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
            StewardSessionId sessionId,
            string expectedRefreshTokenHash,
            string installationId,
            string newRefreshTokenHash,
            DateTimeOffset newRefreshExpiresAt,
            StewardAccessCredentialRecord newAccessCredential,
            DateTimeOffset rotatedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var session) ||
                    session.Revoked ||
                    !string.Equals(session.RefreshTokenHash, expectedRefreshTokenHash, StringComparison.Ordinal) ||
                    !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(StoreRotateRefreshSessionStatus.InvalidCredential);
                }

                _sessions[sessionId] = session with
                {
                    RefreshTokenHash = newRefreshTokenHash,
                    RefreshExpiresAt = newRefreshExpiresAt
                };
                foreach (var hash in _access
                             .Where(pair => pair.Value.SessionId == sessionId)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _access.Remove(hash);
                }

                _access[newAccessCredential.AccessTokenHash] = newAccessCredential;
                return Task.FromResult(StoreRotateRefreshSessionStatus.Rotated);
            }
        }

        public Task<bool> TryRevokeRefreshSessionAsync(
            string refreshTokenHash,
            string installationId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var session = _sessions.Values.FirstOrDefault(candidate =>
                    !candidate.Revoked &&
                    string.Equals(candidate.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal) &&
                    string.Equals(candidate.InstallationId, installationId, StringComparison.Ordinal));
                if (session is null)
                {
                    return Task.FromResult(false);
                }

                _sessions[session.Id] = session with { Revoked = true, RevokedAt = revokedAt };
                foreach (var hash in _access
                             .Where(pair => pair.Value.SessionId == session.Id)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _access.Remove(hash);
                }

                return Task.FromResult(true);
            }
        }
    }

    private sealed class ResponsibilityInspector : ISharedWorldResponsibilityInspector
    {
        private readonly HashSet<(WorldId WorldId, ExternalIdentityRef Identity)> _unresolved = [];

        public void SetUnresolved(WorldId worldId, ExternalIdentityRef identity, bool unresolved)
        {
            if (unresolved)
            {
                _unresolved.Add((worldId, identity));
            }
            else
            {
                _unresolved.Remove((worldId, identity));
            }
        }

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_unresolved.Contains((worldId, identity)));
    }

    private sealed class AccessStore : ISharedWorldMetadataStore, ISharedWorldAccessStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId WorldId, ExternalIdentityRef Identity), SharedWorldMember> _members = [];
        private readonly Dictionary<WorldAccessInvitationId, WorldAccessInvitation> _invitations = [];

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_worlds.TryAdd(world.WorldId, world))
                {
                    return Task.FromResult(false);
                }

                _members[(world.WorldId, accessManager.Identity)] = accessManager;
                return Task.FromResult(true);
            }
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var world);
                return Task.FromResult(world);
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _members.TryGetValue((worldId, identity), out var member);
                return Task.FromResult(member);
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(
                    _members.Values
                        .Where(member => member.Identity == identity && member.Status == SharedWorldMemberStatus.Active)
                        .Select(member => _worlds[member.WorldId])
                        .OrderBy(world => world.CreatedAt)
                        .ToArray());
            }
        }

        public Task<IReadOnlyList<SharedWorldMember>> ListMembersAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<SharedWorldMember>>(
                    _members.Values
                        .Where(member => member.WorldId == worldId)
                        .OrderBy(member => member.AddedAt)
                        .ToArray());
            }
        }

        public Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsForIdentityAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<WorldAccessInvitation>>(
                    _invitations.Values
                        .Where(invitation =>
                            invitation.InvitedIdentity == identity &&
                            invitation.Status == WorldAccessInvitationStatus.Pending)
                        .OrderBy(invitation => invitation.CreatedAt)
                        .ToArray());
            }
        }

        public Task<StoreCreateInvitationStatus> TryCreateInvitationAsync(
            WorldAccessInvitation invitation,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_members.ContainsKey((invitation.WorldId, invitation.InvitedIdentity)))
                {
                    return Task.FromResult(StoreCreateInvitationStatus.TargetAlreadyMember);
                }

                if (_invitations.Values.Any(existing =>
                        existing.WorldId == invitation.WorldId &&
                        existing.InvitedIdentity == invitation.InvitedIdentity &&
                        existing.Status == WorldAccessInvitationStatus.Pending))
                {
                    return Task.FromResult(StoreCreateInvitationStatus.AlreadyInvited);
                }

                _invitations[invitation.Id] = invitation;
                return Task.FromResult(StoreCreateInvitationStatus.Created);
            }
        }

        public Task<StoreInvitationResponseStatus> TryAcceptInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
            => RespondAsync(invitationId, invitedIdentity, respondedAt, accept: true);

        public Task<StoreInvitationResponseStatus> TryDeclineInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
            => RespondAsync(invitationId, invitedIdentity, respondedAt, accept: false);

        public Task<StoreMemberRevocationStatus> TryRevokeMemberAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            bool deferForUnresolvedResponsibility,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_worlds.TryGetValue(worldId, out var world) || world.AccessManager != expectedAccessManager)
                {
                    return Task.FromResult(StoreMemberRevocationStatus.ManagerChanged);
                }

                if (!_members.TryGetValue((worldId, targetIdentity), out var member) ||
                    member.Status != SharedWorldMemberStatus.Active)
                {
                    return Task.FromResult(StoreMemberRevocationStatus.TargetNotActiveMember);
                }

                if (deferForUnresolvedResponsibility)
                {
                    _members[(worldId, targetIdentity)] = member with
                    {
                        Status = SharedWorldMemberStatus.RevocationPending
                    };
                }
                else
                {
                    _members.Remove((worldId, targetIdentity));
                }

                _worlds[worldId] = world with { UpdatedAt = changedAt };
                return Task.FromResult(
                    deferForUnresolvedResponsibility
                        ? StoreMemberRevocationStatus.RevocationPending
                        : StoreMemberRevocationStatus.Revoked);
            }
        }

        public Task<StoreLeaveMemberStatus> TryLeaveWorldAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_worlds.TryGetValue(worldId, out var world) && world.AccessManager == identity)
                {
                    return Task.FromResult(StoreLeaveMemberStatus.IsAccessManager);
                }

                if (!_members.TryGetValue((worldId, identity), out var member) ||
                    member.Status != SharedWorldMemberStatus.Active)
                {
                    return Task.FromResult(StoreLeaveMemberStatus.TargetNotActiveMember);
                }

                _members.Remove((worldId, identity));
                if (world is not null)
                {
                    _worlds[worldId] = world with { UpdatedAt = changedAt };
                }

                return Task.FromResult(StoreLeaveMemberStatus.Left);
            }
        }

        public Task<StoreCompletePendingRevocationStatus> TryCompletePendingRevocationAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_members.TryGetValue((worldId, identity), out var member) ||
                    member.Status != SharedWorldMemberStatus.RevocationPending)
                {
                    return Task.FromResult(StoreCompletePendingRevocationStatus.NotPending);
                }

                _members.Remove((worldId, identity));
                if (_worlds.TryGetValue(worldId, out var world))
                {
                    _worlds[worldId] = world with { UpdatedAt = changedAt };
                }

                return Task.FromResult(StoreCompletePendingRevocationStatus.Completed);
            }
        }

        public Task<StoreTransferAccessManagerStatus> TryTransferAccessManagerAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_worlds.TryGetValue(worldId, out var world) || world.AccessManager != expectedAccessManager)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.ManagerChanged);
                }

                if (world.AccessManager == targetIdentity)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.AlreadyManager);
                }

                if (!_members.TryGetValue((worldId, targetIdentity), out var member) ||
                    member.Status != SharedWorldMemberStatus.Active)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.TargetNotActiveMember);
                }

                _worlds[worldId] = world with
                {
                    AccessManager = targetIdentity,
                    UpdatedAt = changedAt
                };
                return Task.FromResult(StoreTransferAccessManagerStatus.Transferred);
            }
        }

        private Task<StoreInvitationResponseStatus> RespondAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            bool accept)
        {
            lock (_gate)
            {
                if (!_invitations.TryGetValue(invitationId, out var invitation) ||
                    invitation.InvitedIdentity != invitedIdentity)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.NotFoundOrNotInvited);
                }

                if (invitation.Status != WorldAccessInvitationStatus.Pending)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.AlreadyResolved);
                }

                _invitations[invitationId] = invitation with
                {
                    Status = accept ? WorldAccessInvitationStatus.Accepted : WorldAccessInvitationStatus.Declined,
                    RespondedAt = respondedAt
                };
                if (accept)
                {
                    _members[(invitation.WorldId, invitedIdentity)] = new SharedWorldMember(
                        invitation.WorldId,
                        invitedIdentity,
                        SharedWorldMemberStatus.Active,
                        respondedAt);
                }

                return Task.FromResult(StoreInvitationResponseStatus.Completed);
            }
        }
    }
}
