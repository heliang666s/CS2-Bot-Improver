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
    public static bool ShouldUseHeadFirstInMixed(
        BotMatchProfile profile,
        string? weaponName)
        => profile == BotMatchProfile.Competitive && IsPistol(weaponName);

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

public enum CtSaveReason
{
    None,
    EcoNoContact,
    PostPlantOutnumbered,
    LateManDisadvantage,
}

public readonly record struct CtSaveDecision(
    bool ShouldSave,
    CtSaveReason Reason);

public static class CtSavePolicy
{
    public static CtSaveDecision Evaluate(
        BotMatchProfile profile,
        BuyPhase buyPhase,
        RoundPhase roundPhase,
        bool bombPlanted,
        int aliveCt,
        int aliveT,
        bool hasValuableWeapon,
        bool teamHasDefuser,
        bool hasReliableEnemyContact,
        bool probeCompleted,
        float liveElapsedSeconds,
        RetakeContext? retakeContext = null)
    {
        if (profile != BotMatchProfile.Competitive || !hasValuableWeapon)
            return NoSave();

        aliveCt = Math.Max(0, aliveCt);
        aliveT = Math.Max(0, aliveT);
        if (aliveCt == 0)
            return NoSave();

        if (bombPlanted || roundPhase is RoundPhase.BombPlanted or RoundPhase.Retake)
        {
            var decision = RetakeDecisionPolicy.Evaluate(retakeContext ?? new RetakeContext(
                aliveCt,
                aliveT,
                BombPlanted: true,
                BombSecondsRemaining: 999,
                CtDefusers: teamHasDefuser ? 1 : 0,
                CtWeaponTier: hasValuableWeapon ? 8 : 4,
                TWeaponTier: 6,
                CtUtility: 0,
                TUtility: 0,
                TeamScore: 0,
                OpponentScore: 0,
                IsMatchPoint: false,
                PathViable: true,
                BotWeaponValue: hasValuableWeapon ? 10 : 0));
            bool badlyOutnumbered = aliveCt == 1 && aliveT >= 2
                || aliveCt == 2 && aliveT >= 4;
            if (!decision.ShouldRetake && badlyOutnumbered && !teamHasDefuser)
                return new CtSaveDecision(true, CtSaveReason.PostPlantOutnumbered);

            return NoSave();
        }

        if (buyPhase == BuyPhase.Eco
            && probeCompleted
            && !hasReliableEnemyContact
            && aliveCt == 1
            && aliveT >= 2)
        {
            return new CtSaveDecision(true, CtSaveReason.EcoNoContact);
        }

        bool lateManDisadvantage = buyPhase is BuyPhase.Eco or BuyPhase.HalfBuy
            && aliveCt <= 2
            && aliveT >= aliveCt + 2
            && !hasReliableEnemyContact;
        return lateManDisadvantage
            ? new CtSaveDecision(true, CtSaveReason.LateManDisadvantage)
            : NoSave();
    }

    private static CtSaveDecision NoSave()
        => new(false, CtSaveReason.None);
}

public enum CtGambleSite
{
    None,
    A,
    B,
}

public enum CtGambleStage
{
    None,
    Stack,
    Hold,
    Rotate,
    Withdraw,
    Save,
}

public readonly record struct CtGambleDecision(
    bool Enabled,
    CtGambleSite Site,
    CtGambleStage Stage,
    int StackCount,
    int RotationBudget,
    bool ShouldMoveToSite,
    bool ShouldMoveToRetreat,
    bool PreserveWeapon,
    string Reason);

public static class CtGamblePolicy
{
    public static CtGambleSite SelectSite(int roundNumber, int entropy = 0)
        => ((roundNumber ^ entropy) & 1) == 0
            ? CtGambleSite.A
            : CtGambleSite.B;

