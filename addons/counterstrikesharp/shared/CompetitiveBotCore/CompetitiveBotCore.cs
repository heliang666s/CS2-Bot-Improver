using System.Collections.Concurrent;

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

    public static bool ShouldEnforceUtilityInventory(BotMatchProfile profile)
        => profile == BotMatchProfile.Competitive;

    public static BotMatchProfile Resolve(BotMatchProfile configured, bool entertainmentMode)
        => configured == BotMatchProfile.Competitive && entertainmentMode
            ? BotMatchProfile.Arcade
            : configured;
}

public static class AimWeaponPolicy
{
    public readonly record struct PistolAimAdjustment(
        bool ApplyOverride,
        float ReactionDelaySeconds,
        float TargetJitterUnits,
        int JitterSeed);

    public static bool IsCompetitivePistolRound(
        int roundsPlayed,
        int maxRounds,
        int overtimeMaxRounds)
        => !RoundSchedule.IsOvertimeRound(roundsPlayed, maxRounds)
            && RoundSchedule.IsFirstRoundOfHalf(
                roundsPlayed,
                maxRounds,
                overtimeMaxRounds);

    public static bool IsCompetitivePistolRound(BotMatchProfile profile, BuyPhase phase)
        => profile == BotMatchProfile.Competitive && phase == BuyPhase.Pistol;

    public static PistolAimAdjustment GetPistolAimAdjustment(
        BotMatchProfile profile,
        BuyPhase phase,
        int botSlot,
        int targetId,
        float now)
    {
        if (!IsCompetitivePistolRound(profile, phase))
            return new(true, 0f, 0f, 0);

        int seed = HashCode.Combine(botSlot, targetId, (int)(now * 10f));
        int bucket = Math.Abs(seed) % 20;
        return new(
            ApplyOverride: bucket >= 3,
            ReactionDelaySeconds: 0.04f + (bucket % 5) * 0.015f,
            TargetJitterUnits: 2f + (bucket % 3) * 1.5f,
            JitterSeed: seed);
    }

    public static bool ShouldApplyPistolOverride(
        BotMatchProfile profile,
        BuyPhase phase,
        int botSlot,
        int targetId,
        float now)
    {
        if (!IsCompetitivePistolRound(profile, phase))
            return true;

        // Keep a bounded amount of native variance in pistol rounds. The
        // inputs are deterministic, so bots remain reproducible in tests and
        // do not synchronize on one shared RNG.
        return GetPistolAimAdjustment(profile, phase, botSlot, targetId, now).ApplyOverride;
    }

    public static bool ShouldUseHeadFirstInMixed(
        BotMatchProfile profile,
        string? weaponName)
        => false;

    public static bool IsPistol(string? weaponName)
        => weaponName?.Trim().ToLowerInvariant() switch
        {
            "weapon_glock" => true,
            "weapon_usp_silencer" => true,
            "weapon_hkp2000" => true,
            "weapon_p250" => true,
            "weapon_fiveseven" => true,
            "weapon_tec9" => true,
            "weapon_cz75a" => true,
            "weapon_deagle" => true,
            "weapon_elite" => true,
            _ => false,
        };
}

public static class DefuseDecisionPolicy
{
    public static bool ShouldFakeDefuse(
        BotMatchProfile profile,
        bool hasLiveEnemy,
        bool hasDefuser,
        double randomRoll)
    {
        if (!hasLiveEnemy)
            return false;

        double chance = hasDefuser ? 0.10 : 0.66;
        return randomRoll >= 0.0 && randomRoll < chance;
    }

    public static bool ShouldDeployDefuseSmoke(
        BotMatchProfile profile,
        bool hasSmoke,
        bool hasDefuser,
        double randomRoll)
    {
        if (profile == BotMatchProfile.Competitive)
            return hasSmoke;

        return hasDefuser || (randomRoll >= 0.0 && randomRoll < 0.33);
    }
}

public static class CompetitiveTacticalPolicy
{
    public static RoundPhase ResolveRoundPhase(bool freezePeriod, bool bombPlanted)
        => freezePeriod
            ? RoundPhase.Freeze
            : bombPlanted
                ? RoundPhase.BombPlanted
                : RoundPhase.Live;

    public static bool ShouldTrackCtBot(bool isBot, bool takenOver)
        => isBot && !takenOver;

    public static bool ShouldRecordCtDeath(
        BotMatchProfile profile,
        bool isCounterTerroristBot,
        bool takenOver)
        => profile == BotMatchProfile.Competitive
            && ShouldTrackCtBot(isCounterTerroristBot, takenOver);
}

public enum CtGambleSite
{
    None,
    A,
    B,
}

public static class CtTacticalExecutionPolicy
{
    public static bool ShouldAllowNativeActive(CtTacticalDecision decision)
        => decision.State is not (CtTacticalState.Withdraw or CtTacticalState.Save);
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

public enum PurchaseIntent
{
    Pistol,
    Standard,
    AllIn,
    Save,
    LastRound,
}

public enum PistolBuyRole
{
    Auto,
    ArmorEntry,
    Firepower,
    UtilitySupport,
    DefuserSupport,
}

public static class PistolBuyRolePolicy
{
    public static PistolBuyRole ForBotOrdinal(
        TeamSide side,
        int ordinal,
        int botCount)
    {
        if (ordinal == 0)
            return PistolBuyRole.ArmorEntry;
        if (ordinal == 1 || (side == TeamSide.Terrorist && ordinal == 2))
            return PistolBuyRole.Firepower;
        if (side == TeamSide.CounterTerrorist && ordinal == botCount - 1)
            return PistolBuyRole.DefuserSupport;
        return PistolBuyRole.UtilitySupport;
    }
}

public sealed record WeaponSwitchRequest(
    int Slot,
    string Weapon,
    string? ReplaceWeapon = null,
    int Attempt = 0);

public readonly record struct WeaponSwitchSettlement(
    bool ShouldRollbackReplacement,
    bool ShouldRestoreOriginal,
    string Reason);

public static class WeaponSwitchSettlementPolicy
{
    public static WeaponSwitchSettlement Evaluate(
        bool newWeaponSelected,
        bool oldWeaponSelected,
        bool replacementReselected)
    {
        if (!newWeaponSelected)
            return new(false, false, "new-switch-failed-retry");

        if (!oldWeaponSelected)
            return new(true, false, "old-switch-failed-before-drop");

        if (!replacementReselected)
            return new(true, true, "replacement-switch-failed-after-drop");

        return new(false, false, "replacement-complete");
    }
}

public static class BotWeaponSwitchQueue
{
    private static readonly ConcurrentQueue<WeaponSwitchRequest> Requests = new();

    public static void Enqueue(int slot, string weapon, string? replaceWeapon = null)
    {
        if (slot >= 0 && !string.IsNullOrWhiteSpace(weapon))
            Requests.Enqueue(new WeaponSwitchRequest(slot, weapon, replaceWeapon));
    }

    public static void Requeue(WeaponSwitchRequest request)
        => Requests.Enqueue(request);

    public static bool TryRequeueFailed(WeaponSwitchRequest request)
    {
        if (request.Attempt >= 3)
            return false;

        Requests.Enqueue(request with { Attempt = request.Attempt + 1 });
        return true;
    }

    public static IReadOnlyList<WeaponSwitchRequest> Drain()
    {
        var requests = new List<WeaponSwitchRequest>();
        while (Requests.TryDequeue(out var request))
            requests.Add(request);
        return requests;
    }
}

public sealed record TeamEconomySnapshot(
    TeamSide Side,
    IReadOnlyList<int> Money,
    bool IsPistolRound,
    bool IsLastRound,
    bool ForceBuySignal,
    bool OpponentEcoLikely)
{
    public bool IsOvertimeFirstRound { get; init; }
}

public sealed class TacticalEconomyPhaseCache
{
    public bool IsCaptured { get; private set; }
    public BuyPhase CtPhase { get; private set; } = BuyPhase.FullBuy;
    public BuyPhase OpponentPhase { get; private set; } = BuyPhase.FullBuy;

    public void Reset()
    {
        IsCaptured = false;
        CtPhase = BuyPhase.FullBuy;
        OpponentPhase = BuyPhase.FullBuy;
    }

    public void Capture(
        TeamEconomySnapshot ctSnapshot,
        TeamEconomySnapshot opponentSnapshot)
    {
        if (IsCaptured) return;

        CtPhase = BuyPlanner.Classify(ctSnapshot);
        OpponentPhase = BuyPlanner.Classify(opponentSnapshot);
        IsCaptured = true;
    }

    public void Restore(BuyPhase ctPhase, BuyPhase opponentPhase)
    {
        CtPhase = ctPhase;
        OpponentPhase = opponentPhase;
        IsCaptured = true;
    }
}

public enum ArmorLevel
{
    None,
    Half,
    Full,
}

public sealed record PlayerBuyPlan(
    BuyPhase Phase,
    ArmorLevel ArmorLevel,
    string? PrimaryWeapon,
    string? SecondaryWeapon,
    bool BuysHelmet,
    bool BuysDefuser,
    IReadOnlyList<string> Utility,
    int EstimatedCost)
{
    public bool BuysArmor => ArmorLevel != ArmorLevel.None;
    public bool HasCoreWeapon => PrimaryWeapon is not null;
    public int Tier { get; init; }
    public PurchaseIntent Intent { get; init; } = PurchaseIntent.Standard;

    // A team plan may deliberately leave the primary slot empty while the
    // bounded planner looks for a donor. This is not an executable armor-only
    // rifle plan: the transfer planner must either fill it or select another
    // candidate that the bot can afford itself.
    public bool AcceptsTeamPrimary { get; init; }
    public string? RequestedPrimaryWeapon { get; init; }

    // Carried weapons are replaced only through the main-thread settlement
    // queue. Keeping the old weapon on the immutable plan prevents a direct
    // GiveNamedItem path from silently dropping it.
    public string? ReplacePrimaryWeapon { get; init; }
}

public sealed record TeamBuyPlan(
    IReadOnlyDictionary<int, PlayerBuyPlan> BotPlans,
    IReadOnlyDictionary<int, PlayerBuyPlan> HumanObservations,
    int MinTier,
    int MaxTier)
{
    public bool IsBalanced => MaxTier - MinTier <= 1;
    public int TotalCost => BotPlans.Values.Sum(plan => plan.EstimatedCost);
    public TeamBuyMode BuyMode { get; init; } = TeamBuyMode.Full;
    public IReadOnlyList<TransferPlan> Transfers { get; init; } = Array.Empty<TransferPlan>();
    public IReadOnlyList<NextRoundScenarioPrediction> Forecasts { get; init; } = Array.Empty<NextRoundScenarioPrediction>();
    public int HumanTierPenalty { get; init; }
    public string Reason { get; init; } = string.Empty;
    public PurchaseIntent Intent { get; init; } = PurchaseIntent.Standard;
}

public sealed record TransferParticipant(
    int Slot,
    bool IsBot,
    int Money,
    PlayerBuyPlan Plan,
    string? CurrentPrimary);

public sealed record TransferPlan(
    int Donor,
    int Recipient,
    string Item,
    int Cost,
    string Reason);

public readonly record struct TransferPreflightDecision(
    bool ShouldTransfer,
    bool KeepExistingWeapon,
    string Reason);

public static class TransferPreflightPolicy
{
    public static TransferPreflightDecision Evaluate(
        bool recipientHasPrimary,
        bool recipientHasTransferItem)
    {
        if (recipientHasPrimary)
            return new(false, true, "recipient-already-armed");

        if (recipientHasTransferItem)
            return new(false, true, "recipient-already-has-transfer-item");

        return new(true, false, "recipient-primary-slot-free");
    }
}

public static class TeamTransferPlanner
{
    public static IReadOnlyList<TransferPlan> BuildTransfers(
        IReadOnlyList<TransferParticipant> participants)
    {
        var transfers = new List<TransferPlan>();
        var donorSpent = participants.ToDictionary(participant => participant.Slot, _ => 0);
        var recipients = new HashSet<int>();

        foreach (var recipient in participants
                     .Where(participant => participant.IsBot
                         && participant.CurrentPrimary is null
                         && participant.Plan.ArmorLevel != ArmorLevel.None
                         && (participant.Plan.PrimaryWeapon is not null
                             || participant.Plan.AcceptsTeamPrimary))
                     .OrderByDescending(participant => participant.Plan.Tier)
                     .ThenBy(participant => participant.Slot))
        {
            if (donorSpent[recipient.Slot] > 0 || recipients.Contains(recipient.Slot))
                continue;

            string? requestedWeapon = recipient.Plan.PrimaryWeapon
                ?? recipient.Plan.RequestedPrimaryWeapon;
            int cost = BuyPlanner.GetWeaponCost(requestedWeapon);
            if (cost <= 0)
                continue;

            // A donated primary is only valid when the recipient can still
            // buy the armor selected by its plan. Without this preflight the
            // executor could commit armor-only and leave the bot waiting for
            // a weapon that was never legally deliverable.
            int recipientArmorCost = recipient.Plan.AcceptsTeamPrimary
                ? recipient.Plan.EstimatedCost
                : BuyPlanner.GetArmorUpgradeCost(
                    ArmorLevel.None,
                    recipient.Plan.ArmorLevel);
            if (recipient.Money < recipientArmorCost)
                continue;

            var donor = participants
                .Where(candidate => candidate.IsBot
                    && candidate.Slot != recipient.Slot
                    && candidate.CurrentPrimary is null
                    && candidate.Plan.PrimaryWeapon is not null)
                .Where(candidate => !recipients.Contains(candidate.Slot))
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Item = SelectGiftWeapon(
                        candidate.Plan.PrimaryWeapon!,
                        requestedWeapon!),
                })
                .Where(candidate => candidate.Item is not null)
                .Select(candidate => new
                {
                    candidate.Candidate,
                    candidate.Item,
                    Cost = BuyPlanner.GetWeaponCost(candidate.Item!),
                })
                .Select(candidate => new
                {
                    candidate.Candidate,
                    candidate.Item,
                    candidate.Cost,
                    Surplus = candidate.Candidate.Money
                        - OwnPurchaseCost(candidate.Candidate)
                        - donorSpent[candidate.Candidate.Slot],
                })
                .Where(candidate => candidate.Surplus >= candidate.Cost
                    && candidate.Candidate.Plan.Tier >= recipient.Plan.Tier - 1)
                .OrderByDescending(candidate => candidate.Surplus - candidate.Cost)
                .ThenBy(candidate => candidate.Candidate.Slot)
                .FirstOrDefault();

