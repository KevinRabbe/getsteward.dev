using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class BringHereAuthorityServiceTests
{
    private static readonly UserIdentity Owner = new("steam", "76561198000000001", "Owner");

    [Fact]
    public void OneRemoteOwnedHeadIsAvailable()
    {
        var worldId = WorldId.New();
        var claim = Claim(worldId, Owner, "desktop-a", RevisionId.New(), RevisionId.New());

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [claim]);

        Assert.Equal(BringHereAvailability.Available, decision.Availability);
        Assert.Equal(claim, decision.Source);
        Assert.Empty(decision.ConflictingClaims);
    }

    [Fact]
    public void OtherUsersClaimsNeverGrantAuthority()
    {
        var worldId = WorldId.New();
        var other = new UserIdentity("steam", "76561198000000002", "Other");

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [Claim(worldId, other, "desktop-a", RevisionId.New(), RevisionId.New())]);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Null(decision.Source);
    }

    [Fact]
    public void SameHeadOnMultipleRemoteDevicesIsNotAConflict()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var older = Claim(worldId, Owner, "desktop-a", stateId, environmentId);
        var newer = Claim(worldId, Owner, "laptop", stateId, environmentId) with
        {
            ObservedAt = older.ObservedAt.AddMinutes(1)
        };

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [older, newer]);

        Assert.Equal(BringHereAvailability.Available, decision.Availability);
        Assert.Equal(newer, decision.Source);
    }

    [Fact]
    public void DivergentRemoteHeadsFailClosed()
    {
        var worldId = WorldId.New();

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [
                Claim(worldId, Owner, "desktop-a", RevisionId.New(), RevisionId.New()),
                Claim(worldId, Owner, "laptop", RevisionId.New(), RevisionId.New())
            ]);

        Assert.Equal(BringHereAvailability.Conflict, decision.Availability);
        Assert.Null(decision.Source);
        Assert.Equal(2, decision.ConflictingClaims.Count);
    }

    [Fact]
    public void DifferentLocalAndRemoteHeadsFailClosedEvenWithNewerTimestamp()
    {
        var worldId = WorldId.New();
        var local = Claim(worldId, Owner, "desktop-b", RevisionId.New(), RevisionId.New());
        var remote = Claim(worldId, Owner, "desktop-a", RevisionId.New(), RevisionId.New()) with
        {
            ObservedAt = local.ObservedAt.AddHours(1)
        };

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [local, remote]);

        Assert.Equal(BringHereAvailability.Conflict, decision.Availability);
        Assert.Contains(local, decision.ConflictingClaims);
        Assert.Contains(remote, decision.ConflictingClaims);
    }

    [Fact]
    public void ExactHeadAlreadyOnTargetIsAlreadyHere()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var local = Claim(worldId, Owner, "desktop-b", stateId, environmentId);
        var remote = Claim(worldId, Owner, "desktop-a", stateId, environmentId);

        var decision = new BringHereAuthorityService().Resolve(
            worldId,
            Owner,
            "desktop-b",
            [local, remote]);

        Assert.Equal(BringHereAvailability.AlreadyHere, decision.Availability);
        Assert.Equal(remote, decision.Source);
    }

    private static OwnedWorldLocationClaim Claim(
        WorldId worldId,
        UserIdentity owner,
        string installationId,
        RevisionId stateId,
        RevisionId environmentId)
        => new(
            worldId,
            owner.Provider,
            owner.ExternalId,
            installationId,
            stateId,
            environmentId,
            DateTimeOffset.UtcNow);
}