    public static CtGambleDecision Evaluate(
        BotMatchProfile profile,
        BuyPhase buyPhase,
        RoundPhase roundPhase,
        CtGambleSite selectedSite,
        CtGambleSite contactSite,
        bool hasReliableContact,
        float liveElapsedSeconds,
        bool hasValuableWeapon,
        int aliveCt,
        RetakeContext? retakeContext = null)
    {
        if (profile != BotMatchProfile.Competitive
            || selectedSite == CtGambleSite.None
            || roundPhase is not RoundPhase.Live
            || buyPhase is not (BuyPhase.Pistol or BuyPhase.Eco or BuyPhase.HalfBuy or BuyPhase.ForceBuy))
        {
            return Disabled();
        }

        aliveCt = Math.Max(0, aliveCt);
        if (aliveCt == 0)
            return Disabled();

        if (hasReliableContact && contactSite == selectedSite)
        {
            return new CtGambleDecision(
                Enabled: true,
                selectedSite,
                CtGambleStage.Hold,
                StackCount: Math.Min(4, aliveCt),
                RotationBudget: 0,
                ShouldMoveToSite: false,
                ShouldMoveToRetreat: false,
                PreserveWeapon: false,
                Reason: "gambled-site-contact");
        }

        if (hasReliableContact
            && contactSite != CtGambleSite.None
            && contactSite != selectedSite)
        {
            return new CtGambleDecision(
                Enabled: true,
                selectedSite,
                CtGambleStage.Rotate,
                StackCount: Math.Min(4, aliveCt),
                RotationBudget: Math.Clamp(aliveCt >= 4 ? 2 : 1, 1, 2),
                ShouldMoveToSite: false,
                ShouldMoveToRetreat: false,
                PreserveWeapon: false,
                Reason: "other-site-contact");
        }

        return new CtGambleDecision(
            Enabled: true,
            selectedSite,
            CtGambleStage.Stack,
            StackCount: Math.Min(4, aliveCt),
            RotationBudget: 0,
            ShouldMoveToSite: true,
            ShouldMoveToRetreat: false,
            PreserveWeapon: false,
            Reason: "gambled-site-awaiting-information");
    }

    private static CtGambleDecision Disabled()
        => new(
            Enabled: false,
            Site: CtGambleSite.None,
            Stage: CtGambleStage.None,
            StackCount: 0,
            RotationBudget: 0,
            ShouldMoveToSite: false,
            ShouldMoveToRetreat: false,
            PreserveWeapon: false,
            Reason: "gamble-disabled");
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

public sealed record PurchaseIntentContext(
    BuyPhase Phase,
    int TeamScore,
    int OpponentScore,
    int RoundsPlayed,
    int MaxRounds,
    int CurrentTeamPower,
    int NextRoundTeamPower)
{
    public bool HasFeasibleTeamTransfer { get; init; }
    public bool IsMatchPoint { get; init; }
    public bool IsLastRegulationRound { get; init; }
    public bool IsLastRoundOfHalf { get; init; }
    public bool IsNextRoundLastRoundOfHalf { get; init; }
    public bool IsOvertimeFirstRound { get; init; }
}

public static class PurchaseIntentPolicy
{
    public static PurchaseIntent Evaluate(PurchaseIntentContext context)
    {
        if (context.IsOvertimeFirstRound)
            return context.IsMatchPoint ? PurchaseIntent.AllIn : PurchaseIntent.Standard;

        if (context.Phase == BuyPhase.Pistol)
            return PurchaseIntent.Pistol;

        if (context.Phase == BuyPhase.LastRound
            || context.IsLastRegulationRound
            || context.IsLastRoundOfHalf)
            return PurchaseIntent.LastRound;

        if (context.IsMatchPoint
            || context.TeamScore + 2 <= context.OpponentScore
            || context.CurrentTeamPower > context.NextRoundTeamPower + 2)
            return PurchaseIntent.AllIn;

        if (context.HasFeasibleTeamTransfer)
            return PurchaseIntent.Standard;

        if (context.IsNextRoundLastRoundOfHalf
            && context.Phase is BuyPhase.Eco or BuyPhase.Save)
            return PurchaseIntent.Save;

        return context.Phase == BuyPhase.Save
            || (context.Phase == BuyPhase.Eco
                && context.NextRoundTeamPower > context.CurrentTeamPower + 2)
            ? PurchaseIntent.Save
            : PurchaseIntent.Standard;
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
}

public sealed record TeamBuyMember(
    int Slot,
    bool IsBot,
    bool IsAwper,
    IReadOnlyList<PlayerBuyPlan> Candidates);

public sealed record TeamBuyPlan(
    IReadOnlyDictionary<int, PlayerBuyPlan> BotPlans,
    IReadOnlyDictionary<int, PlayerBuyPlan> HumanObservations,
    int MinTier,
    int MaxTier)
{
    public bool IsBalanced => MaxTier - MinTier <= 1;
    public int TotalCost => BotPlans.Values.Sum(plan => plan.EstimatedCost);
    public IReadOnlyList<TransferPlan> Transfers { get; init; } = Array.Empty<TransferPlan>();
    public IReadOnlyList<NextRoundScenarioPrediction> Forecasts { get; init; } = Array.Empty<NextRoundScenarioPrediction>();
    public int HumanTierPenalty { get; init; }
    public string Reason { get; init; } = string.Empty;
    public PurchaseIntent Intent { get; init; } = PurchaseIntent.Standard;
}

public static class TeamTierPlanner
{
    public static TeamBuyPlan Balance(IReadOnlyList<TeamBuyMember> members)
    {
        var humanObservations = members
            .Where(member => !member.IsBot && member.Candidates.Count > 0)
            .ToDictionary(member => member.Slot, member => member.Candidates[0]);

        var botMembers = members
            .Where(member => member.IsBot && member.Candidates.Count > 0)
            .ToArray();
        var ordinaryBots = botMembers.Where(member => !member.IsAwper).ToArray();

        int floor = FindHighestBalancedFloor(ordinaryBots);
        var botPlans = new Dictionary<int, PlayerBuyPlan>();
        foreach (var member in botMembers)
        {
            PlayerBuyPlan selected = member.IsAwper
                ? member.Candidates.OrderByDescending(plan => plan.Tier).ThenByDescending(plan => plan.EstimatedCost).First()
                : member.Candidates
                    .Where(plan => plan.Tier >= floor && plan.Tier <= floor + 1)
                    .OrderByDescending(plan => plan.Tier)
                    .ThenByDescending(plan => plan.EstimatedCost)
                    .FirstOrDefault()
                    ?? member.Candidates.OrderBy(plan => plan.Tier).ThenBy(plan => plan.EstimatedCost).First();
            botPlans[member.Slot] = selected;
        }

        int minTier = ordinaryBots.Length == 0
            ? botPlans.Values.Select(plan => plan.Tier).DefaultIfEmpty(0).Min()
            : botPlans
                .Where(entry => ordinaryBots.Any(member => member.Slot == entry.Key))
                .Select(entry => entry.Value.Tier)
                .DefaultIfEmpty(0)
                .Min();
        int maxTier = ordinaryBots.Length == 0
            ? botPlans.Values.Select(plan => plan.Tier).DefaultIfEmpty(0).Max()
            : botPlans
                .Where(entry => ordinaryBots.Any(member => member.Slot == entry.Key))
                .Select(entry => entry.Value.Tier)
                .DefaultIfEmpty(0)
                .Max();

        return new TeamBuyPlan(botPlans, humanObservations, minTier, maxTier);
    }