            if (donor is null)
                continue;

            donorSpent[donor.Candidate.Slot] += donor.Cost;
            recipients.Add(recipient.Slot);
            transfers.Add(new TransferPlan(
                donor.Candidate.Slot,
                recipient.Slot,
                donor.Item!,
                donor.Cost,
                "donor-surplus-within-tier"));
        }

        return transfers;
    }

    private static int OwnPurchaseCost(TransferParticipant participant)
        => Math.Max(0, participant.Plan.EstimatedCost);

    private static string? SelectGiftWeapon(string donorWeapon, string recipientWeapon)
    {
        if (IsPreferredRifle(donorWeapon)
            && !IsPreferredRifle(recipientWeapon))
            return donorWeapon;

        return recipientWeapon;
    }

    private static bool IsPreferredRifle(string? weapon)
        => weapon is "weapon_ak47"
            or "weapon_m4a1"
            or "weapon_m4a1_silencer";
}

public sealed record EconomyPlayerSeed(int Slot, TeamSide Side, bool IsBot);

public sealed class EconomyPlayerFacts
{
    public EconomyPlayerFacts(int slot, TeamSide side, bool isBot)
    {
        Slot = slot;
        Side = side;
        IsBot = isBot;
    }

    public int Slot { get; }
    public TeamSide Side { get; }
    public bool IsBot { get; }
    public int Kills { get; internal set; }
    public bool Died { get; internal set; }
    public bool AliveAtEnd { get; internal set; }
    public bool SavedPrimary { get; internal set; }
    public bool IsPlanter { get; internal set; }
    public bool IsDefuser { get; internal set; }
}

public sealed class RoundEconomyLedger
{
    private readonly Dictionary<int, EconomyPlayerFacts> _players = new();
    private readonly Dictionary<TeamSide, int> _consecutiveLosses = new()
    {
        [TeamSide.Terrorist] = 0,
        [TeamSide.CounterTerrorist] = 0,
    };

    public IReadOnlyDictionary<int, EconomyPlayerFacts> Players => _players;
    public IReadOnlyDictionary<TeamSide, int> ConsecutiveLosses => _consecutiveLosses;
    public bool BombPlanted { get; private set; }
    public bool BombDefused { get; private set; }
    public bool BombExploded { get; private set; }
    public TeamSide? Winner { get; private set; }

    public void ResetRound(IEnumerable<EconomyPlayerSeed> players)
    {
        _players.Clear();
        foreach (var player in players)
            _players[player.Slot] = new EconomyPlayerFacts(player.Slot, player.Side, player.IsBot);

        BombPlanted = false;
        BombDefused = false;
        BombExploded = false;
        Winner = null;
    }

    public void RecordKill(int attackerSlot, int victimSlot = -1)
    {
        if (!_players.TryGetValue(attackerSlot, out var attacker))
            return;
        if (victimSlot >= 0
            && _players.TryGetValue(victimSlot, out var victim)
            && attacker.Side == victim.Side)
            return;

        attacker.Kills++;
    }

    public void RecordDeath(int victimSlot)
    {
        if (_players.TryGetValue(victimSlot, out var victim))
            victim.Died = true;
    }

    public void RecordBombPlanted(int planterSlot = -1)
    {
        BombPlanted = true;
        if (planterSlot >= 0 && _players.TryGetValue(planterSlot, out var planter))
            planter.IsPlanter = true;
    }

    public void RecordBombDefused(int defuserSlot = -1)
    {
        BombDefused = true;
        if (defuserSlot >= 0 && _players.TryGetValue(defuserSlot, out var defuser))
            defuser.IsDefuser = true;
    }
    public void RecordBombExploded() => BombExploded = true;

    public void CompleteRound(
        TeamSide winningSide,
        IEnumerable<int> aliveSlots,
        IEnumerable<int> savedPrimarySlots)
    {
        var alive = aliveSlots.ToHashSet();
        var saved = savedPrimarySlots.ToHashSet();
        foreach (var player in _players.Values)
        {
            player.AliveAtEnd = alive.Contains(player.Slot) && !player.Died;
            player.SavedPrimary = player.AliveAtEnd && saved.Contains(player.Slot);
        }

        Winner = winningSide;
        var losingSide = winningSide == TeamSide.Terrorist
            ? TeamSide.CounterTerrorist
            : TeamSide.Terrorist;
        _consecutiveLosses[winningSide] = 0;
        _consecutiveLosses[losingSide]++;
    }
}

public sealed record EconomyRewardRules(
    int KillReward,
    float KillRewardFactor,
    int LoserBonus,
    int LoserBonusConsecutive,
    int EliminationTeamReward,
    int TerroristWinBomb,
    int CtDefuseWin,
    int PlayerBombPlanted,
    int PlayerBombDefused,
    int ShorthandedTeamBonus = 0,
    int PlantedBombDefusedBonus = 0);

public enum NextRoundScenario
{
    LossNoPlantNoKillsAllDead,
    LossWithKills,
    LossWithPlant,
    WinNoObjective,
    WinWithKills,
    Best,
}

public sealed record ScenarioParticipant(
    int Slot,
    TeamSide Side,
    int MoneyAfterPurchase,
    PlayerBuyPlan CurrentPlan,
    int Kills,
    bool IsPlanter,
    bool IsDefuser,
    int SavedTier);

public sealed record NextRoundScenarioPrediction(
    NextRoundScenario Scenario,
    IReadOnlyDictionary<int, int> NextMoney,
    int MinTier,
    int CombatPower);

public static class NextRoundPredictor
{
    private sealed record ScenarioDescriptor(
        NextRoundScenario Scenario,
        bool Won,
        bool Objective,
        bool HasKills,
        bool SaveWeapons);

    private static readonly ScenarioDescriptor[] Scenarios =
    [
        new(NextRoundScenario.LossNoPlantNoKillsAllDead, false, false, false, false),
        new(NextRoundScenario.LossWithKills, false, false, true, false),
        new(NextRoundScenario.LossWithPlant, false, true, false, false),
        new(NextRoundScenario.WinNoObjective, true, false, false, false),
        new(NextRoundScenario.WinWithKills, true, false, true, false),
        new(NextRoundScenario.Best, true, true, true, true),
    ];

    public static IReadOnlyList<NextRoundScenarioPrediction> Predict(
        IReadOnlyList<ScenarioParticipant> participants,
        EconomyRewardRules rewards,
        int consecutiveLosses,
        int teamPlayerCount = 0,
        int opponentPlayerCount = 0)
        => Scenarios.Select(descriptor => PredictScenario(
            descriptor,
            participants,
            rewards,
            consecutiveLosses,
            teamPlayerCount > 0 ? teamPlayerCount : participants.Count,
            opponentPlayerCount)).ToArray();

    private static NextRoundScenarioPrediction PredictScenario(
        ScenarioDescriptor descriptor,
        IReadOnlyList<ScenarioParticipant> participants,
        EconomyRewardRules rewards,
        int consecutiveLosses,
        int teamPlayerCount,
        int opponentPlayerCount)
    {
        var nextMoney = new Dictionary<int, int>();
        var nextTiers = new List<int>();

        foreach (var participant in participants)
        {
            int teamReward = descriptor.Won
                ? participant.Side switch
                {
                    TeamSide.Terrorist when descriptor.Objective => rewards.TerroristWinBomb,
                    TeamSide.CounterTerrorist when descriptor.Objective => rewards.CtDefuseWin,
                    _ => rewards.EliminationTeamReward,
                }
                : rewards.LoserBonus + Math.Max(0, consecutiveLosses) * rewards.LoserBonusConsecutive;
            if (opponentPlayerCount > 0 && teamPlayerCount < opponentPlayerCount)
                teamReward += rewards.ShorthandedTeamBonus;
            if (!descriptor.Won
                && descriptor.Objective
                && participant.Side == TeamSide.Terrorist)
                teamReward += rewards.PlantedBombDefusedBonus;
            int killReward = descriptor.HasKills
                ? (int)Math.Round(participant.Kills * rewards.KillReward * rewards.KillRewardFactor)
                : 0;
            int objectiveReward = descriptor.Objective
                ? participant.Side == TeamSide.Terrorist && participant.IsPlanter
                    ? rewards.PlayerBombPlanted
                    : participant.Side == TeamSide.CounterTerrorist && participant.IsDefuser
                        ? rewards.PlayerBombDefused
                        : 0
                : 0;

            int money = Math.Max(0, participant.MoneyAfterPurchase + teamReward + killReward + objectiveReward);
            nextMoney[participant.Slot] = money;
            int purchasedTier = BuyPlanner.BuildPlayerPlan(
                participant.Side,
                BuyPhase.FullBuy,
                money,
                designatedAwper: false,
                opponentEcoLikely: false).Tier;
            int savedTier = descriptor.SaveWeapons ? participant.SavedTier : 0;
            nextTiers.Add(Math.Max(savedTier, purchasedTier));
        }

        return new NextRoundScenarioPrediction(
            descriptor.Scenario,
            nextMoney,
            nextTiers.DefaultIfEmpty(0).Min(),
            nextTiers.Sum());
    }
}

public static class NextRoundEconomyPolicy
{
    public static int EstimateWorstCaseCombatPower(
        IReadOnlyList<ScenarioParticipant> participants,
        EconomyRewardRules rewards,
        int consecutiveLosses,
        int teamPlayerCount = 0,
        int opponentPlayerCount = 0)
        => NextRoundPredictor.Predict(
                participants,
                rewards,
                consecutiveLosses,
                teamPlayerCount,
                opponentPlayerCount)
            .First(prediction => prediction.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead)
            .CombatPower;

    public static bool MeetsWorstCaseFloor(
        NextRoundScenarioPrediction prediction,
        int currentMinTier)
        => prediction.MinTier >= Math.Max(0, currentMinTier - 1);
}

