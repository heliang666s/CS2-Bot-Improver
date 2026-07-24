using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TPrePlantTacticalTests
{
    [Fact]
    public void TSideProbesWithSplitRoutesBeforeCommittingToTheSite()
    {
        var decision = TPrePlantTacticalPolicy.Resolve(new TPrePlantContext(
            BombPlanted: false,
            HasReliableSite: true,
            HasBombCarrier: true,
            CarrierIsHuman: false,
            ProbeComplete: false,
            ContactConfirmed: false,
            SiteBlocked: false,
            AggressivePack: false,
            AliveT: 5,
            AliveCt: 5));

        Assert.Equal(TPrePlantStage.Probe, decision.Stage);
        Assert.True(decision.SplitRoutes);
        Assert.False(decision.UsesSharedTarget);
    }

    [Fact]
    public void HumanCarrierKeepsBotsSplitUntilContactThenExecutes()
    {
        var staged = new TPrePlantContext(
            false, true, true, true, true, false, false, false, 5, 5);
        var contacted = staged with { ContactConfirmed = true };

        Assert.Equal(TPrePlantStage.Split, TPrePlantTacticalPolicy.Resolve(staged).Stage);
        Assert.Equal(TPrePlantStage.Execute, TPrePlantTacticalPolicy.Resolve(contacted).Stage);
        Assert.False(TPrePlantTacticalPolicy.Resolve(contacted).UsesSharedTarget);
    }

    [Fact]
    public void BlockedSiteRotatesAndOnlyAggressivePackMayUseFakeCommit()
    {
        var blocked = TPrePlantTacticalPolicy.Resolve(new TPrePlantContext(
            false, true, true, false, true, false, true, false, 5, 5));
        var fake = TPrePlantTacticalPolicy.Resolve(new TPrePlantContext(
            false, true, true, false, true, false, false, true, 5, 5));

        Assert.Equal(TPrePlantStage.Rotate, blocked.Stage);
        Assert.True(blocked.ShouldRotate);
        Assert.Equal(TPrePlantStage.Fake, fake.Stage);
        Assert.True(fake.UsesSharedTarget);
    }

    [Fact]
    public void SiteBlockedTakesPrecedenceEvenWhenNoReliableSiteIsAvailable()
    {
        var decision = TPrePlantTacticalPolicy.Resolve(new TPrePlantContext(
            false, false, true, false, true, true, true, false, 5, 5));

        Assert.Equal(TPrePlantStage.Rotate, decision.Stage);
        Assert.True(decision.ShouldRotate);
    }

    [Fact]
    public void SplitRoutesAssignBotsAcrossBothSitesInsteadOfOneAnchor()
    {
        var decision = new TPrePlantDecision(
            TPrePlantStage.Split,
            SplitRoutes: true,
            UsesSharedTarget: false,
            ShouldRotate: false,
            Reason: "test");

        var assignments = TPrePlantTacticalPolicy.AssignSites(
            [1, 2, 3, 4],
            decision,
            CtGambleSite.None,
            [CtGambleSite.A, CtGambleSite.B]);

        Assert.Equal(CtGambleSite.A, assignments[1]);
        Assert.Equal(CtGambleSite.B, assignments[2]);
        Assert.Equal(CtGambleSite.A, assignments[3]);
        Assert.Equal(CtGambleSite.B, assignments[4]);
    }

    [Fact]
    public void RotateRoutesAllBotsToTheOppositeConfirmedSite()
    {
        var decision = new TPrePlantDecision(
            TPrePlantStage.Rotate,
            SplitRoutes: true,
            UsesSharedTarget: false,
            ShouldRotate: true,
            Reason: "test");

        var assignments = TPrePlantTacticalPolicy.AssignSites(
            [1, 2, 3],
            decision,
            CtGambleSite.A,
            [CtGambleSite.A, CtGambleSite.B]);

        Assert.All(assignments.Values, site => Assert.Equal(CtGambleSite.B, site));
    }

    [Fact]
    public void KnownSiteWithIncompleteRouteIsMarkedBlockedForNextDecision()
    {
        Assert.True(TPrePlantTacticalPolicy.ShouldMarkRouteBlocked(
            hasReliableSite: true,
            routeTargetsReady: false,
            stage: TPrePlantStage.Probe));
        Assert.False(TPrePlantTacticalPolicy.ShouldMarkRouteBlocked(
            hasReliableSite: false,
            routeTargetsReady: false,
            stage: TPrePlantStage.Probe));
        Assert.False(TPrePlantTacticalPolicy.ShouldMarkRouteBlocked(
            hasReliableSite: true,
            routeTargetsReady: true,
            stage: TPrePlantStage.Probe));
        Assert.False(TPrePlantTacticalPolicy.ShouldMarkRouteBlocked(
            hasReliableSite: true,
            routeTargetsReady: false,
            stage: TPrePlantStage.Stage));
    }
}
