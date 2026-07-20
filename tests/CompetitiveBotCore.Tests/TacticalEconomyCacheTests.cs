using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalEconomyCacheTests
{
    [Fact]
    public void RestoredPrePurchasePhaseCannotBeOverwrittenByPostPurchaseBalances()
    {
        var cache = new TacticalEconomyPhaseCache();

        cache.Restore(BuyPhase.ForceBuy, BuyPhase.FullBuy);
        cache.Capture(
            new TeamEconomySnapshot(
                TeamSide.CounterTerrorist,
                [900, 900, 900, 900, 900],
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false),
            new TeamEconomySnapshot(
                TeamSide.Terrorist,
                [900, 900, 900, 900, 900],
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false));

        Assert.Equal(BuyPhase.ForceBuy, cache.CtPhase);
        Assert.Equal(BuyPhase.FullBuy, cache.OpponentPhase);
    }
}
