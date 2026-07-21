using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CtSavePolicyTests
{
    [Fact]
    public void EcoProbeDoesNotSaveAValuableWeaponInAReasonableTwoVsTwo()
    {
        var decision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            bombPlanted: false,
            aliveCt: 2,
            aliveT: 2,
            hasValuableWeapon: true,
            teamHasDefuser: false,
            hasReliableEnemyContact: false,
            probeCompleted: true,
            liveElapsedSeconds: 20f);

        Assert.Equal(new CtSaveDecision(false, CtSaveReason.None), decision);
    }

    [Theory]
    [InlineData(false, true, 17f)]
    [InlineData(true, false, 20f)]
    [InlineData(false, false, 17f)]
    public void EcoDoesNotSaveBeforeAConfirmedProbeWindow(
        bool hasReliableEnemyContact,
        bool probeCompleted,
        float liveElapsedSeconds)
    {
        var decision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.Eco,
            RoundPhase.Live,
            bombPlanted: false,
            aliveCt: 5,
            aliveT: 5,
            hasValuableWeapon: true,
            teamHasDefuser: false,
            hasReliableEnemyContact,
            probeCompleted,
            liveElapsedSeconds);

        Assert.False(decision.ShouldSave);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    public void PostPlantOutnumberedWithoutTeamDefuserSaves(
        int aliveCt,
        int aliveT)
    {
        var decision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.FullBuy,
            RoundPhase.BombPlanted,
            bombPlanted: true,
            aliveCt,
            aliveT,
            hasValuableWeapon: true,
            teamHasDefuser: false,
            hasReliableEnemyContact: true,
            probeCompleted: false,
            liveElapsedSeconds: 30f);

        Assert.Equal(new CtSaveDecision(true, CtSaveReason.PostPlantOutnumbered), decision);
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(false, 2, 2)]
    [InlineData(false, 2, 3)]
    public void PostPlantDoesNotSaveWhenDefuseOrFightIsStillReasonable(
        bool teamHasDefuser,
        int aliveCt,
        int aliveT)
    {
        var decision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.FullBuy,
            RoundPhase.BombPlanted,
            bombPlanted: true,
            aliveCt,
            aliveT,
            hasValuableWeapon: true,
            teamHasDefuser,
            hasReliableEnemyContact: true,
            probeCompleted: false,
            liveElapsedSeconds: 30f);

        Assert.False(decision.ShouldSave);
    }

    [Fact]
    public void LateManDisadvantageSavesOnEcoOrHalfBuy()
    {
        var decision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            BuyPhase.HalfBuy,
            RoundPhase.Live,
            bombPlanted: false,
            aliveCt: 1,
            aliveT: 3,
            hasValuableWeapon: true,
            teamHasDefuser: false,
            hasReliableEnemyContact: false,
            probeCompleted: false,
            liveElapsedSeconds: 25f);

        Assert.Equal(new CtSaveDecision(true, CtSaveReason.LateManDisadvantage), decision);
    }

    [Theory]
    [InlineData(BotMatchProfile.Arcade)]
    [InlineData(BotMatchProfile.Legacy)]
    public void SavePolicyIsCompetitiveOnly(BotMatchProfile profile)
    {
        var decision = CtSavePolicy.Evaluate(
            profile,
            BuyPhase.Eco,
            RoundPhase.BombPlanted,
            bombPlanted: true,
            aliveCt: 1,
            aliveT: 3,
            hasValuableWeapon: true,
            teamHasDefuser: false,
            hasReliableEnemyContact: false,
            probeCompleted: true,
            liveElapsedSeconds: 30f);

        Assert.False(decision.ShouldSave);
    }
}