public sealed record TeamPlanningMember(
    int Slot,
    bool IsBot,
    bool IsAwper,
    int Money,
    IReadOnlyList<PlayerBuyPlan> Candidates,
    string? CurrentPrimary,
    int SavedTier,
    int Kills = 0,
    bool IsPlanter = false,
    bool IsDefuser = false);

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
    public const int DeaglePrice = 700;
    public const int P250Price = 300;
    public const int Tec9Price = 500;
    public const int FiveSevenPrice = 500;

    public static bool IsPrimaryWeapon(string? weapon)
        => weapon is not null
            && (weapon.StartsWith("weapon_ak", StringComparison.Ordinal)
                || weapon.StartsWith("weapon_m4", StringComparison.Ordinal)
                || weapon is "weapon_galilar" or "weapon_famas" or "weapon_awp"
                or "weapon_aug" or "weapon_sg556" or "weapon_ssg08"
                or "weapon_scar20" or "weapon_g3sg1"
                or "weapon_p90" or "weapon_bizon" or "weapon_negev" or "weapon_m249"
                or "weapon_mp9" or "weapon_mac10" or "weapon_mp7" or "weapon_mp5sd"
                or "weapon_ump45" or "weapon_nova" or "weapon_xm1014"
                or "weapon_sawedoff" or "weapon_mag7");

    public static bool IsCombatLegal(PlayerBuyPlan plan)
    {
        if (plan.PrimaryWeapon is null || !IsPrimaryWeapon(plan.PrimaryWeapon))
        {
            return true;
        }

        return plan.PrimaryWeapon == "weapon_awp" || plan.ArmorLevel != ArmorLevel.None;
    }

    private const int AkPrice = 2700;
    private const int GalilPrice = 1800;
    private const int M4Price = 2900;
    private const int FamasPrice = 1950;
    private const int AwpPrice = 4750;

    public static int GetWeaponCost(string? weapon)
        => weapon switch
        {
            "weapon_ak47" => AkPrice,
            "weapon_galilar" => GalilPrice,
            "weapon_m4a1" or "weapon_m4a1_silencer" => M4Price,
            "weapon_famas" => FamasPrice,
            "weapon_awp" => AwpPrice,
            "weapon_mac10" => 1050,
            "weapon_mp9" => 1250,
            "weapon_deagle" => DeaglePrice,
            "weapon_p250" => P250Price,
            "weapon_tec9" => Tec9Price,
            "weapon_fiveseven" => FiveSevenPrice,
            _ => 0,
        };

    public static int GetAssaultSuitPurchaseCost(ArmorLevel currentArmor)
        => currentArmor == ArmorLevel.None
            ? KevlarPrice + HelmetUpgradePrice
            : HelmetUpgradePrice;

    public static BuyPhase Classify(TeamEconomySnapshot snapshot)
    {
        if (snapshot.IsLastRound) return BuyPhase.LastRound;
        if (snapshot.IsPistolRound && !snapshot.IsOvertimeFirstRound) return BuyPhase.Pistol;

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
        bool opponentEcoLikely,
        ArmorLevel currentArmor = ArmorLevel.None,
        string? currentPrimary = null,
        string? currentSecondary = null,
        bool currentHasHelmet = false,
        bool currentHasDefuser = false,
        IReadOnlyDictionary<string, int>? currentUtility = null,
        PurchaseIntent? purchaseIntent = null,
        PistolBuyRole pistolRole = PistolBuyRole.Auto)
    {
        money = Math.Max(0, money);
        var intent = purchaseIntent ?? DefaultIntentForPhase(phase);
        return BuildCandidatePlans(
                side,
                phase,
                money,
                designatedAwper,
                opponentEcoLikely,
                currentArmor,
                currentPrimary,
                currentSecondary,
                currentHasHelmet,
                currentHasDefuser,
                currentUtility,
                intent,
                pistolRole)
            .First();
    }

    public static PlayerBuyPlan BuildTeamPrimaryRecipientPlan(
        TeamSide side,
        BuyPhase phase,
        int money,
        string preferredPrimary,
        ArmorLevel currentArmor = ArmorLevel.None,
        bool currentHasHelmet = false)
    {
        money = Math.Max(0, money);
        currentArmor = currentArmor switch
        {
            ArmorLevel.None or ArmorLevel.Half or ArmorLevel.Full => currentArmor,
            _ => ArmorLevel.None,
        };

        int fullArmorCost = GetArmorUpgradeCost(currentArmor, ArmorLevel.Full);
        bool canCompleteArmor = currentArmor == ArmorLevel.Full
            || money >= fullArmorCost;
        ArmorLevel armor = canCompleteArmor
            ? ArmorLevel.Full
            : currentArmor != ArmorLevel.None
                ? currentArmor
                : money >= KevlarPrice
                    ? ArmorLevel.Half
                    : ArmorLevel.None;
        int cost = GetArmorUpgradeCost(currentArmor, armor);
        bool buysHelmet = armor == ArmorLevel.Full && !currentHasHelmet;
        if (buysHelmet && currentArmor == ArmorLevel.Full)
            cost += HelmetUpgradePrice;

        bool canReceivePrimary = armor != ArmorLevel.None;
        return new PlayerBuyPlan(
            phase,
            armor,
            PrimaryWeapon: null,
            SecondaryWeapon: null,
            BuysHelmet: buysHelmet,
            BuysDefuser: false,
            Utility: Array.Empty<string>(),
            EstimatedCost: cost)
        {
            // Never score a naked pending rifle as a real rifle plan. The
            // transfer stage must not create an illegal unarmored AK/M4.
            Tier = canReceivePrimary ? GetTier(armor, preferredPrimary, null) : 0,
            Intent = phase == BuyPhase.LastRound
                ? PurchaseIntent.LastRound
                : PurchaseIntent.Standard,
            AcceptsTeamPrimary = canReceivePrimary,
            RequestedPrimaryWeapon = canReceivePrimary ? preferredPrimary : null,
        };
    }

    public static IReadOnlyList<PlayerBuyPlan> BuildCandidatePlans(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool designatedAwper,
        bool opponentEcoLikely,
        ArmorLevel currentArmor = ArmorLevel.None,
        string? currentPrimary = null,
        string? currentSecondary = null,
        bool currentHasHelmet = false,
        bool currentHasDefuser = false,
        IReadOnlyDictionary<string, int>? currentUtility = null,
        PurchaseIntent? purchaseIntent = null,
        PistolBuyRole pistolRole = PistolBuyRole.Auto)
    {
        money = Math.Max(0, money);
        var intent = purchaseIntent ?? DefaultIntentForPhase(phase);
        if (phase == BuyPhase.Pistol)
            return BuildPistolCandidates(
                side,
                money,
                currentArmor,
                currentPrimary,
                currentSecondary,
                currentHasHelmet,
                currentHasDefuser,
                currentUtility,
                intent,
                pistolRole);

        if (phase is BuyPhase.Eco or BuyPhase.Save && intent == PurchaseIntent.Save)
            return [BuildLowBuyPlan(
                side,
                phase,
                currentArmor,
                currentPrimary,
                currentSecondary,
                currentHasHelmet,
                currentHasDefuser,
                intent)];

        return DistinctPlans(SelectPackages(side, phase, money, designatedAwper, opponentEcoLikely)
            .Select(package => BuildPlanFromPackage(
                side,
                phase,
                money,
                package,
                opponentEcoLikely,
                currentArmor,
                currentPrimary,
                currentSecondary,
                currentHasHelmet,
                currentHasDefuser,
                currentUtility,
                intent))
            .Where(plan => ((intent is PurchaseIntent.Standard or PurchaseIntent.AllIn)
                    && (phase is BuyPhase.Eco or BuyPhase.Save))
                || plan.EstimatedCost <= money));
    }

    private static PlayerBuyPlan BuildPlanFromPackage(
        TeamSide side,
        BuyPhase phase,
        int money,
        LoadoutPackage package,
        bool opponentEcoLikely,
        ArmorLevel currentArmor,
        string? currentPrimary,
        string? currentSecondary,
        bool currentHasHelmet,
        bool currentHasDefuser,
        IReadOnlyDictionary<string, int>? currentUtility,
        PurchaseIntent purchaseIntent)
        => BuildPlanFromTargets(
            side,
            phase,
            money,
            package.Armor,
            package.Primary,
            package.Secondary,
            requireHelmet: true,
            opponentEcoLikely,
            currentArmor,
            currentPrimary,
            currentSecondary,
            currentHasHelmet,
            currentHasDefuser,
            currentUtility,
            purchaseIntent);

    private static IReadOnlyList<LoadoutPackage> SelectPackages(
        TeamSide side,
        BuyPhase phase,
        int money,
        bool designatedAwper,
        bool opponentEcoLikely)
    {
        LoadoutPackage[] packages = side == TeamSide.Terrorist
            ? new[]
            {
                new LoadoutPackage(ArmorLevel.Full, "weapon_ak47", null, 3700, 100),
                new LoadoutPackage(ArmorLevel.Full, "weapon_galilar", null, 2800, 99),
                new LoadoutPackage(ArmorLevel.Half, "weapon_ak47", null, 3350, 90),
                new LoadoutPackage(ArmorLevel.Half, "weapon_galilar", null, 2450, 89),
                new LoadoutPackage(ArmorLevel.Full, "weapon_mac10", null, 2050, 80),
                new LoadoutPackage(ArmorLevel.Half, "weapon_mac10", null, 1700, 79),
                new LoadoutPackage(ArmorLevel.Full, null, "weapon_deagle", 1700, 70),
                new LoadoutPackage(ArmorLevel.Full, null, "weapon_tec9", 1500, 69),
                new LoadoutPackage(ArmorLevel.Half, null, "weapon_deagle", 1350, 60),
                new LoadoutPackage(ArmorLevel.Half, null, "weapon_tec9", 1150, 59),
                new LoadoutPackage(ArmorLevel.Full, null, null, 1000, 58),
                new LoadoutPackage(ArmorLevel.Half, null, null, 650, 57),
                new LoadoutPackage(ArmorLevel.None, null, "weapon_deagle", DeaglePrice, 50),
                new LoadoutPackage(ArmorLevel.None, null, "weapon_tec9", Tec9Price, 49),
                new LoadoutPackage(ArmorLevel.None, null, null, 0, 0),
            }
            : new[]
            {
                new LoadoutPackage(ArmorLevel.Full, "weapon_m4a1", null, 3900, 100),
                new LoadoutPackage(ArmorLevel.Full, "weapon_famas", null, 2950, 99),
                new LoadoutPackage(ArmorLevel.Half, "weapon_m4a1", null, 3550, 90),
                new LoadoutPackage(ArmorLevel.Half, "weapon_famas", null, 2600, 89),
                new LoadoutPackage(ArmorLevel.Full, "weapon_mp9", null, 2250, 80),
                new LoadoutPackage(ArmorLevel.Half, "weapon_mp9", null, 1900, 79),
                new LoadoutPackage(ArmorLevel.Full, null, "weapon_deagle", 1700, 70),
                new LoadoutPackage(ArmorLevel.Full, null, "weapon_fiveseven", 1500, 69),
                new LoadoutPackage(ArmorLevel.Half, null, "weapon_deagle", 1350, 60),
                new LoadoutPackage(ArmorLevel.Half, null, "weapon_fiveseven", 1150, 59),
                new LoadoutPackage(ArmorLevel.Full, null, null, 1000, 58),
                new LoadoutPackage(ArmorLevel.Half, null, null, 650, 57),
                new LoadoutPackage(ArmorLevel.None, null, "weapon_deagle", DeaglePrice, 50),
                new LoadoutPackage(ArmorLevel.None, null, "weapon_fiveseven", FiveSevenPrice, 49),
                new LoadoutPackage(ArmorLevel.None, null, null, 0, 0),
            };

        if (designatedAwper)
        {
            packages = packages
                .Append(new LoadoutPackage(ArmorLevel.Full, "weapon_awp", null, KevlarPrice + HelmetUpgradePrice + AwpPrice, 110))
                .ToArray();
        }

        int optionalHelmetDiscount = side == TeamSide.CounterTerrorist && !opponentEcoLikely
            ? HelmetUpgradePrice
            : 0;

        return packages
            .Select(package => package with
            {
                Cost = package.Armor == ArmorLevel.Full
                    ? package.Cost - optionalHelmetDiscount
                    : package.Cost,
            })
            .OrderByDescending(package => package.Score)
            .ThenByDescending(package => package.Cost)
            .ToArray();
    }

    private readonly record struct LoadoutPackage(
        ArmorLevel Armor,
        string? Primary,
        string? Secondary,
        int Cost,
        int Score);

    private static IReadOnlyList<PlayerBuyPlan> BuildPistolCandidates(
        TeamSide side,
        int money,
        ArmorLevel currentArmor,
        string? currentPrimary,
        string? currentSecondary,
        bool currentHasHelmet,
        bool currentHasDefuser,
        IReadOnlyDictionary<string, int>? currentUtility,
        PurchaseIntent purchaseIntent,
        PistolBuyRole pistolRole)
    {
        var options = side == TeamSide.Terrorist
            ? new[]
            {
                new PistolOption(ArmorLevel.Full, "weapon_deagle", KevlarPrice + HelmetUpgradePrice + DeaglePrice, 90),
                new PistolOption(ArmorLevel.Full, "weapon_p250", KevlarPrice + HelmetUpgradePrice + P250Price, 88),
                new PistolOption(ArmorLevel.Full, "weapon_tec9", KevlarPrice + HelmetUpgradePrice + Tec9Price, 89),
                new PistolOption(ArmorLevel.Full, null, KevlarPrice + HelmetUpgradePrice, 80),
                new PistolOption(ArmorLevel.Half, "weapon_deagle", KevlarPrice + DeaglePrice, 70),
                new PistolOption(ArmorLevel.Half, "weapon_p250", KevlarPrice + P250Price, 68),
                new PistolOption(ArmorLevel.Half, "weapon_tec9", KevlarPrice + Tec9Price, 69),
                new PistolOption(ArmorLevel.Half, null, KevlarPrice, 60),
                new PistolOption(ArmorLevel.None, "weapon_deagle", DeaglePrice, 50),
                new PistolOption(ArmorLevel.None, "weapon_p250", P250Price, 48),
                new PistolOption(ArmorLevel.None, "weapon_tec9", Tec9Price, 49),
                new PistolOption(ArmorLevel.None, null, 0, 0),
            }
            : new[]
            {
                new PistolOption(ArmorLevel.Full, "weapon_deagle", KevlarPrice + HelmetUpgradePrice + DeaglePrice, 90),
                new PistolOption(ArmorLevel.Full, "weapon_fiveseven", KevlarPrice + HelmetUpgradePrice + FiveSevenPrice, 89),
                new PistolOption(ArmorLevel.Full, "weapon_p250", KevlarPrice + HelmetUpgradePrice + P250Price, 88),
                new PistolOption(ArmorLevel.Full, null, KevlarPrice + HelmetUpgradePrice, 80),
                new PistolOption(ArmorLevel.Half, "weapon_deagle", KevlarPrice + DeaglePrice, 70),
                new PistolOption(ArmorLevel.Half, "weapon_fiveseven", KevlarPrice + FiveSevenPrice, 69),
                new PistolOption(ArmorLevel.Half, "weapon_p250", KevlarPrice + P250Price, 68),
                new PistolOption(ArmorLevel.Half, null, KevlarPrice, 60),
                new PistolOption(ArmorLevel.None, "weapon_deagle", DeaglePrice, 50),
                new PistolOption(ArmorLevel.None, "weapon_fiveseven", FiveSevenPrice, 49),
                new PistolOption(ArmorLevel.None, "weapon_p250", P250Price, 48),
                new PistolOption(ArmorLevel.None, null, 0, 0),
            };

        var candidates = options
            .OrderByDescending(option => option.Score)
            .ThenByDescending(option => option.Cost)
            .Select(option => BuildPlanFromTargets(
                side,
                BuyPhase.Pistol,
                money,
                option.Armor,
                null,
                option.Secondary,
                requireHelmet: true,
                opponentEcoLikely: false,
                currentArmor,
                currentPrimary,
                currentSecondary,
                currentHasHelmet,
                currentHasDefuser,
                currentUtility))
            .Where(plan => plan.EstimatedCost <= money)
            .ToArray();
        return ApplyPistolRole(
            side,
            money,
            currentArmor,
            currentSecondary,
            currentHasHelmet,
            currentHasDefuser,
            currentUtility,
            purchaseIntent,
            pistolRole,
            candidates);
    }

    private static IReadOnlyList<PlayerBuyPlan> DistinctPlans(
        IEnumerable<PlayerBuyPlan> plans)
        => plans
            .GroupBy(plan => (
                plan.Phase,
                plan.ArmorLevel,
                plan.PrimaryWeapon,
                plan.SecondaryWeapon,
                plan.BuysHelmet,
                plan.BuysDefuser,
                Utility: string.Join('|', plan.Utility),
                plan.EstimatedCost,
                plan.Tier,
                plan.Intent))
            .Select(group => group.First())
            .ToArray();

    private static IReadOnlyList<PlayerBuyPlan> ApplyPistolRole(
        TeamSide side,
        int money,
        ArmorLevel currentArmor,
        string? currentSecondary,
        bool currentHasHelmet,
        bool currentHasDefuser,
        IReadOnlyDictionary<string, int>? currentUtility,
        PurchaseIntent intent,
        PistolBuyRole role,
        IReadOnlyList<PlayerBuyPlan> candidates)
    {
        if (role == PistolBuyRole.Auto)
            return candidates;

        var rolePlans = new List<PlayerBuyPlan>(candidates.Count);
        foreach (var candidate in candidates)
        {
            PlayerBuyPlan plan = candidate;
            switch (role)
            {
                case PistolBuyRole.ArmorEntry:
                    int armorEntryCost = candidate.SecondaryWeapon is { } armorEntryWeapon
                        ? BuyPlanner.GetWeaponCost(armorEntryWeapon)
                        : 0;
                    plan = EnsurePistolArmor(candidate with
                    {
                        SecondaryWeapon = null,
                        Utility = Array.Empty<string>(),
                        BuysDefuser = false,
                        EstimatedCost = Math.Max(0, candidate.EstimatedCost - armorEntryCost),
                    }, currentArmor, ArmorLevel.Half);
                    break;
                case PistolBuyRole.Firepower:
                    if (candidate.SecondaryWeapon is null)
                        continue;
                    plan = EnsurePistolArmor(candidate with
                    {
                        Utility = Array.Empty<string>(),
                        BuysDefuser = false,
                    }, currentArmor, ArmorLevel.Half);
                    break;
                case PistolBuyRole.UtilitySupport:
                    plan = AddPistolUtility(
                        candidate with
                        {
                            SecondaryWeapon = null,
                            EstimatedCost = Math.Max(0, candidate.EstimatedCost
                                - (candidate.SecondaryWeapon is { } supportWeapon
                                    ? BuyPlanner.GetWeaponCost(supportWeapon)
                                    : 0)),
                        },
                        money,
                        currentUtility,
                        smoke: true,
                        flash: true);
                    break;
                case PistolBuyRole.DefuserSupport:
                    plan = AddPistolUtility(
                        candidate with
                        {
                            SecondaryWeapon = null,
                            EstimatedCost = Math.Max(0, candidate.EstimatedCost
                                - (candidate.SecondaryWeapon is { } defuserWeapon
                                    ? BuyPlanner.GetWeaponCost(defuserWeapon)
                                    : 0)),
                        },
                        money,
                        currentUtility,
                        smoke: true,
                        flash: false);
                    if (side == TeamSide.CounterTerrorist
                        && !currentHasDefuser
                        && money - plan.EstimatedCost >= DefuserPrice)
                    {
                        plan = plan with
                        {
                            BuysDefuser = true,
                            EstimatedCost = plan.EstimatedCost + DefuserPrice,
                        };
                    }
                    break;
            }

            if (plan.EstimatedCost <= money && BuyPlanner.IsCombatLegal(plan))
                rolePlans.Add(plan);
        }

        return rolePlans.Count > 0
            ? DistinctPlans(rolePlans)
            : candidates;
    }

    private static PlayerBuyPlan AddPistolUtility(
        PlayerBuyPlan plan,
        int money,
        IReadOnlyDictionary<string, int>? currentUtility,
        bool smoke,
        bool flash)
    {
        var utility = new List<string>(plan.Utility);
        int cost = plan.EstimatedCost;
        void Add(string name, int price)
        {
            if ((currentUtility?.GetValueOrDefault(name) ?? 0) > 0
                || money - cost < price)
                return;
            utility.Add(name);
            cost += price;
        }

        if (smoke) Add("smoke", SmokePrice);
        if (flash) Add("flash", FlashPrice);
        return plan with
        {
            Utility = utility,
            EstimatedCost = cost,
            Tier = GetTier(plan.ArmorLevel, plan.PrimaryWeapon, plan.SecondaryWeapon),
        };
    }

    private static PlayerBuyPlan EnsurePistolArmor(
        PlayerBuyPlan plan,
        ArmorLevel currentArmor,
        ArmorLevel minimumArmor)
    {
        ArmorLevel targetArmor = (ArmorLevel)Math.Max(
            (int)currentArmor,
            Math.Max((int)plan.ArmorLevel, (int)minimumArmor));
        int armorDelta = GetArmorUpgradeCost(plan.ArmorLevel, targetArmor);
        return plan with
        {
            ArmorLevel = targetArmor,
            EstimatedCost = plan.EstimatedCost + armorDelta,
            Tier = GetTier(targetArmor, plan.PrimaryWeapon, plan.SecondaryWeapon),
        };
    }

    private readonly record struct PistolOption(
        ArmorLevel Armor,
        string? Secondary,
        int Cost,
        int Score);

    private static PlayerBuyPlan BuildPlanFromTargets(
        TeamSide side,
        BuyPhase phase,
        int money,
        ArmorLevel targetArmor,
        string? targetPrimary,
        string? targetSecondary,
        bool requireHelmet,
        bool opponentEcoLikely,
        ArmorLevel currentArmor,
        string? currentPrimary,
        string? currentSecondary,
        bool currentHasHelmet,
        bool currentHasDefuser,
        IReadOnlyDictionary<string, int>? currentUtility,
        PurchaseIntent purchaseIntent = PurchaseIntent.Standard)
    {
        ArmorLevel requestedArmor = (ArmorLevel)Math.Max(
            (int)currentArmor,
            (int)targetArmor);
        int remaining = money;
        int cost = 0;

        bool requestsHelmet = ShouldBuyHelmet(
            side,
            phase,
            requireHelmet,
            opponentEcoLikely,
            currentHasHelmet)
            && requestedArmor == ArmorLevel.Full;
        // ArmorLevel.Full means full armor in the executable plan, not a
        // discounted full-buy package. CT rifle rounds may intentionally skip
        // the helmet against rifles, in which case the actual loadout is Half.
        ArmorLevel armor = requestedArmor == ArmorLevel.Full
            && !currentHasHelmet
            && !requestsHelmet
            ? ArmorLevel.Half
            : requestedArmor;
        bool buysHelmet = requestsHelmet && armor == ArmorLevel.Full;
        ArmorLevel bodyArmor = armor == ArmorLevel.Full ? ArmorLevel.Half : armor;
        cost += GetArmorUpgradeCost(currentArmor, bodyArmor);
        if (buysHelmet)
            cost += HelmetUpgradePrice;

        bool shouldReplacePrimary = targetPrimary is not null
            && (currentPrimary is null
                || purchaseIntent is PurchaseIntent.Standard or PurchaseIntent.AllIn or PurchaseIntent.LastRound
                    && GetWeaponTier(targetPrimary) > GetWeaponTier(currentPrimary));
        string? primary = shouldReplacePrimary ? targetPrimary : currentPrimary ?? targetPrimary;
        if (shouldReplacePrimary && targetPrimary is not null)
            cost += GetWeaponCost(targetPrimary);

        bool keepsCurrentSecondary = currentSecondary is not null
            && (targetSecondary is null || !IsDefaultSecondary(currentSecondary));
        string? secondary = keepsCurrentSecondary ? currentSecondary : targetSecondary;
        if (!keepsCurrentSecondary && targetSecondary is not null)
            cost += GetWeaponCost(targetSecondary);

        remaining -= cost;
        bool buysDefuser = side == TeamSide.CounterTerrorist
            && !currentHasDefuser
            && primary is not null
            && remaining >= DefuserPrice
            && phase is BuyPhase.FullBuy or BuyPhase.LastRound;
        if (buysDefuser)
        {
            remaining -= DefuserPrice;
            cost += DefuserPrice;
        }

        var utility = new List<string>();
        if (primary is not null && (phase is BuyPhase.FullBuy or BuyPhase.LastRound))
        {
            AddUtility("smoke", SmokePrice, 1);
            AddUtility("flash", FlashPrice, phase == BuyPhase.LastRound ? 2 : 1);
            AddUtility("he", HePrice, 1);
            AddUtility("molotov", side == TeamSide.Terrorist ? MolotovPrice : IncendiaryPrice, 1);
        }

        return new PlayerBuyPlan(
            phase,
            armor,
            primary,
            secondary,
            buysHelmet,
            buysDefuser,
            utility,
            Math.Max(0, money - remaining))
        {
            Tier = GetTier(armor, primary, secondary),
            Intent = purchaseIntent,
            ReplacePrimaryWeapon = shouldReplacePrimary && currentPrimary is not null
                ? currentPrimary
                : null,
        };

        void AddUtility(string name, int price, int targetCount)
        {
            int alreadyOwned = currentUtility?.GetValueOrDefault(name) ?? 0;
            for (int i = 0; i < targetCount; i++)
            {
                if (i >= alreadyOwned)
                {
                    if (remaining < price)
                        break;
                    remaining -= price;
                    cost += price;
                }
                utility.Add(name);
            }
        }
    }

    private static bool IsDefaultSecondary(string? weapon)
        => weapon is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000";

    private static bool ShouldBuyHelmet(
        TeamSide side,
        BuyPhase phase,
        bool requireHelmet,
        bool opponentEcoLikely,
        bool currentHasHelmet)
        => !currentHasHelmet
            && requireHelmet
            && (side == TeamSide.Terrorist
                || opponentEcoLikely
                || phase == BuyPhase.Pistol);

    public static int GetTier(ArmorLevel armor, string? primary, string? secondary)
    {
        return GetWeaponTier(primary) switch
        {
            3 => armor == ArmorLevel.Full ? 9 : 8,
            2 => armor == ArmorLevel.Full ? 7 : 6,
            1 => armor == ArmorLevel.Full ? 5 : 4,
            _ => secondary is not null
                ? armor == ArmorLevel.Full ? 3 : armor == ArmorLevel.Half ? 2 : 1
                : 0,
        };
    }

    private static int GetPackageTier(LoadoutPackage package)
        => GetTier(package.Armor, package.Primary, package.Secondary);

    public static int GetArmorUpgradeCost(ArmorLevel current, ArmorLevel target)
    {
        if (target <= current)
            return 0;

        return (current, target) switch
        {
            (ArmorLevel.None, ArmorLevel.Half) => KevlarPrice,
            (ArmorLevel.None, ArmorLevel.Full) => KevlarPrice + HelmetUpgradePrice,
            (ArmorLevel.Half, ArmorLevel.Full) => HelmetUpgradePrice,
            _ => 0,
        };
    }

    private static PlayerBuyPlan BuildLowBuyPlan(
        TeamSide side,
        BuyPhase phase,
        ArmorLevel currentArmor,
        string? currentPrimary,
        string? currentSecondary,
        bool currentHasHelmet,
        bool currentHasDefuser,
        PurchaseIntent purchaseIntent)
    {
        // Pure eco means preserving the next rifle round. Half/force decisions
        // are handled by the caller with a non-low phase.
        return new PlayerBuyPlan(
            phase,
            currentArmor,
            currentPrimary,
            currentSecondary,
            false,
            false,
            Array.Empty<string>(),
            0)
        {
            Tier = GetTier(currentArmor, currentPrimary, currentSecondary),
            Intent = purchaseIntent,
        };
    }

    private static PurchaseIntent DefaultIntentForPhase(BuyPhase phase)
        => phase switch
        {
            BuyPhase.Pistol => PurchaseIntent.Pistol,
            BuyPhase.Save or BuyPhase.Eco => PurchaseIntent.Save,
            BuyPhase.LastRound => PurchaseIntent.LastRound,
            _ => PurchaseIntent.Standard,
        };

    private static int GetWeaponTier(string? weapon)
        => weapon switch
        {
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer"
                or "weapon_awp" or "weapon_aug" or "weapon_sg556" => 3,
            "weapon_galilar" or "weapon_famas" or "weapon_ssg08"
                or "weapon_scar20" or "weapon_g3sg1" => 2,
            "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd"
                or "weapon_ump45" or "weapon_p90" or "weapon_bizon"
                or "weapon_nova" or "weapon_xm1014" or "weapon_sawedoff"
                or "weapon_mag7" or "weapon_negev" or "weapon_m249" => 1,
            _ => 0,
        };

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
    public static bool IsOvertimeFirstRound(int roundsPlayed, int maxRounds = 24)
    {
        maxRounds = maxRounds > 0 ? maxRounds : 24;
        return roundsPlayed == maxRounds;
    }

    public static bool IsOvertimeRound(int roundsPlayed, int maxRounds = 24)
    {
        maxRounds = maxRounds > 0 ? maxRounds : 24;
        return roundsPlayed >= maxRounds;
    }

    public static bool IsLastRegulationRound(
        int teamScore,
        int opponentScore,
        int roundsPlayed,
        int maxRounds = 24)
        => roundsPlayed == Math.Max(0, maxRounds - 1)
            && MatchPressurePolicy.Evaluate(
                teamScore,
                opponentScore,
                roundsPlayed,
                new MatchFormatSnapshot(maxRounds, true, 6),
                economySwing: false)
                .IsMustWin;

    public static bool IsFirstRoundOfHalf(
        int roundsPlayed,
        int maxRounds = 24,
        int overtimeMaxRounds = 6)
    {
        if (roundsPlayed < 0) return false;

        maxRounds = maxRounds > 0 ? maxRounds : 24;
        overtimeMaxRounds = overtimeMaxRounds > 0 ? overtimeMaxRounds : 6;

        int half = Math.Max(1, maxRounds / 2);
        if (roundsPlayed == 0 || roundsPlayed == half || roundsPlayed == maxRounds)
            return true;

        if (roundsPlayed < maxRounds) return false;

        int overtimeHalf = Math.Max(1, overtimeMaxRounds / 2);
        return (roundsPlayed - maxRounds) % overtimeHalf == 0;
    }

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