    private static int FindHighestBalancedFloor(IReadOnlyList<TeamBuyMember> ordinaryBots)
    {
        if (ordinaryBots.Count == 0)
            return 0;

        return Enumerable.Range(0, 10)
            .Where(floor => ordinaryBots.All(member => member.Candidates.Any(plan =>
                plan.Tier >= floor && plan.Tier <= floor + 1)))
            .DefaultIfEmpty(0)
            .Max();
    }
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
                         && participant.Plan.PrimaryWeapon is not null)
                     .OrderByDescending(participant => participant.Plan.Tier)
                     .ThenBy(participant => participant.Slot))
        {
            if (donorSpent[recipient.Slot] > 0 || recipients.Contains(recipient.Slot))
                continue;

            int cost = BuyPlanner.GetWeaponCost(recipient.Plan.PrimaryWeapon);
            if (cost <= 0)
                continue;

            var donor = participants
                .Where(candidate => candidate.IsBot && candidate.Slot != recipient.Slot)
                .Where(candidate => !recipients.Contains(candidate.Slot))
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Surplus = candidate.Money
                        - OwnPurchaseCost(candidate)
                        - donorSpent[candidate.Slot],
                })
                .Where(candidate => candidate.Surplus >= cost
                    && candidate.Candidate.Plan.Tier >= recipient.Plan.Tier - 1)
                .OrderByDescending(candidate => candidate.Surplus)
                .ThenBy(candidate => candidate.Candidate.Slot)
                .FirstOrDefault();

            if (donor is null)
                continue;

            donorSpent[donor.Candidate.Slot] += cost;
            recipients.Add(recipient.Slot);
            transfers.Add(new TransferPlan(
                donor.Candidate.Slot,
                recipient.Slot,
                recipient.Plan.PrimaryWeapon!,
                cost,
                "donor-surplus-within-tier"));
        }

        return transfers;
    }

    private static int OwnPurchaseCost(TransferParticipant participant)
        => Math.Max(0, participant.Plan.EstimatedCost);
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

