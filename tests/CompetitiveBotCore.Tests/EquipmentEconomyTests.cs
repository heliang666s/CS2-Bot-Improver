using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class EquipmentEconomyTests
{
    [Fact]
    public void HumanM4WithEffectiveArmorAndFiveHundredCashIsFullReady()
    {
        var human = new PlayerEquipmentSnapshot(
            Slot: 10,
            IsBot: false,
            CurrentMoney: 500,
            RoundStartMoney: 8000,
            ArmorValue: 100,
            HasHelmet: true,
            PrimaryWeapon: "weapon_m4a1",
            SecondaryWeapon: "weapon_hkp2000",
            HasDefuser: false,
            Utility: new Dictionary<string, int>());

        Assert.True(human.IsFullReady(TeamSide.CounterTerrorist));
        Assert.Equal(0, human.FullBuyCompletionCost(TeamSide.CounterTerrorist));
        Assert.Equal(
            BuyPhase.FullBuy,
            BuyPlanner.Classify(new TeamEconomySnapshot(
                TeamSide.CounterTerrorist,
                [500],
                IsPistolRound: false,
                IsLastRound: false,
                ForceBuySignal: false,
                OpponentEcoLikely: false)
            {
                Players = [human],
            }));
    }

    [Fact]
    public void CashConvertedIntoEquipmentDoesNotDoubleCountRoundStartMoney()
    {
        var before = new PlayerEquipmentSnapshot(
            10, false, 8000, 8000, 0, false, null, "weapon_hkp2000", false,
            new Dictionary<string, int>());
        var after = before with
        {
            CurrentMoney = 4100,
            ArmorValue = 100,
            HasHelmet = true,
            PrimaryWeapon = "weapon_m4a1",
        };

        Assert.Equal(8000, before.TotalPlanningValue);
        Assert.Equal(8000, after.TotalPlanningValue);
        Assert.NotEqual(
            after.RoundStartMoney + after.EquipmentValue,
            after.TotalPlanningValue);
    }

    [Fact]
    public void DamagedOnePointArmorIsNotFullReady()
    {
        var player = new PlayerEquipmentSnapshot(
            1, false, 500, 500, 1, true, "weapon_ak47", "weapon_glock", false,
            new Dictionary<string, int>());

        Assert.False(player.IsFullReady(TeamSide.Terrorist));
        Assert.True(player.FullBuyCompletionCost(TeamSide.Terrorist) > 0);
    }

    [Fact]
    public void LowDurabilityArmorUsesTheSameNoneCategoryForPlanningAndPurchase()
    {
        Assert.Equal(
            ArmorLevel.None,
            EquipmentEconomy.ClassifyArmor(1, hasHelmet: false));
        Assert.Equal(
            ArmorLevel.None,
            EquipmentEconomy.ClassifyArmor(49, hasHelmet: true));
        Assert.Equal(
            ArmorLevel.Half,
            EquipmentEconomy.ClassifyArmor(50, hasHelmet: false));
        Assert.Equal(
            BuyPlanner.KevlarPrice + BuyPlanner.HelmetUpgradePrice,
            BuyPlanner.GetAssaultSuitPurchaseCost(
                EquipmentEconomy.ClassifyArmor(1, hasHelmet: false)));

        var snapshot = new PlayerEquipmentSnapshot(
            3,
            IsBot: true,
            CurrentMoney: 1000,
            RoundStartMoney: 1000,
            ArmorValue: 1,
            HasHelmet: true,
            PrimaryWeapon: "weapon_m4a1",
            SecondaryWeapon: "weapon_hkp2000",
            HasDefuser: false,
            Utility: new Dictionary<string, int>());
        Assert.Equal(
            BuyPlanner.KevlarPrice + BuyPlanner.HelmetUpgradePrice,
            snapshot.FullBuyCompletionCost(TeamSide.CounterTerrorist));
    }

    [Fact]
    public void FamasDoesNotReduceTheFullRiflePurchaseCost()
    {
        var player = new PlayerEquipmentSnapshot(
            2, true, 950, 950, 100, true, "weapon_famas", "weapon_hkp2000", false,
            new Dictionary<string, int>());

        Assert.Equal(EquipmentEconomy.GetWeaponPrice("weapon_m4a1"),
            player.FullBuyCompletionCost(TeamSide.CounterTerrorist));
        Assert.False(player.CanReachFullBuy(TeamSide.CounterTerrorist));
    }

    [Fact]
    public void FullBuyDemandUsesTeamFloorAndMustWinRaisesFlashTarget()
    {
        var full = TeamUtilityDemandPolicy.ForPhase(BuyPhase.FullBuy, teamSize: 5);
        var mustWin = TeamUtilityDemandPolicy.ForPhase(
            BuyPhase.FullBuy,
            teamSize: 5,
            mustWin: true);

        Assert.Equal((5, 7, 3, 3, 2),
            (full.Smoke, full.Flash, full.He, full.Fire, full.Defuser));
        Assert.Equal(8, mustWin.Flash);
        Assert.Equal(4, mustWin.PersonalUtilityTarget);
    }

    [Fact]
    public void HumanUtilityCountsTowardTeamFloorButRichBotStillGetsRolePackage()
    {
        var human = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            500,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_ak47",
            currentSecondary: "weapon_glock",
            currentHasHelmet: true,
            currentUtility: new Dictionary<string, int>
            {
                ["smoke"] = 1,
                ["flash"] = 2,
                ["he"] = 1,
                ["molotov"] = 1,
            });
        var botCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            role: BuyRole.TEntry);

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.Terrorist,
            BuyPhase.FullBuy,
            [
                new TeamPlanningMember(1, true, false, 8000, botCandidates, null, 0),
                new TeamPlanningMember(10, false, false, 500, [human], "weapon_ak47", human.Tier),
            ],
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        var bot = result.BotPlans[1];
        Assert.True(bot.Utility.Count >= 3);
        Assert.True(result.HumanObservations[10].Utility.Count == 0
            || result.HumanObservations[10].Utility.Count <= 4);
    }

    [Fact]
    public void HumanAwperPreventsBotFromBuyingSecondAwp()
    {
        var human = BuyPlanner.BuildPlayerPlan(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            500,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_awp",
            currentSecondary: "weapon_hkp2000",
            currentHasHelmet: true);
        var botCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            9000,
            designatedAwper: true,
            opponentEcoLikely: false);

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            [
                new TeamPlanningMember(1, true, true, 9000, botCandidates, null, 0),
                new TeamPlanningMember(10, false, true, 500, [human], "weapon_awp", human.Tier),
            ],
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        Assert.DoesNotContain("weapon_awp", result.BotPlans.Values.Select(p => p.PrimaryWeapon));
    }

    [Fact]
    public void SavedFullRiflesWithLowCashDoNotLookLikeOpponentEco()
    {
        var opponents = Enumerable.Range(1, 5)
            .Select(slot => new PlayerEquipmentSnapshot(
                slot,
                IsBot: true,
                CurrentMoney: 500,
                RoundStartMoney: 500,
                ArmorValue: 100,
                HasHelmet: true,
                PrimaryWeapon: "weapon_ak47",
                SecondaryWeapon: "weapon_glock",
                HasDefuser: false,
                Utility: new Dictionary<string, int>()))
            .ToArray();

        Assert.False(EquipmentEconomy.IsEcoLikely(opponents, TeamSide.CounterTerrorist));
    }

    [Fact]
    public void RichCtBotUpgradesCarriedFamasToM4AndKeepsRoleUtilities()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            6000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_famas",
            currentSecondary: "weapon_hkp2000",
            currentHasHelmet: true,
            role: BuyRole.CtAnchor);
        var plan = candidates.OrderByDescending(p => p.Tier).First();

        Assert.Equal("weapon_m4a1", plan.PrimaryWeapon);
        Assert.InRange(plan.Utility.Count, 3, 4);
        Assert.DoesNotContain(
            candidates,
            candidate => candidate.PrimaryWeapon == "weapon_famas");
    }

    [Fact]
    public void CrossSideFullRifleIsStillFullReadyAfterPickup()
    {
        var player = new PlayerEquipmentSnapshot(
            7, false, 500, 5000, 100, true, "weapon_ak47", "weapon_hkp2000", false,
            new Dictionary<string, int>());

        Assert.True(player.IsFullReady(TeamSide.CounterTerrorist));
    }

    [Fact]
    public void EmptyStandardCandidatesAlwaysCarryAnExplainableReason()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            0,
            designatedAwper: false,
            opponentEcoLikely: false);

        Assert.NotEmpty(candidates);
        Assert.All(candidates.Where(plan => plan.EstimatedCost == 0), plan =>
            Assert.Contains(plan.EmptyPlanReason, new[]
            {
                "explicit-save",
                "already-equipped",
                "waiting-for-gift",
            }));
    }

    [Fact]
    public void PlannerWeaponCostUsesEquipmentEconomyPriceTable()
    {
        Assert.Equal(
            EquipmentEconomy.GetWeaponPrice("weapon_p90"),
            BuyPlanner.GetWeaponCost("weapon_p90"));
        Assert.Equal(
            EquipmentEconomy.GetWeaponPrice("weapon_awp"),
            BuyPlanner.GetWeaponCost("weapon_awp"));
    }

    [Fact]
    public void HalfBuyDoesNotStackExpensivePistolOnCarriedSmg()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.HalfBuy,
            5000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Half,
            currentPrimary: "weapon_mp9",
            currentSecondary: "weapon_hkp2000",
            currentHasHelmet: false,
            role: BuyRole.CtRotator);

        Assert.DoesNotContain(
            candidates,
            plan => plan.PrimaryWeapon == "weapon_mp9"
                && plan.EstimatedCost > 0
                && plan.SecondaryWeapon == "weapon_deagle");
    }

    [Fact]
    public void ExistingFlashOnlyOffsetsTheFirstFlashInADoubleFlashPackage()
    {
        var plans = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_m4a1",
            currentSecondary: "weapon_hkp2000",
            currentHasHelmet: true,
            currentUtility: new Dictionary<string, int> { ["flash"] = 1 },
            role: BuyRole.CtRotator);

        Assert.Contains(plans, plan => plan.PrimaryWeapon == "weapon_m4a1"
            && plan.Utility.Count(item => item == "flash") == 2
            && plan.EstimatedCost == EquipmentEconomy.DefuserPrice
                + EquipmentEconomy.SmokePrice
                + EquipmentEconomy.FlashPrice
                + EquipmentEconomy.HePrice);
    }

    [Fact]
    public void NoDefuserVariantRebuildsUtilitiesWithTheReleasedBudget()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            4700,
            designatedAwper: false,
            opponentEcoLikely: false,
            role: BuyRole.CtRotator);

        Assert.Contains(candidates, plan =>
            !plan.BuysDefuser
            && plan.PrimaryWeapon == "weapon_m4a1"
            && plan.Utility.Count == 4
            && plan.EstimatedCost <= 4700);
    }

    [Fact]
    public void ExistingBotDefuserCountsTowardTheTeamDefuserQuota()
    {
        var holderCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            currentArmor: ArmorLevel.Full,
            currentPrimary: "weapon_m4a1",
            currentSecondary: "weapon_hkp2000",
            currentHasHelmet: true,
            currentHasDefuser: true,
            role: BuyRole.CtRotator);
        var freshCandidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            role: BuyRole.CtRotator);
        var members = new List<TeamPlanningMember>
        {
            new(1, true, false, 8000, holderCandidates, "weapon_m4a1", 8,
                IsDefuser: true),
        };
        members.AddRange(Enumerable.Range(2, 4).Select(slot =>
            new TeamPlanningMember(slot, true, false, 8000, freshCandidates, null, 0)));

        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        Assert.Equal(1, result.BotPlans.Values.Count(plan => plan.BuysDefuser));
    }

    [Fact]
    public void CtRotatorHasAFireVariantForTeamUtilityCoverage()
    {
        var plans = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            role: BuyRole.CtRotator);

        Assert.Contains(plans, plan => plan.Utility.Contains("molotov"));
    }

    [Fact]
    public void FullCtPlannerUsesAtLeastThreeFireItemsAndTwoDefusers()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            8000,
            designatedAwper: false,
            opponentEcoLikely: false,
            role: BuyRole.CtRotator);
        var members = Enumerable.Range(1, 5)
            .Select(slot => new TeamPlanningMember(
                slot,
                IsBot: true,
                IsAwper: false,
                Money: 8000,
                Candidates: candidates,
                CurrentPrimary: null,
                SavedTier: 0))
            .ToArray();
        var result = BoundedTeamBuyPlanner.Optimize(
            TeamSide.CounterTerrorist,
            BuyPhase.FullBuy,
            members,
            currentMinTier: 0,
            buyMode: TeamBuyMode.Full);

        int fireCount = result.BotPlans.Values.Sum(plan => plan.Utility.Count(
            item => item is "molotov" or "weapon_molotov" or "incendiary" or "weapon_incgrenade"));
        int defuserCount = result.BotPlans.Values.Count(plan => plan.BuysDefuser);

        Assert.True(fireCount >= 3,
            $"fire={fireCount}; plans={string.Join(",", result.BotPlans.Values.Select(plan => string.Join("/", plan.Utility)))}");
        Assert.Equal(2, defuserCount);
    }

    [Fact]
    public void MustWinRolePackagesStillContainFourUtilitiesWhenPhaseIsEco()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            10000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.AllIn,
            role: BuyRole.CtRotator);

        Assert.Contains(candidates, plan => plan.Utility.Count >= 4);
    }

    [Fact]
    public void AutoAllInNeverPlansMoreThanTheCompetitiveGrenadeCapacity()
    {
        var candidates = BuyPlanner.BuildCandidatePlans(
            TeamSide.CounterTerrorist,
            BuyPhase.Eco,
            10000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.AllIn,
            role: BuyRole.Auto);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, plan => Assert.InRange(plan.Utility.Count, 0, 4));
    }
}
