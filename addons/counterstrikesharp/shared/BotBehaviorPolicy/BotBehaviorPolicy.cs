namespace BotBehaviorPolicy;

public readonly record struct SmokeVolume(float X, float Y, float Z, float Radius);

public static class VisibilityGeometry
{
    public static bool SegmentIntersectsAnySmoke(
        float startX,
        float startY,
        float startZ,
        float endX,
        float endY,
        float endZ,
        IReadOnlyList<SmokeVolume> smokes)
    {
        foreach (var smoke in smokes)
        {
            if (SegmentIntersectsSphere(
                    startX,
                    startY,
                    startZ,
                    endX,
                    endY,
                    endZ,
                    smoke.X,
                    smoke.Y,
                    smoke.Z,
                    smoke.Radius))
            {
                return true;
            }
        }

        return false;
    }

    public static bool SegmentIntersectsSphere(
        float startX,
        float startY,
        float startZ,
        float endX,
        float endY,
        float endZ,
        float centerX,
        float centerY,
        float centerZ,
        float radius)
    {
        if (radius <= 0f)
            return false;

        float dx = endX - startX;
        float dy = endY - startY;
        float dz = endZ - startZ;
        float lengthSquared = dx * dx + dy * dy + dz * dz;
        float t = lengthSquared <= float.Epsilon
            ? 0f
            : ((centerX - startX) * dx
                + (centerY - startY) * dy
                + (centerZ - startZ) * dz) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);

        float closestX = startX + t * dx;
        float closestY = startY + t * dy;
        float closestZ = startZ + t * dz;
        float offsetX = closestX - centerX;
        float offsetY = closestY - centerY;
        float offsetZ = closestZ - centerZ;
        return offsetX * offsetX + offsetY * offsetY + offsetZ * offsetZ
            <= radius * radius;
    }
}

public enum PurchaseArmor
{
    None,
    Half,
    Full,
}

public readonly record struct PrimaryPurchaseCandidate(
    string Weapon,
    int Price,
    PurchaseArmor Armor);

public static class RiflePurchasePolicy
{
    public static bool ShouldReplace(
        PrimaryPurchaseCandidate current,
        PrimaryPurchaseCandidate preferred)
        => current.Weapon is not ("weapon_awp" or "weapon_scar20" or "weapon_g3sg1")
            && Score(preferred) > Score(current);

    public static PrimaryPurchaseCandidate? SelectBestAffordable(
        IEnumerable<PrimaryPurchaseCandidate> candidates,
        int money,
        PurchaseArmor currentArmor)
    {
        var affordable = candidates
            .Where(candidate => IsCombatLegal(candidate))
            .Where(candidate => candidate.Armor >= currentArmor)
            .Where(candidate => TotalCost(candidate, currentArmor) <= money)
            .OrderByDescending(Score)
            .ThenByDescending(candidate => candidate.Armor)
            .ThenByDescending(candidate => candidate.Price)
            .ToList();

        return affordable.Count == 0 ? null : affordable[0];
    }

    public static int Score(PrimaryPurchaseCandidate candidate)
    {
        int weaponScore = candidate.Weapon switch
        {
            "weapon_awp" => 95,
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" => 100,
            "weapon_aug" or "weapon_sg556" => 96,
            "weapon_galilar" or "weapon_famas" => 82,
            "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" => 70,
            "weapon_mp9" or "weapon_mac10" or "weapon_mp7"
                or "weapon_mp5sd" or "weapon_ump45" or "weapon_bizon" => 35,
            "weapon_p90" or "weapon_nova" or "weapon_xm1014"
                or "weapon_sawedoff" or "weapon_mag7" => 30,
            _ => 24,
        };

        int armorScore = candidate.Armor switch
        {
            PurchaseArmor.Full => 50,
            PurchaseArmor.Half => 20,
            _ => 0,
        };
        return weaponScore + armorScore;
    }

    public static bool IsCombatLegal(PrimaryPurchaseCandidate candidate)
        => candidate.Price > 0
            && (candidate.Weapon == "weapon_awp"
                || !IsRifle(candidate.Weapon)
                || candidate.Armor != PurchaseArmor.None);

    public static bool IsRifle(string weapon)
        => weapon is "weapon_ak47"
            or "weapon_m4a1"
            or "weapon_m4a1_silencer"
            or "weapon_aug"
            or "weapon_sg556"
            or "weapon_galilar"
            or "weapon_famas"
            or "weapon_ssg08"
            or "weapon_awp"
            or "weapon_scar20"
            or "weapon_g3sg1";

    public static bool IsPrimaryWeapon(string weapon)
        => IsRifle(weapon)
            || weapon is "weapon_negev"
                or "weapon_m249"
                or "weapon_mp9"
                or "weapon_mac10"
                or "weapon_mp7"
                or "weapon_mp5sd"
                or "weapon_ump45"
                or "weapon_bizon"
                or "weapon_p90"
                or "weapon_nova"
                or "weapon_xm1014"
                or "weapon_sawedoff"
                or "weapon_mag7";

    public static int ArmorUpgradeCost(PurchaseArmor current, PurchaseArmor target)
    {
        if (target <= current)
            return 0;

        return (current, target) switch
        {
            (PurchaseArmor.None, PurchaseArmor.Half) => 650,
            (PurchaseArmor.None, PurchaseArmor.Full) => 1000,
            (PurchaseArmor.Half, PurchaseArmor.Full) => 350,
            _ => 0,
        };
    }

    public static int TotalCost(
        PrimaryPurchaseCandidate candidate,
        PurchaseArmor currentArmor)
        => candidate.Price + ArmorUpgradeCost(currentArmor, candidate.Armor);
}
