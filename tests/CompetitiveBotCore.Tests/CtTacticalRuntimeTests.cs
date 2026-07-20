using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CtTacticalRuntimeTests
{
    [Theory]
    [InlineData(5, 2, 2, 1)]
    [InlineData(4, 2, 1, 1)]
    [InlineData(3, 1, 1, 1)]
    [InlineData(2, 1, 1, 0)]
    [InlineData(1, 1, 0, 0)]
    public void AssignCtRolesKeepsLogicalDefenseGroups(int count, int groupACount, int groupBCount, int responderCount)
    {
        var runtime = new CompetitiveTacticalRuntime();
        runtime.Reset(CreateContext());

        var roles = runtime.AssignCtRoles(CreateBots(count));

        Assert.Equal(groupACount, roles.Values.Count(role => role == CtRole.AnchorGroupA));
        Assert.Equal(groupBCount, roles.Values.Count(role => role == CtRole.AnchorGroupB));
        Assert.Equal(responderCount,
            roles.Values.Count(role => role is CtRole.Rotator or CtRole.Information));
    }

    [Fact]
    public void NormalOpeningAllowsAtMostOneInformationBotToProbe()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);

        Assert.DoesNotContain(runtime.DecideAll(14.9f), decision => decision.IsActive);

        var decisions = runtime.DecideAll(16f);

        Assert.Equal(1, decisions.Count(decision => decision.IsActive));
        Assert.Contains(decisions, decision =>
            decision.IsActive && decision.State == CtTacticalState.Information);
        Assert.Equal(1, decisions.Max(decision => decision.ActiveBudget));
    }

    [Fact]
    public void InformationProbeTimesOutAndEntersWithdrawWithCooldown()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.DecideAll(16f);

        var timedOut = runtime.DecideAll(20.1f);
        Assert.Contains(timedOut, decision => decision.State == CtTacticalState.Withdraw);
        Assert.DoesNotContain(timedOut, decision => decision.IsActive);

        var cooldown = runtime.DecideAll(26f);
        Assert.DoesNotContain(cooldown, decision => decision.IsActive);
    }

    [Fact]
    public void EcoProbeWithoutContactTransitionsValuableBotsToSave()
    {
        var runtime = new CompetitiveTacticalRuntime();
        runtime.Reset(CreateContext(BuyPhase.Eco) with
        {
            AliveTeam = 5,
            AliveOpponent = 5,
        });
        runtime.AssignCtRoles(CreateBots(5).Select(bot => bot with
        {
            HasValuableWeapon = true,
        }).ToArray());

        runtime.DecideAll(15f);
        var decisions = runtime.DecideAll(19f);

        Assert.Equal(5, decisions.Count(decision => decision.State == CtTacticalState.Save));
        Assert.DoesNotContain(decisions, decision => decision.IsActive);
    }

    [Fact]
    public void PostPlantTwoVsFourWithoutDefuserTransitionsToSave()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.SetEconomy(BuyPhase.FullBuy, BuyPhase.FullBuy, aliveCt: 2, aliveT: 4);
        runtime.AssignCtRoles(CreateBots(2).Select(bot => bot with
        {
            HasValuableWeapon = true,
        }).ToArray());
        runtime.SetPhase(RoundPhase.BombPlanted);

        var decisions = runtime.DecideAll(20f);

        Assert.Equal(2, decisions.Count(decision => decision.State == CtTacticalState.Save));
    }

    [Fact]
    public void PostPlantWithTeamDefuserKeepsRetakePriority()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.SetEconomy(BuyPhase.FullBuy, BuyPhase.FullBuy, aliveCt: 2, aliveT: 4);
        runtime.SetTeamDefuser(true);
        runtime.SetPhase(RoundPhase.BombPlanted);

        Assert.All(runtime.DecideAll(20f), decision =>
            Assert.Equal(CtTacticalState.Retake, decision.State));
    }

    [Fact]
    public void OneCtDeathOnlyActivatesOneResponder()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.DecideAll(10f);
        int victim = runtime.Roles.First(entry => entry.Value == CtRole.AnchorGroupA).Key;

        runtime.RecordCtDeath(victim, 20f);
        var decisions = runtime.DecideAll(20.5f);

        Assert.Single(decisions, decision => decision.IsActive);
        Assert.Contains(decisions, decision =>
            decision.IsActive && decision.State == CtTacticalState.Rotate);
    }

    [Fact]
    public void TwoDeathsInOneGroupWithinWindowTriggerReinforce()
    {
        var runtime = CreateRuntime(BuyPhase.ForceBuy);
        var victims = runtime.Roles
            .Where(entry => entry.Value == CtRole.AnchorGroupA)
            .Select(entry => entry.Key)
            .ToArray();

        runtime.RecordCtDeath(victims[0], 20f);
        runtime.RecordCtDeath(victims[1], 23.2f);
        var decisions = runtime.DecideAll(23.5f);

        Assert.Contains(decisions, decision => decision.State == CtTacticalState.Reinforce);
        Assert.Equal(2, decisions.Count(decision => decision.IsActive));
    }

    [Fact]
    public void StaleLowConfidenceContactDoesNotTriggerTeamRotation()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.RecordContact(new CtContact(
            SourceSlot: 2,
            RecordedAt: 10f,
            Confidence: ContactConfidence.Low,
            X: 1f,
            Y: 2f,
            Z: 3f));

        var decisions = runtime.DecideAll(15f);

        Assert.DoesNotContain(decisions, decision =>
            decision.IsActive && decision.State is CtTacticalState.Rotate or CtTacticalState.Reinforce);
        Assert.Equal(1, decisions.Max(decision => decision.ActiveBudget));
    }

    [Fact]
    public void HighConfidenceContactDecaysToMediumBeforeItExpires()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.RecordContact(new CtContact(
            SourceSlot: 2,
            RecordedAt: 10f,
            Confidence: ContactConfidence.High,
            X: 1f,
            Y: 2f,
            Z: 3f));

        var decisions = runtime.DecideAll(12.5f);

        Assert.Equal(ContactConfidence.Medium, runtime.Context.LastContact?.Confidence);
        Assert.Contains(decisions, decision => decision.IsActive);
    }

    [Fact]
    public void HighConfidenceContactEventuallyDecaysToLow()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.RecordContact(new CtContact(
            SourceSlot: 7,
            RecordedAt: 10f,
            Confidence: ContactConfidence.High,
            X: 1f,
            Y: 2f,
            Z: 3f));

        runtime.DecideAll(13.5f);

        Assert.Equal(ContactConfidence.Low, runtime.Context.LastContact?.Confidence);
    }

    [Theory]
    [InlineData(BuyPhase.FullBuy, 1)]
    [InlineData(BuyPhase.HalfBuy, 2)]
    [InlineData(BuyPhase.ForceBuy, 2)]
    [InlineData(BuyPhase.Eco, 2)]
    [InlineData(BuyPhase.Save, 0)]
    public void EconomyPhaseControlsActiveCtBudget(BuyPhase phase, int expectedBudget)
    {
        var runtime = CreateRuntime(phase);

        var decisions = runtime.DecideAll(10f);

        Assert.Equal(expectedBudget, decisions.Max(decision => decision.ActiveBudget));
        Assert.InRange(decisions.Count(decision => decision.IsActive), 0, expectedBudget);
    }

    [Fact]
    public void HighAggressionOnlyGetsOneActiveRoleAndDoesNotMakeEveryonePush()
    {
        var runtime = new CompetitiveTacticalRuntime();
        runtime.Reset(CreateContext(BuyPhase.ForceBuy));
        var bots = CreateBots(5).Select(bot => bot with
        {
            Aggression = bot.Slot == 1 ? 1f : 0.2f,
            Teamwork = bot.Slot == 2 ? 1f : 0.5f,
        }).ToArray();
        runtime.AssignCtRoles(bots);

        var decisions = runtime.DecideAll(10f);

        Assert.InRange(decisions.Count(decision => decision.IsActive), 0, 2);
        Assert.Contains(runtime.Roles[1], new[] { CtRole.Information, CtRole.Rotator });
    }

    [Fact]
    public void BombPlantAndSaveHavePriorityOverOpeningBehavior()
    {
        var runtime = CreateRuntime(BuyPhase.FullBuy);
        runtime.SetPhase(RoundPhase.BombPlanted);

        Assert.All(runtime.DecideAll(10f), decision =>
            Assert.Equal(CtTacticalState.Retake, decision.State));

        runtime.SetPhase(RoundPhase.Save);

        Assert.All(runtime.DecideAll(11f), decision =>
            Assert.Equal(CtTacticalState.Save, decision.State));
    }

    [Fact]
    public void EconomicRoundMovesTheCtTeamToTheSelectedGambleSite()
    {
        var runtime = CreateRuntime(BuyPhase.Eco);
        runtime.SetCtGambleSite(CtGambleSite.B);

        var decisions = runtime.DecideAll(8f);

        Assert.Equal(5, decisions.Count(decision => decision.ShouldMoveToGambleSite));
        Assert.All(decisions, decision => Assert.Equal(CtGambleSite.B, decision.TargetSite));
        Assert.DoesNotContain(decisions, decision => decision.IsActive);

        var laterDecisions = runtime.DecideAll(16f);
        Assert.Equal(0, runtime.Context.ActiveProbeCount);
        Assert.All(laterDecisions, decision =>
            Assert.Equal(CtTacticalState.Withdraw, decision.State));
    }

    [Fact]
    public void OtherSiteContactOnlyMovesUpToTwoResponders()
    {
        var runtime = CreateRuntime(BuyPhase.Eco);
        runtime.SetCtGambleSite(CtGambleSite.A);
        runtime.RecordContact(new CtContact(
            SourceSlot: 2,
            RecordedAt: 8f,
            Confidence: ContactConfidence.High,
            X: 1f,
            Y: 2f,
            Z: 3f)
        {
            Site = CtGambleSite.B,
        });

        var decisions = runtime.DecideAll(8.5f);

        int rotatingResponders = decisions.Count(decision =>
            decision.IsActive && decision.TargetSite == CtGambleSite.B);
        Assert.InRange(rotatingResponders, 1, 2);
        Assert.DoesNotContain(decisions, decision =>
            decision.IsActive && decision.TargetSite == CtGambleSite.A);
    }

    [Fact]
    public void NoContactMovesTheGambleTeamToRetreatThenSave()
    {
        var runtime = CreateRuntime(BuyPhase.Eco);
        runtime.SetCtGambleSite(CtGambleSite.A);
        runtime.UpdateBotSnapshots(CreateBots(5).Select(bot => bot with
        {
            HasValuableWeapon = true,
        }).ToArray());

        var withdrawing = runtime.DecideAll(13f);
        Assert.Equal(5, withdrawing.Count(decision => decision.ShouldMoveToRetreat));

        var saving = runtime.DecideAll(18f);
        Assert.Equal(5, saving.Count(decision =>
            decision.State == CtTacticalState.Save && decision.ShouldMoveToRetreat));
    }

    [Fact]
    public void WithdrawAndSaveNeverAllowNativeActiveSearch()
    {
        var runtime = CreateRuntime(BuyPhase.Eco);
        runtime.SetCtGambleSite(CtGambleSite.A);
        runtime.UpdateBotSnapshots(CreateBots(5).Select(bot => bot with
        {
            HasValuableWeapon = true,
        }).ToArray());

        var decisions = runtime.DecideAll(13f);

        Assert.All(decisions, decision =>
            Assert.False(CtTacticalExecutionPolicy.ShouldAllowNativeActive(decision)));
    }

    [Fact]
    public void ContactsAreIsolatedPerBot()
    {
        var runtime = CreateRuntime(BuyPhase.Eco);
        runtime.SetCtGambleSite(CtGambleSite.A);
        int responder = runtime.Roles.First(entry =>
            entry.Value is CtRole.Information or CtRole.Rotator).Key;
        int anchor = runtime.Roles.First(entry =>
            entry.Value is CtRole.AnchorGroupA or CtRole.AnchorGroupB).Key;

        runtime.RecordContactForBot(responder, new CtContact(
            SourceSlot: 20,
            RecordedAt: 8f,
            Confidence: ContactConfidence.High,
            X: 10f,
            Y: 20f,
            Z: 30f)
        {
            Site = CtGambleSite.B,
        });
        runtime.RecordContactForBot(anchor, new CtContact(
            SourceSlot: 21,
            RecordedAt: 8f,
            Confidence: ContactConfidence.High,
            X: 40f,
            Y: 50f,
            Z: 60f)
        {
            Site = CtGambleSite.A,
        });

        var decisions = runtime.DecideAll(8.5f);

        Assert.Contains(decisions, decision =>
            decision.Slot == responder
            && decision.State == CtTacticalState.Rotate
            && decision.TargetSite == CtGambleSite.B
            && decision.IsActive);
        Assert.Contains(decisions, decision =>
            decision.Slot == anchor
            && decision.State == CtTacticalState.Hold
            && decision.TargetSite == CtGambleSite.A
            && !decision.IsActive);
    }

    private static CompetitiveTacticalRuntime CreateRuntime(BuyPhase phase)
    {
        var runtime = new CompetitiveTacticalRuntime();
        runtime.Reset(CreateContext(phase));
        runtime.AssignCtRoles(CreateBots(5));
        return runtime;
    }

    private static RoundContext CreateContext(BuyPhase phase = BuyPhase.FullBuy)
        => new(3, 1, 1, 2, false, 2, false, null, 5, 5, RoundPhase.Live)
        {
            CtBuyPhase = phase,
            OpponentBuyPhase = BuyPhase.FullBuy,
        };

    private static CtBotSnapshot[] CreateBots(int count)
        => Enumerable.Range(1, count)
            .Select(slot => new CtBotSnapshot(
                Slot: slot,
                Alive: true,
                Aggression: slot == 5 ? 0.8f : 0.5f,
                Teamwork: 0.6f,
                IsAwper: slot == 4))
            .ToArray();
}
