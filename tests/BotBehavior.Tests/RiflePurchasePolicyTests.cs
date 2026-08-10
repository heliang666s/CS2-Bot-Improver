using BotBehaviorPolicy;
using Xunit;

namespace BotBehavior.Tests;

public sealed class RiflePurchasePolicyTests
{
    [Fact]
    public void FullArmorGalilRanksAboveHalfArmorAk()
    {
        var selected = RiflePurchasePolicy.SelectBestAffordable(
            new[]
            {
                new PrimaryPurchaseCandidate(
                    "weapon_galilar", 1800, PurchaseArmor.Full),
                new PrimaryPurchaseCandidate(
                    "weapon_ak47", 2700, PurchaseArmor.Half),
            },
            money: 4000,
            currentArmor: PurchaseArmor.Half);

        Assert.Equal("weapon_galilar", selected?.Weapon);
    }

    [Fact]
    public void SameBudgetRifleRanksAboveSmg()
    {
        var candidates = new[]
        {
            new PrimaryPurchaseCandidate("weapon_mac10", 1050, PurchaseArmor.Full),
            new PrimaryPurchaseCandidate("weapon_galilar", 1800, PurchaseArmor.Full),
        };

        var selected = RiflePurchasePolicy.SelectBestAffordable(
            candidates, money: 3000, PurchaseArmor.None);

        Assert.Equal("weapon_galilar", selected?.Weapon);
    }

    [Fact]
    public void BudgetShortfallRetainsAffordableLowPriceGun()
    {
        var candidates = new[]
        {
            new PrimaryPurchaseCandidate("weapon_mac10", 1050, PurchaseArmor.Half),
            new PrimaryPurchaseCandidate("weapon_galilar", 1800, PurchaseArmor.Full),
        };

        var selected = RiflePurchasePolicy.SelectBestAffordable(
            candidates, money: 1700, PurchaseArmor.None);

        Assert.Equal("weapon_mac10", selected?.Weapon);
    }

    [Fact]
    public void BudgetShortfallCanStillChooseAnUnarmoredSmg()
    {
        var selected = RiflePurchasePolicy.SelectBestAffordable(
            new[]
            {
                new PrimaryPurchaseCandidate("weapon_mac10", 1050, PurchaseArmor.None),
                new PrimaryPurchaseCandidate("weapon_galilar", 1800, PurchaseArmor.Full),
            },
            money: 1050,
            currentArmor: PurchaseArmor.None);

        Assert.Equal("weapon_mac10", selected?.Weapon);
    }

    [Fact]
    public void ExistingAwprIsNotReplacedByRiflePreference()
    {
        var current = new PrimaryPurchaseCandidate(
            "weapon_awp", 4750, PurchaseArmor.Full);
        var preferred = new PrimaryPurchaseCandidate(
            "weapon_ak47", 2700, PurchaseArmor.Full);

        Assert.False(RiflePurchasePolicy.ShouldReplace(current, preferred));
    }

    [Fact]
    public void NonAwprRifleWithoutArmorIsIllegalButAwprIsLegal()
    {
        Assert.False(RiflePurchasePolicy.IsCombatLegal(
            new PrimaryPurchaseCandidate("weapon_ak47", 2700, PurchaseArmor.None)));
        Assert.False(RiflePurchasePolicy.IsCombatLegal(
            new PrimaryPurchaseCandidate("weapon_ssg08", 1700, PurchaseArmor.None)));
        Assert.True(RiflePurchasePolicy.IsCombatLegal(
            new PrimaryPurchaseCandidate("weapon_mac10", 1050, PurchaseArmor.None)));
        Assert.True(RiflePurchasePolicy.IsCombatLegal(
            new PrimaryPurchaseCandidate("weapon_awp", 4750, PurchaseArmor.None)));
    }

    [Fact]
    public void NoAffordableCandidateReturnsNoPurchase()
    {
        Assert.Null(RiflePurchasePolicy.SelectBestAffordable(
            new[]
            {
                new PrimaryPurchaseCandidate("weapon_galilar", 1800, PurchaseArmor.Full),
            },
            money: 1000,
            currentArmor: PurchaseArmor.None));
    }
}
