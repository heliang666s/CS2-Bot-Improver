using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CtGamblePolicyTests
{
    [Theory]
    [InlineData(BuyPhase.Eco)]
    [InlineData(BuyPhase.HalfBuy)]
    [InlineData(BuyPhase.ForceBuy)]
    public void EconomicRoundConcentratesFourBotsAtTheSelectedSite(BuyPhase phase)
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            phase,
            RoundPhase.Live,
            CtGambleSite.B,
            CtGambleSite.None,
            hasReliableContact: false,
            liveElapsedSeconds: 6f,
            hasValuableWeapon: false,
            aliveCt: 5);

        Assert.Equal(CtGambleSite.B, decision.Site);
        Assert.Equal(CtGambleStage.Stack, decision.Stage);
        Assert.Equal(4, decision.StackCount);
        Assert.Equal(0, decision.RotationBudget);
        Assert.True(decision.ShouldMoveToSite);
        Assert.False(decision.ShouldMoveToRetreat);
    }

    [Fact]
    public void ContactAtTheGambledSiteKeepsTheStackTogether()
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            CtGambleSite.A,
            CtGambleSite.A,
            hasReliableContact: true,
            liveElapsedSeconds: 9f,
            hasValuableWeapon: false,
            aliveCt: 5);

        Assert.Equal(CtGambleStage.Hold, decision.Stage);
        Assert.Equal(0, decision.RotationBudget);
        Assert.False(decision.ShouldMoveToRetreat);
    }

    [Fact]
    public void ContactAtTheOtherSiteAllowsAtMostTwoRotators()
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            CtGambleSite.A,
            CtGambleSite.B,
            hasReliableContact: true,
            liveElapsedSeconds: 9f,
            hasValuableWeapon: false,
            aliveCt: 5);

        Assert.Equal(CtGambleStage.Rotate, decision.Stage);
        Assert.Equal(2, decision.RotationBudget);
        Assert.False(decision.ShouldMoveToRetreat);
    }

    [Fact]
    public void NoContactStartsRetreatAfterTwelveSeconds()
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            CtGambleSite.B,
            CtGambleSite.None,
            hasReliableContact: false,
            liveElapsedSeconds: 13f,
            hasValuableWeapon: true,
            aliveCt: 5);

        Assert.Equal(CtGambleStage.Withdraw, decision.Stage);
        Assert.True(decision.ShouldMoveToRetreat);
        Assert.True(decision.PreserveWeapon);
    }

    [Fact]
    public void NoContactAtEighteenSecondsEntersSave()
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            CtGambleSite.A,
            CtGambleSite.None,
            hasReliableContact: false,
            liveElapsedSeconds: 18f,
            hasValuableWeapon: true,
            aliveCt: 5);

        Assert.Equal(CtGambleStage.Save, decision.Stage);
        Assert.True(decision.ShouldMoveToRetreat);
        Assert.True(decision.PreserveWeapon);
    }

    [Theory]
    [InlineData(BotMatchProfile.Arcade)]
    [InlineData(BotMatchProfile.Legacy)]
    public void GambleIsCompetitiveOnly(BotMatchProfile profile)
    {
        var decision = CtGamblePolicy.Evaluate(
            profile,
            BuyPhase.Eco,
            RoundPhase.Live,
            CtGambleSite.A,
            CtGambleSite.None,
            hasReliableContact: false,
            liveElapsedSeconds: 18f,
            hasValuableWeapon: true,
            aliveCt: 5);

        Assert.False(decision.Enabled);
        Assert.Equal(CtGambleStage.None, decision.Stage);
    }

    [Fact]
    public void GambleStopsWhenBombIsPlanted()
    {
        var decision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.BombPlanted,
            CtGambleSite.A,
            CtGambleSite.A,
            hasReliableContact: true,
            liveElapsedSeconds: 10f,
            hasValuableWeapon: false,
            aliveCt: 5);

        Assert.False(decision.Enabled);
        Assert.Equal(CtGambleStage.None, decision.Stage);
    }
}
