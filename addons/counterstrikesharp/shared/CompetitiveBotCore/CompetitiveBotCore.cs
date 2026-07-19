namespace CompetitiveBotCore;

public enum BotMatchProfile
{
    Competitive,
    Arcade,
    Legacy,
}

public static class ProfilePolicy
{
    public static BotMatchProfile Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "arcade" => BotMatchProfile.Arcade,
            "legacy" => BotMatchProfile.Legacy,
            _ => BotMatchProfile.Competitive,
        };

    public static string NormalizeNadeMode(BotMatchProfile profile, string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        if (normalized is not ("off" or "normal" or "more" or "max"))
            normalized = "normal";

        return profile == BotMatchProfile.Competitive && normalized is "more" or "max"
            ? "normal"
            : normalized;
    }

    public static bool IsCompetitive(BotMatchProfile profile) => profile == BotMatchProfile.Competitive;

    public static BotMatchProfile Resolve(BotMatchProfile configured, bool entertainmentMode)
        => configured == BotMatchProfile.Competitive && entertainmentMode
            ? BotMatchProfile.Arcade
            : configured;
}

public static class ProfileConfig
{
    public const string FileName = "bot_improver_profile.cfg";

    public static string DefaultPath(string gameDirectory)
        => Path.Combine(gameDirectory, "cfg", FileName);

    public static BotMatchProfile Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return BotMatchProfile.Competitive;

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("bot_improver_profile", StringComparison.OrdinalIgnoreCase))
                    return ProfilePolicy.Parse(parts[1]);
            }
        }
        catch
        {
            // A missing or malformed profile must not make the plugin unload.
        }

        return BotMatchProfile.Competitive;
    }
}

public enum TeamSide
{
    Terrorist,
    CounterTerrorist,
}

public enum BuyPhase
{
    Pistol,
    Eco,
    HalfBuy,
    ForceBuy,
    FullBuy,
    Bonus,
    Save,
    LastRound,
}

public sealed record TeamEconomySnapshot(
    TeamSide Side,
    IReadOnlyList<int> Money,
    bool IsPistolRound,
    bool IsLastRound,
    bool ForceBuySignal,
    bool OpponentEcoLikely);

public sealed record PlayerBuyPlan(
    BuyPhase Phase,
    string? PrimaryWeapon,
    bool BuysArmor,
    bool BuysHelmet,
    bool BuysDefuser,
    IReadOnlyList<string> Utility,
    int EstimatedCost)
{
    public bool HasCoreWeapon => PrimaryWeapon is not null;
}

public static class BuyPlanner
{
    public const int KevlarPrice = 650;
    public const int HelmetUpgradePrice = 350;
    public const int DefuserPrice = 400;
    public const int SmokePrice = 300;
    public const int FlashPrice = 200;
    public const int HePrice = 300;
    public const int MolotovPrice = 400;
    public const int IncendiaryPrice = 500;

    private const int AkPrice = 2700;
    private const int GalilPrice = 1800;
    private const int M4Price = 2900;
    private const int FamasPrice = 1950;
    private const int AwpPrice = 4750;

    public static BuyPhase Classify(TeamEconomySnapshot snapshot)
    {
        if (snapshot.IsLastRound) return BuyPhase.LastRound;
        if (snapshot.IsPistolRound) return BuyPhase.Pistol;

        int rifleArmorCost = CoreRiflePrice(snapshot.Side) + KevlarPrice;
        int fullBuyers = snapshot.Money.Count(money => money >= rifleArmorCost);
        int teamSize = Math.Max(1, snapshot.Money.Count);
        int fullBuyThreshold = Math.Max(1, (int)Math.Ceiling(teamSize * 0.80d));
        int halfBuyThreshold = Math.Max(1, (int)Math.Ceiling(teamSize * 0.60d));
        if (fullBuyers >= fullBuyThreshold) return BuyPhase.FullBuy;
        if (fullBuyers >= halfBuyThreshold) return BuyPhase.HalfBuy;
        if (snapshot.ForceBuySignal && snapshot.Money.Any(money => money >= KevlarPrice + 300))
            return BuyPhase.ForceBuy;

        return BuyPhase.Eco;
    }

