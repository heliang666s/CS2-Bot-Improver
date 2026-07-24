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
    public void DeferredWeaponGrantRetriesRecipientAndDonorRearmWithoutDuplicatingPurchase()
    {
        var donor = new FakeInventoryPort(new InventorySnapshot(
            7000, ArmorLevel.Full, null, "weapon_hkp2000", true, false, Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var recipient = new FakeInventoryPort(new InventorySnapshot(
            1000, ArmorLevel.Full, null, "weapon_glock", true, false, Array.Empty<string>()))
        {
            DelayGrants = true,
        };
        var transaction = new WeaponGrantTransaction(donor, recipient);

        var first = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.True(first.Retryable);
        Assert.Single(donor.BoughtItems, item => item == "weapon_m4a1");

        donor.ApplyLastDelayedPurchase();
        var second = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.True(second.Retryable);
        Assert.Single(recipient.GrantedItems, item => item == "weapon_m4a1");

        recipient.ApplyDelayedGrant();
        var third = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.True(third.Retryable);
        Assert.Equal(2, donor.BoughtItems.Count(item => item == "weapon_m4a1"));

        donor.ApplyLastDelayedPurchase();
        var committed = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.True(committed.Committed);
        Assert.Equal("weapon_m4a1", donor.State.PrimaryWeapon);
        Assert.Equal("weapon_m4a1", recipient.State.PrimaryWeapon);
        Assert.Equal(2, donor.BoughtItems.Count(item => item == "weapon_m4a1"));
    }

    [Fact]
    public void DelayedGrantFailureCleansPendingBeforeDonorRearmRecovery()
    {
        var donor = new FakeInventoryPort(new InventorySnapshot(
            7000, ArmorLevel.Full, null, "weapon_hkp2000", true, false, Array.Empty<string>()))
        {
            FailBuyAfterFirstPurchaseItem = "weapon_m4a1",
        };
        var recipient = new FakeInventoryPort(new InventorySnapshot(
            1000, ArmorLevel.Full, null, "weapon_glock", true, false, Array.Empty<string>()))
        {
            DelayGrants = true,
        };
        var transaction = new WeaponGrantTransaction(donor, recipient);

        Assert.True(transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1").Retryable);
        recipient.ApplyDelayedGrant();

        var failed = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.False(failed.Committed);
        Assert.Equal("donor-rearm-failed", failed.Reason);
        Assert.False(donor.HasPending("weapon_m4a1"));
        Assert.False(recipient.HasPending("weapon_m4a1"));
        Assert.Null(donor.State.PrimaryWeapon);
        Assert.Null(recipient.State.PrimaryWeapon);
        Assert.Equal(7000, donor.State.Money);
    }

    [Fact]
    public void ExternalRecipientWeaponCancelsGiftAndRestoresOnlyDonor()
    {
        var donor = new FakeInventoryPort(new InventorySnapshot(
            7000, ArmorLevel.Full, null, "weapon_hkp2000", true, false, Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var recipient = new FakeInventoryPort(new InventorySnapshot(
            1000, ArmorLevel.Full, null, "weapon_glock", true, false, Array.Empty<string>()));
        var transaction = new WeaponGrantTransaction(donor, recipient);

        Assert.True(transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1").Retryable);

        recipient.SetExternalPrimary("weapon_m4a1");
        donor.ApplyLastDelayedPurchase();

        var cancelled = transaction.Execute(
            "weapon_m4a1",
            BuyPlanner.GetWeaponCost("weapon_m4a1"),
            donorRearmWeapon: "weapon_m4a1");

        Assert.False(cancelled.Committed);
        Assert.Equal("recipient-external-weapon", cancelled.Reason);
        Assert.Equal(1, donor.BoughtItems.Count(item => item == "weapon_m4a1"));
        Assert.Equal(7000, donor.State.Money);
        Assert.Null(donor.State.PrimaryWeapon);
        Assert.Equal("weapon_m4a1", recipient.State.PrimaryWeapon);
        Assert.False(recipient.HasPending("weapon_m4a1"));
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

    [Fact]
    public void UtilityPurchaseFailureDoesNotRollbackAlreadyPurchasedCoreLoadout()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            6000,
            ArmorLevel.None,
            null,
            "weapon_hkp2000",
            false,
            false,
            Array.Empty<string>()))
        {
            FailBuyItems = new HashSet<string> { "weapon_smokegrenade" },
        };
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            BuysHelmet: true,
            BuysDefuser: false,
            Utility: ["smoke"],
            EstimatedCost: 4200);

        var result = new BuyExecutionTransaction(port)
            .Execute(plan, TeamSide.CounterTerrorist);

        Assert.False(result.Committed);
        Assert.Equal(InventoryTransactionStage.Utility, result.Stage);
        Assert.Equal("weapon_m4a1", port.State.PrimaryWeapon);
        Assert.Equal(ArmorLevel.Full, port.State.Armor);
        Assert.True(port.State.HasHelmet);
        Assert.DoesNotContain("weapon_smokegrenade", port.State.Utility);
    }

    [Fact]
    public void DelayedCoreConfirmationIsRetryableWithoutRollingBackEarlierState()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            5000,
            ArmorLevel.Full,
            null,
            "weapon_hkp2000",
            true,
            false,
            Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            BuysHelmet: false,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: BuyPlanner.GetWeaponCost("weapon_m4a1"));

        var result = new BuyExecutionTransaction(port)
            .Execute(plan, TeamSide.CounterTerrorist);

        Assert.False(result.Committed);
        Assert.True(result.Retryable);
        Assert.Equal(InventoryTransactionStage.Primary, result.Stage);
        Assert.Null(port.State.PrimaryWeapon);
        Assert.Equal(ArmorLevel.Full, port.State.Armor);
    }

    [Fact]
    public void RetryingADeferredPurchaseDoesNotIssueTheSameBuyTwice()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            5000,
            ArmorLevel.Full,
            null,
            "weapon_hkp2000",
            true,
            false,
            Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            BuysHelmet: false,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: BuyPlanner.GetWeaponCost("weapon_m4a1"));
        var transaction = new BuyExecutionTransaction(port);

        Assert.True(transaction.Execute(plan, TeamSide.CounterTerrorist).Retryable);
        Assert.True(transaction.Execute(plan, TeamSide.CounterTerrorist).Retryable);
        Assert.Single(port.BoughtItems, item => item == "weapon_m4a1");
    }

    [Fact]
    public void DeferredDoubleFlashAdvancesToSecondFlashAndFollowingHe()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            5000,
            ArmorLevel.Full,
            "weapon_m4a1",
            "weapon_hkp2000",
            true,
            false,
            Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            BuysHelmet: false,
            BuysDefuser: false,
            Utility: ["flash", "flash", "he"],
            EstimatedCost: 700);
        var transaction = new BuyExecutionTransaction(port);

        Assert.True(transaction.Execute(plan, TeamSide.CounterTerrorist).Retryable);
        port.ApplyDelayedUtility("weapon_flashbang");

        Assert.True(transaction.Execute(plan, TeamSide.CounterTerrorist).Retryable);
        Assert.Equal(2, port.BoughtItems.Count(item => item == "weapon_flashbang"));
        port.ApplyDelayedUtility("weapon_flashbang");

        Assert.True(transaction.Execute(plan, TeamSide.CounterTerrorist).Retryable);
        Assert.Contains("weapon_hegrenade", port.BoughtItems);
    }

    [Fact]
    public void DeferredFullLoadoutGetsEnoughAttemptsToCommitAfterEveryNextTickConfirmation()
    {
        var port = new FakeInventoryPort(new InventorySnapshot(
            10000,
            ArmorLevel.None,
            null,
            "weapon_hkp2000",
            false,
            false,
            Array.Empty<string>()))
        {
            DelayPurchases = true,
        };
        var plan = new PlayerBuyPlan(
            BuyPhase.FullBuy,
            ArmorLevel.Full,
            "weapon_m4a1",
            null,
            BuysHelmet: false,
            BuysDefuser: true,
            Utility: ["smoke", "flash", "flash", "he"],
            EstimatedCost: 5000);
        var transaction = new BuyExecutionTransaction(port);
        var attempts = BuyExecutionTransaction.EstimateMaxConfirmationAttempts(plan);

        InventoryTransactionResult result = default;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            result = transaction.Execute(plan, TeamSide.CounterTerrorist);
            if (result.Retryable)
                port.ApplyLastDelayedPurchase();
            else
                break;
        }

        Assert.True(result.Committed, $"stage={result.Stage}; reason={result.Reason}");
        Assert.Equal(8, attempts);
        Assert.Equal(ArmorLevel.Full, port.State.Armor);
        Assert.Equal("weapon_m4a1", port.State.PrimaryWeapon);
        Assert.True(port.State.HasDefuser);
        Assert.Equal(2, port.State.Utility.Count(item => item == "weapon_flashbang"));
        Assert.Contains("weapon_hegrenade", port.State.Utility);
    }

    private sealed class FakeInventoryPort : IInventoryPort, IDeferredInventoryConfirmation, IPendingInventoryPurchase
    {
        public FakeInventoryPort(InventorySnapshot state)
        {
            State = state;
        }

        public InventorySnapshot State { get; private set; }
        public List<string> BoughtItems { get; } = new();
        public List<string> RemovedItems { get; } = new();
        public bool RejectGrant { get; init; }
        public bool DelayPurchases { get; init; }
        public bool DelayGrants { get; init; }
        public bool RequiresNextTickConfirmation => DelayPurchases || DelayGrants;
        private readonly HashSet<(string Item, int ExpectedCount)> _pendingPurchases = new();

        public List<string> GrantedItems { get; } = new();

        public bool IsPurchasePending(string itemName, int expectedCount)
            => _pendingPurchases.Contains((itemName, expectedCount));

        public void MarkPurchasePending(string itemName, int expectedCount)
            => _pendingPurchases.Add((itemName, expectedCount));

        public void ClearPurchasePending(string itemName, int expectedCount)
            => _pendingPurchases.Remove((itemName, expectedCount));
        public bool HasPending(string itemName, int expectedCount = 1)
            => _pendingPurchases.Contains((itemName, expectedCount));
        public IReadOnlySet<string> FailBuyItems { get; init; }
            = new HashSet<string>(StringComparer.Ordinal);
        public string? FailBuyAfterFirstPurchaseItem { get; init; }

        public void ApplyDelayedUtility(string itemName)
            => State = State with { Utility = State.Utility.Append(itemName).ToArray() };

        public void ApplyDelayedGrant()
            => State = State with { PrimaryWeapon = GrantedItems[^1] };

        public void SetExternalPrimary(string itemName)
            => State = State with { PrimaryWeapon = itemName };

        public void ApplyLastDelayedPurchase()
        {
            string itemName = BoughtItems[^1];
            State = State with
            {
                Armor = itemName switch
                {
                    "item_kevlar" => ArmorLevel.Half,
                    "item_assaultsuit" => ArmorLevel.Full,
                    _ => State.Armor,
                },
                HasHelmet = itemName == "item_assaultsuit" || State.HasHelmet,
                HasDefuser = itemName == "item_defuser" || State.HasDefuser,
                PrimaryWeapon = BuyPlanner.IsPrimaryWeapon(itemName)
                    ? itemName
                    : State.PrimaryWeapon,
                Utility = itemName.StartsWith("weapon_", StringComparison.Ordinal)
                    && itemName is not "weapon_m4a1" and not "weapon_ak47"
                    ? State.Utility.Append(itemName).ToArray()
                    : State.Utility,
            };
        }

        public InventorySnapshot Capture() => State;

        public bool TryBuy(string itemName)
        {
            BoughtItems.Add(itemName);
            if (DelayPurchases)
                return true;
            if (FailBuyItems.Contains(itemName)) return false;
            if (FailBuyAfterFirstPurchaseItem == itemName
                && BoughtItems.Count(item => item == itemName) > 1)
                return false;
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
            GrantedItems.Add(itemName);
            if (DelayGrants)
                return true;
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
