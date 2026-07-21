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
    public void PistolRoundWithStartingMoneyBuysArmorInsteadOfReturningEmptyPlan()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 800,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.True(plan.BuysArmor);
        Assert.Null(plan.PrimaryWeapon);
        Assert.Equal(BuyPlanner.KevlarPrice, plan.EstimatedCost);
    }

    [Fact]
    public void PistolRoundAndEcoUseDifferentPlans()
    {
        var pistolPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Pistol,
            money: 800,
            designatedAwper: false,
            opponentEcoLikely: false);
        var ecoPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            money: 800,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.NotEqual(pistolPlan.EstimatedCost, ecoPlan.EstimatedCost);
        Assert.True(pistolPlan.BuysArmor);
        Assert.False(ecoPlan.BuysArmor);
    }

    [Fact]
    public void PistolRoundNeverBuysAPrimaryWeapon()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 5000,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Null(plan.PrimaryWeapon);
        Assert.True(plan.BuysArmor);
    }

    [Fact]
    public void PistolRoundWithOnlyFiveHundredBuysTStrongPistolWithoutArmor()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 500,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.None, plan.ArmorLevel);
        Assert.Equal("weapon_tec9", plan.SecondaryWeapon);
        Assert.Equal(500, plan.EstimatedCost);
    }

    [Fact]
    public void PistolRoundCanCombineFullArmorAndStrongPistol()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 1500,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.Full, plan.ArmorLevel);
        Assert.True(plan.BuysHelmet);
        Assert.Equal("weapon_tec9", plan.SecondaryWeapon);
        Assert.Equal(1500, plan.EstimatedCost);
    }

    [Fact]
    public void CounterTerroristPistolUsesFiveSevenAsStrongFallback()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Pistol,
            money: 500,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.None, plan.ArmorLevel);
        Assert.Equal("weapon_fiveseven", plan.SecondaryWeapon);
    }

    [Fact]
    public void ArmorUpgradeCostUsesOnlyMissingArmorIncrement()
    {
        Assert.Equal(650, BuyPlanner.GetArmorUpgradeCost(ArmorLevel.None, ArmorLevel.Half));
        Assert.Equal(1000, BuyPlanner.GetArmorUpgradeCost(ArmorLevel.None, ArmorLevel.Full));
        Assert.Equal(350, BuyPlanner.GetArmorUpgradeCost(ArmorLevel.Half, ArmorLevel.Full));
        Assert.Equal(0, BuyPlanner.GetArmorUpgradeCost(ArmorLevel.Full, ArmorLevel.Full));
    }

    [Fact]
    public void NonPistolBuyFallsBackToStrongSecondaryWhenNoPrimaryFits()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.ForceBuy,
            money: 1200,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Null(plan.PrimaryWeapon);
        Assert.Equal(ArmorLevel.Half, plan.ArmorLevel);
        Assert.Equal("weapon_tec9", plan.SecondaryWeapon);
        Assert.Equal(1150, plan.EstimatedCost);
    }

    [Fact]
    public void ThreeThousandTChoosesFullArmorGalilOverHalfArmorAk()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.Full, plan.ArmorLevel);
        Assert.Equal("weapon_galilar", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_ak47", plan.PrimaryWeapon);
        Assert.True(plan.EstimatedCost <= 3000);
    }

    [Fact]
    public void TwentyFiveHundredTChoosesHalfArmorGalilOverFullArmorMac10()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.HalfBuy,
            money: 2500,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.Half, plan.ArmorLevel);
        Assert.Equal("weapon_galilar", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_mac10", plan.PrimaryWeapon);
    }

    [Fact]
    public void CounterTerroristPackageOrderingPrefersFullFamasWhenM4DoesNotFit()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Equal(ArmorLevel.Full, plan.ArmorLevel);
        Assert.Equal("weapon_famas", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_mp9", plan.PrimaryWeapon);
    }

    [Fact]
    public void TeamTierPlannerKeepsOrdinaryBotsWithinOneTier()
    {
        var richCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var poorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var teamPlan = TeamTierPlanner.Balance(
        [
            new TeamBuyMember(1, IsBot: true, IsAwper: false, richCandidates),
            new TeamBuyMember(2, IsBot: true, IsAwper: false, poorCandidates),
        ]);

        Assert.True(teamPlan.IsBalanced);
        Assert.InRange(teamPlan.MaxTier - teamPlan.MinTier, 0, 1);
        Assert.NotEqual(0, teamPlan.BotPlans[1].Tier);
        Assert.NotEqual(9, teamPlan.BotPlans[2].Tier);
    }

    [Fact]
    public void AwperCanRemainSpecialTierWithoutDraggingOrdinaryBotsDown()
    {
        var awpCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 10000,
            designatedAwper: true,
            opponentEcoLikely: true);
        var rifleCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var teamPlan = TeamTierPlanner.Balance(
        [
            new TeamBuyMember(1, IsBot: true, IsAwper: true, awpCandidates),
            new TeamBuyMember(2, IsBot: true, IsAwper: false, rifleCandidates),
        ]);

        Assert.Equal(9, teamPlan.BotPlans[1].Tier);
        Assert.InRange(teamPlan.BotPlans[2].Tier, 6, 7);
    }

    [Fact]
    public void HumanObservationIsReadOnlyAndNotAssignedABotPlan()
    {
        var humanPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var botCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var teamPlan = TeamTierPlanner.Balance(
        [
            new TeamBuyMember(10, IsBot: false, IsAwper: false, [humanPlan]),
            new TeamBuyMember(11, IsBot: true, IsAwper: false, botCandidates),
        ]);

        Assert.DoesNotContain(10, teamPlan.BotPlans.Keys);
        Assert.Equal(humanPlan.Tier, teamPlan.HumanObservations[10].Tier);
    }

    [Fact]
    public void RichBotCanFundPoorBotsMissingTheirPlannedPrimary()
    {
        var donorPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var recipientPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var transfers = TeamTransferPlanner.BuildTransfers(
        [
            new TransferParticipant(1, IsBot: true, Money: 8000, donorPlan, CurrentPrimary: null),
            new TransferParticipant(2, IsBot: true, Money: 3000, recipientPlan, CurrentPrimary: null),
        ]);

        var transfer = Assert.Single(transfers);
        Assert.Equal(1, transfer.Donor);
        Assert.Equal(2, transfer.Recipient);
        Assert.Equal("weapon_galilar", transfer.Item);
        Assert.Equal(1800, transfer.Cost);
    }

    [Fact]
    public void ExistingRecipientPrimaryPreventsDuplicateTransfer()
    {
        var donorPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var recipientPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var transfers = TeamTransferPlanner.BuildTransfers(
        [
            new TransferParticipant(1, IsBot: true, Money: 8000, donorPlan, CurrentPrimary: null),
            new TransferParticipant(2, IsBot: true, Money: 3000, recipientPlan, CurrentPrimary: "weapon_galilar"),
        ]);

        Assert.Empty(transfers);
    }

    [Fact]
    public void DonorWithoutSurplusDoesNotCreateTransfer()
    {
        var donorPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3700,
            designatedAwper: false,
            opponentEcoLikely: false);
        var recipientPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var transfers = TeamTransferPlanner.BuildTransfers(
        [
            new TransferParticipant(1, IsBot: true, Money: 3700, donorPlan, CurrentPrimary: null),
            new TransferParticipant(2, IsBot: true, Money: 3000, recipientPlan, CurrentPrimary: null),
        ]);

        Assert.Empty(transfers);
    }

    [Fact]
    public void EconomyLedgerRecordsKillsBombOutcomeAndSavedPrimary()
    {
        var ledger = new RoundEconomyLedger();
        ledger.ResetRound(
        [
            new EconomyPlayerSeed(1, TeamSide.Terrorist, IsBot: true),
            new EconomyPlayerSeed(2, TeamSide.CounterTerrorist, IsBot: true),
        ]);

        ledger.RecordKill(1);
        ledger.RecordDeath(2);
        ledger.RecordBombPlanted();
        ledger.RecordBombExploded();
        ledger.CompleteRound(TeamSide.Terrorist, aliveSlots: [1], savedPrimarySlots: [1]);

        var t = ledger.Players[1];
        var ct = ledger.Players[2];
        Assert.Equal(1, t.Kills);
        Assert.False(t.Died);
        Assert.True(t.SavedPrimary);
        Assert.True(ct.Died);
        Assert.Equal(TeamSide.Terrorist, ledger.Winner);
        Assert.True(ledger.BombPlanted);
        Assert.True(ledger.BombExploded);
        Assert.Equal(1, ledger.ConsecutiveLosses[TeamSide.CounterTerrorist]);
    }

    [Fact]
    public void ResetRoundClearsFactsAndPreservesLossStreak()
    {
        var ledger = new RoundEconomyLedger();
        ledger.ResetRound([new EconomyPlayerSeed(1, TeamSide.Terrorist, IsBot: true)]);
        ledger.CompleteRound(TeamSide.CounterTerrorist, aliveSlots: [], savedPrimarySlots: []);
        ledger.ResetRound([new EconomyPlayerSeed(1, TeamSide.Terrorist, IsBot: true)]);

        Assert.Equal(0, ledger.Players[1].Kills);
        Assert.Equal(1, ledger.ConsecutiveLosses[TeamSide.Terrorist]);
        Assert.False(ledger.BombPlanted);
        Assert.Null(ledger.Winner);
    }

    [Fact]
    public void NextRoundPredictorProducesBestAndWorstScenarioBounds()
    {
        var currentPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var rewards = new EconomyRewardRules(
            KillReward: 300,
            KillRewardFactor: 1f,
            LoserBonus: 1400,
            LoserBonusConsecutive: 500,
            EliminationTeamReward: 3250,
            TerroristWinBomb: 3500,
            CtDefuseWin: 3500,
            PlayerBombPlanted: 300,
            PlayerBombDefused: 300);

        var predictions = NextRoundPredictor.Predict(
        [
            new ScenarioParticipant(
                1,
                TeamSide.Terrorist,
                MoneyAfterPurchase: 200,
                currentPlan,
                Kills: 2,
                IsPlanter: true,
                IsDefuser: false,
                SavedTier: currentPlan.Tier),
        ],
        rewards,
        consecutiveLosses: 2);

        Assert.Equal(6, predictions.Count);
        var worst = predictions.Single(prediction => prediction.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead);
        var plantedLoss = predictions.Single(prediction => prediction.Scenario == NextRoundScenario.LossWithPlant);
        var best = predictions.Single(prediction => prediction.Scenario == NextRoundScenario.Best);
        Assert.True(plantedLoss.NextMoney[1] > worst.NextMoney[1]);
        Assert.True(best.NextMoney[1] > plantedLoss.NextMoney[1]);
        Assert.True(best.MinTier >= worst.MinTier);
    }

    [Fact]
    public void SavedPrimaryCanRaiseNextRoundFloorAndWorstCaseIsCheckedSeparately()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var rewards = new EconomyRewardRules(300, 1f, 1400, 500, 3250, 3500, 3500, 300, 300);

        var predictions = NextRoundPredictor.Predict(
        [new ScenarioParticipant(1, TeamSide.Terrorist, 0, plan, 0, false, false, plan.Tier)],
        rewards,
        consecutiveLosses: 0);

        var best = predictions.Single(prediction => prediction.Scenario == NextRoundScenario.Best);
        var worst = predictions.Single(prediction => prediction.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead);
        Assert.Equal(plan.Tier, best.MinTier);
        Assert.InRange(worst.MinTier, 0, plan.Tier - 1);
        Assert.True(NextRoundEconomyPolicy.MeetsWorstCaseFloor(worst, currentMinTier: 3));
        Assert.False(NextRoundEconomyPolicy.MeetsWorstCaseFloor(worst, currentMinTier: 4));
    }

    [Fact]
    public void TeamDpMaintainsTierBalanceAndEmitsTransferPlan()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var richCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var poorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(1, IsBot: true, IsAwper: false, 8000, richCandidates, null, 0),
                new TeamDpMember(2, IsBot: true, IsAwper: false, 3000, poorCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0);

        Assert.True(plan.IsBalanced);
        Assert.InRange(plan.MaxTier - plan.MinTier, 0, 1);
        Assert.NotEmpty(plan.Forecasts);
        Assert.Contains(plan.Transfers, transfer => transfer.Recipient == 2);
    }

    [Fact]
    public void TeamDpAllowsAwperExceptionWhileBalancingOrdinaryBots()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var awpCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 10000,
            designatedAwper: true,
            opponentEcoLikely: true);
        var rifleCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(1, IsBot: true, IsAwper: true, 10000, awpCandidates, null, 0),
                new TeamDpMember(2, IsBot: true, IsAwper: false, 3000, rifleCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0);

        Assert.Equal(9, plan.BotPlans[1].Tier);
        Assert.InRange(plan.BotPlans[2].Tier, 6, 7);
        Assert.True(plan.IsBalanced);
    }

    [Fact]
    public void PistolTeamWithDifferentCashUsesBalancedFallbackInsteadOfCrashing()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var richCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 1500,
            designatedAwper: false,
            opponentEcoLikely: false);
        var poorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 800,
            designatedAwper: false,
            opponentEcoLikely: false);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            [
                new TeamDpMember(1, IsBot: true, IsAwper: false, 1500, richCandidates, null, 0),
                new TeamDpMember(2, IsBot: true, IsAwper: false, 800, poorCandidates, null, 0),
            ],
            currentMinTier: 9,
            rewards,
            consecutiveLosses: 0);

        Assert.NotNull(plan);
        Assert.True(plan.IsBalanced);
        Assert.Contains("fallback", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingHalfArmorAndPrimaryOnlyPayMissingArmorAndPreservePrimary()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.HalfBuy,
            money: 1000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Half,
            currentPrimary: "weapon_galilar",
            currentSecondary: "weapon_glock",
            currentHasHelmet: false,
            currentHasDefuser: false);

        Assert.Equal(ArmorLevel.Full, plan.ArmorLevel);
        Assert.Equal("weapon_galilar", plan.PrimaryWeapon);
        Assert.Equal("weapon_glock", plan.SecondaryWeapon);
        Assert.True(plan.BuysHelmet);
        Assert.Equal(BuyPlanner.HelmetUpgradePrice, plan.EstimatedCost);
    }

    [Theory]
    [InlineData(ArmorLevel.None, 1000)]
    [InlineData(ArmorLevel.Half, 350)]
    public void AssaultSuitPurchasePriceMatchesMissingArmor(ArmorLevel currentArmor, int expected)
    {
        Assert.Equal(expected, BuyPlanner.GetAssaultSuitPurchaseCost(currentArmor));
    }

    [Fact]
    public void PistolRoundReplacesStartingDefaultPistolWithUpgrade()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Pistol,
            money: 1500,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentSecondary: "weapon_glock");

        Assert.Equal("weapon_tec9", plan.SecondaryWeapon);
        Assert.Equal(1500, plan.EstimatedCost);
    }

    [Fact]
    public void UtilityPlanRepresentsTargetInventoryAfterExistingItems()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.LastRound,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_ak47",
            currentSecondary: "weapon_glock",
            currentHasHelmet: true,
            currentUtility: new Dictionary<string, int> { ["flash"] = 1 });

        Assert.Equal(1, plan.Utility.Count(item => item == "smoke"));
        Assert.Equal(2, plan.Utility.Count(item => item == "flash"));
        Assert.Equal(1, plan.Utility.Count(item => item == "he"));
        Assert.Equal(1, plan.Utility.Count(item => item == "molotov"));
    }

    [Fact]
    public void EconomyLedgerIgnoresFriendlyFireKills()
    {
        var ledger = new RoundEconomyLedger();
        ledger.ResetRound(
        [
            new EconomyPlayerSeed(1, TeamSide.Terrorist, IsBot: true),
            new EconomyPlayerSeed(2, TeamSide.Terrorist, IsBot: true),
            new EconomyPlayerSeed(3, TeamSide.CounterTerrorist, IsBot: true),
        ]);

        var recordKill = typeof(RoundEconomyLedger).GetMethod(
            "RecordKill",
            [typeof(int), typeof(int)]);
        Assert.NotNull(recordKill);

        recordKill!.Invoke(ledger, new object[] { 1, 2 });
        recordKill.Invoke(ledger, new object[] { 1, 3 });

        Assert.Equal(1, ledger.Players[1].Kills);
    }

    [Fact]
    public void EconomyRewardRulesExposeNativeTeamBonuses()
    {
        var properties = typeof(EconomyRewardRules)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet();

        Assert.Contains("ShorthandedTeamBonus", properties);
        Assert.Contains("PlantedBombDefusedBonus", properties);
    }

    [Fact]
    public void NextRoundForecastIncludesShorthandedAndPlantedBombBonuses()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Eco,
            money: 0,
            designatedAwper: false,
            opponentEcoLikely: false);
        var rewards = new EconomyRewardRules(
            KillReward: 0,
            KillRewardFactor: 1f,
            LoserBonus: 100,
            LoserBonusConsecutive: 0,
            EliminationTeamReward: 0,
            TerroristWinBomb: 0,
            CtDefuseWin: 0,
            PlayerBombPlanted: 0,
            PlayerBombDefused: 0,
            ShorthandedTeamBonus: 1000,
            PlantedBombDefusedBonus: 600);

        var predictions = NextRoundPredictor.Predict(
        [new ScenarioParticipant(1, TeamSide.Terrorist, 0, plan, 0, true, false, 0)],
            rewards,
            consecutiveLosses: 0,
            teamPlayerCount: 1,
            opponentPlayerCount: 2);

        var plantedLoss = predictions.Single(item => item.Scenario == NextRoundScenario.LossWithPlant);
        Assert.Equal(1700, plantedLoss.NextMoney[1]);
    }

    [Fact]
    public void StandardBuyUpgradesAWeakerCarriedPrimaryWhenItFits()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.None,
            currentPrimary: "weapon_mac10",
            currentSecondary: "weapon_glock");

        Assert.Equal("weapon_galilar", plan.PrimaryWeapon);
        Assert.NotEqual("weapon_ak47", plan.PrimaryWeapon);
        Assert.True(plan.EstimatedCost <= 3000);
    }

    [Fact]
    public void TeamDpForecastAccountsForPrimaryTransferCashMovement()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var donorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var recipientCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(1, IsBot: true, IsAwper: false, 8000, donorCandidates, null, 0),
                new TeamDpMember(2, IsBot: true, IsAwper: false, 3000, recipientCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0);

        var transfer = Assert.Single(plan.Transfers, item => item.Recipient == 2);
        var worst = plan.Forecasts.Single(item =>
            item.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead);

        Assert.True(worst.NextMoney[1] < 8000 - plan.BotPlans[1].EstimatedCost);
        Assert.True(worst.NextMoney[2] > 3000 - plan.BotPlans[2].EstimatedCost);
        Assert.Equal("weapon_galilar", transfer.Item);
    }

    [Fact]
    public void TransferPlannerNeverMakesOneBotBothDonorAndRecipient()
    {
        var richPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var poorPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var transfers = TeamTransferPlanner.BuildTransfers(
        [
            new TransferParticipant(1, IsBot: true, Money: 8000, richPlan, CurrentPrimary: null),
            new TransferParticipant(2, IsBot: true, Money: 8000, richPlan, CurrentPrimary: null),
            new TransferParticipant(3, IsBot: true, Money: 3000, poorPlan, CurrentPrimary: null),
        ]);

        var donors = transfers.Select(transfer => transfer.Donor).ToHashSet();
        var recipients = transfers.Select(transfer => transfer.Recipient).ToHashSet();
        Assert.Empty(donors.Intersect(recipients));
    }

    [Fact]
    public void HumanTierIsUsedAsSoftTeamBalanceSignalWithoutBuyingForHuman()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var humanPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 0,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_mac10",
            currentSecondary: "weapon_glock",
            currentHasHelmet: true);
        var botCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(10, IsBot: false, IsAwper: false, 0, [humanPlan], null, 0),
                new TeamDpMember(11, IsBot: true, IsAwper: false, 3000, botCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0);

        Assert.DoesNotContain(10, plan.BotPlans.Keys);
        Assert.Equal(5, plan.HumanObservations[10].Tier);
        Assert.Equal(0, plan.HumanTierPenalty);
        Assert.InRange(plan.BotPlans[11].Tier, 5, 6);
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

    [Fact]
    public void OvertimeFirstRoundWithStartingMoneyIsAFullBuy()
    {
        var snapshot = new TeamEconomySnapshot(
            TeamSide.CounterTerrorist,
            [10000, 10000, 10000, 10000],
            IsPistolRound: true,
            IsLastRound: false,
            ForceBuySignal: false,
            OpponentEcoLikely: false)
        {
            IsOvertimeFirstRound = true,
        };

        Assert.Equal(BuyPhase.FullBuy, BuyPlanner.Classify(snapshot));
    }
}