    public static PlayerBuyPlan BuildPlayerPlan(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool designatedAwper,
        bool opponentEcoLikely)
    {
        money = Math.Max(0, money);
        if (phase is BuyPhase.Pistol or BuyPhase.Eco or BuyPhase.Save)
            return BuildLowBuyPlan(side, phase, money, opponentEcoLikely);

        int remaining = money;
        bool armor = remaining >= KevlarPrice;
        if (armor) remaining -= KevlarPrice;

        string? primary = null;
        int primaryCost = 0;

        // An AWP is a role purchase, never a generic fallback. It must not
        // consume the money required for the team's normal rifle plan.
        if (designatedAwper && remaining >= AwpPrice + SmokePrice)
        {
            primary = "weapon_awp";
            primaryCost = AwpPrice;
        }
        else
        {
            (primary, primaryCost) = SelectPrimary(side, phase, remaining);
        }

        if (primary is not null)
            remaining -= primaryCost;

        bool helmet = side == TeamSide.CounterTerrorist
            && primary is not null
            && opponentEcoLikely
            && remaining >= HelmetUpgradePrice;
        if (helmet) remaining -= HelmetUpgradePrice;

        bool defuser = side == TeamSide.CounterTerrorist
            && primary is not null
            && remaining >= DefuserPrice
            && phase is BuyPhase.FullBuy or BuyPhase.LastRound;
        if (defuser) remaining -= DefuserPrice;

        var utility = new List<string>();
        if (primary is not null && (phase is BuyPhase.FullBuy or BuyPhase.LastRound))
        {
            if (remaining >= SmokePrice)
            {
                utility.Add("smoke");
                remaining -= SmokePrice;
            }

            if (remaining >= FlashPrice)
            {
                utility.Add("flash");
                remaining -= FlashPrice;
                if (phase == BuyPhase.LastRound && remaining >= FlashPrice)
                {
                    utility.Add("flash");
                    remaining -= FlashPrice;
                }
            }

            if (remaining >= HePrice)
            {
                utility.Add("he");
                remaining -= HePrice;
            }

            int firePrice = side == TeamSide.Terrorist ? MolotovPrice : IncendiaryPrice;
            if (remaining >= firePrice)
            {
                utility.Add("molotov");
                remaining -= firePrice;
            }
        }

        return new PlayerBuyPlan(
            phase,
            primary,
            armor,
            helmet,
            defuser,
            utility,
            money - remaining);
    }

    private static PlayerBuyPlan BuildLowBuyPlan(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool opponentEcoLikely)
    {
        // Pure eco means preserving the next rifle round. Half/force decisions
        // are handled by the caller with a non-low phase.
        return new PlayerBuyPlan(
            phase,
            null,
            false,
            false,
            false,
            Array.Empty<string>(),
            0);
    }

    private static (string? Weapon, int Cost) SelectPrimary(TeamSide side, BuyPhase phase, int remaining)
    {
        if (side == TeamSide.Terrorist)
        {
            if (remaining >= AkPrice) return ("weapon_ak47", AkPrice);
            if (remaining >= GalilPrice) return ("weapon_galilar", GalilPrice);
            if (phase is BuyPhase.HalfBuy or BuyPhase.ForceBuy && remaining >= 1050)
                return ("weapon_mac10", 1050);
        }
        else
        {
            if (remaining >= M4Price) return ("weapon_m4a1", M4Price);
            if (remaining >= FamasPrice) return ("weapon_famas", FamasPrice);
            if (phase is BuyPhase.HalfBuy or BuyPhase.ForceBuy && remaining >= 1250)
                return ("weapon_mp9", 1250);
        }

        return (null, 0);
    }

    private static int CoreRiflePrice(TeamSide side)
        => side == TeamSide.Terrorist ? AkPrice : M4Price;
}

public sealed class DefaultBuyPlanner : IBuyPlanner
{
    public BuyPhase Classify(TeamEconomySnapshot snapshot) => BuyPlanner.Classify(snapshot);

    public PlayerBuyPlan BuildPlayerPlan(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool designatedAwper,
        bool opponentEcoLikely)
        => BuyPlanner.BuildPlayerPlan(side, phase, money, designatedAwper, opponentEcoLikely);
}