public readonly record struct RoundScoreSnapshot(int Terrorist, int CounterTerrorist);

public static class RoundScoreReader
{
    private static readonly string[] TerroristScoreNames =
    [
        "TeamTScore", "TerroristScore", "TScore",
        "m_iTeamTScore", "m_iTerroristScore", "m_iTScore",
    ];

    private static readonly string[] CounterTerroristScoreNames =
    [
        "TeamCTScore", "CounterTerroristScore", "CTScore",
        "m_iTeamCTScore", "m_iCounterTerroristScore", "m_iCTScore",
    ];

    public static bool TryRead(object? gameRules, out RoundScoreSnapshot score)
    {
        score = default;
        if (gameRules == null
            || !TryReadNamedValue(gameRules, TerroristScoreNames, out int terrorist)
            || !TryReadNamedValue(gameRules, CounterTerroristScoreNames, out int counterTerrorist)
            || terrorist < 0
            || counterTerrorist < 0)
        {
            return false;
        }

        score = new RoundScoreSnapshot(terrorist, counterTerrorist);
        return true;
    }

    private static bool TryReadNamedValue(
        object source,
        IEnumerable<string> names,
        out int value)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        foreach (string name in names)
        {
            try
            {
                var property = source.GetType().GetProperty(name, flags);
                if (property?.GetValue(source) is IConvertible propertyValue)
                {
                    value = Convert.ToInt32(propertyValue);
                    return true;
                }

                var field = source.GetType().GetField(name, flags);
                if (field?.GetValue(source) is IConvertible fieldValue)
                {
                    value = Convert.ToInt32(fieldValue);
                    return true;
                }
            }
            catch
            {
                // The generated GameRules schema varies across CSS builds.
            }
        }

