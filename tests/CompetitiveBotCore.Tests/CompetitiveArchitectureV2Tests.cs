using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CompetitiveArchitectureV2Tests
{
    [Theory]
    [InlineData(9, 12, MatchPressure.Elimination)]
    [InlineData(10, 12, MatchPressure.Elimination)]
    [InlineData(11, 12, MatchPressure.Elimination)]
    [InlineData(12, 9, MatchPressure.Clinch)]
    public void RegulationPressureUsesBothSidesOfTheCurrentRound(
        int teamScore,
        int opponentScore,
        MatchPressure expected)
    {
        var result = MatchPressurePolicy.Evaluate(
            teamScore,
            opponentScore,
            roundsPlayed: 21,
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6),
            economySwing: false);

        Assert.Equal(expected, result.Level);
        Assert.True(result.TeamCanEndMatch || result.OpponentCanEndMatch);
    }

    [Fact]
    public void OvertimeThirteenToTwelveIsNotAClinch()
    {
        var result = MatchPressurePolicy.Evaluate(
            teamScore: 13,
            opponentScore: 12,
            roundsPlayed: 25,
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6),
            economySwing: false);

        Assert.Equal(MatchPressure.Normal, result.Level);
        Assert.False(result.TeamCanEndMatch);
        Assert.False(result.OpponentCanEndMatch);
    }

    [Fact]
    public void BoundedPlannerTrimsCandidatesAndFrontier()
    {
        var members = Enumerable.Range(1, 5)
            .Select(slot => new TeamPlanningMember(
                Slot: slot,
                IsBot: true,
                IsAwper: false,
                Money: 6000,
                Candidates: Enumerable.Range(0, 20)
                    .Select(index => new PlayerBuyPlan(
                        BuyPhase.FullBuy,
                        ArmorLevel.Full,
                        index == 0 ? "weapon_m4a1" : "weapon_famas",
                        null,
                        BuysHelmet: true,
                        BuysDefuser: false,
                        Utility: Array.Empty<string>(),
                        EstimatedCost: 2000 + index)
                    {
                        Tier = 5 + (index % 4),
                    })
                    .ToArray(),
                CurrentPrimary: null,
                SavedTier: 0))
            .ToArray();

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            purchaseIntent: PurchaseIntent.Standard,
            options: new BoundedPlannerOptions(MaxCandidatesPerBot: 6, MaxFrontierStates: 256));

        Assert.True(result.Diagnostics.MaxCandidatesPerBot <= 6);
        Assert.True(result.Diagnostics.MaxFrontierStates <= 256);
        Assert.NotEmpty(result.Plan.BotPlans);
        Assert.Equal(5, result.Plan.BotPlans.Count);
        Assert.True(result.Diagnostics.TimedOut || result.Plan.Reason == "bounded-frontier");
    }

    [Fact]
    public void NoDonorPlanDoesNotCreateAnArmorOnlyRifleLoadout()
    {
        var plan = new PlayerBuyPlan(
            BuyPhase.HalfBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            "weapon_hkp2000",
            BuysHelmet: true,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: 3900)
        {
            Tier = 9,
        };

        Assert.True(BuyPlanner.IsCombatLegal(plan));
        Assert.False(BuyPlanner.IsCombatLegal(plan with
        {
            ArmorLevel = ArmorLevel.None,
        }));

        Assert.True(BuyPlanner.IsCombatLegal(plan with
        {
            ArmorLevel = ArmorLevel.None,
            PrimaryWeapon = "weapon_awp",
        }));
    }

    [Fact]
    public void FullArmorGalilBeatsHalfArmorAkInTeamPlanning()
    {
        var candidates = new PlayerBuyPlan[]
        {
            new(
                BuyPhase.FullBuy,
                ArmorLevel.Full,
                "weapon_galilar",
                null,
                BuysHelmet: true,
                BuysDefuser: false,
                Utility: Array.Empty<string>(),
                EstimatedCost: 2800)
            {
                Tier = BuyPlanner.GetTier(
                    ArmorLevel.Full,
                    "weapon_galilar",
                    null),
            },
            new(
                BuyPhase.FullBuy,
                ArmorLevel.Half,
                "weapon_ak47",
                null,
                BuysHelmet: false,
                BuysDefuser: false,
                Utility: Array.Empty<string>(),
                EstimatedCost: 3350)
            {
                Tier = BuyPlanner.GetTier(
                    ArmorLevel.Half,
                    "weapon_ak47",
                    null),
            },
        };
        var members = new[]
        {
            new TeamPlanningMember(
                Slot: 1,
                IsBot: true,
                IsAwper: false,
                Money: 4000,
                Candidates: candidates,
                CurrentPrimary: null,
                SavedTier: 0),
        };

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        Assert.Equal("weapon_galilar", result.BotPlans[1].PrimaryWeapon);
        Assert.Equal(ArmorLevel.Full, result.BotPlans[1].ArmorLevel);
    }

    [Fact]
    public void SameBudgetCandidateChainPrefersRifleOverSmg()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 3000,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Contains(candidates, plan => plan.PrimaryWeapon == "weapon_galilar");
        Assert.Contains(candidates, plan => plan.PrimaryWeapon == "weapon_mac10");

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [new TeamPlanningMember(
                Slot: 1,
                IsBot: true,
                IsAwper: false,
                Money: 3000,
                Candidates: candidates,
                CurrentPrimary: null,
                SavedTier: 0)],
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        Assert.Equal("weapon_galilar", result.BotPlans[1].PrimaryWeapon);
    }

    [Fact]
    public void BudgetConstrainedCandidateChainRetainsLowPricePrimary()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            money: 1700,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.Contains(candidates, plan => plan.PrimaryWeapon == "weapon_mac10");
        Assert.All(candidates, plan => Assert.True(plan.EstimatedCost <= 1700));

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [new TeamPlanningMember(
                Slot: 1,
                IsBot: true,
                IsAwper: false,
                Money: 1700,
                Candidates: candidates,
                CurrentPrimary: null,
                SavedTier: 0)],
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        Assert.Equal("weapon_mac10", result.BotPlans[1].PrimaryWeapon);
    }

    [Fact]
    public void TimeoutFallbackUsesTheSameRifleOrderingAsFrontier()
    {
        var candidates = Enumerable.Range(0, 12)
            .Select(index => new PlayerBuyPlan(
                BuyPhase.FullBuy,
                ArmorLevel.Full,
                index % 2 == 0 ? "weapon_galilar" : "weapon_mac10",
                null,
                BuysHelmet: false,
                BuysDefuser: false,
                Utility: index % 3 == 0 ? ["smoke", "flash"] : [],
                EstimatedCost: index % 2 == 0 ? 2800 : 2050)
            {
                Tier = index % 2 == 0 ? 8 : 7,
            })
            .ToArray();
        var members = Enumerable.Range(1, 100)
            .Select(slot => new TeamPlanningMember(
                Slot: slot,
                IsBot: true,
                IsAwper: false,
                Money: 4000,
                Candidates: candidates,
                CurrentPrimary: null,
                SavedTier: 0))
            .ToArray();

        var frontier = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            options: new BoundedPlannerOptions(
                MaxCandidatesPerBot: 6,
                MaxFrontierStates: 256,
                HardBudgetMilliseconds: 1000),
            buyMode: TeamBuyMode.Full);
        var fallback = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            options: new BoundedPlannerOptions(
                MaxCandidatesPerBot: 6,
                MaxFrontierStates: 256,
                HardBudgetMilliseconds: 1),
            buyMode: TeamBuyMode.Full);

        Assert.True(fallback.Diagnostics.TimedOut);
        Assert.Equal(
            frontier.Plan.BotPlans.OrderBy(entry => entry.Key).Select(entry => entry.Value.PrimaryWeapon),
            fallback.Plan.BotPlans.OrderBy(entry => entry.Key).Select(entry => entry.Value.PrimaryWeapon));
    }

    [Fact]
    public async Task LatestOnlyWorkerDropsStaleResultAfterNewSnapshotArrives()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var published = new List<PlanningResult<int>>();
        using var worker = new LatestOnlyPlanningWorker<int, int>(
            input =>
            {
                if (input == 1)
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(2));
                }

                return input * 10;
            },
            result => published.Add(result));

        worker.Submit(1, new PlanVersion(7, 1, 1));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        worker.Submit(2, new PlanVersion(7, 2, 2));
        releaseFirst.Set();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (published.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Single(published);
        Assert.Equal(2, published[0].Version.SnapshotValue);
        Assert.Equal(20, published[0].Result);
    }

    [Fact]
    public void PistolRolesProduceArmorFirepowerUtilityAndDefuserMix()
    {
        var roles = Enumerable.Range(0, 5)
            .Select(index => PistolBuyRolePolicy.ForBotOrdinal(
                TeamSide.CounterTerrorist,
                index,
                botCount: 5))
            .ToArray();

        Assert.Contains(PistolBuyRole.ArmorEntry, roles);
        Assert.Contains(PistolBuyRole.Firepower, roles);
        Assert.Contains(PistolBuyRole.UtilitySupport, roles);
        Assert.Contains(PistolBuyRole.DefuserSupport, roles);

        var support = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Pistol,
            money: 2500,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Pistol,
            pistolRole: PistolBuyRole.UtilitySupport);
        Assert.Contains(support, plan => plan.Utility.Contains("smoke"));
    }

    [Fact]
    public void CtNonEcoRiflePlanUsesHalfArmorWhenHelmetIsNotBought()
    {
        var plans = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            money: 3550,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Standard);

        var m4 = Assert.Single(plans, plan => plan.PrimaryWeapon == "weapon_m4a1");

        Assert.Equal(ArmorLevel.Half, m4.ArmorLevel);
        Assert.False(m4.BuysHelmet);
        Assert.Equal(3550, m4.EstimatedCost);
    }

    [Fact]
    public void PistolFirepowerRecalculatesArmorCostAndTier()
    {
        var lowMoneyPlans = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Pistol,
            money: 800,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Pistol,
            pistolRole: PistolBuyRole.Firepower);
        var lowMoneyP250 = Assert.Single(
            lowMoneyPlans, plan => plan.SecondaryWeapon == "weapon_p250");

        Assert.Equal(ArmorLevel.None, lowMoneyP250.ArmorLevel);
        Assert.Equal(300, lowMoneyP250.EstimatedCost);

        var enoughMoneyPlans = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Pistol,
            money: 950,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.Pistol,
            pistolRole: PistolBuyRole.Firepower);
        var halfArmorP250 = Assert.Single(
            enoughMoneyPlans, plan => plan.SecondaryWeapon == "weapon_p250"
                && plan.ArmorLevel == ArmorLevel.Half);

        Assert.Equal(ArmorLevel.Half, halfArmorP250.ArmorLevel);
        Assert.Equal(950, halfArmorP250.EstimatedCost);
        Assert.Equal(2, halfArmorP250.Tier);
    }

    [Fact]
    public void FullCtTeamKeepsM4OverMatchingAWeakFamasMember()
    {
        var members = new[]
        {
            new TeamPlanningMember(
                Slot: 1,
                IsBot: true,
                IsAwper: false,
                Money: 7000,
                Candidates: new[]
                {
                    RiflePlan("weapon_m4a1", ArmorLevel.Full, 3900, 9),
                    RiflePlan("weapon_famas", ArmorLevel.Half, 2600, 7),
                },
                CurrentPrimary: null,
                SavedTier: 0),
        }.Concat(Enumerable.Range(2, 4).Select(slot =>
            new TeamPlanningMember(
                slot,
                IsBot: true,
                IsAwper: false,
                Money: 2600,
                Candidates: new[] { RiflePlan("weapon_famas", ArmorLevel.Half, 2600, 7) },
                CurrentPrimary: null,
                SavedTier: 0))).ToArray();

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            purchaseIntent: PurchaseIntent.Standard,
            buyMode: TeamBuyMode.Full);

        Assert.Equal("weapon_m4a1", result.Plan.BotPlans[1].PrimaryWeapon);
    }

    [Fact]
    public void DesignatedCtAwperSurvivesFrontierPruningWhenTeamCanAffordIt()
    {
        var members = Enumerable.Range(1, 5)
            .Select(slot => new TeamPlanningMember(
                slot,
                IsBot: true,
                IsAwper: slot == 1,
                Money: 8000,
                Candidates: BuyPlanner.BuildCandidatePlans(
                    TeamSide.CounterTerrorist,
                    BuyPhase.FullBuy,
                    money: 8000,
                    designatedAwper: slot == 1,
                    opponentEcoLikely: false,
                    purchaseIntent: PurchaseIntent.Standard),
                CurrentPrimary: null,
                SavedTier: 0))
            .ToArray();

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            purchaseIntent: PurchaseIntent.Standard,
            buyMode: TeamBuyMode.Full);

        Assert.Equal("weapon_awp", result.Plan.BotPlans[1].PrimaryWeapon);
        Assert.Equal(1, result.Plan.BotPlans.Values.Count(plan => plan.PrimaryWeapon == "weapon_awp"));
    }

    [Fact]
    public void DonorCanUpgradePoorMemberWithPreferredRifle()
    {
        var members = new[]
        {
            new TeamPlanningMember(
                1,
                IsBot: true,
                IsAwper: false,
                Money: 7000,
                Candidates: new[] { RiflePlan("weapon_m4a1", ArmorLevel.Full, 3900, 9) },
                CurrentPrimary: null,
                SavedTier: 0),
            new TeamPlanningMember(
                2,
                IsBot: true,
                IsAwper: false,
                Money: 2600,
                Candidates: new[] { RiflePlan("weapon_famas", ArmorLevel.Half, 2600, 7) },
                CurrentPrimary: null,
                SavedTier: 0),
        };

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            purchaseIntent: PurchaseIntent.Standard,
            buyMode: TeamBuyMode.Full);

        var transfer = Assert.Single(result.Plan.Transfers);
        Assert.Equal(1, transfer.Donor);
        Assert.Equal(2, transfer.Recipient);
        Assert.Equal("weapon_m4a1", transfer.Item);
        Assert.Equal("weapon_m4a1", result.Plan.BotPlans[2].PrimaryWeapon);
    }

    private static PlayerBuyPlan RiflePlan(
        string weapon,
        ArmorLevel armor,
        int cost,
        int tier)
        => new(
            BuyPhase.FullBuy,
            armor,
            weapon,
            null,
            BuysHelmet: armor == ArmorLevel.Full,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: cost)
        {
            Tier = tier,
        };
}
