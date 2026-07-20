using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalPolicyTests
{
    [Theory]
    [InlineData(true, false, RoundPhase.Freeze)]
    [InlineData(false, true, RoundPhase.BombPlanted)]
    [InlineData(false, false, RoundPhase.Live)]
    public void ResolveRoundPhaseUsesGameRulesState(
        bool freezePeriod,
        bool bombPlanted,
        RoundPhase expected)
    {
        Assert.Equal(expected,
            CompetitiveTacticalPolicy.ResolveRoundPhase(freezePeriod, bombPlanted));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void TakeoverBotsAreNotEligibleForCompetitiveTactics(
        bool isBot,
        bool takenOver,
        bool expected)
    {
        Assert.Equal(expected,
            CompetitiveTacticalPolicy.ShouldTrackCtBot(isBot, takenOver));
    }

    [Theory]
    [InlineData(BotMatchProfile.Competitive, true, false, true)]
    [InlineData(BotMatchProfile.Competitive, true, true, false)]
    [InlineData(BotMatchProfile.Competitive, false, false, false)]
    [InlineData(BotMatchProfile.Arcade, true, false, false)]
    public void TacticalDeathRotationOnlyTracksUncontrolledCompetitiveBots(
        BotMatchProfile profile,
        bool isBot,
        bool takenOver,
        bool expected)
    {
        Assert.Equal(expected,
            CompetitiveTacticalPolicy.ShouldRecordCtDeath(profile, isBot, takenOver));
    }

    [Fact]
    public void EconomyPhaseCacheKeepsThePhaseCapturedBeforePurchase()
    {
        var cache = new TacticalEconomyPhaseCache();
        cache.Capture(
            new TeamEconomySnapshot(
                TeamSide.CounterTerrorist,
                new[] { 5000, 5000, 5000, 5000, 5000 },
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false),
            new TeamEconomySnapshot(
                TeamSide.Terrorist,
                new[] { 5000, 5000, 5000, 5000, 5000 },
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false));

        cache.Capture(
            new TeamEconomySnapshot(
                TeamSide.CounterTerrorist,
                new[] { 500, 500, 500, 500, 500 },
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false),
            new TeamEconomySnapshot(
                TeamSide.Terrorist,
                new[] { 500, 500, 500, 500, 500 },
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false));

        Assert.Equal(BuyPhase.FullBuy, cache.CtPhase);
        Assert.Equal(BuyPhase.FullBuy, cache.OpponentPhase);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(12, true)]
    [InlineData(24, true)]
    [InlineData(1, false)]
    [InlineData(11, false)]
    [InlineData(23, false)]
    [InlineData(25, false)]
    public void RoundScheduleIdentifiesPistolRoundsWithoutUsingMoney(
        int roundsPlayed,
        bool expected)
    {
        Assert.Equal(expected,
            RoundSchedule.IsFirstRoundOfHalf(roundsPlayed, maxRounds: 24, overtimeMaxRounds: 6));
    }
}