        value = 0;
        return false;
    }
}

public static class BombTimerPolicy
{
    public static bool TryResolveSeconds(object? bomb, float currentTime, out int seconds)
    {
        seconds = 0;
        if (bomb == null)
            return false;

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        foreach (var property in bomb.GetType().GetProperties(flags))
        {
            if (!LooksLikeBombTimer(property.Name))
                continue;
            if (property.GetValue(bomb) is IConvertible raw
                && TryConvertToSeconds(raw, currentTime, out seconds))
                return true;
        }

        foreach (var field in bomb.GetType().GetFields(flags))
        {
            if (!LooksLikeBombTimer(field.Name))
                continue;
            if (field.GetValue(bomb) is IConvertible raw
                && TryConvertToSeconds(raw, currentTime, out seconds))
                return true;
        }

        return false;
    }

    private static bool LooksLikeBombTimer(string name)
        => name.Contains("TimerTime", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BlowTime", StringComparison.OrdinalIgnoreCase)
            || name.Contains("C4Blow", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DetonateTime", StringComparison.OrdinalIgnoreCase);

    private static bool TryConvertToSeconds(
        IConvertible rawValue,
        float currentTime,
        out int seconds)
    {
        try
        {
            float raw = rawValue.ToSingle(System.Globalization.CultureInfo.InvariantCulture);
            if (raw > currentTime)
            {
                seconds = Math.Max(0, (int)Math.Ceiling(raw - currentTime));
                return true;
            }

            if (raw is > 0 and <= 60)
            {
                seconds = (int)Math.Ceiling(raw);
                return true;
            }
        }
        catch
        {
            // Ignore malformed schema values and let the caller use its safe
            // urgency fallback.
        }

        seconds = 0;
        return false;
    }
}

public static class WeaponValuePolicy
{
    public static bool IsHighValue(string? weapon)
        => GetSaveValue(weapon) >= 7;

    public static int GetSaveValue(string? weapon)
        => weapon switch
        {
            "weapon_awp" => 10,
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer"
                or "weapon_sg556" or "weapon_aug" => 8,
            "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" => 7,
            "weapon_galilar" or "weapon_famas" => 5,
            "weapon_mp9" or "weapon_mac10" or "weapon_mp5sd"
                or "weapon_ump45" or "weapon_p90" => 3,
            _ => 0,
        };
}

public readonly record struct WeaponGrantSettlement(bool Confirmed, bool ShouldRefund);

public static class WeaponGrantPolicy
{
    public static WeaponGrantSettlement Evaluate(
        bool callAccepted,
        bool hasWeapon,
        bool hasConflictingPrimary = false)
        => new(
            Confirmed: callAccepted && hasWeapon && !hasConflictingPrimary,
            ShouldRefund: callAccepted && (!hasWeapon || hasConflictingPrimary));
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
    RoundPhase Phase)
{
    // These are init-only so existing consumers can keep using the compact
    // constructor while tactical consumers can carry the shared economy and
    // information state without taking a dependency on CounterStrikeSharp.
    public BuyPhase CtBuyPhase { get; init; } = BuyPhase.FullBuy;
    public BuyPhase OpponentBuyPhase { get; init; } = BuyPhase.FullBuy;
    public bool CtTeamHasDefuser { get; init; }
    public CtContact? LastContact { get; init; }
    public int ActiveProbeCount { get; init; }
    public int RotationUrgency { get; init; }
    public float LiveElapsedSeconds { get; init; }
    public CtGambleSite SelectedGambleSite { get; init; } = CtGambleSite.None;
    public int BombSecondsRemaining { get; init; } = 40;
    public int CtWeaponTier { get; init; }
    public int OpponentWeaponTier { get; init; }
    public int CtUtilityCount { get; init; }
    public int OpponentUtilityCount { get; init; }
    public bool RetakePathViable { get; init; } = true;
    public bool RetakePathKnown { get; init; } = true;
    public bool BombTimerKnown { get; init; } = true;
    public MatchPressure Pressure { get; init; } = MatchPressure.Normal;
    public CtThreatEvaluation Threat { get; init; }
}

public sealed record RetakeContext(
    int AliveCt,
    int AliveT,
    bool BombPlanted,
    int BombSecondsRemaining,
    int CtDefusers,
    int CtWeaponTier,
    int TWeaponTier,
    int CtUtility,
    int TUtility,
    int TeamScore,
    int OpponentScore,
    MatchPressure Pressure,
    bool PathViable,
    int BotWeaponValue)
{
    public CtGambleSite BombSite { get; init; } = CtGambleSite.None;
    public CtContact? LastKill { get; init; }
    public bool BombTimerKnown { get; init; } = true;
    public bool PathKnown { get; init; } = true;
}

public sealed record RetakeDecision(
    bool ShouldRetake,
    int CurrentRoundScore,
    int NextRoundValue,
    string Reason);

public static class RetakeDecisionPolicy
{
    public static RetakeDecision Evaluate(RetakeContext context)
    {
        if (!context.BombPlanted)
            return new RetakeDecision(true, 100, 0, "no-bomb-to-save");

        int current = 0;
        current += Math.Clamp(context.AliveCt, 0, 5) * 24;
        current -= Math.Clamp(context.AliveT, 0, 5) * 18;
        current += Math.Clamp(context.CtDefusers, 0, context.AliveCt) * 22;
        current += Math.Clamp(context.CtUtility, 0, 5) * 5;
        current -= Math.Clamp(context.TUtility, 0, 5) * 3;
        current += Math.Clamp(context.CtWeaponTier - context.TWeaponTier, -4, 4) * 4;
        current += !context.BombTimerKnown
            ? 0
            : context.BombSecondsRemaining >= 15 ? 16
            : context.BombSecondsRemaining >= 10 ? 8
            : -18;
        current += !context.PathKnown
            ? -8
            : context.PathViable ? 18 : -55;

        bool mustWin = context.Pressure is MatchPressure.Clinch or MatchPressure.Elimination;
        if (mustWin || context.TeamScore < context.OpponentScore)
            current += 18;

        int nextRound = Math.Clamp(context.BotWeaponValue, 0, 12) * 7;
        nextRound += context.CtWeaponTier >= 8 ? 14 : context.CtWeaponTier >= 6 ? 7 : 0;

        bool impossible = context.AliveCt <= 0
            || (context.PathKnown && !context.PathViable)
            || (context.BombTimerKnown
                && context.BombSecondsRemaining <= 8
                && context.CtDefusers == 0)
            || (context.AliveCt == 1 && context.AliveT >= 3);
        bool shouldRetake = !impossible
            && (current >= 50 || current + 5 >= nextRound || mustWin);
        string reason = shouldRetake
            ? $"retake-score-{current}-vs-save-{nextRound}"
            : impossible
                ? $"retake-unrealistic-{current}-vs-save-{nextRound}"
                : $"save-score-{current}-vs-{nextRound}";

        return new RetakeDecision(shouldRetake, current, nextRound, reason);
    }
}

public enum TPostPlantAction
{
    Hold,
    MoveToSite,
    RetreatFromBomb,
    Repath,
}

public enum TPostPlantTargetKind
{
    None,
    Site,
    Retreat,
}

public static class TPostPlantExecutionPolicy
{
    public static TPostPlantTargetKind TargetKind(TPostPlantAction action)
        => action switch
        {
            TPostPlantAction.Hold => TPostPlantTargetKind.Site,
            TPostPlantAction.MoveToSite => TPostPlantTargetKind.Site,
            TPostPlantAction.RetreatFromBomb => TPostPlantTargetKind.Retreat,
            _ => TPostPlantTargetKind.None,
        };
}

public sealed record TPostPlantContext(
    bool BombPlanted,
    int BombSecondsRemaining,
    int AliveT,
    int AliveCt,
    bool PathViable,
    int CtDefusers = 0,
    bool CtRetakePathViable = true,
    bool RetreatPathViable = true,
    bool BombTimerKnown = true);

public sealed record TPostPlantDecision(TPostPlantAction Action, string Reason);

public static class TPostPlantPolicy
{
    public static TPostPlantDecision Evaluate(TPostPlantContext context)
    {
        if (!context.BombPlanted)
            return new TPostPlantDecision(TPostPlantAction.Hold, "bomb-not-planted");

        int defuseSeconds = context.CtDefusers > 0 ? 5 : 10;
        bool ctCanCompleteDefuse = context.AliveCt > 0
            && context.CtRetakePathViable
            && (!context.BombTimerKnown
                || context.BombSecondsRemaining > defuseSeconds);
        if (!ctCanCompleteDefuse)
        {
            if (!context.RetreatPathViable)
                return new TPostPlantDecision(
                    TPostPlantAction.Repath,
                    "postplant-retreat-path-unavailable");

            string reason = context.AliveCt == 0
                ? "defuse-impossible-no-ct"
                : !context.CtRetakePathViable
                    ? "defuse-impossible-no-path"
                    : "defuse-impossible-clock";
            return new TPostPlantDecision(TPostPlantAction.RetreatFromBomb, reason);
        }

        if (!context.PathViable)
            return new TPostPlantDecision(TPostPlantAction.Repath, "postplant-site-path-unavailable");

        if ((context.BombTimerKnown && context.BombSecondsRemaining <= 12)
            || context.AliveT <= context.AliveCt)
            return new TPostPlantDecision(TPostPlantAction.MoveToSite, "late-postplant-collapse");
        return new TPostPlantDecision(TPostPlantAction.Hold, "defensive-postplant");
    }
}

public readonly record struct RetreatPosition(float X, float Y, float Z);

public sealed record TPostPlantRetreatParticipant(
    int Slot,
    RetreatPosition Position,
    int Health = 100);

public static class TPostPlantRetreatPlanner
{
    public static IReadOnlyDictionary<int, RetreatPosition> AssignTargets(
        IReadOnlyList<TPostPlantRetreatParticipant> participants,
        IReadOnlyList<RetreatPosition> candidates,
        RetreatPosition bombPosition)
    {
        var uniqueCandidates = candidates
            .Distinct()
            .ToList();
        if (participants.Count == 0 || uniqueCandidates.Count == 0)
            return new Dictionary<int, RetreatPosition>();

        var assignments = new Dictionary<int, RetreatPosition>();
        foreach (var participant in participants
                     .OrderBy(participant => participant.Health)
                     .ThenByDescending(participant => Distance(participant.Position, bombPosition))
                     .ThenBy(participant => participant.Slot))
        {
            var pool = uniqueCandidates.Count > 0
                ? uniqueCandidates
                : candidates.ToList();
            var target = pool
                .OrderByDescending(candidate => ScoreCandidate(
                    candidate,
                    participant.Position,
                    bombPosition,
                    assignments.Values))
                .ThenBy(candidate => candidate.X)
                .ThenBy(candidate => candidate.Y)
                .ThenBy(candidate => candidate.Z)
                .First();

            assignments[participant.Slot] = target;
            uniqueCandidates.Remove(target);
        }

        return assignments;
    }

    private static float ScoreCandidate(
        RetreatPosition candidate,
        RetreatPosition participantPosition,
        RetreatPosition bombPosition,
        IEnumerable<RetreatPosition> assignedTargets)
    {
        float bombDistance = Distance(candidate, bombPosition);
        float teammateSeparation = assignedTargets
            .Select(target => Distance(candidate, target))
            .DefaultIfEmpty(bombDistance)
            .Min();
        float travelDistance = Distance(candidate, participantPosition);

        // Prefer safe distance first, then spread the surviving Ts, while
        // keeping a small preference for the closest safe anchor per Bot.
        return bombDistance * 0.55f
            + teammateSeparation * 0.35f
            - travelDistance * 0.10f;
    }

    private static float Distance(RetreatPosition left, RetreatPosition right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public enum CtRole
{
    AnchorGroupA,
    AnchorGroupB,
    Rotator,
    Information,
    Playmaker,
}

public enum CtTacticalState
{
    Setup,
    Hold,
    Information,
    Withdraw,
    Rotate,
    Reinforce,
    Retake,
    Save,
}

public enum ContactConfidence
{
    None,
    Low,
    Medium,
    High,
}

public sealed record CtBotSnapshot(
    int Slot,
    bool Alive,
    float Aggression,
    float Teamwork,
    bool IsAwper)
{
    public bool HasValuableWeapon { get; init; }
}

public sealed record CtContact(
    int SourceSlot,
    float RecordedAt,
    ContactConfidence Confidence,
    float X,
    float Y,
    float Z,
    bool IsHistorical = false)
{
    public CtGambleSite Site { get; init; } = CtGambleSite.None;
}

public sealed record CtDeathEvent(
    int VictimSlot,
    CtRole VictimRole,
    float RecordedAt,
    CtGambleSite Site = CtGambleSite.None);

public sealed record CtTacticalDecision(
    int Slot,
    CtRole Role,
    CtTacticalState State,
    int ActiveBudget,
    bool IsActive,
    bool ShouldRepath,
    bool ShouldRun,
    string Reason)
{
    public CtGambleSite TargetSite { get; init; } = CtGambleSite.None;
    public bool ShouldMoveToGambleSite { get; init; }
    public bool ShouldMoveToRetreat { get; init; }
    public bool PreserveWeapon { get; init; }
}

public sealed class CompetitiveTacticalRuntime
{
    private const float ProbeDuration = 4f;
    private const float ProbeCooldown = 11f;
    private const float ContactLifetime = 4f;
    private const float SameGroupDeathWindow = 3.5f;

    private readonly Dictionary<int, CtRole> _roles = new();
    private readonly Dictionary<int, CtBotSnapshot> _bots = new();
    private readonly Dictionary<int, float> _probeStartedAt = new();
    private readonly Dictionary<int, float> _probeEndedAt = new();
    private readonly Dictionary<int, CtContact> _contactsByBot = new();
    private readonly List<CtDeathEvent> _recentDeaths = new();
    private CtEcoPlan? _ecoPlan;
    private float _liveStartedAt;
    private RoundContext _context = new(
        0, 1, 0, 0, false, 0, false, null, 5, 5, RoundPhase.Freeze);
    private CtContact? _lastContact;

    public RoundContext Context => _context;
    public IReadOnlyDictionary<int, CtRole> Roles => _roles;

    public void Reset(RoundContext context)
    {
        _context = context;
        _roles.Clear();
        _bots.Clear();
        _probeStartedAt.Clear();
        _probeEndedAt.Clear();
        _contactsByBot.Clear();
        _recentDeaths.Clear();
        _ecoPlan = null;
        _lastContact = context.LastContact;
        _liveStartedAt = 0f;
    }

    public IReadOnlyDictionary<int, CtRole> AssignCtRoles(
        IReadOnlyList<CtBotSnapshot> bots)
    {
        _bots.Clear();
        foreach (var bot in bots.Where(bot => bot.Alive))
            _bots[bot.Slot] = bot;

        _roles.Clear();
        var alive = _bots.Values
            .OrderByDescending(bot => bot.Aggression)
            .ThenByDescending(bot => bot.Teamwork)
            .ThenBy(bot => bot.Slot)
            .ToList();

        if (alive.Count == 0)
            return _roles;

        if (alive.Count >= 5)
        {
            // Keep the fifth job as a single information/rotation responder.
            // A high-aggression rifle profile is the information player; an AWP
            // or calmer profile is the rotator. The two logical anchor groups
            // remain intact and are intentionally not mapped to map coordinates.
            var responder = alive[0];
            _roles[responder.Slot] = responder.IsAwper || responder.Aggression < 0.65f
                ? CtRole.Rotator
                : CtRole.Information;

            var anchors = alive.Skip(1).ToList();
            for (int i = 0; i < anchors.Count; i++)
                _roles[anchors[i].Slot] = i < 2
                    ? CtRole.AnchorGroupA
                    : CtRole.AnchorGroupB;
        }
        else if (alive.Count == 4)
        {
            for (int i = 0; i < 2; i++)
                _roles[alive[i].Slot] = CtRole.AnchorGroupA;
            _roles[alive[2].Slot] = CtRole.AnchorGroupB;
            _roles[alive[3].Slot] = CtRole.Rotator;
        }
        else if (alive.Count == 3)
        {
            _roles[alive[0].Slot] = CtRole.AnchorGroupA;
            _roles[alive[1].Slot] = CtRole.AnchorGroupB;
            _roles[alive[2].Slot] = CtRole.Rotator;
        }
        else if (alive.Count == 2)
        {
            _roles[alive[0].Slot] = CtRole.AnchorGroupA;
            _roles[alive[1].Slot] = CtRole.AnchorGroupB;
        }
        else
        {
            _roles[alive[0].Slot] = CtRole.AnchorGroupA;
        }

        return _roles;
    }

    public void UpdateBotSnapshots(IReadOnlyList<CtBotSnapshot> bots)
    {
        _bots.Clear();
        foreach (var bot in bots.Where(bot => bot.Alive))
            _bots[bot.Slot] = bot;
    }

    public void SetEconomy(
        BuyPhase ctBuyPhase,
        BuyPhase opponentBuyPhase,
        int aliveCt,
        int aliveT)
    {
        _context = _context with
        {
            CtBuyPhase = ctBuyPhase,
            OpponentBuyPhase = opponentBuyPhase,
            AliveTeam = Math.Max(0, aliveCt),
            AliveOpponent = Math.Max(0, aliveT),
        };
    }

    public void SetTeamDefuser(bool hasDefuser)
    {
        _context = _context with { CtTeamHasDefuser = hasDefuser };
    }

    public void SetThreat(CtThreatEvaluation threat)
        => _context = _context with { Threat = threat };

    public void SetRetakeInfo(
        int bombSecondsRemaining,
        int ctWeaponTier,
        int opponentWeaponTier,
        int ctUtilityCount,
        int opponentUtilityCount,
        bool pathViable,
        MatchPressure pressure,
        bool bombTimerKnown = true,
        bool pathKnown = true)
    {
        _context = _context with
        {
            BombSecondsRemaining = Math.Max(0, bombSecondsRemaining),
            CtWeaponTier = Math.Max(0, ctWeaponTier),
            OpponentWeaponTier = Math.Max(0, opponentWeaponTier),
            CtUtilityCount = Math.Max(0, ctUtilityCount),
            OpponentUtilityCount = Math.Max(0, opponentUtilityCount),
            RetakePathViable = pathViable,
            BombTimerKnown = bombTimerKnown,
            RetakePathKnown = pathKnown,
            Pressure = pressure,
        };
    }

    public void SetCtGambleSite(CtGambleSite site)
    {
        if (site == CtGambleSite.None
            || _context.SelectedGambleSite != CtGambleSite.None
            || _context.BombPlanted)
            return;

        _context = _context with { SelectedGambleSite = site };
        if (_ecoPlan is null && _bots.Count > 0)
        {
            // A plan submitted by the new coordinator always wins. This
            // small in-memory plan only keeps direct Core/runtime callers
            // deterministic before the first background result arrives.
            _ecoPlan = new CtEcoPlan(
                CtEcoTactic.FourPlusOneGamble,
                site,
                _bots.Values
                    .OrderBy(bot => bot.Slot)
                    .Select(bot => new CtEcoAssignment(
                        bot.Slot,
                        CtEcoRole.Crossfire,
                        site,
                        Math.Abs(bot.Slot) % 4,
                        IsEntry: false,
                        KeepBackdoor: bot.Slot == _bots.Keys.Max()))
                    .ToArray(),
                "runtime-site-anchor");
        }
    }

    public void SetEcoPlan(CtEcoPlan? plan)
    {
        _ecoPlan = plan;
        if (plan is not null)
            _context = _context with { SelectedGambleSite = plan.Site };
    }

    public void SetPhase(RoundPhase phase, float liveStartedAt = -1f)
    {
        _context = _context with
        {
            Phase = phase,
            BombPlanted = phase is RoundPhase.BombPlanted or RoundPhase.Retake,
        };

        if (phase == RoundPhase.Live && liveStartedAt >= 0f)
            _liveStartedAt = liveStartedAt;
    }

    public void SetBomb(bool planted)
    {
        _context = _context with
        {
            BombPlanted = planted,
            Phase = planted ? RoundPhase.BombPlanted : RoundPhase.Live,
            SelectedGambleSite = planted ? CtGambleSite.None : _context.SelectedGambleSite,
        };
    }

    public void SetBomb(CtGambleSite site, bool planted)
    {
        _context = _context with
        {
            BombPlanted = planted,
            KnownBombsite = site switch
            {
                CtGambleSite.A => "A",
                CtGambleSite.B => "B",
                _ => _context.KnownBombsite,
            },
            Phase = planted ? RoundPhase.BombPlanted : RoundPhase.Live,
            SelectedGambleSite = planted ? CtGambleSite.None : _context.SelectedGambleSite,
        };
    }

    public void RecordContact(CtContact contact)
    {
        _lastContact = contact;
        _context = _context with { LastContact = contact };
        foreach (int slot in _bots.Keys)
            _contactsByBot[slot] = contact;
    }

    public void RecordContactForBot(int botSlot, CtContact contact)
    {
        _contactsByBot[botSlot] = contact;
        if (_lastContact == null || contact.RecordedAt >= _lastContact.RecordedAt)
        {
            _lastContact = contact;
            _context = _context with { LastContact = contact };
        }
    }

    public void RecordCtDeath(
        int victimSlot,
        float now,
        CtGambleSite site = CtGambleSite.None)
    {
        if (!_roles.TryGetValue(victimSlot, out var role))
            return;

        _recentDeaths.Add(new CtDeathEvent(victimSlot, role, now, site));
        _context = _context with
        {
            AliveTeam = Math.Max(0, _context.AliveTeam - 1),
            RotationUrgency = Math.Min(2, _context.RotationUrgency + 1),
        };
    }

    public void StartInformationProbe(int slot, float now)
    {
        if (!_roles.TryGetValue(slot, out var role)
            || role is not (CtRole.Information or CtRole.Playmaker or CtRole.Rotator))
            return;

        if (_probeStartedAt.ContainsKey(slot))
            return;

        if (_probeEndedAt.TryGetValue(slot, out float endedAt)
            && now - endedAt < ProbeCooldown)
            return;

        _probeStartedAt[slot] = now;
        _context = _context with { ActiveProbeCount = _probeStartedAt.Count };
    }

    public CtTacticalContext CreatePlanningSnapshot(float now)
    {
        Prune(now);
        EnsureOpeningProbe(now);

        var contacts = _contactsByBot
            .Select(entry => (entry.Key, Contact: EffectiveContact(entry.Value, now)))
            .Where(entry => entry.Contact is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Contact!);
        var effectiveContact = contacts.Values
            .OrderByDescending(contact => contact.RecordedAt)
            .FirstOrDefault();
        var round = _context with
        {
            LastContact = effectiveContact,
            ActiveProbeCount = _probeStartedAt.Count,
            LiveElapsedSeconds = Math.Max(0f, now - _liveStartedAt),
        };

        // The worker receives only immutable snapshots. Every collection is
        // copied here so event handlers can continue updating runtime state on
        // the game thread without racing the planner.
        return new CtTacticalContext(
            round,
            _bots.Values.ToArray(),
            new Dictionary<int, CtRole>(_roles),
            _recentDeaths.ToArray(),
            contacts,
            new Dictionary<int, float>(_probeStartedAt),
            new Dictionary<int, float>(_probeEndedAt),
            now,
            _ecoPlan);
    }

    private void EnsureOpeningProbe(float now)
    {
        if (_context.Phase != RoundPhase.Live
            || !HasOpeningProbeBudget()
            || _probeStartedAt.Count > 0
            || now - _liveStartedAt < 15f)
            return;

        var candidate = _roles
            .Where(entry => entry.Value is CtRole.Information or CtRole.Playmaker or CtRole.Rotator)
            .Select(entry => entry.Key)
            .FirstOrDefault(-1);
        if (candidate >= 0)
            StartInformationProbe(candidate, now);
    }

    private bool HasOpeningProbeBudget()
        => _context.CtBuyPhase is not BuyPhase.Save
            && _context.Phase is not RoundPhase.Save
            && _context.AliveTeam > 0;

    private void Prune(float now)
    {
        foreach (int slot in _probeStartedAt.Keys.ToList())
        {
            if (now - _probeStartedAt[slot] < ProbeDuration)
                continue;

            _probeEndedAt[slot] = _probeStartedAt[slot] + ProbeDuration;
            _probeStartedAt.Remove(slot);
        }

        // Keep a small buffer beyond the decision window so an event exactly
        // on the 3.5 second boundary is still available to the director.
        _recentDeaths.RemoveAll(death => now - death.RecordedAt > SameGroupDeathWindow + 1.5f);
        foreach (int slot in _contactsByBot.Keys.ToList())
        {
            if (now - _contactsByBot[slot].RecordedAt > ContactLifetime)
                _contactsByBot.Remove(slot);
        }
        _context = _context with { ActiveProbeCount = _probeStartedAt.Count };
    }

    private static CtContact? EffectiveContact(CtContact contact, float now)
    {
        float age = Math.Max(0f, now - contact.RecordedAt);
        if (age > ContactLifetime)
            return null;

        var confidence = contact.Confidence;
        if (age > 3f)
            confidence = ContactConfidence.Low;
        else if (age > 2f && confidence == ContactConfidence.High)
            confidence = ContactConfidence.Medium;

        return contact with
        {
            Confidence = confidence,
            IsHistorical = contact.IsHistorical || age > 1.5f,
        };
    }

}

public sealed record CtTacticalContext(
    RoundContext Round,
    IReadOnlyList<CtBotSnapshot> Bots,
    IReadOnlyDictionary<int, CtRole> Roles,
    IReadOnlyList<CtDeathEvent> RecentCtDeaths,
    IReadOnlyDictionary<int, CtContact> ContactsByBot,
    IReadOnlyDictionary<int, float> ProbeStartedAt,
    IReadOnlyDictionary<int, float> ProbeEndedAt,
    float Now,
    CtEcoPlan? EcoPlan = null)
{
    public CtContact? ContactFor(int botSlot)
        => ContactsByBot.TryGetValue(botSlot, out var contact) ? contact : null;
}

public static class CtTacticalDecisionPlanner
{
    public static IReadOnlyList<CtTacticalDecision> Plan(CtTacticalContext context)
    {
        var director = new CompetitiveTacticalDirector();
        var decisions = context.Bots
            .Select(bot => director.DecideCt(bot, context))
            .ToList();

        int budget = decisions.Count == 0
            ? 0
            : decisions.Max(decision => decision.ActiveBudget);
        var active = decisions
            .Where(decision => decision.IsActive)
            .OrderByDescending(decision => DecisionPriority(decision.State))
            .ThenByDescending(decision => context.Bots
                .First(bot => bot.Slot == decision.Slot).Aggression)
            .ThenBy(decision => decision.Slot)
            .ToList();

        if (active.Count <= budget)
            return decisions;

        var allowed = active.Take(budget).Select(decision => decision.Slot).ToHashSet();
        for (int index = 0; index < decisions.Count; index++)
        {
            if (decisions[index].IsActive && !allowed.Contains(decisions[index].Slot))
                decisions[index] = decisions[index] with { IsActive = false };
        }

        return decisions;
    }

    private static int DecisionPriority(CtTacticalState state)
        => state switch
        {
            CtTacticalState.Retake => 100,
            CtTacticalState.Reinforce => 90,
            CtTacticalState.Rotate => 80,
            CtTacticalState.Information => 70,
            CtTacticalState.Withdraw => 20,
            _ => 0,
        };
}

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
    private const float SameGroupDeathWindow = 3.5f;

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

    public CtTacticalDecision DecideCt(
        CtBotSnapshot bot,
        CtTacticalContext context)
    {
        CtRole role = context.Roles.GetValueOrDefault(bot.Slot, CtRole.AnchorGroupA);
        int activeBudget = GetActiveBudget(context.Round, context.Bots.Count);

        if (!bot.Alive)
        {
            return Decision(bot, role, CtTacticalState.Save, activeBudget,
                isActive: false, shouldRepath: false, shouldRun: false, "dead");
        }

        var contact = context.ContactFor(bot.Slot);
        bool reliableContact = contact != null
            && contact.Confidence is ContactConfidence.Medium or ContactConfidence.High;

        if (context.Round.BombPlanted
            || context.Round.Phase is RoundPhase.BombPlanted or RoundPhase.Retake)
        {
            var retake = RetakeDecisionPolicy.Evaluate(new RetakeContext(
                context.Round.AliveTeam,
                context.Round.AliveOpponent,
                BombPlanted: true,
                context.Round.BombSecondsRemaining,
                context.Round.CtTeamHasDefuser ? 1 : 0,
                context.Round.CtWeaponTier,
                context.Round.OpponentWeaponTier,
                context.Round.CtUtilityCount,
                context.Round.OpponentUtilityCount,
                context.Round.TeamScore,
                context.Round.OpponentScore,
                context.Round.Pressure,
                context.Round.RetakePathViable,
                bot.HasValuableWeapon ? 10 : 4)
            {
                BombTimerKnown = context.Round.BombTimerKnown,
                PathKnown = context.Round.RetakePathKnown,
                BombSite = context.Round.KnownBombsite?.Equals("B", StringComparison.OrdinalIgnoreCase) == true
                    ? CtGambleSite.B
                    : CtGambleSite.A,
                LastKill = context.Round.LastContact,
            });
            if (!retake.ShouldRetake)
            {
                return Decision(bot, role, CtTacticalState.Save, 0,
                    isActive: false, shouldRepath: true, shouldRun: true,
                    retake.Reason) with
                {
                    ShouldMoveToRetreat = true,
                    PreserveWeapon = bot.HasValuableWeapon,
                };
            }

            return Decision(bot, role, CtTacticalState.Retake, activeBudget,
                isActive: true, shouldRepath: true, shouldRun: true,
                retake.Reason);
        }

        bool responderRole = role is CtRole.Rotator or CtRole.Information or CtRole.Playmaker;
        bool hasLowThreat = context.Round.Threat.SiteA >= CtThreatLevel.Low
            || context.Round.Threat.SiteB >= CtThreatLevel.Low
            || context.Round.Threat.Mid >= CtThreatLevel.Low;
        if (hasLowThreat && responderRole)
        {
            return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                isActive: true, shouldRepath: true, shouldRun: true,
                context.Round.Threat.Mid >= CtThreatLevel.Low
                    ? "mid-threat-pre-rotate"
                    : "site-threat-pre-rotate") with
            {
                TargetSite = ResolveThreatSite(context, contact),
            };
        }

        if (context.RecentCtDeaths.Count > 0 && responderRole)
        {
            return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                isActive: true, shouldRepath: true, shouldRun: true, "ct-death") with
            {
                TargetSite = ResolveThreatSite(context, contact),
            };
        }

        bool isProbe = context.ProbeStartedAt.TryGetValue(bot.Slot, out float probeStartedAt);
        if (isProbe && context.Now - probeStartedAt < 4f)
        {
            return Decision(bot, role, CtTacticalState.Information, activeBudget,
                isActive: responderRole, shouldRepath: false, shouldRun: true, "opening-probe");
        }

        if (context.ProbeEndedAt.TryGetValue(bot.Slot, out float probeEndedAt)
            && context.Now - probeEndedAt < 11f)
        {
            return Decision(bot, role, CtTacticalState.Withdraw, activeBudget,
                isActive: false, shouldRepath: false, shouldRun: false, "probe-timeout");
        }

        var ecoAssignment = context.EcoPlan?.Assignments
            .FirstOrDefault(assignment => assignment.Slot == bot.Slot);
        if (ecoAssignment is not null
            && context.Round.Phase == RoundPhase.Live
            && context.Round.CtBuyPhase is BuyPhase.Pistol or BuyPhase.Eco or BuyPhase.HalfBuy or BuyPhase.ForceBuy)
        {
            bool otherSiteContact = reliableContact
                && contact?.Site is CtGambleSite.A or CtGambleSite.B
                && contact.Site != ecoAssignment.Site;
            if (!otherSiteContact
                && context.Round.Threat.ConfirmedSite is CtGambleSite.A or CtGambleSite.B
                && context.Round.Threat.ConfirmedSite != ecoAssignment.Site
                && responderRole)
            {
                return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                    isActive: true, shouldRepath: true, shouldRun: true,
                    "confirmed-site-threat") with
                {
                    TargetSite = context.Round.Threat.ConfirmedSite,
                };
            }
            if (otherSiteContact && responderRole)
            {
                return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                    isActive: true, shouldRepath: true, shouldRun: true,
                    "threat-site-rotation") with
                {
                    TargetSite = contact!.Site,
                };
            }

            CtTacticalState state = ecoAssignment.Role is CtEcoRole.Information
                or CtEcoRole.MidControl
                ? CtTacticalState.Information
                : CtTacticalState.Hold;
            return Decision(bot, role, state, 0,
                isActive: false, shouldRepath: true, shouldRun: true,
                $"eco-{context.EcoPlan!.Tactic}") with
            {
                TargetSite = ecoAssignment.Site,
                ShouldMoveToGambleSite = true,
            };
        }

