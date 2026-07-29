using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalRoundPlanTests
{
    [Fact]
    public void FastExecuteCarriesRoleSpecificUtilityRequirements()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 12,
            MapName: "de_mirage",
            HasTwoBombSites: true,
            BuyPhase: BuyPhase.FullBuy,
            AliveT: 5,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4, 5],
            HumanCarrierSlot: null,
            CurrentAwperSlot: 5,
            PreviousIntents: []);

        var candidates = TacticalRoundPlanner.BuildCandidates(context);
        var plan = Assert.Single(candidates, candidate =>
            candidate.Intent == TTacticalIntent.FastExecute);

        Assert.Equal(5, plan.RoleAssignments.Count);
        Assert.Contains(plan.RoleAssignments, assignment =>
            assignment.Role == TTacticalRole.Entry);
        Assert.Contains(plan.RoleAssignments, assignment =>
            assignment.Role == TTacticalRole.Support);
        Assert.Contains(plan.RequiredUtility, requirement =>
            requirement.UtilityType == TacticalUtilityType.Smoke
            && requirement.RequiredCount > 0);
        Assert.Contains(plan.RequiredUtility, requirement =>
            requirement.UtilityType == TacticalUtilityType.Flash
            && requirement.RequiredCount > 0);
        Assert.Contains(plan.RequiredUtility, requirement =>
            requirement.UtilityType == TacticalUtilityType.Fire
            && requirement.RequiredCount > 0);
    }

    [Fact]
    public void DefaultSplitAssignsMultipleGroupsWithoutAggressivePack()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 13,
            MapName: "de_ancient",
            HasTwoBombSites: true,
            BuyPhase: BuyPhase.FullBuy,
            AliveT: 5,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4, 5],
            HumanCarrierSlot: null,
            CurrentAwperSlot: null,
            PreviousIntents: []);

        var plan = Assert.Single(
            TacticalRoundPlanner.BuildCandidates(context),
            candidate => candidate.Intent == TTacticalIntent.DefaultSplit);

        Assert.True(plan.RoleAssignments.Select(assignment => assignment.Group).Distinct().Count() >= 2);
        Assert.True(plan.RoleAssignments.Select(assignment => assignment.Site).Distinct().Count() >= 2);
        Assert.False(plan.UsesSharedTarget);
    }

    [Fact]
    public void HumanCarrierCreatesCarrierSupportAssignments()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 14,
            MapName: "de_inferno",
            HasTwoBombSites: true,
            BuyPhase: BuyPhase.HalfBuy,
            AliveT: 4,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4],
            HumanCarrierSlot: 99,
            CurrentAwperSlot: null,
            PreviousIntents: []);

        var candidates = TacticalRoundPlanner.BuildCandidates(context);
        Assert.All(candidates, plan =>
            Assert.Contains(plan.RoleAssignments, assignment =>
                assignment.Role == TTacticalRole.CarrierSupport
                && assignment.IsHumanDependent));
    }

    [Fact]
    public void SameSeedSelectsTheSameNearOptimalPlan()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 15,
            MapName: "de_dust2",
            HasTwoBombSites: true,
            BuyPhase: BuyPhase.FullBuy,
            AliveT: 5,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4, 5],
            HumanCarrierSlot: null,
            CurrentAwperSlot: null,
            PreviousIntents: [TTacticalIntent.FastExecute]);
        var candidates = TacticalRoundPlanner.BuildCandidates(context);

        var first = TacticalRoundPlanner.SelectPlan(candidates, seed: 42);
        var second = TacticalRoundPlanner.SelectPlan(candidates, seed: 42);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.Intent, second.Intent);
    }

    [Fact]
    public void RepeatedIntentIsPenalizedWhenAnotherCandidateIsExecutable()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 16,
            MapName: "de_nuke",
            HasTwoBombSites: true,
            BuyPhase: BuyPhase.FullBuy,
            AliveT: 5,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4, 5],
            HumanCarrierSlot: null,
            CurrentAwperSlot: null,
            PreviousIntents: [TTacticalIntent.FastExecute]);

        var selected = TacticalRoundPlanner.SelectPlan(
            TacticalRoundPlanner.BuildCandidates(context),
            seed: 7);

        Assert.NotEqual(TTacticalIntent.FastExecute, selected.Intent);
    }

    [Fact]
    public void SingleSitePlansNeverAssignTheUnavailableFallbackSite()
    {
        var context = new TacticalRoundPlanContext(
            RoundKey: 17,
            MapName: "custom_single_site",
            HasTwoBombSites: false,
            BuyPhase: BuyPhase.FullBuy,
            AliveT: 5,
            AliveCt: 5,
            BotSlots: [1, 2, 3, 4, 5],
            HumanCarrierSlot: null,
            CurrentAwperSlot: null,
            PreviousIntents: []);

        var candidates = TacticalRoundPlanner.BuildCandidates(context);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, plan =>
            Assert.All(plan.RoleAssignments, assignment =>
                Assert.Equal(CtGambleSite.A, assignment.Site)));
    }
}
