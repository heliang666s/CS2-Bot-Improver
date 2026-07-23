using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CompetitiveTacticalV2Tests
{
    [Theory]
    [InlineData(CtThreatEventKind.Sound, 0, false)]
    [InlineData(CtThreatEventKind.Sound, 1, true)]
    [InlineData(CtThreatEventKind.AttackUtility, 0, false)]
    [InlineData(CtThreatEventKind.AttackUtility, 1, true)]
    [InlineData(CtThreatEventKind.CtDamage, 0, true)]
    [InlineData(CtThreatEventKind.CtDeath, 0, true)]
    [InlineData(CtThreatEventKind.BombFound, 0, true)]
    public void TeamThreatRequiresAnInformedListenerForAmbientEvidence(
        CtThreatEventKind kind,
        int informedListeners,
        bool expected)
    {
        Assert.Equal(
            expected,
            CtThreatAdmissionPolicy.ShouldPromoteToTeam(
                kind,
                informedListeners));
    }

    [Fact]
    public void FourPlusOneUsesDistinctCrossfireAssignments()
    {
        var bots = Enumerable.Range(1, 5)
            .Select(slot => new CtBotSnapshot(slot, true, 0.5f + slot * 0.05f, 0.8f, false))
            .ToArray();
        var plan = CtEcoTacticalPlanner.Plan(new CtEcoPlanningContext(
            MapName: "de_unknown",
            MapKnown: false,
            BuyPhase: BuyPhase.Eco,
            OpponentBuyPhase: BuyPhase.FullBuy,
            Pressure: MatchPressure.Elimination,
            Bots: bots,
            PreviousTactic: null,
            PreviousAttackSite: CtGambleSite.None,
            HasFlash: true,
            HasMolotov: false));

        Assert.Equal(CtEcoTactic.FourPlusOneGamble, plan.Tactic);
        Assert.Equal(5, plan.Assignments.Count);
        Assert.Equal(4, plan.Assignments.Count(assignment => assignment.Role == CtEcoRole.Crossfire));
        Assert.Equal(1, plan.Assignments.Count(assignment => assignment.Role == CtEcoRole.Information));
        Assert.Equal(4, plan.Assignments
            .Where(assignment => assignment.Role == CtEcoRole.Crossfire)
            .Select(assignment => assignment.PointIndex)
            .Distinct()
            .Count());
    }

    [Fact]
    public void ThreatHysteresisKeepsRotationAfterShortSilence()
    {
        var events = new[]
        {
            new CtThreatEvent(CtThreatEventKind.Sound, CtGambleSite.A, 10f, 1),
            new CtThreatEvent(CtThreatEventKind.AttackUtility, CtGambleSite.A, 10.4f, 1),
            new CtThreatEvent(CtThreatEventKind.CtDamage, CtGambleSite.A, 10.6f, 1),
            new CtThreatEvent(CtThreatEventKind.MultipleEnemies, CtGambleSite.A, 10.8f, 3),
        };

        var atPeak = CtThreatPlanner.Evaluate(events, now: 11f, MatchPressure.Normal);
        var afterSilence = CtThreatPlanner.Evaluate(events, now: 14.5f, MatchPressure.Normal);

        Assert.Equal(CtThreatLevel.Confirmed, atPeak.SiteA);
        Assert.True(afterSilence.SiteA >= CtThreatLevel.High);
        Assert.Equal(CtGambleSite.A, afterSilence.ConfirmedSite);
    }

    [Fact]
    public void EliminationPressureRaisesThreatSensitivityAndMidThreatIsRetained()
    {
        var events = new[]
        {
            new CtThreatEvent(CtThreatEventKind.Sound, CtGambleSite.None, 10f, 2),
        };

        var normal = CtThreatPlanner.Evaluate(events, now: 10f, MatchPressure.Normal);
        var elimination = CtThreatPlanner.Evaluate(events, now: 10f, MatchPressure.Elimination);

        Assert.True(normal.Mid > CtThreatLevel.None);
        Assert.True(elimination.Mid > normal.Mid);
    }

    [Fact]
    public void EliminationPressureDoesNotCreateThreatWithoutEvidence()
    {
        var evaluation = CtThreatPlanner.Evaluate(
            Array.Empty<CtThreatEvent>(),
            now: 10f,
            MatchPressure.Elimination);

        Assert.Equal(CtThreatLevel.None, evaluation.SiteA);
        Assert.Equal(CtThreatLevel.None, evaluation.SiteB);
        Assert.Equal(CtThreatLevel.None, evaluation.Mid);
        Assert.Equal(CtGambleSite.None, evaluation.ConfirmedSite);
    }

    [Fact]
    public void PostPlantPlannerKeepsGuardAndInterceptRoles()
    {
        var plan = TPostPlantPlanner.Plan(
            bombSite: CtGambleSite.B,
            bombRemainingSeconds: 25,
            new[]
            {
                new TPostPlantBotSnapshot(1, true, 100, 100f),
                new TPostPlantBotSnapshot(2, true, 100, 800f),
                new TPostPlantBotSnapshot(3, true, 100, 1200f),
            });

        var guard = Assert.Single(plan.Assignments, assignment => assignment.Role == TPostPlantRole.BombGuard);
        var interceptor = Assert.Single(plan.Assignments, assignment => assignment.Role == TPostPlantRole.DefuseInterceptor);
        Assert.Equal(TPostPlantWatchTarget.Bomb, guard.WatchTarget);
        Assert.Equal(TPostPlantWatchTarget.DefuseRoute, interceptor.WatchTarget);
        Assert.True(TPostPlantPlanner.ShouldRetreat(etaSeconds: 20f, safetyBufferSeconds: 3f, bombRemainingSeconds: 22));
        Assert.False(TPostPlantPlanner.ShouldRetreat(etaSeconds: 10f, safetyBufferSeconds: 3f, bombRemainingSeconds: 22));
    }

    [Fact]
    public void KnownAggressivePackUsesDistinctEntryAndSupportPoints()
    {
        var bots = Enumerable.Range(1, 5)
            .Select(slot => new CtBotSnapshot(slot, true, 0.8f, 0.8f, false))
            .ToArray();

        var plan = CtEcoTacticalPlanner.Plan(new CtEcoPlanningContext(
            MapName: "de_mirage",
            MapKnown: true,
            BuyPhase: BuyPhase.Eco,
            OpponentBuyPhase: BuyPhase.Eco,
            Pressure: MatchPressure.Normal,
            Bots: bots,
            PreviousTactic: null,
            PreviousAttackSite: CtGambleSite.None,
            HasFlash: true,
            HasMolotov: false));

        Assert.Equal(CtEcoTactic.AggressivePack, plan.Tactic);
        Assert.True(plan.Assignments.Select(assignment => assignment.PointIndex).Distinct().Count() >= 3);
        Assert.Single(plan.Assignments, assignment => assignment.IsEntry);
        Assert.Single(plan.Assignments, assignment => assignment.KeepBackdoor);
    }

    [Fact]
    public void KnownMapProfilesGiveEntryAndMidDifferentRoutes()
    {
        var entry = new CtEcoAssignment(
            Slot: 1,
            Role: CtEcoRole.PackEntry,
            Site: CtGambleSite.A,
            PointIndex: 0,
            IsEntry: true,
            KeepBackdoor: false);
        var mid = new CtEcoAssignment(
            Slot: 2,
            Role: CtEcoRole.MidControl,
            Site: CtGambleSite.A,
            PointIndex: 2,
            IsEntry: false,
            KeepBackdoor: false);

        var mirageEntry = CtMapProfileCatalog.GetTacticalOffset(
            "de_mirage", entry, CtEcoTactic.AggressivePack);
        var dustEntry = CtMapProfileCatalog.GetTacticalOffset(
            "de_dust2", entry, CtEcoTactic.AggressivePack);
        var mirageMid = CtMapProfileCatalog.GetTacticalOffset(
            "de_mirage", mid, CtEcoTactic.MidTrap);

        Assert.NotEqual(mirageEntry, dustEntry);
        Assert.NotEqual(mirageEntry, mirageMid);
        Assert.True(CtMapProfileCatalog.IsKnown("de_mirage"));
        Assert.False(CtMapProfileCatalog.IsKnown("de_unknown"));
    }

    [Fact]
    public void KnownMapProfilesExposeIndependentRouteAnchors()
    {
        Assert.True(CtMapProfileCatalog.TryGetAnchorProfile(
            "de_mirage",
            out var mirage));
        Assert.True(CtMapProfileCatalog.TryGetAnchorProfile(
            "de_dust2",
            out var dust));

        Assert.NotEqual(mirage.EntryFraction, dust.EntryFraction);
        Assert.NotEqual(mirage.MidBias, dust.MidBias);
        Assert.NotEqual(mirage.CloseFraction, dust.CloseFraction);
        Assert.False(CtMapProfileCatalog.TryGetAnchorProfile(
            "de_unknown",
            out _));
    }

    [Fact]
    public void RotateDecisionCarriesReliableContactSite()
    {
        var contact = new CtContact(2, 10f, ContactConfidence.High, 1f, 2f, 3f)
        {
            Site = CtGambleSite.B,
        };
        var context = new CtTacticalContext(
            new RoundContext(3, 1, 1, 2, false, 0, false, null, 5, 5, RoundPhase.Live)
            {
                CtBuyPhase = BuyPhase.FullBuy,
                LastContact = contact,
            },
            new[] { new CtBotSnapshot(1, true, 0.5f, 0.8f, false) },
            new Dictionary<int, CtRole> { [1] = CtRole.Rotator },
            Array.Empty<CtDeathEvent>(),
            new Dictionary<int, CtContact> { [1] = contact },
            new Dictionary<int, float>(),
            new Dictionary<int, float>(),
            Now: 10.5f);

        var decision = Assert.Single(CtTacticalDecisionPlanner.Plan(context));

        Assert.Equal(CtTacticalState.Rotate, decision.State);
        Assert.Equal(CtGambleSite.B, decision.TargetSite);
    }

    [Fact]
    public void LowThreatStillProducesActionablePreRotateTarget()
    {
        var round = new RoundContext(
            3,
            1,
            1,
            1,
            false,
            0,
            false,
            null,
            5,
            5,
            RoundPhase.Live)
        {
            CtBuyPhase = BuyPhase.FullBuy,
            Threat = new CtThreatEvaluation(
                CtThreatLevel.Low,
                CtThreatLevel.None,
                CtThreatLevel.None,
                CtGambleSite.A,
                10f),
        };
        var context = new CtTacticalContext(
            round,
            new[] { new CtBotSnapshot(1, true, 0.8f, 0.8f, false) },
            new Dictionary<int, CtRole> { [1] = CtRole.Rotator },
            Array.Empty<CtDeathEvent>(),
            new Dictionary<int, CtContact>(),
            new Dictionary<int, float>(),
            new Dictionary<int, float>(),
            10.5f);

        var decision = Assert.Single(CtTacticalDecisionPlanner.Plan(context));

        Assert.Equal(CtTacticalState.Rotate, decision.State);
        Assert.Equal(CtGambleSite.A, decision.TargetSite);
        Assert.True(decision.IsActive);
    }

    [Fact]
    public void StrongerBSiteEvidenceWinsPreRotateTarget()
    {
        var round = new RoundContext(
            3,
            1,
            1,
            1,
            false,
            0,
            false,
            null,
            5,
            5,
            RoundPhase.Live)
        {
            CtBuyPhase = BuyPhase.FullBuy,
            SelectedGambleSite = CtGambleSite.A,
            Threat = new CtThreatEvaluation(
                CtThreatLevel.None,
                CtThreatLevel.Low,
                CtThreatLevel.None,
                CtGambleSite.None,
                10f),
        };
        var context = new CtTacticalContext(
            round,
            new[] { new CtBotSnapshot(1, true, 0.8f, 0.8f, false) },
            new Dictionary<int, CtRole> { [1] = CtRole.Rotator },
            Array.Empty<CtDeathEvent>(),
            new Dictionary<int, CtContact>(),
            new Dictionary<int, float>(),
            new Dictionary<int, float>(),
            10.5f);

        var decision = Assert.Single(CtTacticalDecisionPlanner.Plan(context));

        Assert.Equal(CtTacticalState.Rotate, decision.State);
        Assert.Equal(CtGambleSite.B, decision.TargetSite);
        Assert.True(decision.IsActive);
    }

    [Fact]
    public void PostPlantRetreatUsesDistanceToSafeTargetInsteadOfBombDistance()
    {
        var plan = TPostPlantPlanner.Plan(
            bombSite: CtGambleSite.A,
            bombRemainingSeconds: 10,
            new[]
            {
                new TPostPlantBotSnapshot(
                    1,
                    Alive: true,
                    Health: 100,
                    DistanceToBomb: 2000f,
                    DistanceToSafeTarget: 100f),
            });

        Assert.False(plan.ShouldRetreat);
    }
}