        bool shouldSaveEconomyWeapon = context.Round.Pressure != MatchPressure.Elimination
            && context.Round.CtBuyPhase is BuyPhase.Eco or BuyPhase.HalfBuy
            && context.Round.AliveTeam <= 2
            && context.Round.AliveOpponent >= context.Round.AliveTeam + 2
            && !reliableContact
            && context.Round.LiveElapsedSeconds >= 4f;
        if (shouldSaveEconomyWeapon)
        {
            return Decision(bot, role, CtTacticalState.Save, 0,
                isActive: false, shouldRepath: false, shouldRun: false,
                "save-economy-man-disadvantage");
        }

        if (context.Round.Phase == RoundPhase.Save)
        {
            return Decision(bot, role, CtTacticalState.Save, activeBudget,
                isActive: false, shouldRepath: false, shouldRun: false, "save");
        }

        if (context.Round.Phase == RoundPhase.Freeze)
        {
            return Decision(bot, role, activeBudget > 0 ? CtTacticalState.Setup : CtTacticalState.Save,
                activeBudget, isActive: false, shouldRepath: false, shouldRun: false, "freeze");
        }

        CtRole? breachedGroup = context.RecentCtDeaths
            .Where(death => death.VictimRole is CtRole.AnchorGroupA or CtRole.AnchorGroupB
                && context.Now - death.RecordedAt <= SameGroupDeathWindow + 0.01f)
            .GroupBy(death => death.VictimRole)
            .Where(group => group.Count() >= 2)
            .Select(group => (CtRole?)group.Key)
            .FirstOrDefault();

