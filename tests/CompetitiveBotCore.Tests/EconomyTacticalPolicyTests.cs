using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class EconomyTacticalPolicyTests
{
    [Fact]
    public void StandardBuyUpgradesMp9WhenTheBotCanAffordTheNextTier()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            money: 4000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentPrimary: "weapon_mp9",
            purchaseIntent: PurchaseIntent.Standard);

        Assert.Equal("weapon_m4a1", plan.PrimaryWeapon);
        Assert.True(plan.Tier > BuyPlanner.GetTier(ArmorLevel.Full, "weapon_mp9", null));
    }

    [Fact]
    public void WeaponSwitchQueueCarriesReplacementAndDoesNotAffectCashPlanning()
    {
        BotWeaponSwitchQueue.Enqueue(7, "weapon_m4a1", "weapon_mp9");

        var request = Assert.Single(BotWeaponSwitchQueue.Drain());
        Assert.Equal(7, request.Slot);
        Assert.Equal("weapon_m4a1", request.Weapon);
        Assert.Equal("weapon_mp9", request.ReplaceWeapon);
        Assert.Empty(BotWeaponSwitchQueue.Drain());
    }

    [Fact]
    public void WeaponSwitchQueueCanRetainARequestWhenTheControllerIsTemporarilyUnavailable()
    {
        var request = new WeaponSwitchRequest(8, "weapon_ak47");

        BotWeaponSwitchQueue.Requeue(request);

        Assert.Equal(request, Assert.Single(BotWeaponSwitchQueue.Drain()));
    }

    [Fact]
    public void FailedOldWeaponSelectionRollsBackTheReplacement()
    {
        var settlement = WeaponSwitchSettlementPolicy.Evaluate(
            newWeaponSelected: true,
            oldWeaponSelected: false,
            replacementReselected: false);

        Assert.True(settlement.ShouldRollbackReplacement);
        Assert.False(settlement.ShouldRestoreOriginal);
    }

    [Fact]
    public void FailedReplacementReselectionRollsBackAndRestoresTheOriginal()
    {
        var settlement = WeaponSwitchSettlementPolicy.Evaluate(
            newWeaponSelected: true,
            oldWeaponSelected: true,
            replacementReselected: false);

        Assert.True(settlement.ShouldRollbackReplacement);
        Assert.True(settlement.ShouldRestoreOriginal);
    }

    [Fact]
    public void FailedWeaponSwitchIsRetriedOnlyWithinABoundedWindow()
    {
        var request = new WeaponSwitchRequest(8, "weapon_ak47", "weapon_famas");

        Assert.True(BotWeaponSwitchQueue.TryRequeueFailed(request));
        var retry = Assert.Single(BotWeaponSwitchQueue.Drain());
        Assert.Equal(1, retry.Attempt);
        Assert.True(BotWeaponSwitchQueue.TryRequeueFailed(retry));
        retry = Assert.Single(BotWeaponSwitchQueue.Drain());
        Assert.Equal(2, retry.Attempt);
        Assert.True(BotWeaponSwitchQueue.TryRequeueFailed(retry));
        retry = Assert.Single(BotWeaponSwitchQueue.Drain());
        Assert.Equal(3, retry.Attempt);
        Assert.False(BotWeaponSwitchQueue.TryRequeueFailed(retry));
        Assert.Empty(BotWeaponSwitchQueue.Drain());
    }

    [Fact]
    public void SaveIntentKeepsAnExistingFamas()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Save,
            money: 5000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentPrimary: "weapon_famas",
            purchaseIntent: PurchaseIntent.Save);

        Assert.Equal("weapon_famas", plan.PrimaryWeapon);
        Assert.Equal(BuyPhase.Save, plan.Phase);
    }

    [Fact]
    public void EcoWithAFeasibleDonorTransferDoesNotSelectSave()
    {
        var intent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.Eco,
            TeamScore: 8,
            OpponentScore: 10,
            RoundsPlayed: 18,
            MaxRounds: 24,
            CurrentTeamPower: 8,
            NextRoundTeamPower: 7)
        {
            HasFeasibleTeamTransfer = true,
        });

        Assert.NotEqual(PurchaseIntent.Save, intent);
        Assert.Contains(intent, new[] { PurchaseIntent.Standard, PurchaseIntent.AllIn });
    }

    [Fact]
    public void EcoUsesSaveWhenTheWorstCaseNextRoundForecastIsStronger()
    {
        var currentPlan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);
        var rewards = new EconomyRewardRules(0, 1f, 1400, 0, 0, 0, 0, 0, 0);
        var participants = new[]
        {
            new ScenarioParticipant(1, TeamSide.CounterTerrorist, 3000, currentPlan, 0, false, false, currentPlan.Tier),
            new ScenarioParticipant(2, TeamSide.CounterTerrorist, 3000, currentPlan, 0, false, false, currentPlan.Tier),
        };

        int nextRoundPower = NextRoundEconomyPolicy.EstimateWorstCaseCombatPower(
            participants,
            rewards,
            consecutiveLosses: 0,
            teamPlayerCount: 2,
            opponentPlayerCount: 2);
        var intent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.Eco,
            TeamScore: 8,
            OpponentScore: 8,
            RoundsPlayed: 10,
            MaxRounds: 24,
            CurrentTeamPower: 0,
            NextRoundTeamPower: nextRoundPower));

        Assert.True(nextRoundPower > 2);
        Assert.Equal(PurchaseIntent.Save, intent);
    }

    [Fact]
    public void HalfEndIsLastRoundAndThePreviousRoundKnowsTheBoundary()
    {
        var lastRoundIntent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.FullBuy,
            TeamScore: 6,
            OpponentScore: 5,
            RoundsPlayed: 11,
            MaxRounds: 24,
            CurrentTeamPower: 8,
            NextRoundTeamPower: 8)
        {
            IsLastRoundOfHalf = true,
        });
        var previousRoundIntent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.Eco,
            TeamScore: 5,
            OpponentScore: 5,
            RoundsPlayed: 10,
            MaxRounds: 24,
            CurrentTeamPower: 4,
            NextRoundTeamPower: 4)
        {
            IsNextRoundLastRoundOfHalf = true,
        });

        Assert.Equal(PurchaseIntent.LastRound, lastRoundIntent);
        Assert.Equal(PurchaseIntent.Save, previousRoundIntent);
    }

    [Fact]
    public void DpPrioritizesTheNextHalfEndRoundForecastWhenTheBoundaryIsOneRoundAway()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var strongCurrentPlan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_famas",
            null,
            BuysHelmet: true,
            BuysDefuser: false,
            Array.Empty<string>(),
            EstimatedCost: 3000)
        {
            Tier = 2,
            Intent = PurchaseIntent.Standard,
        };
        var savePlan = new PlayerBuyPlan(
            BuyPhase.Eco,
            ArmorLevel.None,
            null,
            null,
            BuysHelmet: false,
            BuysDefuser: false,
            Array.Empty<string>(),
            EstimatedCost: 0)
        {
            Tier = 0,
            Intent = PurchaseIntent.Save,
        };
        var candidates = new[] { strongCurrentPlan, savePlan };
        var context = new PurchaseIntentContext(
            BuyPhase.FullBuy,
            TeamScore: 5,
            OpponentScore: 5,
            RoundsPlayed: 10,
            MaxRounds: 24,
            CurrentTeamPower: 4,
            NextRoundTeamPower: 4)
        {
            IsNextRoundLastRoundOfHalf = true,
        };

        var plan = TeamDpPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(1, true, false, 4000, candidates, null, 0),
                new TeamDpMember(2, true, false, 4000, candidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0,
            purchaseIntent: PurchaseIntent.Standard,
            purchaseContext: context);

        Assert.All(plan.BotPlans.Values, selected => Assert.Equal(0, selected.EstimatedCost));
    }

    [Fact]
    public void OvertimeFirstRoundIsNotASecondPistolRound()
    {
        Assert.True(RoundSchedule.IsOvertimeFirstRound(24, 24));

        var intent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.Pistol,
            TeamScore: 12,
            OpponentScore: 12,
            RoundsPlayed: 24,
            MaxRounds: 24,
            CurrentTeamPower: 0,
            NextRoundTeamPower: 0)
        {
            IsOvertimeFirstRound = true,
        });

        Assert.Equal(PurchaseIntent.Standard, intent);
    }

    [Theory]
    [InlineData("weapon_ssg08")]
    [InlineData("weapon_aug")]
    public void StandardBuyDoesNotDowngradeAnUnsupportedButValuablePrimary(string currentPrimary)
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.ForceBuy,
            money: 2000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary,
            purchaseIntent: PurchaseIntent.Standard);

        Assert.Equal(currentPrimary, plan.PrimaryWeapon);
    }

    [Fact]
    public void AllInCanLeaveAnUnevenTeamWhenTheRoundIsWorthMoreThanTheEconomy()
    {
        var intent = PurchaseIntentPolicy.Evaluate(new PurchaseIntentContext(
            BuyPhase.HalfBuy,
            TeamScore: 10,
            OpponentScore: 12,
            RoundsPlayed: 22,
            MaxRounds: 24,
            CurrentTeamPower: 8,
            NextRoundTeamPower: 3)
        {
            IsMatchPoint = true,
        });

        Assert.Equal(PurchaseIntent.AllIn, intent);
    }

    [Fact]
    public void HumanObservationCannotBecomeATransferRecipient()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 8000,
            designatedAwper: false,
            opponentEcoLikely: false);

        var transfers = TeamTransferPlanner.BuildTransfers(
        [
            new TransferParticipant(1, IsBot: true, Money: 8000, plan, CurrentPrimary: null),
            new TransferParticipant(2, IsBot: false, Money: 1000, plan, CurrentPrimary: null),
        ]);

        Assert.Empty(transfers);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    public void TransferPreflightCancelsWhenTheRecipientAlreadyHasTheTargetSlot(
        bool recipientHasPrimary,
        bool recipientHasTransferItem,
        bool expectedShouldTransfer,
        bool expectedKeepExistingWeapon)
    {
        var decision = TransferPreflightPolicy.Evaluate(
            recipientHasPrimary,
            recipientHasTransferItem);

        Assert.Equal(expectedShouldTransfer, decision.ShouldTransfer);
        Assert.Equal(expectedKeepExistingWeapon, decision.KeepExistingWeapon);
    }

    [Fact]
    public void RichBotsCanBuyForTwoPoorBotsInsteadOfReturningFourZeroBuys()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var richCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            5000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_m4a1",
            purchaseIntent: PurchaseIntent.Standard);
        var poorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            1000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Standard);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            [
                new TeamDpMember(1, true, false, 5000, richCandidates, "weapon_m4a1", 9),
                new TeamDpMember(2, true, false, 5000, richCandidates, "weapon_m4a1", 9),
                new TeamDpMember(3, true, false, 1000, poorCandidates, null, 0),
                new TeamDpMember(4, true, false, 1000, poorCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0,
            purchaseIntent: PurchaseIntent.Standard);

        Assert.Equal(2, plan.Transfers.Count);
        Assert.All([3, 4], slot => Assert.Equal("weapon_m4a1", plan.BotPlans[slot].PrimaryWeapon));
    }

    [Fact]
    public void StandardBuyCanUseAWeaponlessRichBotAsTheDonor()
    {
        var rewards = new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0);
        var richCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            5000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Standard);
        var poorCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            1000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Standard);

        var plan = TeamDpPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            [
                new TeamDpMember(1, true, false, 5000, richCandidates, null, 0),
                new TeamDpMember(2, true, false, 1000, poorCandidates, null, 0),
            ],
            currentMinTier: 0,
            rewards,
            consecutiveLosses: 0,
            purchaseIntent: PurchaseIntent.Standard);

        Assert.Contains(plan.Transfers, transfer => transfer.Donor == 1 && transfer.Recipient == 2);
        Assert.NotNull(plan.BotPlans[2].PrimaryWeapon);
        Assert.True(plan.BotPlans[2].Tier > 0);
    }

    [Fact]
    public void TwoVsTwoWithAPathAndDefuserRetakesEvenWhenTheClockIsLow()
    {
        var decision = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            AliveCt: 2,
            AliveT: 2,
            BombPlanted: true,
            BombSecondsRemaining: 18,
            CtDefusers: 1,
            CtWeaponTier: 8,
            TWeaponTier: 7,
            CtUtility: 3,
            TUtility: 1,
            TeamScore: 10,
            OpponentScore: 11,
            IsMatchPoint: true,
            PathViable: true,
            BotWeaponValue: 5));

        Assert.True(decision.ShouldRetake);
    }

    [Fact]
    public void OneVsThreeWithoutDefuserAndWithoutPathSavesOnlyBecauseRetakeIsUnrealistic()
    {
        var decision = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            AliveCt: 1,
            AliveT: 3,
            BombPlanted: true,
            BombSecondsRemaining: 8,
            CtDefusers: 0,
            CtWeaponTier: 9,
            TWeaponTier: 7,
            CtUtility: 0,
            TUtility: 3,
            TeamScore: 8,
            OpponentScore: 12,
            IsMatchPoint: false,
            PathViable: false,
            BotWeaponValue: 10));

        Assert.False(decision.ShouldRetake);
        Assert.Contains("unrealistic", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostPlantLateClockProducesMovementToTheBombSite()
    {
        var decision = TPostPlantPolicy.Evaluate(new TPostPlantContext(
            BombPlanted: true,
            BombSecondsRemaining: 9,
            AliveT: 2,
            AliveCt: 2,
            PathViable: true,
            CtDefusers: 1,
            CtRetakePathViable: true));

        Assert.Equal(TPostPlantAction.MoveToSite, decision.Action);
    }

    [Fact]
    public void PostPlantRetreatsWhenNoCtCanCompleteTheDefuse()
    {
        var decision = TPostPlantPolicy.Evaluate(new TPostPlantContext(
            BombPlanted: true,
            BombSecondsRemaining: 4,
            AliveT: 2,
            AliveCt: 1,
            PathViable: true,
            CtDefusers: 0,
            CtRetakePathViable: true));

        Assert.Equal(TPostPlantAction.RetreatFromBomb, decision.Action);
        Assert.Contains("defuse-impossible", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostPlantRetreatTargetsAreDistributedByBotPosition()
    {
        var targets = TPostPlantRetreatPlanner.AssignTargets(
            [
                new TPostPlantRetreatParticipant(1, new RetreatPosition(100f, 0f, 0f)),
                new TPostPlantRetreatParticipant(2, new RetreatPosition(-100f, 0f, 0f)),
                new TPostPlantRetreatParticipant(3, new RetreatPosition(0f, 100f, 0f)),
            ],
            [
                new RetreatPosition(1200f, 0f, 0f),
                new RetreatPosition(-1200f, 0f, 0f),
                new RetreatPosition(0f, 1200f, 0f),
            ],
            new RetreatPosition(0f, 0f, 0f));

        Assert.Equal(3, targets.Count);
        Assert.Equal(3, targets.Values.Distinct().Count());
        Assert.NotEqual(targets[1], targets[2]);
        Assert.NotEqual(targets[2], targets[3]);
    }

    [Fact]
    public void PostPlantRepathDoesNotSelectACombatTarget()
    {
        Assert.Equal(
            TPostPlantTargetKind.None,
            TPostPlantExecutionPolicy.TargetKind(TPostPlantAction.Repath));
    }

    [Fact]
    public void LowerHealthBotGetsTheFartherRetreatAnchorFirst()
    {
        var targets = TPostPlantRetreatPlanner.AssignTargets(
            [
                new TPostPlantRetreatParticipant(
                    1,
                    new RetreatPosition(100f, 0f, 0f),
                    Health: 100),
                new TPostPlantRetreatParticipant(
                    2,
                    new RetreatPosition(-100f, 0f, 0f),
                    Health: 20),
            ],
            [
                new RetreatPosition(1200f, 0f, 0f),
                new RetreatPosition(0f, 1000f, 0f),
            ],
            new RetreatPosition(0f, 0f, 0f));

        Assert.Equal(new RetreatPosition(1200f, 0f, 0f), targets[2]);
        Assert.Equal(new RetreatPosition(0f, 1000f, 0f), targets[1]);
    }

    [Fact]
    public void UnknownBombTimerDoesNotPretendTheBombIsAlreadyAboutToExplode()
    {
        var decision = TPostPlantPolicy.Evaluate(new TPostPlantContext(
            BombPlanted: true,
            BombSecondsRemaining: 0,
            AliveT: 3,
            AliveCt: 2,
            PathViable: true,
            CtDefusers: 0,
            CtRetakePathViable: true,
            BombTimerKnown: false));

        Assert.Equal(TPostPlantAction.Hold, decision.Action);
    }

    [Fact]
    public void TwoBotsCanMakeDifferentSaveDecisionsFromTheirWeaponValue()
    {
        var lowValue = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            2, 2, true, 10, 0, 7, 7, 1, 2, 10, 10, false, true, 2));
        var awpValue = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            2, 2, true, 10, 0, 7, 7, 1, 2, 10, 10, false, true, 10));

        Assert.True(lowValue.ShouldRetake);
        Assert.False(awpValue.ShouldRetake);
    }

    [Fact]
    public void UnknownBombTimerDoesNotMakeAPlayableTwoVsTwoRetakeImpossible()
    {
        var decision = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            AliveCt: 2,
            AliveT: 2,
            BombPlanted: true,
            BombSecondsRemaining: 0,
            CtDefusers: 1,
            CtWeaponTier: 7,
            TWeaponTier: 7,
            CtUtility: 1,
            TUtility: 1,
            TeamScore: 10,
            OpponentScore: 11,
            IsMatchPoint: false,
            PathViable: true,
            BotWeaponValue: 2)
        {
            BombTimerKnown = false,
        });

        Assert.True(decision.ShouldRetake);
        Assert.DoesNotContain("unrealistic", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNavConnectivityDoesNotMakeAPlayableRetakeImpossible()
    {
        var decision = RetakeDecisionPolicy.Evaluate(new RetakeContext(
            AliveCt: 2,
            AliveT: 2,
            BombPlanted: true,
            BombSecondsRemaining: 18,
            CtDefusers: 1,
            CtWeaponTier: 7,
            TWeaponTier: 7,
            CtUtility: 1,
            TUtility: 1,
            TeamScore: 10,
            OpponentScore: 11,
            IsMatchPoint: false,
            PathViable: false,
            BotWeaponValue: 2)
        {
            PathKnown = false,
        });

        Assert.True(decision.ShouldRetake);
    }

    [Theory]
    [InlineData(12, 10, 20, false, true)]
    [InlineData(12, 9, 21, false, true)]
    [InlineData(12, 11, 23, false, true)]
    [InlineData(11, 11, 22, false, false)]
    [InlineData(12, 12, 24, true, false)]
    [InlineData(13, 12, 24, true, false)]
    [InlineData(15, 12, 27, true, true)]
    [InlineData(15, 14, 29, true, true)]
    public void MatchPointAndLastRoundUseTheScoreNotTheHardcodedTwelveTwelveBoundary(
        int teamScore,
        int opponentScore,
        int roundsPlayed,
        bool expectedOvertime,
        bool expectedMatchPoint)
    {
        Assert.Equal(expectedMatchPoint,
            RoundSchedule.IsMatchPoint(teamScore, opponentScore, roundsPlayed, 24));
        Assert.Equal(expectedOvertime,
            RoundSchedule.IsOvertimeRound(roundsPlayed, 24));
    }

    [Fact]
    public void RegulationLastRoundRequiresARealMatchPoint()
    {
        Assert.True(RoundSchedule.IsLastRegulationRound(12, 11, 23, 24));
        Assert.False(RoundSchedule.IsLastRegulationRound(11, 11, 23, 24));
        Assert.False(RoundSchedule.IsLastRegulationRound(12, 12, 23, 24));
        Assert.False(RoundSchedule.IsLastRegulationRound(12, 12, 24, 24));
    }

    [Fact]
    public void OvertimeFirstRoundIsNotMatchPointAfterAOneRoundLead()
    {
        Assert.False(RoundSchedule.IsMatchPoint(13, 12, 25, 24, 6));
    }

    [Fact]
    public void ReadsAuthoritativeScoresFromTheGameRulesShape()
    {
        var rules = new FakeScoreRules
        {
            TeamTScore = 12,
            TeamCTScore = 11,
        };

        Assert.True(RoundScoreReader.TryRead(rules, out var score));
        Assert.Equal(12, score.Terrorist);
        Assert.Equal(11, score.CounterTerrorist);
    }

    [Fact]
    public void BombTimerReaderSupportsAbsoluteBlowTime()
    {
        var bomb = new FakeBomb { BlowTime = 109.2f };

        Assert.True(BombTimerPolicy.TryResolveSeconds(bomb, 100f, out var seconds));
        Assert.Equal(10, seconds);
    }

    [Fact]
    public void BombTimerReaderReportsUnknownSchemaInsteadOfInventingFortySeconds()
    {
        Assert.False(BombTimerPolicy.TryResolveSeconds(new object(), 100f, out _));
    }

    [Fact]
    public void Mp9AndFamasDoNotCountAsHighValueSaveWeapons()
    {
        Assert.False(WeaponValuePolicy.IsHighValue("weapon_mp9"));
        Assert.False(WeaponValuePolicy.IsHighValue("weapon_famas"));
        Assert.True(WeaponValuePolicy.IsHighValue("weapon_awp"));
    }

    [Fact]
    public void ImpossibleDpBudgetReturnsAnEmptyPlanInsteadOfThrowing()
    {
        var expensive = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            true,
            false,
            Array.Empty<string>(),
            4000)
        {
            Tier = 8,
        };

        var plan = TeamDpPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            [
                new TeamDpMember(1, true, false, 0, [expensive], null, 0),
                new TeamDpMember(2, true, false, 0, [expensive], null, 0),
            ],
            currentMinTier: 0,
            new EconomyRewardRules(0, 1f, 0, 0, 0, 0, 0, 0, 0),
            consecutiveLosses: 0,
            purchaseIntent: PurchaseIntent.AllIn);

        Assert.Empty(plan.BotPlans);
        Assert.Equal("no-affordable-team-plan", plan.Reason);
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    public void WeaponGrantSettlementRequiresObservedInventory(
        bool callAccepted,
        bool hasWeapon,
        bool confirmed,
        bool shouldRefund)
    {
        var settlement = WeaponGrantPolicy.Evaluate(callAccepted, hasWeapon);

        Assert.Equal(confirmed, settlement.Confirmed);
        Assert.Equal(shouldRefund, settlement.ShouldRefund);
    }

    [Fact]
    public void TransferGrantIsRefundedWhenAnotherPrimaryWinsTheRace()
    {
        var settlement = WeaponGrantPolicy.Evaluate(
            callAccepted: true,
            hasWeapon: true,
            hasConflictingPrimary: true);

        Assert.False(settlement.Confirmed);
        Assert.True(settlement.ShouldRefund);
    }

    private sealed class FakeScoreRules
    {
        public int TeamTScore { get; init; }
        public int TeamCTScore { get; init; }
    }

    private sealed class FakeBomb
    {
        public float BlowTime { get; init; }
    }
}
