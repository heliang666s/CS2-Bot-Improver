using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalBuyDemandTests
{
    [Fact]
    public void AffordableTacticalDemandIsAHardTeamConstraint()
    {
        var members = new TeamPlanningMember[]
        {
            new(
                Slot: 1,
                IsBot: false,
                IsAwper: false,
                Money: 0,
                Candidates:
                [
                    Plan(["smoke"], cost: 0),
                ],
                CurrentPrimary: "weapon_ak47",
                SavedTier: 3),
            new(
                Slot: 2,
                IsBot: true,
                IsAwper: false,
                Money: 2000,
                Candidates:
                [
                    Plan(["flash"], cost: 0),
                    Plan(["smoke", "flash"], cost: 200),
                ],
                CurrentPrimary: "weapon_ak47",
                SavedTier: 3),
            new(
                Slot: 3,
                IsBot: true,
                IsAwper: false,
                Money: 2000,
                Candidates:
                [
                    Plan(["flash"], cost: 0),
                    Plan(["smoke", "flash"], cost: 200),
                ],
                CurrentPrimary: "weapon_ak47",
                SavedTier: 3),
        };

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            tacticalUtilityDemand: new TeamUtilityDemand(1, 2, 0, 0, 0, 1)
            {
                IsHardRequirement = true,
            });

        Assert.True(result.Plan.TacticalDemandSatisfied,
            $"reason={result.Reason}, bots={string.Join(';', result.BotPlans.Values.Select(plan => string.Join(',', plan.Utility)))}");
        Assert.True(1 <= result.BotPlans.Values.Sum(plan => plan.Utility.Count(item => item == "smoke"))
            + result.HumanObservations.Values.Sum(plan => plan.Utility.Count(item => item == "smoke")));
        Assert.True(2 <= result.BotPlans.Values.Sum(plan => plan.Utility.Count(item => item == "flash"))
            + result.HumanObservations.Values.Sum(plan => plan.Utility.Count(item => item == "flash")));
    }

    [Fact]
    public void UnaffordableTacticalDemandIsReportedForPlanFallback()
    {
        var members = new TeamPlanningMember[]
        {
            new(
                Slot: 1,
                IsBot: true,
                IsAwper: false,
                Money: 0,
                Candidates: [Plan([], cost: 0)],
                CurrentPrimary: "weapon_ak47",
                SavedTier: 3),
        };

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            tacticalUtilityDemand: new TeamUtilityDemand(1, 0, 0, 0, 0, 1)
            {
                IsHardRequirement = true,
            });

        Assert.False(result.Plan.TacticalDemandSatisfied);
        Assert.NotEmpty(result.BotPlans);
    }

    [Fact]
    public void TacticalRolePackageReachesThePerPlayerPlanner()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 6000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Standard,
            role: BuyRole.TEntry,
            tacticalUtilityPackage: ["smoke", "flash", "flash", "he"]);

        Assert.Contains(candidates, plan =>
            plan.PrimaryWeapon == "weapon_ak47"
            && plan.Utility.Count(item => item == "smoke") >= 1
            && plan.Utility.Count(item => item == "flash") >= 2
            && plan.Utility.Count(item => item == "he") >= 1);
    }

    private static PlayerBuyPlan Plan(IReadOnlyList<string> utility, int cost)
        => new(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_ak47",
            null,
            BuysHelmet: true,
            BuysDefuser: false,
            utility,
            cost)
        {
            Tier = 3,
        };
}