        if (breachedGroup.HasValue)
        {
            bool canReinforce = role is CtRole.Rotator
                or CtRole.Information
                or CtRole.Playmaker
                || (role is CtRole.AnchorGroupA or CtRole.AnchorGroupB
                    && role != breachedGroup.Value);
            return Decision(bot, role, CtTacticalState.Reinforce, activeBudget,
                isActive: canReinforce, shouldRepath: canReinforce, shouldRun: canReinforce,
                "same-group-deaths") with
            {
                TargetSite = context.RecentCtDeaths
                    .Where(death => death.VictimRole == breachedGroup.Value)
                    .OrderByDescending(death => death.RecordedAt)
                    .Select(death => death.Site)
                    .FirstOrDefault(site => site != CtGambleSite.None),
            };
        }

        var economyCandidate = context.Bots
            .Where(candidate => candidate.Alive
                && context.Roles.GetValueOrDefault(candidate.Slot) is CtRole.AnchorGroupA or CtRole.AnchorGroupB)
            .OrderByDescending(candidate => candidate.Aggression)
            .ThenByDescending(candidate => candidate.Teamwork)
            .ThenBy(candidate => candidate.Slot)
            .FirstOrDefault();
        bool economyPlaymaker = activeBudget >= 2
            && context.Round.LiveElapsedSeconds >= 15f
            && context.Round.CtBuyPhase is BuyPhase.HalfBuy or BuyPhase.ForceBuy
                or BuyPhase.Eco
            && (context.Round.CtBuyPhase != BuyPhase.Eco
                || context.Round.OpponentBuyPhase == BuyPhase.FullBuy)
            && economyCandidate?.Slot == bot.Slot
            && bot.Aggression >= 0.65f;