public static class RoundSchedule
{
    public static bool IsLastRoundOfHalf(int roundsPlayed, int maxRounds = 24, int overtimeMaxRounds = 6)
    {
        if (roundsPlayed < 0) return false;
        maxRounds = maxRounds > 0 ? maxRounds : 24;
        overtimeMaxRounds = overtimeMaxRounds > 0 ? overtimeMaxRounds : 6;

        int half = Math.Max(1, maxRounds / 2);
        if (roundsPlayed == half - 1 || roundsPlayed == maxRounds - 1)
            return true;

        if (roundsPlayed < maxRounds) return false;
        int overtimeHalf = Math.Max(1, overtimeMaxRounds / 2);
        return (roundsPlayed - maxRounds + 1) % overtimeHalf == 0;
    }

    public static bool IsSecondToLastRoundOfHalf(int roundsPlayed, int maxRounds = 24)
    {
        if (roundsPlayed < 0) return false;
        maxRounds = maxRounds > 0 ? maxRounds : 24;
        int half = Math.Max(1, maxRounds / 2);
        return roundsPlayed == half - 2 || roundsPlayed == maxRounds - 2;
    }
}

public enum RoundPhase
{
    Freeze,
    Live,
    BombPlanted,
    Retake,
    Save,
    RoundEnd,
}

public sealed record RoundContext(
    int RoundNumber,
    int Half,
    int TeamScore,
    int OpponentScore,
    bool IsLastRound,
    int ConsecutiveLosses,
    bool BombPlanted,
    string? KnownBombsite,
    int AliveTeam,
    int AliveOpponent,
    RoundPhase Phase);

public enum BotInfoSource
{
    None,
    Visual,
    Sound,
    Damage,
    Bomb,
}

public sealed record EnemyMemory(
    int EnemyId,
    float X,
    float Y,
    float Z,
    BotInfoSource Source,
    float RecordedAt,
    float Confidence,
    bool IsHistorical);

public interface IRoundState
{
    RoundContext Current { get; }
    void SetPhase(RoundPhase phase);
    void SetBomb(string? site, bool planted);
}

public interface IBotMemory
{
    void Record(int botId, EnemyMemory memory);
    bool TryGet(int botId, int enemyId, float now, out EnemyMemory memory);
    void Forget(int botId);
}

public interface IBuyPlanner
{
    BuyPhase Classify(TeamEconomySnapshot snapshot);
    PlayerBuyPlan BuildPlayerPlan(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool designatedAwper,
        bool opponentEcoLikely);
}

public interface IUtilityLedger
{
    int ConsumedTotal { get; }
    int Remaining(UtilityType type);
    bool TryConsume(UtilityType type, UtilitySource source);
    bool Refund(UtilityType type);
}

public interface ITacticalDirector
{
    TacticalDirective Decide(
        TeamSide side,
        RoundContext context,
        bool hasReliableEnemyInfo,
        bool isInformationRole);
}

public interface IVisibilityPolicy
{
    bool CanOverrideAim(
        bool rayTraceAvailable,
        bool nativeTargetVisible,
        bool smokeObscured,
        bool infoIsFresh,
        bool isHistoricalPosition);
}

public sealed class RoundState : IRoundState
{
    public RoundContext Current { get; private set; } = new(
        RoundNumber: 0,
        Half: 1,
        TeamScore: 0,
        OpponentScore: 0,
        IsLastRound: false,
        ConsecutiveLosses: 0,
        BombPlanted: false,
        KnownBombsite: null,
        AliveTeam: 5,
        AliveOpponent: 5,
        Phase: RoundPhase.Freeze);

    public void SetPhase(RoundPhase phase) => Current = Current with { Phase = phase };

    public void SetBomb(string? site, bool planted)
        => Current = Current with
        {
            BombPlanted = planted,
            KnownBombsite = site,
            Phase = planted ? RoundPhase.BombPlanted : Current.Phase,
        };
}

public sealed class BotMemory : IBotMemory
{
    private readonly Dictionary<(int BotId, int EnemyId), EnemyMemory> _entries = new();

