using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldLiveMemberRevocationTests
{
    [Fact]
    public void RevokeCancelsExistingTokenBeforeObserversRun()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var token = registry.GetCancellationToken(worldId, 12, member);
        var notifications = 0;
        registry.Revoked += revocation =>
        {
            notifications++;
            Assert.Equal(worldId, revocation.WorldId);
            Assert.Equal((ulong)12, revocation.AuthorityGeneration);
            Assert.Equal(member.ExternalId, revocation.Member.ExternalId);
            Assert.True(token.IsCancellationRequested);
            Assert.True(registry.IsRevoked(worldId, 12, member));
        };

        Assert.True(registry.Revoke(worldId, 12, member));

        Assert.True(token.IsCancellationRequested);
        Assert.True(registry.IsRevoked(worldId, 12, member));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void DuplicateRevokeIsIdempotentAndNotifiesOnlyOnce()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var notifications = 0;
        registry.Revoked += _ => notifications++;

        Assert.True(registry.Revoke(worldId, 4, member));
        Assert.False(registry.Revoke(worldId, 4, member));

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void RevocationIsBoundToExactAuthorityGeneration()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var generation12 = registry.GetCancellationToken(worldId, 12, member);
        var generation13 = registry.GetCancellationToken(worldId, 13, member);

        registry.Revoke(worldId, 12, member);

        Assert.True(generation12.IsCancellationRequested);
        Assert.False(generation13.IsCancellationRequested);
        Assert.True(registry.IsRevoked(worldId, 12, member));
        Assert.False(registry.IsRevoked(worldId, 13, member));
    }

    [Fact]
    public void RestoreAfterExplicitReadmissionCreatesFreshUsableToken()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var oldToken = registry.GetCancellationToken(worldId, 8, member);
        registry.Revoke(worldId, 8, member);

        Assert.True(registry.Restore(worldId, 8, member));
        var freshToken = registry.GetCancellationToken(worldId, 8, member);

        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(freshToken.IsCancellationRequested);
        Assert.False(registry.IsRevoked(worldId, 8, member));
    }

    [Fact]
    public void RestoreCannotDetachAnActiveNonRevokedOperation()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var existingToken = registry.GetCancellationToken(worldId, 9, member);

        Assert.False(registry.Restore(worldId, 9, member));
        Assert.True(registry.Revoke(worldId, 9, member));

        Assert.True(existingToken.IsCancellationRequested);
    }

    [Fact]
    public void ClearWorldCancelsOnlyTheExactWorldGeneration()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var otherWorldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var generation12 = registry.GetCancellationToken(worldId, 12, member);
        var generation13 = registry.GetCancellationToken(worldId, 13, member);
        var otherWorld = registry.GetCancellationToken(otherWorldId, 12, member);

        var cleared = registry.ClearWorld(worldId, 12);

        Assert.Equal(1, cleared);
        Assert.True(generation12.IsCancellationRequested);
        Assert.False(generation13.IsCancellationRequested);
        Assert.False(otherWorld.IsCancellationRequested);
    }

    [Fact]
    public void StableIdentityIgnoresProviderCaseAndDisplayName()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var original = new UserIdentity("steam", "7002", "Old Persona");
        var renamed = new UserIdentity("STEAM", "7002", "New Persona");
        var token = registry.GetCancellationToken(worldId, 5, original);

        registry.Revoke(worldId, 5, renamed);

        Assert.True(token.IsCancellationRequested);
        Assert.True(registry.IsRevoked(worldId, 5, original));
    }
}
