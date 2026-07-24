namespace CompetitiveBotCore;

public enum EquipmentCombatTier
{
    Eco,
    Pistol,
    Low,
    Mid,
    Full,
}

public sealed record PlayerEquipmentSnapshot(
    int Slot,
    bool IsBot,
    int CurrentMoney,
    int RoundStartMoney,
    int ArmorValue,
    bool HasHelmet,
    string? PrimaryWeapon,
    string? SecondaryWeapon,
    bool HasDefuser,
    IReadOnlyDictionary<string, int>? Utility)
{
    public int EquipmentValue => EquipmentEconomy.CalculateEquipmentValue(this);

    public int TotalPlanningValue => CurrentMoney + EquipmentValue;

    public bool HasEffectiveArmor => ArmorValue >= EquipmentEconomy.EffectiveArmorThreshold;

    public bool HasFullArmor => ArmorValue >= EquipmentEconomy.FullArmorThreshold;

    public EquipmentCombatTier CombatTier
        => EquipmentEconomy.ClassifyCombatTier(this);

    public bool IsFullReady(TeamSide side)
        => EquipmentEconomy.IsFullReady(this, side);

    public int FullBuyCompletionCost(TeamSide side)
        => EquipmentEconomy.GetFullBuyCompletionCost(this, side);

    public bool CanReachFullBuy(TeamSide side)
        => IsFullReady(side) || CurrentMoney >= FullBuyCompletionCost(side);
}

public static class EquipmentEconomy
{
    public const int EffectiveArmorThreshold = 50;
    public const int FullArmorThreshold = 100;
    public const int KevlarPrice = 650;
    public const int HelmetPrice = 350;
    public const int DefuserPrice = 400;
    public const int SmokePrice = 300;
    public const int FlashPrice = 200;
    public const int HePrice = 300;
    public const int MolotovPrice = 400;
    public const int IncendiaryPrice = 500;
    public const int DeaglePrice = 700;
    public const int P250Price = 300;
    public const int Tec9Price = 500;
    public const int FiveSevenPrice = 500;