public sealed record TeamDpMember(
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

public static class TeamDpPlanner
{
    private sealed record DpSelection(
        IReadOnlyDictionary<int, PlayerBuyPlan> Plans,
        IReadOnlyList<TransferPlan> Transfers,
        IReadOnlyList<NextRoundScenarioPrediction> Forecasts,
        int CurrentPower,
        int WorstMinTier,
        int WorstCombatPower,
        int AllScenarioCombatPower,
        int UnusedCash,
        bool MeetsFloor,
        bool IsBalanced,
        int TierGap,
        int HumanTierPenalty,
        int CommittedCost);

    public static TeamBuyPlan Optimize(
        TeamSide side,
        BuyPhase phase,
        IReadOnlyList<TeamDpMember> members,
        int currentMinTier,
        EconomyRewardRules rewards,
        int consecutiveLosses,
        int opponentPlayerCount = 0,
        PurchaseIntent purchaseIntent = PurchaseIntent.Standard,
        PurchaseIntentContext? purchaseContext = null)
    {
        bool prioritizeNextRoundBoundary = purchaseContext?.IsNextRoundLastRoundOfHalf == true;
        bool aggressiveIntent = purchaseIntent is PurchaseIntent.AllIn or PurchaseIntent.LastRound;
        var humanObservations = members
            .Where(member => !member.IsBot && member.Candidates.Count > 0)
            .ToDictionary(member => member.Slot, member => member.Candidates[0]);
        var bots = members
            .Where(member => member.IsBot && member.Candidates.Count > 0)
            .OrderBy(member => member.Slot)
            .ToArray();
        if (bots.Length == 0)
        {
            return new TeamBuyPlan(
                new Dictionary<int, PlayerBuyPlan>(),
                humanObservations,
                0,
                0)
            {
                Reason = "no-bot-candidates",
            };
        }

        int teamBudget = bots.Sum(member => Math.Max(0, member.Money));
        var suffixMinimumCost = new int[bots.Length + 1];
        for (int i = bots.Length - 1; i >= 0; i--)
        {
            int minimum = bots[i].Candidates
                .Select(candidate => EffectivePurchaseCost(bots[i], candidate))
                .DefaultIfEmpty(0)
                .Min();
            suffixMinimumCost[i] = suffixMinimumCost[i + 1] + minimum;
        }

        DpSelection? best = null;
        DpSelection? relaxed = null;
        var selected = new Dictionary<int, PlayerBuyPlan>();

        void Search(
            int index,
            int totalCost,
            int awpCount,
            int ordinaryMin,
            int ordinaryMax,
            bool allowUnbalanced)
        {
            // Transfers only redistribute the cost between bots; they cannot
            // reduce the team's total committed cost. Prune impossible
            // branches before reaching the expensive forecast calculation.
            if (totalCost > teamBudget
                || totalCost > teamBudget - suffixMinimumCost[index])
                return;

            if (awpCount > 1)
                return;
            if (!allowUnbalanced
                && ordinaryMin != int.MaxValue
                && ordinaryMax - ordinaryMin > 1)
                return;

            if (index == bots.Length)
            {
                var transferParticipants = bots
                    .Select(member => new TransferParticipant(
                        member.Slot,
                        IsBot: true,
                        member.Money,
                        selected[member.Slot],
                        member.CurrentPrimary))
                    .ToArray();
                var transfers = TeamTransferPlanner.BuildTransfers(transferParticipants);
                var purchaseCosts = BuildPurchaseCosts(bots, selected, transfers);
                if (purchaseCosts.Values.Any(cost => cost < 0)
                    || purchaseCosts.Any(entry => entry.Value > bots.First(member => member.Slot == entry.Key).Money))
                    return;
                int committedCost = purchaseCosts.Values.Sum();
                if (committedCost > teamBudget)
                    return;
                var scenarioParticipants = bots
                    .Select(member =>
                    {
                        var plan = selected[member.Slot];
                        return new ScenarioParticipant(
                            member.Slot,
                            side,
                            Math.Max(0, member.Money - purchaseCosts[member.Slot]),
                            plan,
                            member.Kills,
                            member.IsPlanter,
                            member.IsDefuser,
                            Math.Max(member.SavedTier, plan.Tier));
                    })
                    .ToArray();
                var forecasts = NextRoundPredictor.Predict(
                    scenarioParticipants,
                    rewards,
                    consecutiveLosses,
                    teamPlayerCount: members.Count,
                    opponentPlayerCount);
                var worst = forecasts.First(forecast =>
                    forecast.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead);
                bool meetsFloor = NextRoundEconomyPolicy.MeetsWorstCaseFloor(worst, currentMinTier);
                int tierGap = ordinaryMin == int.MaxValue ? 0 : ordinaryMax - ordinaryMin;
                bool isBalanced = tierGap <= 1;
                int humanTierPenalty = ComputeHumanTierPenalty(selected, humanObservations);
                var choice = new DpSelection(
                    new Dictionary<int, PlayerBuyPlan>(selected),
                    transfers,
                    forecasts,
                    selected.Values.Sum(plan => plan.Tier),
                    worst.MinTier,
                    worst.CombatPower,
                    forecasts.Sum(forecast => forecast.CombatPower),
                    teamBudget - totalCost,
                    meetsFloor,
                    isBalanced,
                    tierGap,
                    humanTierPenalty,
                    committedCost);

                if (aggressiveIntent)
                {
                    if (relaxed is null || IsBetterAggressive(choice, relaxed))
                        relaxed = choice;
                }
                else if (meetsFloor && isBalanced)
                {
                    if (best is null || IsBetter(choice, best, prioritizeNextRoundBoundary))
                        best = choice;
                }
                else if (relaxed is null || IsBetterFallback(choice, relaxed))
                {
                    relaxed = choice;
                }
                return;
            }

            var member = bots[index];
            foreach (var candidate in member.Candidates
                         .OrderByDescending(plan => plan.Tier)
                         .ThenByDescending(plan => plan.EstimatedCost))
            {
                selected[member.Slot] = candidate;
                int nextMin = ordinaryMin;
                int nextMax = ordinaryMax;
                if (!member.IsAwper)
                {
                    nextMin = Math.Min(nextMin, candidate.Tier);
                    nextMax = Math.Max(nextMax, candidate.Tier);
                }

                Search(
                    index + 1,
                    totalCost + EffectivePurchaseCost(member, candidate),
                    awpCount + (candidate.PrimaryWeapon == "weapon_awp" ? 1 : 0),
                    nextMin,
                    nextMax,
                    allowUnbalanced);
            }
            selected.Remove(member.Slot);
        }

        Search(0, 0, 0, int.MaxValue, int.MinValue, allowUnbalanced: aggressiveIntent);
        if (relaxed is null)
            Search(0, 0, 0, int.MaxValue, int.MinValue, allowUnbalanced: true);

        if (best is null && relaxed is null)
        {
            return new TeamBuyPlan(
                new Dictionary<int, PlayerBuyPlan>(),
                humanObservations,
                0,
                0)
            {
                Intent = purchaseIntent,
                Reason = "no-affordable-team-plan",
            };
        }

        var chosen = aggressiveIntent
            ? relaxed!
            : best ?? relaxed!;
        var ordinaryTiers = bots
            .Where(member => !member.IsAwper)
            .Select(member => chosen.Plans[member.Slot].Tier)
            .ToArray();
        return new TeamBuyPlan(
            chosen.Plans,
            humanObservations,
            ordinaryTiers.DefaultIfEmpty(0).Min(),
            ordinaryTiers.DefaultIfEmpty(0).Max())
        {
            Transfers = chosen.Transfers,
            Forecasts = chosen.Forecasts,
            HumanTierPenalty = chosen.HumanTierPenalty,
            Intent = purchaseIntent,
            Reason = purchaseIntent == PurchaseIntent.AllIn
                ? "all-in-current-round-power"
                : purchaseIntent == PurchaseIntent.LastRound
                ? "last-round-current-round-power"
                : chosen.MeetsFloor && chosen.IsBalanced
                ? "robust-team-dp"
                : chosen.IsBalanced
                    ? "balanced-floor-unmet-fallback"
                    : "unavoidable-tier-gap-fallback",
        };
    }

    private static int EffectivePurchaseCost(TeamDpMember member, PlayerBuyPlan plan)
        => Math.Max(0, plan.EstimatedCost);

    private static Dictionary<int, int> BuildPurchaseCosts(
        IReadOnlyList<TeamDpMember> members,
        IReadOnlyDictionary<int, PlayerBuyPlan> plans,
        IReadOnlyList<TransferPlan> transfers)
    {
        var costs = members.ToDictionary(
            member => member.Slot,
            member => EffectivePurchaseCost(member, plans[member.Slot]));

        foreach (var transfer in transfers)
        {
            costs[transfer.Donor] += transfer.Cost;
            costs[transfer.Recipient] = Math.Max(0, costs[transfer.Recipient] - transfer.Cost);
        }

        return costs;
    }

    private static int ComputeHumanTierPenalty(
        IReadOnlyDictionary<int, PlayerBuyPlan> botPlans,
        IReadOnlyDictionary<int, PlayerBuyPlan> humanObservations)
    {
        if (humanObservations.Count == 0)
            return 0;

        return botPlans.Values
            .Select(botPlan => humanObservations.Values
                .Select(humanPlan => Math.Max(0, Math.Abs(botPlan.Tier - humanPlan.Tier) - 1))
                .DefaultIfEmpty(0)
                .Min())
            .Sum();
    }

    private static bool IsBetter(
        DpSelection candidate,
        DpSelection current,
        bool prioritizeNextRoundBoundary = false)
        => candidate.HumanTierPenalty != current.HumanTierPenalty
            ? candidate.HumanTierPenalty < current.HumanTierPenalty
            : prioritizeNextRoundBoundary && candidate.WorstCombatPower != current.WorstCombatPower
            ? candidate.WorstCombatPower > current.WorstCombatPower
            : candidate.CurrentPower != current.CurrentPower
            ? candidate.CurrentPower > current.CurrentPower
            : candidate.WorstMinTier != current.WorstMinTier
                ? candidate.WorstMinTier > current.WorstMinTier
                : candidate.WorstCombatPower != current.WorstCombatPower
                    ? candidate.WorstCombatPower > current.WorstCombatPower
                    : candidate.AllScenarioCombatPower != current.AllScenarioCombatPower
                        ? candidate.AllScenarioCombatPower > current.AllScenarioCombatPower
                        : candidate.UnusedCash < current.UnusedCash;

    private static bool IsBetterAggressive(DpSelection candidate, DpSelection current)
        => candidate.CurrentPower != current.CurrentPower
            ? candidate.CurrentPower > current.CurrentPower
            : candidate.AllScenarioCombatPower != current.AllScenarioCombatPower
                ? candidate.AllScenarioCombatPower > current.AllScenarioCombatPower
                : candidate.WorstCombatPower != current.WorstCombatPower
                    ? candidate.WorstCombatPower > current.WorstCombatPower
                    : candidate.UnusedCash < current.UnusedCash;

    private static bool IsBetterFallback(DpSelection candidate, DpSelection current)
        => candidate.IsBalanced != current.IsBalanced
            ? candidate.IsBalanced
            : candidate.TierGap != current.TierGap
                ? candidate.TierGap < current.TierGap
                : candidate.MeetsFloor != current.MeetsFloor
                    ? candidate.MeetsFloor
                    : IsBetter(candidate, current);
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
    public const int DeaglePrice = 700;
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
        PurchaseIntent? purchaseIntent = null)
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
                intent)
            .First();
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
        PurchaseIntent? purchaseIntent = null)
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
                intent);

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

        return SelectPackages(side, phase, money, designatedAwper, opponentEcoLikely)
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
                || plan.EstimatedCost <= money)
            .ToArray();
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
        PurchaseIntent purchaseIntent)
    {
        var options = side == TeamSide.Terrorist
            ? new[]
            {
                new PistolOption(ArmorLevel.Full, "weapon_deagle", KevlarPrice + HelmetUpgradePrice + DeaglePrice, 90),
                new PistolOption(ArmorLevel.Full, "weapon_tec9", KevlarPrice + HelmetUpgradePrice + Tec9Price, 89),
                new PistolOption(ArmorLevel.Full, null, KevlarPrice + HelmetUpgradePrice, 80),
                new PistolOption(ArmorLevel.Half, "weapon_deagle", KevlarPrice + DeaglePrice, 70),
                new PistolOption(ArmorLevel.Half, "weapon_tec9", KevlarPrice + Tec9Price, 69),
                new PistolOption(ArmorLevel.Half, null, KevlarPrice, 60),
                new PistolOption(ArmorLevel.None, "weapon_deagle", DeaglePrice, 50),
                new PistolOption(ArmorLevel.None, "weapon_tec9", Tec9Price, 49),
                new PistolOption(ArmorLevel.None, null, 0, 0),
            }
            : new[]
            {
                new PistolOption(ArmorLevel.Full, "weapon_deagle", KevlarPrice + HelmetUpgradePrice + DeaglePrice, 90),
                new PistolOption(ArmorLevel.Full, "weapon_fiveseven", KevlarPrice + HelmetUpgradePrice + FiveSevenPrice, 89),
                new PistolOption(ArmorLevel.Full, null, KevlarPrice + HelmetUpgradePrice, 80),
                new PistolOption(ArmorLevel.Half, "weapon_deagle", KevlarPrice + DeaglePrice, 70),
                new PistolOption(ArmorLevel.Half, "weapon_fiveseven", KevlarPrice + FiveSevenPrice, 69),
                new PistolOption(ArmorLevel.Half, null, KevlarPrice, 60),
                new PistolOption(ArmorLevel.None, "weapon_deagle", DeaglePrice, 50),
                new PistolOption(ArmorLevel.None, "weapon_fiveseven", FiveSevenPrice, 49),
                new PistolOption(ArmorLevel.None, null, 0, 0),
            };

        return options
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
        ArmorLevel armor = (ArmorLevel)Math.Max((int)currentArmor, (int)targetArmor);
        int remaining = money;
        int cost = 0;

        bool buysHelmet = ShouldBuyHelmet(
            side,
            phase,
            requireHelmet,
            opponentEcoLikely,
            currentHasHelmet)
            && armor == ArmorLevel.Full;
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

    public static bool IsMatchPoint(
        int teamScore,
        int opponentScore,
        int roundsPlayed,
        int maxRounds = 24,
        int overtimeMaxRounds = 6)
    {
        if (teamScore <= opponentScore || roundsPlayed < 0)
            return false;

        maxRounds = maxRounds > 0 ? maxRounds : 24;
        overtimeMaxRounds = overtimeMaxRounds > 0 ? overtimeMaxRounds : 6;
        int regulationTarget = Math.Max(1, maxRounds / 2);
        int regulationWinScore = regulationTarget + 1;

        if (roundsPlayed < maxRounds)
        {
            // A team one round short of the regulation win target can close
            // the match in the next round. This is score-driven, not tied to
            // the 23rd round or a 12-12 boundary.
            return teamScore == regulationWinScore - 1;
        }

        int overtimeHalf = Math.Max(1, overtimeMaxRounds / 2);
        int overtimeBlock = (roundsPlayed - maxRounds) / overtimeMaxRounds;
        int blockBaseScore = regulationTarget + overtimeBlock * overtimeHalf;
        int teamOvertimeWins = teamScore - blockBaseScore;
        int opponentOvertimeWins = opponentScore - blockBaseScore;
        int overtimeWinScore = overtimeHalf + 1;

        // One OT round lead is not enough to be match point. In a standard
        // 6-round OT, 15-12 or 15-14 is match point; 13-12 is not.
        return teamOvertimeWins == overtimeWinScore - 1
            && opponentOvertimeWins < overtimeWinScore;
    }

    public static bool IsLastRegulationRound(
        int teamScore,
        int opponentScore,
        int roundsPlayed,
        int maxRounds = 24)
        => roundsPlayed == Math.Max(0, maxRounds - 1)
            && IsMatchPoint(teamScore, opponentScore, roundsPlayed, maxRounds);

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
    public bool IsMatchPoint { get; init; }
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
    bool IsMatchPoint,
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

        if (context.IsMatchPoint || context.TeamScore < context.OpponentScore)
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
            && (current >= 50 || current + 5 >= nextRound || context.IsMatchPoint);
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
    float RecordedAt);

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

    private readonly CompetitiveTacticalDirector _director = new();
    private readonly Dictionary<int, CtRole> _roles = new();
    private readonly Dictionary<int, CtBotSnapshot> _bots = new();
    private readonly Dictionary<int, float> _probeStartedAt = new();
    private readonly Dictionary<int, float> _probeEndedAt = new();
    private readonly Dictionary<int, CtContact> _contactsByBot = new();
    private readonly List<CtDeathEvent> _recentDeaths = new();
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

    public void SetRetakeInfo(
        int bombSecondsRemaining,
        int ctWeaponTier,
        int opponentWeaponTier,
        int ctUtilityCount,
        int opponentUtilityCount,
        bool pathViable,
        bool isMatchPoint,
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
            IsMatchPoint = isMatchPoint,
        };
    }

    public void SetCtGambleSite(CtGambleSite site)
    {
        if (site == CtGambleSite.None
            || _context.SelectedGambleSite != CtGambleSite.None
            || _context.BombPlanted)
            return;

        _context = _context with { SelectedGambleSite = site };
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

    public void RecordCtDeath(int victimSlot, float now)
    {
        if (!_roles.TryGetValue(victimSlot, out var role))
            return;

        _recentDeaths.Add(new CtDeathEvent(victimSlot, role, now));
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

    public IReadOnlyList<CtTacticalDecision> DecideAll(float now)
    {
        Prune(now);
        EnsureOpeningProbe(now);

        var effectiveContacts = _contactsByBot
            .Select(entry => (entry.Key, Contact: EffectiveContact(entry.Value, now)))
            .Where(entry => entry.Contact is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Contact!);
        var effectiveContact = effectiveContacts.Values
            .OrderByDescending(contact => contact.RecordedAt)
            .FirstOrDefault();
        _context = _context with
        {
            LastContact = effectiveContact,
            ActiveProbeCount = _probeStartedAt.Count,
            LiveElapsedSeconds = Math.Max(0f, now - _liveStartedAt),
        };

        var tacticalContext = new CtTacticalContext(
            _context,
            _bots.Values.ToArray(),
            _roles,
            _recentDeaths.ToArray(),
            effectiveContacts,
            _probeStartedAt,
            _probeEndedAt,
            now);

        var decisions = _bots.Values
            .Select(bot => _director.DecideCt(bot, tacticalContext))
            .ToList();

        int budget = decisions.Count == 0
            ? 0
            : decisions.Max(decision => decision.ActiveBudget);
        var active = decisions
            .Where(decision => decision.IsActive)
            .OrderByDescending(decision => DecisionPriority(decision.State))
            .ThenByDescending(decision => _bots[decision.Slot].Aggression)
            .ThenBy(decision => decision.Slot)
            .ToList();

        if (active.Count > budget)
        {
            var allowed = active.Take(budget).Select(decision => decision.Slot).ToHashSet();
            for (int i = 0; i < decisions.Count; i++)
            {
                if (decisions[i].IsActive && !allowed.Contains(decisions[i].Slot))
                    decisions[i] = decisions[i] with { IsActive = false };
            }
        }

        return decisions;
    }

    private void EnsureOpeningProbe(float now)
    {
        if (_context.Phase != RoundPhase.Live
            || ActiveBudget() <= 0
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

    private int ActiveBudget()
        => _context.Phase switch
        {
            RoundPhase.Save => 0,
            RoundPhase.BombPlanted or RoundPhase.Retake => _bots.Count,
            _ => _context.CtBuyPhase switch
            {
                BuyPhase.Save => 0,
                BuyPhase.HalfBuy or BuyPhase.ForceBuy or BuyPhase.Eco => 2,
                _ => 1,
            },
        };

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

public sealed record CtTacticalContext(
    RoundContext Round,
    IReadOnlyList<CtBotSnapshot> Bots,
    IReadOnlyDictionary<int, CtRole> Roles,
    IReadOnlyList<CtDeathEvent> RecentCtDeaths,
    IReadOnlyDictionary<int, CtContact> ContactsByBot,
    IReadOnlyDictionary<int, float> ProbeStartedAt,
    IReadOnlyDictionary<int, float> ProbeEndedAt,
    float Now)
{
    public CtContact? ContactFor(int botSlot)
        => ContactsByBot.TryGetValue(botSlot, out var contact) ? contact : null;
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
        bool probeCompleted = context.ProbeEndedAt.Count > 0;

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
                context.Round.IsMatchPoint,
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
        if (context.RecentCtDeaths.Count > 0 && responderRole)
        {
            return Decision(bot, role, CtTacticalState.Rotate, activeBudget,
                isActive: true, shouldRepath: true, shouldRun: true, "ct-death");
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

        var gambleDecision = CtGamblePolicy.Evaluate(
            BotMatchProfile.Competitive,
            context.Round.CtBuyPhase,
            context.Round.Phase,
            context.Round.SelectedGambleSite,
            contact?.Site ?? CtGambleSite.None,
            reliableContact,
            context.Round.LiveElapsedSeconds,
            bot.HasValuableWeapon,
            context.Round.AliveTeam);
        if (gambleDecision.Enabled)
        {
            if (gambleDecision.Stage == CtGambleStage.Save)
            {
                return Decision(bot, role, CtTacticalState.Save, 0,
                    isActive: false, shouldRepath: true, shouldRun: true,
                    gambleDecision.Reason) with
                {
                    TargetSite = gambleDecision.Site,
                    ShouldMoveToRetreat = true,
                    PreserveWeapon = gambleDecision.PreserveWeapon,
                };
            }

            if (gambleDecision.Stage == CtGambleStage.Withdraw)
            {
                return Decision(bot, role, CtTacticalState.Withdraw, 0,
                    isActive: false, shouldRepath: true, shouldRun: true,
                    gambleDecision.Reason) with
                {
                    TargetSite = gambleDecision.Site,
                    ShouldMoveToRetreat = true,
                    PreserveWeapon = gambleDecision.PreserveWeapon,
                };
            }

            if (gambleDecision.Stage == CtGambleStage.Rotate)
            {
                bool canRotate = role is CtRole.Information
                    or CtRole.Rotator
                    or CtRole.Playmaker;
                if (canRotate && contact?.Site is CtGambleSite.A or CtGambleSite.B)
                {
                    return Decision(bot, role, CtTacticalState.Rotate, gambleDecision.RotationBudget,
                        isActive: true, shouldRepath: true, shouldRun: true,
                        gambleDecision.Reason) with
                    {
                        TargetSite = contact.Site,
                    };
                }

                return Decision(bot, role, CtTacticalState.Hold, 0,
                    isActive: false, shouldRepath: false, shouldRun: true,
                    "gambled-site-hold") with
                {
                    TargetSite = gambleDecision.Site,
                    ShouldMoveToGambleSite = true,
                };
            }

            return Decision(bot, role, CtTacticalState.Hold, 0,
                isActive: false, shouldRepath: false, shouldRun: true,
                gambleDecision.Reason) with
            {
                TargetSite = gambleDecision.Site,
                ShouldMoveToGambleSite = gambleDecision.Stage is CtGambleStage.Stack or CtGambleStage.Hold,
            };
        }

        var saveDecision = CtSavePolicy.Evaluate(
            BotMatchProfile.Competitive,
            context.Round.CtBuyPhase,
            context.Round.Phase,
            context.Round.BombPlanted,
            context.Round.AliveTeam,
            context.Round.AliveOpponent,
            bot.HasValuableWeapon,
            context.Round.CtTeamHasDefuser,
            reliableContact,
            probeCompleted,
            context.Round.LiveElapsedSeconds);
        if (saveDecision.ShouldSave)
        {
            return Decision(bot, role, CtTacticalState.Save, 0,
                isActive: false, shouldRepath: false, shouldRun: false,
                $"save-{saveDecision.Reason}");
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
                "same-group-deaths");
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
                    isActive: true, shouldRepath: true, shouldRun: true, "reliable-contact");
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