        if (reliableContact)
        {
            if (responderRole)
            {
                return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                    isActive: true, shouldRepath: true, shouldRun: true, "reliable-contact") with
                {
                    TargetSite = contact?.Site ?? CtGambleSite.None,
                };
            }

            return Decision(bot, economyPlaymaker ? CtRole.Playmaker : role,
                economyPlaymaker ? CtTacticalState.Information : CtTacticalState.Hold,
                activeBudget, isActive: economyPlaymaker, shouldRepath: false,
                shouldRun: economyPlaymaker, economyPlaymaker ? "force-contact" : "anchor-contact");
        }

        // A Half/Force/Eco round can still make one controlled information
        // attempt, but never turns every high-aggression profile active.
        if (economyPlaymaker)
        {
            return Decision(bot, CtRole.Playmaker, CtTacticalState.Information, activeBudget,
                isActive: true, shouldRepath: false, shouldRun: true, "eco-trap");
        }

        return Decision(bot, role, CtTacticalState.Hold, activeBudget,
            isActive: false, shouldRepath: false, shouldRun: false, "no-reliable-contact");
    }

    private static CtGambleSite ResolveThreatSite(
        CtTacticalContext context,
        CtContact? contact)
    {
        CtThreatLevel siteA = context.Round.Threat.SiteA;
        CtThreatLevel siteB = context.Round.Threat.SiteB;
        if (siteA > CtThreatLevel.None || siteB > CtThreatLevel.None)
        {
            // Site evidence is more specific than a generic contact or the
            // previously selected gamble. Route to the stronger site first;
            // use the contact/fallback only when both site levels tie.
            if (siteA > siteB)
                return CtGambleSite.A;
            if (siteB > siteA)
                return CtGambleSite.B;

            if (contact?.Site is CtGambleSite.A or CtGambleSite.B)
                return contact.Site;

            if (context.Round.SelectedGambleSite is CtGambleSite.A or CtGambleSite.B)
                return context.Round.SelectedGambleSite;

            return CtGambleSite.A;
        }

        if (contact?.Site is CtGambleSite.A or CtGambleSite.B)
            return contact.Site;

        // Mid/connector information has no bomb site of its own. Route the
        // responder to the weaker site anchor so the main-thread executor
        // always receives an actionable A/B target instead of a nameless
        // Rotate decision.
        if (context.Round.Threat.Mid >= CtThreatLevel.Medium)
        {
            return context.Round.Threat.SiteA <= context.Round.Threat.SiteB
                ? CtGambleSite.A
                : CtGambleSite.B;
        }

        CtGambleSite deathSite = context.RecentCtDeaths
            .OrderByDescending(death => death.RecordedAt)
            .Select(death => death.Site)
            .FirstOrDefault(site => site is CtGambleSite.A or CtGambleSite.B);
        return deathSite is CtGambleSite.A or CtGambleSite.B
            ? deathSite
            : context.Round.SelectedGambleSite is CtGambleSite.A or CtGambleSite.B
                ? context.Round.SelectedGambleSite
                : CtGambleSite.A;
    }

    public IReadOnlyDictionary<int, CtRole> AssignCtRoles(
        IReadOnlyList<CtBotSnapshot> bots)
    {
        // The mutable runtime owns role state. This helper is intentionally
        // exposed on the director as a pure entry point for callers that only
        // need the deterministic role budget.
        var roles = new Dictionary<int, CtRole>();
        var alive = bots.Where(bot => bot.Alive)
            .OrderByDescending(bot => bot.Aggression)
            .ThenByDescending(bot => bot.Teamwork)
            .ThenBy(bot => bot.Slot)
            .ToList();
        if (alive.Count == 0) return roles;

        if (alive.Count >= 5)
        {
            var responder = alive[0];
            roles[responder.Slot] = responder.IsAwper || responder.Aggression < 0.65f
                ? CtRole.Rotator
                : CtRole.Information;
            foreach (var (bot, index) in alive.Skip(1).Select((bot, index) => (bot, index)))
                roles[bot.Slot] = index < 2 ? CtRole.AnchorGroupA : CtRole.AnchorGroupB;
        }
        else if (alive.Count == 4)
        {
            for (int i = 0; i < 2; i++) roles[alive[i].Slot] = CtRole.AnchorGroupA;
            roles[alive[2].Slot] = CtRole.AnchorGroupB;
            roles[alive[3].Slot] = CtRole.Rotator;
        }
        else if (alive.Count == 3)
        {
            roles[alive[0].Slot] = CtRole.AnchorGroupA;
            roles[alive[1].Slot] = CtRole.AnchorGroupB;
            roles[alive[2].Slot] = CtRole.Rotator;
        }
        else if (alive.Count == 2)
        {
            roles[alive[0].Slot] = CtRole.AnchorGroupA;
            roles[alive[1].Slot] = CtRole.AnchorGroupB;
        }
        else
        {
            roles[alive[0].Slot] = CtRole.AnchorGroupA;
        }

        return roles;
    }

    private static CtTacticalDecision Decision(
        CtBotSnapshot bot,
        CtRole role,
        CtTacticalState state,
        int activeBudget,
        bool isActive,
        bool shouldRepath,
        bool shouldRun,
        string reason)
        => new(bot.Slot, role, state, activeBudget, isActive,
            shouldRepath, shouldRun, reason);

    private static int GetActiveBudget(RoundContext context, int aliveCount)
        => context.Phase switch
        {
            RoundPhase.Save => 0,
            RoundPhase.BombPlanted or RoundPhase.Retake => aliveCount,
            _ => context.CtBuyPhase switch
            {
                BuyPhase.Save => 0,
                BuyPhase.HalfBuy or BuyPhase.ForceBuy or BuyPhase.Eco => 2,
                _ => 1,
            },
        };
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

public static class VisibilityGeometry
{
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
        float t = lengthSquared <= 0.0001f
            ? 0f
            : Math.Clamp(
                ((centerX - startX) * dx
                    + (centerY - startY) * dy
                    + (centerZ - startZ) * dz) / lengthSquared,
                0f,
                1f);

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
