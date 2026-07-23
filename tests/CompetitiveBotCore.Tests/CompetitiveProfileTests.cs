using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CompetitiveProfileTests
{
    [Fact]
    public void CompetitiveProfile_DowngradesUnlimitedNadesToNormal()
    {
        Assert.Equal("normal", ProfilePolicy.NormalizeNadeMode(BotMatchProfile.Competitive, "max"));
        Assert.Equal("more", ProfilePolicy.NormalizeNadeMode(BotMatchProfile.Arcade, "more"));
    }

    [Fact]
    public void FFAOrScoutsSignal_UsesArcadeOnlyWhenProfileWasDefaultCompetitive()
    {
        Assert.Equal(BotMatchProfile.Arcade,
            ProfilePolicy.Resolve(BotMatchProfile.Competitive, entertainmentMode: true));
        Assert.Equal(BotMatchProfile.Legacy,
            ProfilePolicy.Resolve(BotMatchProfile.Legacy, entertainmentMode: true));
    }

    [Fact]
    public void NonCompetitiveProfilesDoNotRequireInventoryLedger()
    {
        var method = typeof(ProfilePolicy).GetMethod(
            "ShouldEnforceUtilityInventory",
            [typeof(BotMatchProfile)]);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, [BotMatchProfile.Competitive])!);
        Assert.False((bool)method.Invoke(null, [BotMatchProfile.Arcade])!);
        Assert.False((bool)method.Invoke(null, [BotMatchProfile.Legacy])!);
    }

    [Theory]
    [InlineData(BotMatchProfile.Competitive, "weapon_glock", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_usp_silencer", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_hkp2000", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_p250", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_fiveseven", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_tec9", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_cz75a", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_deagle", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_revolver", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_elite", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_ak47", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_m4a1", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_mp9", false)]
    [InlineData(BotMatchProfile.Competitive, "weapon_awp", false)]
    [InlineData(BotMatchProfile.Arcade, "weapon_glock", false)]
    [InlineData(BotMatchProfile.Legacy, "weapon_deagle", false)]
    [InlineData(BotMatchProfile.Competitive, null, false)]
    public void CompetitivePistolsUseHeadFirstAimOnlyInCompetitiveProfile(
        BotMatchProfile profile,
        string? weaponName,
        bool expected)
    {
        Assert.Equal(expected,
            AimWeaponPolicy.ShouldUseHeadFirstInMixed(profile, weaponName));
    }

    [Fact]
    public void OvertimeOpeningIsNotACompetitivePistolRound()
    {
        Assert.False(AimWeaponPolicy.IsCompetitivePistolRound(
            roundsPlayed: 24,
            maxRounds: 24,
            overtimeMaxRounds: 6));
        Assert.True(AimWeaponPolicy.IsCompetitivePistolRound(
            roundsPlayed: 12,
            maxRounds: 24,
            overtimeMaxRounds: 6));
    }

    [Fact]
    public void PistolAimAdjustmentAddsBoundedHumanization()
    {
        var adjustment = AimWeaponPolicy.GetPistolAimAdjustment(
            BotMatchProfile.Competitive,
            BuyPhase.Pistol,
            botSlot: 1,
            targetId: 2,
            now: 10f);

        Assert.InRange(adjustment.ReactionDelaySeconds, 0.03f, 0.12f);
        Assert.InRange(adjustment.TargetJitterUnits, 1.5f, 6f);
    }

    [Theory]
    [InlineData(BotMatchProfile.Competitive, false, GrenadeExecutionRoute.Cancel, false)]
    [InlineData(BotMatchProfile.Competitive, true, GrenadeExecutionRoute.RealInventoryThrow, true)]
    [InlineData(BotMatchProfile.Arcade, false, GrenadeExecutionRoute.GeneratedProjectile, false)]
    [InlineData(BotMatchProfile.Legacy, false, GrenadeExecutionRoute.GeneratedProjectile, false)]
    public void GrenadeExecutionNeverFakesCompetitiveThrows(
        BotMatchProfile profile,
        bool realThrowApiAvailable,
        GrenadeExecutionRoute expectedRoute,
        bool expectedInventorySettlement)
    {
        var decision = GrenadeExecutionPolicy.Resolve(profile, realThrowApiAvailable);

        Assert.Equal(expectedRoute, decision.Route);
        Assert.Equal(expectedInventorySettlement, decision.SettleInventoryOnlyAfterEngineConfirmation);
    }

    [Fact]
    public void FailedRealThrowDoesNotSettleInventoryOrCooldown()
    {
        Assert.False(GrenadeExecutionPolicy.ShouldSettleInventory(
            GrenadeExecutionRoute.RealInventoryThrow,
            engineConfirmedThrow: false));
        Assert.True(GrenadeExecutionPolicy.ShouldSettleInventory(
            GrenadeExecutionRoute.RealInventoryThrow,
            engineConfirmedThrow: true));
    }

    [Theory]
    [InlineData(UtilitySource.DefuseSmoke, true)]
    [InlineData(UtilitySource.FlashSupport, true)]
    [InlineData(UtilitySource.PlantSmoke, true)]
    [InlineData(UtilitySource.LineupThrow, false)]
    [InlineData(UtilitySource.MolotovEscape, false)]
    [InlineData(UtilitySource.Retaliation, false)]
    public void BombActionUtilitySourcesPreserveTheActiveAction(
        UtilitySource source,
        bool expected)
    {
        Assert.Equal(
            expected,
            GrenadeExecutionPolicy.ShouldPreserveBombAction(source));
    }

    [Theory]
    [InlineData(false, false, 1, 0, true)]
    [InlineData(false, false, 2, 1, true)]
    [InlineData(false, false, 1, 1, false)]
    [InlineData(false, true, 1, 0, false)]
    [InlineData(true, false, 1, 0, false)]
    public void ConsumedUnconfirmedRealThrowRemainsPending(
        bool engineConfirmed,
        bool projectileDetected,
        int inventoryBefore,
        int inventoryAfter,
        bool expected)
    {
        Assert.Equal(
            expected,
            GrenadeExecutionPolicy.ShouldKeepRealThrowPending(
                engineConfirmed,
                projectileDetected,
                inventoryBefore,
                inventoryAfter));
    }

    [Fact]
    public void PistolAimDifficultyIsStrictlyMonotonic()
    {
        var easy = AimDifficultyPolicy.Resolve(AimDifficultyTier.Easy);
        var medium = AimDifficultyPolicy.Resolve(AimDifficultyTier.Medium);
        var high = AimDifficultyPolicy.Resolve(AimDifficultyTier.High);

        Assert.True(easy.ReactionDelaySeconds > medium.ReactionDelaySeconds);
        Assert.True(medium.ReactionDelaySeconds > high.ReactionDelaySeconds);
        Assert.True(easy.InitialAimErrorUnits > medium.InitialAimErrorUnits);
        Assert.True(medium.InitialAimErrorUnits > high.InitialAimErrorUnits);
        Assert.True(easy.FocusDelaySeconds > medium.FocusDelaySeconds);
        Assert.True(medium.FocusDelaySeconds > high.FocusDelaySeconds);
        Assert.True(easy.BurstStability < medium.BurstStability);
        Assert.True(medium.BurstStability < high.BurstStability);
    }

    [Fact]
    public void EasyPistolRecoveryAddsRealFireCadenceWhileHighDoesNot()
    {
        Assert.True(
            AimDifficultyPolicy.PistolFireRecoverySeconds(AimDifficultyTier.Easy, 0)
            > AimDifficultyPolicy.PistolFireRecoverySeconds(AimDifficultyTier.Medium, 0));
        Assert.Equal(
            0f,
            AimDifficultyPolicy.PistolFireRecoverySeconds(AimDifficultyTier.High, 0));
    }

    [Theory]
    [InlineData(TeamSide.Terrorist, "molotov", "weapon_molotov", 46)]
    [InlineData(TeamSide.CounterTerrorist, "molotov", "weapon_incgrenade", 48)]
    [InlineData(TeamSide.CounterTerrorist, "incgrenade", "weapon_incgrenade", 48)]
    public void GrenadeInventoryPolicyUsesTheTeamsRealIncendiary(
        TeamSide side,
        string grenadeType,
        string expectedWeapon,
        int expectedDefinition)
    {
        Assert.Equal(
            expectedWeapon,
            GrenadeInventoryPolicy.ExpectedWeapon(grenadeType, side));
        Assert.Equal(
            expectedDefinition,
            GrenadeInventoryPolicy.GetItemDefinition(grenadeType, side));
    }

    [Fact]
    public void GrenadeEventSettlementRequiresThePendingWeaponType()
    {
        Assert.True(GrenadeInventoryPolicy.MatchesThrownWeapon(
            "smoke",
            "weapon_smokegrenade",
            TeamSide.Terrorist));
        Assert.False(GrenadeInventoryPolicy.MatchesThrownWeapon(
            "smoke",
            "weapon_flashbang",
            TeamSide.Terrorist));
        Assert.True(GrenadeInventoryPolicy.MatchesThrownWeapon(
            "molotov",
            "weapon_incgrenade",
            TeamSide.CounterTerrorist));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 0, false)]
    public void FailedRealThrowRestoresOnlyAnActuallyConsumedItem(
        int inventoryBefore,
        int inventoryAfter,
        bool expectedRestore)
    {
        Assert.Equal(
            expectedRestore,
            GrenadeInventoryPolicy.ShouldRestoreAfterFailedRealThrow(
                inventoryBefore,
                inventoryAfter));
    }

    [Fact]
    public void RoundTransitionNeverRestoresAConsumedRealThrow()
    {
        Assert.False(
            GrenadeInventoryPolicy.ShouldRestoreAfterFailedRealThrow(
                inventoryBefore: 1,
                inventoryAfter: 0,
                allowPhysicalRestore: false));
    }

    [Fact]
    public void BuyExecutionGenerationRejectsAnOlderScheduledPlan()
    {
        Assert.True(BuyExecutionPolicy.IsLatestGeneration(7, 7));
        Assert.False(BuyExecutionPolicy.IsLatestGeneration(7, 8));
    }

    [Fact]
    public void InventoryInvalidationAdvancesExecutionGeneration()
    {
        long invalidated = BuyExecutionPolicy.InvalidateGeneration(7);

        Assert.Equal(8, invalidated);
        Assert.False(BuyExecutionPolicy.IsLatestGeneration(7, invalidated));
    }

    [Fact]
    public void FinalCalibrationStartsOnlyOnePollingChain()
    {
        Assert.True(BuyPollingPolicy.ShouldStartPolling(false));
        Assert.False(BuyPollingPolicy.ShouldStartPolling(true));
        Assert.True(BuyPollingPolicy.ShouldContinuePolling(
            finalWindowStillOpen: true,
            attempt: 63));
        Assert.False(BuyPollingPolicy.ShouldContinuePolling(
            finalWindowStillOpen: false,
            attempt: 8));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 7, true)]
    [InlineData(false, 8, false)]
    [InlineData(true, 0, false)]
    public void HotReloadScoreRecoveryUsesBoundedRetries(
        bool recovered,
        int attempt,
        bool expectedRetry)
    {
        Assert.Equal(
            expectedRetry,
            ScoreRecoveryPolicy.ShouldRetry(recovered, attempt));
    }

    [Fact]
    public void CompetitiveRealThrowRetargetsTheEngineProjectileToRecordedTrajectory()
    {
        Assert.True(GrenadeTrajectoryPolicy.TryCreate(
            originX: 10f,
            originY: 20f,
            originZ: 30f,
            velocityX: 400f,
            velocityY: -250f,
            velocityZ: 300f,
            out GrenadeThrowTrajectory trajectory));

        Assert.True(GrenadeTrajectoryPolicy.ShouldRetargetEngineProjectile(
            BotMatchProfile.Competitive,
            engineConfirmedThrow: true,
            trajectory));
        Assert.Equal(10f, trajectory.OriginX);
        Assert.Equal(-250f, trajectory.VelocityY);
    }

    [Fact]
    public void InvalidOrUnconfirmedRealThrowNeverRetargetsAProjectile()
    {
        Assert.False(GrenadeTrajectoryPolicy.TryCreate(
            originX: float.NaN,
            originY: 20f,
            originZ: 30f,
            velocityX: 0f,
            velocityY: 0f,
            velocityZ: 0f,
            out _));
        Assert.False(GrenadeTrajectoryPolicy.ShouldRetargetEngineProjectile(
            BotMatchProfile.Competitive,
            engineConfirmedThrow: false,
            trajectory: null));
    }

    [Theory]
    [InlineData(true, 0.10f, 0.25f, false)]
    [InlineData(false, 0.10f, 0.25f, false)]
    [InlineData(false, 0.25f, 0.25f, true)]
    [InlineData(false, 0.30f, 0.25f, true)]
    public void InventoryCalibrationScansOnlyWhenDirtyOrFallbackAuditIsDue(
        bool inventoryDirty,
        float now,
        float nextAuditAt,
        bool expected)
    {
        Assert.Equal(
            expected,
            InventoryCalibrationPolicy.ShouldScan(
                finalWindowOpen: true,
                inventoryDirty,
                now,
                nextAuditAt));
        Assert.False(InventoryCalibrationPolicy.ShouldScan(
            finalWindowOpen: false,
            inventoryDirty: true,
            now,
            nextAuditAt));
    }

    [Fact]
    public void DirtyInventoryUsesTheRoundAuditMergeWindow()
    {
        Assert.False(InventoryCalibrationPolicy.ShouldScan(
            finalWindowOpen: true,
            inventoryDirty: true,
            now: 0.10f,
            nextAuditAt: 0.25f));
        Assert.True(InventoryCalibrationPolicy.ShouldScan(
            finalWindowOpen: true,
            inventoryDirty: true,
            now: 0.25f,
            nextAuditAt: 0.25f));
    }

    [Fact]
    public void EconomyClassificationUsesRoundStartMoneyInsteadOfCurrentMoney()
    {
        Assert.Equal(
            new[] { 16000, 16000 },
            EconomySnapshotPolicy.ForPhaseClassification(
                roundStartMoney: new[] { 16000, 16000 },
                currentMoney: new[] { 500, 500 }));
        Assert.Equal(
            new[] { 500, 500 },
            EconomySnapshotPolicy.ForPhaseClassification(
                roundStartMoney: Array.Empty<int>(),
                currentMoney: new[] { 500, 500 }));
    }

    [Fact]
    public void SuppressedNativeBuyKeepsOpeningMoneyForAwperCandidates()
    {
        int planningMoney = CompetitiveBuyBudgetPolicy.GetPlanningMoney(
            roundStartMoney: 8000,
            currentMoney: 4000,
            nativeBuySuppressed: true);

        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            planningMoney,
            designatedAwper: true,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_famas",
            currentHasHelmet: true,
            purchaseIntent: PurchaseIntent.Standard);

        Assert.Equal(8000, planningMoney);
        var executableWithOpeningBalance = CompetitiveBuyBudgetPolicy
            .FilterExecutablePlans(candidates, planningMoney);
        var executableAfterNativePurchase = CompetitiveBuyBudgetPolicy
            .FilterExecutablePlans(candidates, currentMoney: 4000);

        Assert.Contains(
            executableWithOpeningBalance,
            plan => plan.PrimaryWeapon == "weapon_awp");
        Assert.DoesNotContain(
            executableAfterNativePurchase,
            plan => plan.PrimaryWeapon == "weapon_awp");
        Assert.Equal(4000, CompetitiveBuyBudgetPolicy.GetPlanningMoney(
            roundStartMoney: 8000,
            currentMoney: 4000,
            nativeBuySuppressed: false));
    }

    [Fact]
    public void NativeBuySuppressionRetainsOriginalLimitAcrossRepeatedEntry()
    {
        var firstEntry = CompetitiveBuySuppressionPolicy.Enter(
            CompetitiveBuySuppressionState.Inactive,
            currentEcoLimit: 2800);
        var repeatedEntry = CompetitiveBuySuppressionPolicy.Enter(
            firstEntry,
            currentEcoLimit: 16001);

        Assert.True(firstEntry.IsActive);
        Assert.Equal(2800, repeatedEntry.OriginalEcoLimit);
        Assert.Equal(2800, CompetitiveBuySuppressionPolicy.RestoreValue(repeatedEntry));
        Assert.False(CompetitiveBuySuppressionPolicy.Exit(repeatedEntry).IsActive);
    }

    [Fact]
    public void ExecutionWindowRejectsPlansArrivingAfterLock()
    {
        Assert.True(FreezeBuyPolicy.ShouldAcceptPlan(
            FreezeBuyStage.FinalCalibration,
            finalCalibrationCompleted: true));
        Assert.False(FreezeBuyPolicy.ShouldAcceptPlan(
            FreezeBuyStage.Execution,
            finalCalibrationCompleted: true));
        Assert.False(FreezeBuyPolicy.ShouldAcceptPlan(
            FreezeBuyStage.PostFreezeCheck,
            finalCalibrationCompleted: true));
        Assert.True(FreezeBuyPolicy.HasExecutionBudget(
            now: 4.2f,
            freezeEndAt: 5f,
            requiredSeconds: FreezeBuyPolicy.MaximumDispatchDelaySeconds));
        Assert.False(FreezeBuyPolicy.HasExecutionBudget(
            now: 4.8f,
            freezeEndAt: 5f,
            requiredSeconds: FreezeBuyPolicy.MaximumDispatchDelaySeconds));
    }

    [Fact]
    public void ExecutionWindowStartsBeforeFreezeEnds()
    {
        Assert.Equal(4.2f, FreezeBuyPolicy.ExecutionAt(0f, 5f), precision: 3);
        Assert.Equal(5f, FreezeBuyPolicy.EndAt(0f, 5f), precision: 3);
    }

    [Fact]
    public void PostPlantRosterCountsHumansForDecisionButNotActionOwnership()
    {
        var summary = PostPlantRosterPolicy.Summarize(new[]
        {
            new PostPlantPlayerSnapshot(TeamSide.Terrorist, Alive: true, HasDefuser: false),
            new PostPlantPlayerSnapshot(TeamSide.CounterTerrorist, Alive: true, HasDefuser: false),
            new PostPlantPlayerSnapshot(TeamSide.CounterTerrorist, Alive: true, HasDefuser: true),
            new PostPlantPlayerSnapshot(TeamSide.CounterTerrorist, Alive: false, HasDefuser: true),
        });

        Assert.Equal(1, summary.AliveTerrorists);
        Assert.Equal(2, summary.AliveCounterTerrorists);
        Assert.Equal(1, summary.CtDefusers);
    }
}
