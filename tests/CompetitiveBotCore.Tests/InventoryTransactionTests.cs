using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class InventoryTransactionTests
{
    [Fact]
    public void InventorySnapshotNormalizesProductionUtilityAliases()
    {
        var snapshot = new InventorySnapshot(
            1000,
            ArmorLevel.Half,
            null,
            "weapon_glock",
            false,
            false,
            new[] { "smoke", "flash", "he", "molotov" });

        Assert.Equal(
            [
                "weapon_smokegrenade",
                "weapon_flashbang",
                "weapon_hegrenade",
                "weapon_molotov",
            ],
            snapshot.Utility);
        Assert.True(snapshot.Contains("weapon_smokegrenade"));
        Assert.True(snapshot.Contains("flash"));
    }

    [Fact]
    public void BuyTransactionKeepsCarriedPrimaryAndAddsOnlyMissingItems()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            Money: 4000,
            Armor: ArmorLevel.Full,
            PrimaryWeapon: "weapon_m4a1",
            SecondaryWeapon: "weapon_hkp2000",
            HasHelmet: true,
            HasDefuser: false,
            Utility: Array.Empty<string>()));
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            "weapon_deagle",
            BuysHelmet: true,
            BuysDefuser: true,
            Utility: new[] { "smoke" },
            EstimatedCost: 1000);

        var result = new BuyExecutionTransaction(port).Execute(plan, TeamSide.CounterTerrorist);

        Assert.True(result.Committed);
        Assert.Equal("weapon_m4a1", port.State.PrimaryWeapon);
        Assert.Equal("weapon_deagle", port.State.SecondaryWeapon);
        Assert.True(port.State.HasDefuser);
        Assert.Contains("weapon_smokegrenade", port.State.Utility);
        Assert.DoesNotContain("weapon_usp_silencer", port.BoughtItems);
    }

    [Fact]
    public void BuyTransactionRemovesCarriedPrimaryBeforeSafeUpgrade()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            5000,
            ArmorLevel.Half,
            "weapon_mp9",
            "weapon_hkp2000",
            false,
            false,
            Array.Empty<string>()));
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Half,
            "weapon_m4a1",
            null,
            BuysHelmet: false,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: BuyPlanner.GetWeaponCost("weapon_m4a1"))
        {
            ReplacePrimaryWeapon = "weapon_mp9",
        };

        var result = new BuyExecutionTransaction(port)
            .Execute(plan, TeamSide.CounterTerrorist);

        Assert.True(result.Committed);
        Assert.Equal("weapon_m4a1", port.State.PrimaryWeapon);
        Assert.Contains("weapon_mp9", port.RemovedItems);
    }

    [Fact]
    public void WeaponGrantTransactionRollsBackBothInventoriesWhenRecipientCannotConfirm()
    {
        var donor = new FakeInventoryPort(new InventorySnapshot(
            6000, ArmorLevel.Full, null, "weapon_hkp2000", true, false, Array.Empty<string>()));
        var recipient = new FakeInventoryPort(new InventorySnapshot(
            1000, ArmorLevel.None, null, "weapon_glock", false, false, Array.Empty<string>()))
        {
            RejectGrant = true,
        };

        var result = new WeaponGrantTransaction(donor, recipient)
            .Execute(
                "weapon_m4a1",
                BuyPlanner.GetWeaponCost("weapon_m4a1"),
                donorRearmWeapon: "weapon_m4a1");

        Assert.False(result.Committed);
        Assert.Null(donor.State.PrimaryWeapon);
        Assert.Null(recipient.State.PrimaryWeapon);
        Assert.Equal(6000, donor.State.Money);
        Assert.Equal(1000, recipient.State.Money);
    }

    [Fact]
    public void WeaponGrantTransactionRearmsDonorAfterGift()
    {
        var donor = new FakeInventoryPort(new InventorySnapshot(
            7000, ArmorLevel.Full, null, "weapon_hkp2000", true, false, Array.Empty<string>()));
        var recipient = new FakeInventoryPort(new InventorySnapshot(
            1000, ArmorLevel.Half, null, "weapon_glock", false, false, Array.Empty<string>()));

        var result = new WeaponGrantTransaction(donor, recipient)
            .Execute(
                "weapon_m4a1",
                BuyPlanner.GetWeaponCost("weapon_m4a1"),
                donorRearmWeapon: "weapon_m4a1");

        Assert.True(result.Committed);
        Assert.Equal("weapon_m4a1", donor.State.PrimaryWeapon);
        Assert.Equal("weapon_m4a1", recipient.State.PrimaryWeapon);
        Assert.Equal(1200, donor.State.Money);
    }

    [Fact]
    public void UtilityThrowTransactionConsumesRealInventoryAndRestoresOnSpawnFailure()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            0, ArmorLevel.None, null, "weapon_glock", false, false,
            new[] { "weapon_smokegrenade" }));
        var ledger = new UtilityLedger(new UtilityInventory(Smoke: 1, Flash: 0, He: 0, Molotov: 0));
        var transaction = new UtilityThrowTransaction(port, ledger);

        Assert.False(transaction.Execute(UtilityType.Smoke, UtilitySource.LineupThrow, spawnSucceeded: false).Committed);
        Assert.Contains("weapon_smokegrenade", port.State.Utility);
        Assert.Equal(1, ledger.Remaining(UtilityType.Smoke));

        Assert.True(transaction.Execute(UtilityType.Smoke, UtilitySource.LineupThrow, spawnSucceeded: true).Committed);
        Assert.DoesNotContain("weapon_smokegrenade", port.State.Utility);
        Assert.Equal(0, ledger.Remaining(UtilityType.Smoke));
    }

    private sealed class FakeInventoryPort : IInventoryPort
    {
        public FakeInventoryPort(InventorySnapshot state)
        {
            State = state;
        }

        public InventorySnapshot State { get; private set; }
        public List<string> BoughtItems { get; } = new();
        public List<string> RemovedItems { get; } = new();
        public bool RejectGrant { get; init; }

        public InventorySnapshot Capture() => State;

        public bool TryBuy(string itemName)
        {
            BoughtItems.Add(itemName);
            int cost = BuyPlanner.GetWeaponCost(itemName);
            if (itemName == "item_kevlar") cost = BuyPlanner.KevlarPrice;
            if (itemName == "item_assaultsuit") cost = BuyPlanner.GetAssaultSuitPurchaseCost(State.Armor);
            if (itemName == "item_defuser") cost = BuyPlanner.DefuserPrice;
            if (itemName == "weapon_deagle") cost = BuyPlanner.DeaglePrice;
            if (itemName == "weapon_smokegrenade") cost = BuyPlanner.SmokePrice;
            if (State.Money < cost) return false;

            State = State with
            {
                Money = State.Money - cost,
                Armor = itemName switch
                {
                    "item_kevlar" => ArmorLevel.Half,
                    "item_assaultsuit" => ArmorLevel.Full,
                    _ => State.Armor,
                },
                HasHelmet = itemName == "item_assaultsuit" || State.HasHelmet,
                HasDefuser = itemName == "item_defuser" || State.HasDefuser,
                PrimaryWeapon = BuyPlanner.IsPrimaryWeapon(itemName) ? itemName : State.PrimaryWeapon,
                SecondaryWeapon = itemName is "weapon_deagle" ? itemName : State.SecondaryWeapon,
                Utility = itemName == "weapon_smokegrenade"
                    ? State.Utility.Append(itemName).ToArray()
                    : State.Utility,
            };
            return true;
        }

        public bool TryRemove(string itemName)
        {
            RemovedItems.Add(itemName);
            if (State.PrimaryWeapon == itemName)
            {
                State = State with { PrimaryWeapon = null };
                return true;
            }

            if (State.SecondaryWeapon == itemName)
            {
                State = State with { SecondaryWeapon = null };
                return true;
            }

            int index = Array.IndexOf(State.Utility.ToArray(), itemName);
            if (index < 0) return false;
            State = State with { Utility = State.Utility.Where((_, current) => current != index).ToArray() };
            return true;
        }

        public bool TryGrant(string itemName)
        {
            if (RejectGrant) return false;
            if (BuyPlanner.IsPrimaryWeapon(itemName))
            {
                State = State with { PrimaryWeapon = itemName };
                return true;
            }

            return false;
        }

        public bool Contains(string itemName)
            => State.PrimaryWeapon == itemName
                || State.SecondaryWeapon == itemName
                || State.Utility.Contains(itemName);

        public bool TryRestore(InventorySnapshot snapshot)
        {
            State = snapshot;
            return true;
        }
    }
}
