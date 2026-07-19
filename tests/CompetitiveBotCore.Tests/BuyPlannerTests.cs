using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class BuyPlannerTests
{
    [Fact]
    public void FourPlayersAbleToBuyRifleAndArmor_IsFullBuy()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.Terrorist,
            [4000, 4000, 4000, 4000, 1000],
            IsPistolRound: false,
            IsLastRound: false,
            ForceBuySignal: false,
            OpponentEcoLikely: false);

        Assert.Equal(BuyPhase.FullBuy, BuyPlanner.Classify(snapshot));
    }

    [Fact]
    public void ThreeRifleBuyersWithoutForceSignal_IsHalfBuy()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.CounterTerrorist,
            [3600, 3600, 3600, 1000, 1000],
            IsPistolRound: false,
            IsLastRound: false,
            ForceBuySignal: false,
            OpponentEcoLikely: false);

        Assert.Equal(BuyPhase.HalfBuy, BuyPlanner.Classify(snapshot));
    }

    [Fact]
    public void SmallTeamWithOneBotCanStillReachFullBuy()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.CounterTerrorist,
            [4000],
            IsPistolRound: false,
            IsLastRound: false,
            ForceBuySignal: false,
            OpponentEcoLikely: false);

        Assert.Equal(BuyPhase.FullBuy, BuyPlanner.Classify(snapshot));
    }

    [Fact]
    public void ForceSignalWithCheapCoreBuyers_IsForceBuy()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.Terrorist,
            [1900, 1900, 1900, 800, 800],
            IsPistolRound: false,
            IsLastRound: false,
            ForceBuySignal: true,
            OpponentEcoLikely: false);

        Assert.Equal(BuyPhase.ForceBuy, BuyPlanner.Classify(snapshot));
        Assert.Equal("weapon_mac10", BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist, BuyPhase.ForceBuy, 1900, false, false).PrimaryWeapon);
    }

    [Fact]
    public void LastRoundStillRespectsCoreWeaponBeforeUtility()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.LastRound,
            money: 1000,
            designatedAwper: false,
            opponentEcoLikely: true);

        Assert.Null(plan.PrimaryWeapon);
        Assert.Empty(plan.Utility);
        Assert.False(plan.BuysDefuser);
    }

    [Fact]
    public void LastRoundAlwaysSpendsCoreBudget()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.CounterTerrorist,
            [8000, 8000, 8000, 8000, 8000],
            IsPistolRound: false,
            IsLastRound: true,
            ForceBuySignal: false,
            OpponentEcoLikely: false);

        Assert.Equal(BuyPhase.LastRound, BuyPlanner.Classify(snapshot));
    }

    [Fact]
    public void TSideFallsBackToGalil_NotScout_WhenAkIsNotAffordable()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 2500,
            designatedAwper: true,
            opponentEcoLikely: false);

        Assert.Equal("weapon_galilar", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_ssg08", plan.PrimaryWeapon);
        Assert.True(plan.BuysArmor);
    }

    [Fact]
    public void CTSideFallsBackToFamas_NotScout_WhenM4IsNotAffordable()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: true,
            opponentEcoLikely: false);

        Assert.Equal("weapon_famas", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_ssg08", plan.PrimaryWeapon);
        Assert.True(plan.BuysArmor);
        Assert.False(plan.BuysHelmet);
    }

    [Fact]
    public void AwpIsOnlyBoughtWhenItDoesNotReplaceCoreRiflePlan()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 5000,
            designatedAwper: true,
            opponentEcoLikely: false);

        Assert.Equal("weapon_m4a1", plan.PrimaryWeapon);
    }

    [Fact]
    public void FullBuyAddsUtilityOnlyAfterArmorAndPrimary()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 5000,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal("weapon_ak47", plan.PrimaryWeapon);
        Assert.True(plan.BuysArmor);
        Assert.Equal(["smoke", "flash", "he", "molotov"], plan.Utility);
        Assert.True(plan.EstimatedCost >= BuyPlanner.KevlarPrice + 2700);
    }

    [Fact]
    public void LastRoundBoundaryIsExplicit()
    {
        Assert.True(RoundSchedule.IsLastRoundOfHalf(11));
        Assert.True(RoundSchedule.IsLastRoundOfHalf(23));
        Assert.False(RoundSchedule.IsLastRoundOfHalf(22));
        Assert.True(RoundSchedule.IsSecondToLastRoundOfHalf(22));
    }
}
