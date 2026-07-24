namespace CompetitiveBotCore;

public enum BuyRole
{
    Auto,
    TEntry,
    TSupport,
    TLurk,
    CtAnchor,
    CtRotator,
    AWPer,
}

public readonly record struct TeamUtilityDemand(
    int Smoke,
    int Flash,
    int He,
    int Fire,
    int Defuser,
    int PersonalUtilityTarget);

public static class TeamUtilityDemandPolicy
{
    public static TeamUtilityDemand ForPhase(
        BuyPhase phase,
        int teamSize,
        bool mustWin = false)
    {
        int n = Math.Max(1, teamSize);
        if (mustWin)
            return new(
                n,
                Ceiling(n * 1.6d),
                Ceiling(n * 0.6d),
                Ceiling(n * 0.6d),
                Ceiling(n * 0.5d),
                4);

        return phase switch
        {
            BuyPhase.FullBuy or BuyPhase.LastRound => new(
                n,
                Ceiling(n * 1.4d),
                Ceiling(n * 0.6d),
                Ceiling(n * 0.6d),
                Ceiling(n * 0.4d),
                4),
            BuyPhase.HalfBuy => new(
                Ceiling(n * 0.8d),
                n,
                Ceiling(n * 0.4d),
                Ceiling(n * 0.4d),
                Ceiling(n * 0.3d),
                3),
            BuyPhase.ForceBuy => new(
                Ceiling(n * 0.6d),
                Ceiling(n * 0.8d),
                1,
                1,
                1,
                2),
            _ => new(0, 0, 0, 0, 0, 0),
        };
    }

    public static IReadOnlyList<string> RolePackage(
        BuyRole role,
        TeamSide side)
        => RolePackages(role, side)[0];

    public static IReadOnlyList<IReadOnlyList<string>> RolePackages(
        BuyRole role,
        TeamSide side)
        => role switch
        {
            BuyRole.TEntry =>
            [
                ["smoke", "flash", "flash", "he"],
                ["smoke", "flash", "he", "molotov"],
            ],
            BuyRole.TSupport =>
            [
                ["smoke", "flash", "flash", "molotov"],
                ["smoke", "flash", "he", "molotov"],
            ],
            BuyRole.TLurk =>
            [
                ["smoke", "flash", "molotov", "he"],
                ["smoke", "flash", "flash", "molotov"],
            ],
            BuyRole.CtAnchor =>
            [
                ["smoke", "molotov", "he", "flash"],
                ["smoke", "flash", "he", "molotov"],
            ],
            BuyRole.CtRotator =>
            [
                ["smoke", "flash", "flash", "he"],
                ["smoke", "flash", "he", "molotov"],
            ],
            BuyRole.AWPer =>
            [
                ["smoke", "flash", "flash", "he"],
                ["smoke", "flash", "he", "molotov"],
            ],
            _ =>
            [
                ["smoke", "flash", "he", "molotov"],
            ],
        };

    private static int Ceiling(double value)
        => Math.Max(1, (int)Math.Ceiling(value));
}
