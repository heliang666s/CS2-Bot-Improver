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
}
