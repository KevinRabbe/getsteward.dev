using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class OwnedPrivateWorldCatalogServiceTests
{
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    private readonly OwnedPrivateWorldCatalogService _service = new();

    [Fact]
    public void RemotePresentedWorldIsDiscoverableAndAvailable()
    {
        var worldId = WorldId.New();
        var claim = Claim(
            worldId,
            "pc-source",
            RevisionId.New(),
            RevisionId.New(),
            "Factory World",
            "factorio");

        var entry = Assert.Single(_service.Resolve(
            Owner,
            "pc-target",
            [claim]));

        Assert.Equal(worldId, entry.WorldId);
        Assert.Equal("Factory World", entry.Name);
        Assert.Equal("factorio", entry.GameAdapterId);
        Assert.Equal(BringHereAvailability.Available, entry.Availability);
        Assert.Equal(claim, entry.Source);
    }

    [Fact]
    public void PresentationlessWorldIsNotDiscoverableButPresentationlessClaimStillAffectsAuthority()
    {
        var hiddenWorld = Claim(
            WorldId.New(),
            "pc-hidden",
            RevisionId.New(),
            RevisionId.New(),
            name: null,
            adapterId: null);
        var worldId = WorldId.New();
        var visibleState = RevisionId.New();
        var visibleEnvironment = RevisionId.New();
        var visible = Claim(
            worldId,
            "pc-a",
            visibleState,
            visibleEnvironment,
            "Visible World",
            "factorio");
        var hiddenDivergent = Claim(
            worldId,
            "pc-b",
            RevisionId.New(),
            RevisionId.New(),
            name: null,
            adapterId: null);

        var entries = _service.Resolve(
            Owner,
            "pc-target",
            [hiddenWorld, visible, hiddenDivergent]);

        var entry = Assert.Single(entries);
        Assert.Equal(worldId, entry.WorldId);
        Assert.Equal(BringHereAvailability.Conflict, entry.Availability);
        Assert.Equal(2, entry.ConflictingClaims.Count);
        Assert.DoesNotContain(entries, item => item.WorldId == hiddenWorld.WorldId);
    }

    [Fact]
    public void NewestNameWinsOnlyForPresentationWhileAdapterMustAgree()
    {
        var worldId = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();
        var older = Claim(
            worldId,
            "pc-a",
            state,
            environment,
            "Old Name",
            "factorio",
            Now);
        var newer = Claim(
            worldId,
            "pc-b",
            state,
            environment,
            "New Name",
            "factorio",
            Now.AddMinutes(1));

        var entry = Assert.Single(_service.Resolve(
            Owner,
            "pc-target",
            [older, newer]));

        Assert.Equal("New Name", entry.Name);
        Assert.Equal("factorio", entry.GameAdapterId);
        Assert.Equal(BringHereAvailability.Available, entry.Availability);
    }

    [Fact]
    public void AdapterDisagreementReturnsDisabledConflictWithoutChoosingAdapter()
    {
        var worldId = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();
        var claims = new[]
        {
            Claim(worldId, "pc-a", state, environment, "World", "factorio"),
            Claim(worldId, "pc-b", state, environment, "World", "other.adapter")
        };

        var entry = Assert.Single(_service.Resolve(
            Owner,
            "pc-target",
            claims));

        Assert.Equal(BringHereAvailability.Conflict, entry.Availability);
        Assert.Null(entry.GameAdapterId);
        Assert.Null(entry.Source);
        Assert.Equal(2, entry.ConflictingClaims.Count);
        Assert.Contains("adapter ID", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetClaimAtSameHeadIsAlreadyHere()
    {
        var worldId = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();
        var claims = new[]
        {
            Claim(worldId, "pc-target", state, environment, "World", "factorio"),
            Claim(worldId, "pc-source", state, environment, "World", "factorio")
        };

        var entry = Assert.Single(_service.Resolve(
            Owner,
            "pc-target",
            claims));

        Assert.Equal(BringHereAvailability.AlreadyHere, entry.Availability);
    }

    [Fact]
    public void OtherOwnerClaimsAreExcludedBeforeGrouping()
    {
        var owned = Claim(
            WorldId.New(),
            "pc-a",
            RevisionId.New(),
            RevisionId.New(),
            "Owned",
            "factorio");
        var foreign = Claim(
            WorldId.New(),
            "pc-b",
            RevisionId.New(),
            RevisionId.New(),
            "Foreign",
            "factorio") with
        {
            OwnerExternalId = "owner-2"
        };

        var entry = Assert.Single(_service.Resolve(
            Owner,
            "pc-target",
            [foreign, owned]));

        Assert.Equal(owned.WorldId, entry.WorldId);
    }

    private static OwnedWorldLocationClaim Claim(
        WorldId worldId,
        string installationId,
        RevisionId stateId,
        RevisionId environmentId,
        string? name,
        string? adapterId,
        DateTimeOffset? observedAt = null)
        => new(
            worldId,
            Owner.Provider,
            Owner.ExternalId,
            installationId,
            stateId,
            environmentId,
            observedAt ?? Now)
        {
            Presentation = name is null
                ? null
                : new OwnedWorldPresentation(name, adapterId!)
        };
}