    public void Record(int botId, EnemyMemory memory)
        => _entries[(botId, memory.EnemyId)] = memory;

    public bool TryGet(int botId, int enemyId, float now, out EnemyMemory memory)
    {
        if (!_entries.TryGetValue((botId, enemyId), out memory!))
            return false;

        float age = Math.Max(0f, now - memory.RecordedAt);
        float decayedConfidence = memory.Confidence * MathF.Exp(-age / 3.5f);
        bool historical = memory.IsHistorical || age > 1.5f;
        memory = memory with { Confidence = decayedConfidence, IsHistorical = historical };
        return decayedConfidence > 0.05f;
    }

    public void Forget(int botId)
    {
        foreach (var key in _entries.Keys.Where(key => key.BotId == botId).ToList())
            _entries.Remove(key);
    }
}

public sealed class CompetitiveTacticalDirector : ITacticalDirector
{
    public TacticalDirective Decide(
        TeamSide side,
        RoundContext context,
        bool hasReliableEnemyInfo,
        bool isInformationRole)
    {
        if (context.Phase == RoundPhase.BombPlanted || context.BombPlanted)
            return side == TeamSide.CounterTerrorist
                ? TacticalDirective.Retake
                : TacticalDirective.PostPlant;

        if (context.Phase == RoundPhase.Save)
            return TacticalDirective.Save;

        if (side == TeamSide.CounterTerrorist)
        {
            if (!hasReliableEnemyInfo)
                return TacticalDirective.HoldAnchor;
            return isInformationRole
                ? TacticalDirective.ProbeAndFallBack
                : TacticalDirective.Rotate;
        }

        return hasReliableEnemyInfo
            ? TacticalDirective.Execute
            : TacticalDirective.Stage;
    }
}

public enum TacticalDirective
{
    HoldAnchor,
    ProbeAndFallBack,
    Rotate,
    Stage,
    Execute,
    Retake,
    PostPlant,
    Save,
}

public sealed class CompetitiveVisibilityPolicy : IVisibilityPolicy
{
    public bool CanOverrideAim(
        bool rayTraceAvailable,
        bool nativeTargetVisible,
        bool smokeObscured,
        bool infoIsFresh,
        bool isHistoricalPosition)
        => rayTraceAvailable
            && nativeTargetVisible
            && !smokeObscured
            && infoIsFresh
            && !isHistoricalPosition;
}

public enum UtilityType
{
    Smoke,
    Flash,
    He,
    Molotov,
}

public enum UtilitySource
{
    LineupThrow,
    PlantSmoke,
    DefuseSmoke,
    FlashSupport,
    MolotovEscape,
    Retaliation,
}

public sealed record UtilityInventory(int Smoke, int Flash, int He, int Molotov)
{
    public int Total => Math.Max(0, Smoke) + Math.Max(0, Flash) + Math.Max(0, He) + Math.Max(0, Molotov);
}

public sealed class UtilityLedger : IUtilityLedger
{
    private readonly Dictionary<UtilityType, int> _remaining;
    private readonly Dictionary<UtilityType, int> _consumed = new();

    public UtilityLedger(UtilityInventory inventory)
    {
        _remaining = new Dictionary<UtilityType, int>
        {
            [UtilityType.Smoke] = Math.Max(0, inventory.Smoke),
            [UtilityType.Flash] = Math.Max(0, inventory.Flash),
            [UtilityType.He] = Math.Max(0, inventory.He),
            [UtilityType.Molotov] = Math.Max(0, inventory.Molotov),
        };
    }

    public int ConsumedTotal => _consumed.Values.Sum();

    public int Remaining(UtilityType type) => _remaining[type];

    public bool TryConsume(UtilityType type, UtilitySource source)
    {
        if (_remaining[type] <= 0) return false;
        _remaining[type]--;
        _consumed[type] = _consumed.GetValueOrDefault(type) + 1;
        return true;
    }

    public bool Refund(UtilityType type)
    {
        if (!_consumed.TryGetValue(type, out int consumed) || consumed <= 0)
            return false;

        _consumed[type] = consumed - 1;
        _remaining[type]++;
        return true;
    }
}