    private static readonly IReadOnlyDictionary<string, int> WeaponPrices =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["weapon_ak47"] = 2700,
            ["weapon_galilar"] = 1800,
            ["weapon_m4a1"] = 2900,
            ["weapon_m4a1_silencer"] = 2900,
            ["weapon_famas"] = 1950,
            ["weapon_awp"] = 4750,
            ["weapon_aug"] = 3300,
            ["weapon_sg556"] = 3000,
            ["weapon_ssg08"] = 1700,
            ["weapon_scar20"] = 5000,
            ["weapon_g3sg1"] = 5000,
            ["weapon_mac10"] = 1050,
            ["weapon_mp9"] = 1250,
            ["weapon_mp7"] = 1500,
            ["weapon_mp5sd"] = 1500,
            ["weapon_ump45"] = 1200,
            ["weapon_p90"] = 2350,
            ["weapon_bizon"] = 1400,
            ["weapon_nova"] = 1050,
            ["weapon_xm1014"] = 2000,
            ["weapon_sawedoff"] = 1100,
            ["weapon_mag7"] = 1300,
            ["weapon_negev"] = 1700,
            ["weapon_m249"] = 5200,
            ["weapon_deagle"] = 700,
            ["weapon_p250"] = 300,
            ["weapon_tec9"] = 500,
            ["weapon_fiveseven"] = 500,
            ["weapon_cz75a"] = 500,
            ["weapon_revolver"] = 600,
        };

    private static readonly IReadOnlyDictionary<string, int> UtilityPrices =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["smoke"] = SmokePrice,
            ["weapon_smokegrenade"] = SmokePrice,
            ["flash"] = FlashPrice,
            ["weapon_flashbang"] = FlashPrice,
            ["he"] = HePrice,
            ["weapon_hegrenade"] = HePrice,
            ["molotov"] = MolotovPrice,
            ["weapon_molotov"] = MolotovPrice,
            ["weapon_incgrenade"] = IncendiaryPrice,
            ["incendiary"] = IncendiaryPrice,
        };

    public static int GetWeaponPrice(string? weapon)
        => weapon is not null && WeaponPrices.TryGetValue(weapon, out int price)
            ? price
            : 0;

    public static int GetUtilityPrice(string? utility)
        => utility is not null && UtilityPrices.TryGetValue(utility, out int price)
            ? price
            : 0;

    public static ArmorLevel ClassifyArmor(int armorValue, bool hasHelmet)
        => armorValue < EffectiveArmorThreshold
            ? ArmorLevel.None
            : hasHelmet ? ArmorLevel.Full : ArmorLevel.Half;

    public static int CalculateEquipmentValue(PlayerEquipmentSnapshot snapshot)
    {
        int value = snapshot.ArmorValue > 0 ? KevlarPrice : 0;
        if (snapshot.HasHelmet)
            value += HelmetPrice;
        value += GetWeaponPrice(snapshot.PrimaryWeapon);
        value += GetWeaponPrice(snapshot.SecondaryWeapon);
        if (snapshot.HasDefuser)
            value += DefuserPrice;
        if (snapshot.Utility is not null)
        {
            foreach (var entry in snapshot.Utility)
                value += GetUtilityPrice(entry.Key) * Math.Max(0, entry.Value);
        }

        return value;
    }

    public static bool IsFullReady(
        PlayerEquipmentSnapshot snapshot,
        TeamSide side)
        => snapshot.HasEffectiveArmor
            && IsFullPrimary(snapshot.PrimaryWeapon, side);

    public static bool IsEcoLikely(
        IEnumerable<PlayerEquipmentSnapshot> players,
        TeamSide side)
    {
        var snapshots = players.ToArray();
        return snapshots.Length > 0
            && snapshots.Count(snapshot =>
                !snapshot.IsFullReady(side)
                && !snapshot.CanReachFullBuy(side))
                >= Math.Max(1, snapshots.Length - 1);
    }

    public static int GetFullBuyCompletionCost(
        PlayerEquipmentSnapshot snapshot,
        TeamSide side)
    {
        ArmorLevel currentArmor = ClassifyArmor(
            snapshot.ArmorValue,
            snapshot.HasHelmet);
        int armorCost = currentArmor switch
        {
            ArmorLevel.None => KevlarPrice + HelmetPrice,
            _ => 0,
        };
        string preferred = side == TeamSide.Terrorist
            ? "weapon_ak47"
            : "weapon_m4a1";
        int weaponCost = IsFullPrimary(snapshot.PrimaryWeapon, side)
            ? 0
            // CS2 does not trade the carried rifle in for a discount when a
            // new rifle is bought. A FAMAS/Galil therefore contributes zero
            // cash toward the replacement cost.
            : GetWeaponPrice(preferred);
        return armorCost + weaponCost;
    }

    public static EquipmentCombatTier ClassifyCombatTier(
        PlayerEquipmentSnapshot snapshot)
    {
        if (snapshot.PrimaryWeapon == "weapon_awp"
            || IsPreferredRifle(snapshot.PrimaryWeapon))
            return snapshot.HasEffectiveArmor
                ? EquipmentCombatTier.Full
                : EquipmentCombatTier.Mid;
        if (IsMidTierPrimary(snapshot.PrimaryWeapon))
            return EquipmentCombatTier.Mid;
        if (IsLowTierPrimary(snapshot.PrimaryWeapon))
            return EquipmentCombatTier.Low;
        if (snapshot.SecondaryWeapon is not null)
            return snapshot.HasEffectiveArmor
                ? EquipmentCombatTier.Pistol
                : EquipmentCombatTier.Pistol;
        return EquipmentCombatTier.Eco;
    }

    public static bool IsFullPrimary(string? weapon, TeamSide side)
        // A picked-up rifle is still a full-buy rifle. The owning side changes
        // the preferred replacement, not whether the current weapon is useful.
        => weapon is "weapon_awp"
            or "weapon_ak47"
            or "weapon_sg556"
            or "weapon_m4a1"
            or "weapon_m4a1_silencer"
            or "weapon_aug";

    public static bool IsPreferredRifle(string? weapon)
        => weapon is "weapon_ak47"
            or "weapon_m4a1"
            or "weapon_m4a1_silencer"
            or "weapon_aug"
            or "weapon_sg556";

    public static bool IsMidTierPrimary(string? weapon)
        => weapon is "weapon_galilar"
            or "weapon_famas"
            or "weapon_ssg08"
            or "weapon_scar20"
            or "weapon_g3sg1";

    public static bool IsLowTierPrimary(string? weapon)
        => weapon is "weapon_mac10"
            or "weapon_mp9"
            or "weapon_mp7"
            or "weapon_mp5sd"
            or "weapon_ump45"
            or "weapon_p90"
            or "weapon_bizon"
            or "weapon_nova"
            or "weapon_xm1014"
            or "weapon_sawedoff"
            or "weapon_mag7"
            or "weapon_negev"
            or "weapon_m249";
}
