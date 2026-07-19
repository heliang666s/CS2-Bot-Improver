using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class UtilityLedgerTests
{
    [Fact]
    public void LedgerRejectsDuplicateSmokeAndTracksSpecialThrows()
    {
        var ledger = new UtilityLedger(new UtilityInventory(Smoke: 1, Flash: 2, He: 1, Molotov: 1));

        Assert.True(ledger.TryConsume(UtilityType.Smoke, UtilitySource.LineupThrow));
        Assert.False(ledger.TryConsume(UtilityType.Smoke, UtilitySource.DefuseSmoke));
        Assert.True(ledger.TryConsume(UtilityType.Flash, UtilitySource.FlashSupport));
        Assert.True(ledger.TryConsume(UtilityType.He, UtilitySource.Retaliation));
        Assert.True(ledger.TryConsume(UtilityType.Molotov, UtilitySource.MolotovEscape));
        Assert.Equal(4, ledger.ConsumedTotal);
    }

    [Fact]
    public void LedgerDoesNotSpawnAfterBudgetIsExhausted()
    {
        var ledger = new UtilityLedger(new UtilityInventory(Smoke: 0, Flash: 0, He: 0, Molotov: 1));

        Assert.True(ledger.TryConsume(UtilityType.Molotov, UtilitySource.MolotovEscape));
        Assert.False(ledger.TryConsume(UtilityType.Molotov, UtilitySource.Retaliation));
    }
}
