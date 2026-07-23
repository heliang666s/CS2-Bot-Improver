using System.Diagnostics;

namespace CompetitiveBotCore;

public enum MatchPressure
{
    Normal,
    EconomySwing,
    HalfClosing,
    Clinch,
    Elimination,
}

public readonly record struct MatchFormatSnapshot(
    int MaxRounds,
    bool OvertimeEnabled,
    int OvertimeMaxRounds)
{
    public int RegulationHalf => Math.Max(1, MaxRounds > 0 ? MaxRounds / 2 : 12);
    public int OvertimeHalf => Math.Max(1, OvertimeMaxRounds > 0 ? OvertimeMaxRounds / 2 : 3);
}

public readonly record struct MatchPressureResult(
    MatchPressure Level,
    bool TeamCanEndMatch,
    bool OpponentCanEndMatch,
    bool IsHalfClosing)
{
    public bool RequiresAllIn
        => TeamCanEndMatch || OpponentCanEndMatch || IsHalfClosing;

    public bool IsMustWin => TeamCanEndMatch || OpponentCanEndMatch;
}

public static class MatchPressurePolicy
{
    public static MatchPressureResult Evaluate(
        int teamScore,
        int opponentScore,
        int roundsPlayed,
        MatchFormatSnapshot format,
        bool economySwing)
    {
        int maxRounds = format.MaxRounds > 0 ? format.MaxRounds : 24;
        int overtimeMaxRounds = format.OvertimeMaxRounds > 0 ? format.OvertimeMaxRounds : 6;
        int regulationHalf = Math.Max(1, maxRounds / 2);
        int overtimeHalf = Math.Max(1, overtimeMaxRounds / 2);
        roundsPlayed = Math.Max(0, roundsPlayed);

        bool teamCanEnd;
        bool opponentCanEnd;
        bool halfClosing;

        if (!format.OvertimeEnabled || roundsPlayed < maxRounds)
        {
            int target = regulationHalf + 1;
            teamCanEnd = teamScore >= target - 1 && teamScore > opponentScore;
            opponentCanEnd = opponentScore >= target - 1 && opponentScore > teamScore;
            halfClosing = roundsPlayed == regulationHalf - 1
                || roundsPlayed == maxRounds - 1;
        }
        else
        {
            int overtimeRoundsPlayed = roundsPlayed - maxRounds;
            int block = overtimeRoundsPlayed / overtimeMaxRounds;
            int blockBaseScore = regulationHalf + block * overtimeHalf;
            int teamOvertimeWins = teamScore - blockBaseScore;
            int opponentOvertimeWins = opponentScore - blockBaseScore;
            int target = overtimeHalf + 1;

            teamCanEnd = teamOvertimeWins >= target - 1
                && teamOvertimeWins > opponentOvertimeWins;
            opponentCanEnd = opponentOvertimeWins >= target - 1
                && opponentOvertimeWins > teamOvertimeWins;

            int roundInBlock = overtimeRoundsPlayed % overtimeMaxRounds;
            halfClosing = roundInBlock % overtimeHalf == overtimeHalf - 1;
        }

        MatchPressure level = teamCanEnd
            ? MatchPressure.Clinch
            : opponentCanEnd
                ? MatchPressure.Elimination
                : halfClosing
                    ? MatchPressure.HalfClosing
                    : economySwing
                        ? MatchPressure.EconomySwing
                        : MatchPressure.Normal;

        return new(level, teamCanEnd, opponentCanEnd, halfClosing);
    }
}

public enum TeamBuyMode
{
    Eco,
    Half,
    Force,
    Full,
    MustWin,
}

public static class TeamBuyModePolicy
{
    public static PurchaseIntent ToPurchaseIntent(TeamBuyMode mode, BuyPhase phase)
        => phase == BuyPhase.Pistol
            ? PurchaseIntent.Pistol
            : mode switch
            {
                TeamBuyMode.Eco => PurchaseIntent.Save,
                TeamBuyMode.MustWin => PurchaseIntent.AllIn,
                TeamBuyMode.Half => PurchaseIntent.Standard,
                TeamBuyMode.Force => PurchaseIntent.Standard,
                _ => PurchaseIntent.Standard,
            };

    public static TeamBuyMode Resolve(
        MatchPressureResult pressure,
        BuyPhase phase,
        bool canReachFullBuy,
        bool forceSignal)
        => pressure.RequiresAllIn
            ? TeamBuyMode.MustWin
            : Resolve(pressure.Level, phase, canReachFullBuy, forceSignal);

    public static TeamBuyMode Resolve(
        MatchPressure pressure,
        BuyPhase phase,
        bool canReachFullBuy,
        bool forceSignal)
    {
        if (pressure is MatchPressure.Clinch
            or MatchPressure.Elimination
            or MatchPressure.HalfClosing)
        {
            return TeamBuyMode.MustWin;
        }

        if (phase == BuyPhase.LastRound)
            return TeamBuyMode.MustWin;

        if (phase == BuyPhase.FullBuy && canReachFullBuy)
            return TeamBuyMode.Full;
        if (phase == BuyPhase.ForceBuy || forceSignal)
            return TeamBuyMode.Force;
        if (phase == BuyPhase.HalfBuy)
            return TeamBuyMode.Half;
        return TeamBuyMode.Eco;
    }
}

public readonly record struct SnapshotVersion(int RoundKey, long Value);

public readonly record struct PlanVersion(
    int RoundKey,
    long SnapshotValue,
    long PlanValue)
{
    public bool Matches(PlanVersion other)
        => RoundKey == other.RoundKey
            && SnapshotValue == other.SnapshotValue
            && PlanValue == other.PlanValue;
}

public sealed record PlanningResult<TResult>(
    PlanVersion Version,
    TResult Result);

public sealed class LatestOnlyPlanningWorker<TInput, TResult> : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<TInput, TResult> _planner;
    private readonly Action<PlanningResult<TResult>> _publish;
    private Pending? _pending;
    private PlanVersion _latestVersion;
    private bool _running;
    private bool _disposed;

    private sealed record Pending(TInput Input, PlanVersion Version);

    public LatestOnlyPlanningWorker(
        Func<TInput, TResult> planner,
        Action<PlanningResult<TResult>> publish)
    {
        _planner = planner;
        _publish = publish;
    }

    public void Submit(TInput input, PlanVersion version)
    {
        lock (_gate)
        {
            if (_disposed || IsOlder(version, _latestVersion))
                return;

            _latestVersion = version;
            _pending = new Pending(input, version);
            if (_running)
                return;

            _running = true;
            _ = Task.Run(ProcessLoop);
        }
    }

    public void Reset(PlanVersion baseline)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Invalidate work from a previous round/map. A round key normally
            // increases, but map changes and hot reloads can move it backwards.
            _latestVersion = baseline;
            _pending = null;
        }
    }

    private void ProcessLoop()
    {
        while (true)
        {
            Pending? work;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _running = false;
                    return;
                }

                work = _pending;
                _pending = null;
            }

            TResult result;
            try
            {
                result = _planner(work.Input);
            }
            catch
            {
                // A strategy failure must not kill the worker or block newer
                // snapshots. The next submission remains eligible to run.
                continue;
            }

            bool stillCurrent;
            lock (_gate)
            {
                stillCurrent = !_disposed && work.Version.Matches(_latestVersion);
            }

            if (stillCurrent)
                _publish(new PlanningResult<TResult>(work.Version, result));
        }
    }

    private static bool IsOlder(PlanVersion candidate, PlanVersion current)
    {
        if (candidate.RoundKey != current.RoundKey)
            return candidate.RoundKey < current.RoundKey;
        if (candidate.SnapshotValue != current.SnapshotValue)
            return candidate.SnapshotValue < current.SnapshotValue;
        return candidate.PlanValue < current.PlanValue;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
        }
    }
}

public readonly record struct BombSnapshot(
    bool Planted,
    string? Site,
    float PlantedAt,
    float BlowAt,
    bool TimerKnown,
    int SecondsRemaining);

public readonly record struct TeamThreatSnapshot(
    float SiteAThreat,
    float SiteBThreat,
    float MidThreat,
    float LastUpdatedAt,
    bool ConfirmedBombSite);

public readonly record struct InventorySnapshot
{
    public InventorySnapshot(
        int Money,
        ArmorLevel Armor,
        string? PrimaryWeapon,
        string? SecondaryWeapon,
        bool HasHelmet,
        bool HasDefuser,
        IReadOnlyList<string>? Utility)
    {
        this.Money = Money;
        this.Armor = Armor;
        this.PrimaryWeapon = PrimaryWeapon;
        this.SecondaryWeapon = SecondaryWeapon;
        this.HasHelmet = HasHelmet;
        this.HasDefuser = HasDefuser;
        this.Utility = (Utility ?? Array.Empty<string>())
            .Select(InventoryItemNames.Normalize)
            .ToArray();
    }

    public int Money { get; init; }
    public ArmorLevel Armor { get; init; }
    public string? PrimaryWeapon { get; init; }
    public string? SecondaryWeapon { get; init; }
    public bool HasHelmet { get; init; }
    public bool HasDefuser { get; init; }
    public IReadOnlyList<string> Utility { get; init; }

    public bool Contains(string itemName)
    {
        string normalized = InventoryItemNames.Normalize(itemName);
        return PrimaryWeapon == normalized
            || SecondaryWeapon == normalized
            || Utility.Contains(normalized, StringComparer.Ordinal);
    }
}

public static class InventoryItemNames
{
    public static string Normalize(string itemName)
        => itemName switch
        {
            "smoke" => "weapon_smokegrenade",
            "flash" => "weapon_flashbang",
            "he" => "weapon_hegrenade",
            "molotov" => "weapon_molotov",
            _ => itemName,
        };
}

public interface IInventoryPort
{
    InventorySnapshot Capture();
    bool TryBuy(string itemName);
    bool TryRemove(string itemName);
    bool TryGrant(string itemName);
    bool Contains(string itemName);
    bool TryRestore(InventorySnapshot snapshot);
}

public enum InventoryTransactionStage
{
    Capture,
    Armor,
    Primary,
    Secondary,
    Helmet,
    Defuser,
    Utility,
    Commit,
}

public readonly record struct InventoryTransactionResult(
    bool Committed,
    InventoryTransactionStage Stage,
    string Reason)
{
    public static InventoryTransactionResult Success()
        => new(true, InventoryTransactionStage.Commit, "committed");
}

public sealed class BuyExecutionTransaction
{
    private readonly IInventoryPort _port;

    public BuyExecutionTransaction(IInventoryPort port)
    {
        _port = port;
    }

    public InventoryTransactionResult Execute(
        PlayerBuyPlan plan,
        TeamSide side,
        string? grantedPrimary = null)
    {
        var before = _port.Capture();
        if (!BuyPlanner.IsCombatLegal(plan))
            return Rollback(before, InventoryTransactionStage.Capture, "illegal-primary-without-armor");

        if (plan.BuysArmor && before.Armor == ArmorLevel.None)
        {
            string armorItem = plan.ArmorLevel == ArmorLevel.Full
                ? "item_assaultsuit"
                : "item_kevlar";
            if (!BuyAndConfirm(armorItem, snapshot => snapshot.Armor != ArmorLevel.None))
                return Rollback(before, InventoryTransactionStage.Armor, "armor-purchase-failed");
        }

        if (plan.BuysHelmet && !_port.Capture().HasHelmet
            && !BuyAndConfirm("item_assaultsuit", snapshot => snapshot.HasHelmet))
        {
            return Rollback(before, InventoryTransactionStage.Helmet, "helmet-purchase-failed");
        }

        if (plan.PrimaryWeapon is { } primary)
        {
            var current = _port.Capture().PrimaryWeapon;
            if (!string.Equals(current, primary, StringComparison.Ordinal))
            {
                if (plan.ReplacePrimaryWeapon is { } replacement
                    && string.Equals(current, replacement, StringComparison.Ordinal)
                    && !_port.TryRemove(replacement))
                {
                    return Rollback(
                        before,
                        InventoryTransactionStage.Primary,
                        "replacement-remove-failed");
                }

                bool granted = string.Equals(grantedPrimary, primary, StringComparison.Ordinal)
                    && _port.Contains(primary);
                if (!granted && !BuyAndConfirm(
                        primary,
                        snapshot => string.Equals(snapshot.PrimaryWeapon, primary, StringComparison.Ordinal)))
                {
                    return Rollback(before, InventoryTransactionStage.Primary, "primary-confirmation-failed");
                }
            }
        }

        if (plan.SecondaryWeapon is { } secondary)
        {
            var current = _port.Capture().SecondaryWeapon;
            if (!string.Equals(current, secondary, StringComparison.Ordinal))
            {
                if (current is not null && !_port.TryRemove(current))
                    return Rollback(before, InventoryTransactionStage.Secondary, "default-pistol-removal-failed");
                if (!BuyAndConfirm(
                        secondary,
                        snapshot => string.Equals(snapshot.SecondaryWeapon, secondary, StringComparison.Ordinal)))
                {
                    return Rollback(before, InventoryTransactionStage.Secondary, "secondary-confirmation-failed");
                }
            }
        }

        if (plan.BuysDefuser && side == TeamSide.CounterTerrorist
            && !_port.Capture().HasDefuser
            && !BuyAndConfirm("item_defuser", snapshot => snapshot.HasDefuser))
        {
            return Rollback(before, InventoryTransactionStage.Defuser, "defuser-purchase-failed");
        }

        foreach (var utilityGroup in plan.Utility
                     .Select(utility => ToUtilityItem(utility, side))
                     .Where(item => item is not null)
                     .GroupBy(item => item!, StringComparer.Ordinal))
        {
            string item = utilityGroup.Key;
            int currentCount = _port.Capture().Utility.Count(
                owned => string.Equals(owned, item, StringComparison.Ordinal));
            for (int index = currentCount; index < utilityGroup.Count(); index++)
            {
                int expectedCount = index + 1;
                if (!BuyAndConfirm(
                        item,
                        snapshot => snapshot.Utility.Count(
                            owned => string.Equals(owned, item, StringComparison.Ordinal))
                            >= expectedCount))
                {
                    return Rollback(
                        before,
                        InventoryTransactionStage.Utility,
                        $"utility-purchase-failed:{item}");
                }
            }
        }

        return InventoryTransactionResult.Success();
    }

    private bool BuyAndConfirm(string itemName, Func<InventorySnapshot, bool> confirmation)
        => _port.TryBuy(itemName) && confirmation(_port.Capture());

    private InventoryTransactionResult Rollback(
        InventorySnapshot before,
        InventoryTransactionStage stage,
        string reason)
    {
        _port.TryRestore(before);
        return new InventoryTransactionResult(false, stage, reason);
    }

    public static string? ToUtilityItem(string utility, TeamSide side)
        => utility switch
        {
            "smoke" => "weapon_smokegrenade",
            "flash" => "weapon_flashbang",
            "he" => "weapon_hegrenade",
            "molotov" when side == TeamSide.Terrorist => "weapon_molotov",
            "molotov" => "weapon_incgrenade",
            "weapon_smokegrenade" => "weapon_smokegrenade",
            "weapon_flashbang" => "weapon_flashbang",
            "weapon_hegrenade" => "weapon_hegrenade",
            "weapon_molotov" => "weapon_molotov",
            "weapon_incgrenade" => "weapon_incgrenade",
            _ => null,
        };
}

public sealed class WeaponGrantTransaction
{
    private readonly IInventoryPort _donor;
    private readonly IInventoryPort _recipient;

    public WeaponGrantTransaction(IInventoryPort donor, IInventoryPort recipient)
    {
        _donor = donor;
        _recipient = recipient;
    }

    public InventoryTransactionResult Execute(
        string weapon,
        int expectedCost,
        string? donorRearmWeapon = null)
    {
        var donorBefore = _donor.Capture();
        var recipientBefore = _recipient.Capture();
        if (!BuyPlanner.IsPrimaryWeapon(weapon)
            || BuyPlanner.GetWeaponCost(weapon) != expectedCost)
        {
            return new(false, InventoryTransactionStage.Primary, "invalid-transfer-item");
        }
        if (donorRearmWeapon is not null
            && (!BuyPlanner.IsPrimaryWeapon(donorRearmWeapon)
                || BuyPlanner.GetWeaponCost(donorRearmWeapon) <= 0))
        {
            return new(false, InventoryTransactionStage.Primary, "invalid-donor-rearm-item");
        }

        if (!_donor.TryBuy(weapon) || !_donor.Contains(weapon))
            return Rollback(donorBefore, recipientBefore, "donor-purchase-failed");
        if (!_donor.TryRemove(weapon))
            return Rollback(donorBefore, recipientBefore, "donor-removal-failed");
        if (!_recipient.TryGrant(weapon) || !_recipient.Contains(weapon))
            return Rollback(donorBefore, recipientBefore, "recipient-confirmation-failed");
        if (donorRearmWeapon is not null
            && (!_donor.TryBuy(donorRearmWeapon)
                || !_donor.Contains(donorRearmWeapon)))
        {
            return Rollback(donorBefore, recipientBefore, "donor-rearm-failed");
        }

        return InventoryTransactionResult.Success();
    }

    private InventoryTransactionResult Rollback(
        InventorySnapshot donorBefore,
        InventorySnapshot recipientBefore,
        string reason)
    {
        _donor.TryRestore(donorBefore);
        _recipient.TryRestore(recipientBefore);
        return new(false, InventoryTransactionStage.Primary, reason);
    }
}

public sealed class UtilityThrowTransaction
{
    private readonly IInventoryPort _port;
    private readonly IUtilityLedger _ledger;

    public UtilityThrowTransaction(IInventoryPort port, IUtilityLedger ledger)
    {
        _port = port;
        _ledger = ledger;
    }

    public InventoryTransactionResult Execute(
        UtilityType type,
        UtilitySource source,
        bool spawnSucceeded)
    {
        string item = type switch
        {
            UtilityType.Smoke => "weapon_smokegrenade",
            UtilityType.Flash => "weapon_flashbang",
            UtilityType.He => "weapon_hegrenade",
            UtilityType.Molotov => "weapon_molotov",
            _ => string.Empty,
        };
        var before = _port.Capture();
        if (item.Length == 0 || !_port.Contains(item))
            return new(false, InventoryTransactionStage.Utility, "real-inventory-missing");
        if (!_ledger.TryConsume(type, source))
            return new(false, InventoryTransactionStage.Utility, "team-utility-budget-exhausted");
        if (!_port.TryRemove(item))
        {
            _ledger.Refund(type);
            return new(false, InventoryTransactionStage.Utility, "real-inventory-remove-failed");
        }

        if (spawnSucceeded)
            return InventoryTransactionResult.Success();

        _port.TryGrant(item);
        _port.TryRestore(before);
        _ledger.Refund(type);
        return new(false, InventoryTransactionStage.Utility, "projectile-spawn-failed");
    }
}

public sealed record RoundSnapshot(
    SnapshotVersion Version,
    MatchFormatSnapshot Format,
    MatchPressureResult Pressure,
    int RoundNumber,
    int RoundsPlayed,
    int TeamScore,
    int OpponentScore,
    TeamSide Side,
    RoundPhase Phase,
    BuyPhase BuyPhase,
    TeamBuyMode BuyMode,
    BombSnapshot Bomb,
    TeamThreatSnapshot Threat,
    float CapturedAt);

public readonly record struct BoundedPlannerOptions(
    int MaxCandidatesPerBot = 6,
    int MaxFrontierStates = 256,
    int HardBudgetMilliseconds = 5)
{
    public int CandidateLimit => Math.Max(1, MaxCandidatesPerBot);
    public int FrontierLimit => Math.Max(1, MaxFrontierStates);
    public int BudgetMilliseconds => Math.Max(1, HardBudgetMilliseconds);
}

public readonly record struct BoundedPlannerDiagnostics(
    int MaxCandidatesPerBot,
    int MaxFrontierStates,
    bool TimedOut,
    long ElapsedTicks)
{
    public double ElapsedMilliseconds
        => ElapsedTicks * 1000d / Stopwatch.Frequency;
}

public sealed record BoundedTeamBuyResult(
    TeamBuyPlan Plan,
    BoundedPlannerDiagnostics Diagnostics)
{
    // These projections keep assertions concise while making the bounded
    // result the only public team-planning result type.
    public bool IsBalanced => Plan.IsBalanced;
    public int MinTier => Plan.MinTier;
    public int MaxTier => Plan.MaxTier;
    public string Reason => Plan.Reason;
    public IReadOnlyDictionary<int, PlayerBuyPlan> BotPlans => Plan.BotPlans;
    public IReadOnlyDictionary<int, PlayerBuyPlan> HumanObservations => Plan.HumanObservations;
    public IReadOnlyList<TransferPlan> Transfers => Plan.Transfers;
    public IReadOnlyList<NextRoundScenarioPrediction> Forecasts => Plan.Forecasts;
    public int HumanTierPenalty => Plan.HumanTierPenalty;
}

public static class BoundedTeamBuyPlanner
{
    private sealed record CandidateMember(
        TeamPlanningMember Member,
        IReadOnlyList<PlayerBuyPlan> Candidates);

    private sealed record FrontierState(
        int[] CandidateIndexes,
        int TotalCost,
        int MinTier,
        int MaxTier,
        int TierSum,
        int AwpCount,
        int PreferredRifleCount,
        int LimitedSmgCount,
        int UtilityCoverage,
        int DefuserCount);

    public static BoundedTeamBuyResult Optimize(
        TeamSide side,
        BuyPhase phase,
        IReadOnlyList<TeamPlanningMember> members,
        int currentMinTier,
        EconomyRewardRules? rewards = null,
        int consecutiveLosses = 0,
        int opponentPlayerCount = 0,
        PurchaseIntent purchaseIntent = PurchaseIntent.Standard,
        BoundedPlannerOptions? options = null,
        TeamBuyMode buyMode = TeamBuyMode.Full)
    {
        var limits = options ?? new BoundedPlannerOptions();
        long started = Stopwatch.GetTimestamp();
        long deadline = started +
            (long)(Stopwatch.Frequency * (limits.BudgetMilliseconds / 1000d));
        bool timedOut = false;
        int maxFrontier = 1;

        var humanObservations = members
            .Where(member => !member.IsBot && member.Candidates.Count > 0)
            .ToDictionary(member => member.Slot, member => member.Candidates[0]);
        var bots = members
            .Where(member => member.IsBot && member.Candidates.Count > 0)
            .OrderBy(member => member.Slot)
            .Select(member => new CandidateMember(member, TrimCandidates(member, limits.CandidateLimit)))
            .Where(member => member.Candidates.Count > 0)
            .ToArray();
        bool preferredRifleExists = side == TeamSide.CounterTerrorist
            ? bots.Any(member => member.Candidates.Any(candidate => IsPreferredRifle(candidate.PrimaryWeapon, side)))
            : bots.Any(member => member.Candidates.Any(candidate => IsPreferredRifle(candidate.PrimaryWeapon, side)));

        if (bots.Length == 0)
        {
            return new BoundedTeamBuyResult(
                new TeamBuyPlan(
                    new Dictionary<int, PlayerBuyPlan>(),
                    humanObservations,
                    0,
                    0)
                {
                    Intent = purchaseIntent,
                    BuyMode = buyMode,
                    Reason = "no-bot-candidates",
                },
                new BoundedPlannerDiagnostics(0, 1, false, Stopwatch.GetTimestamp() - started));
        }

        int teamBudget = bots.Sum(member => Math.Max(0, member.Member.Money));
        int utilityCoverageTarget = Math.Max(3, bots.Length * 3);
        var frontier = new List<FrontierState>
        {
            new(Array.Empty<int>(), 0, int.MaxValue, int.MinValue, 0, 0, 0, 0, 0, 0),
        };

        for (int memberIndex = 0; memberIndex < bots.Length; memberIndex++)
        {
            var member = bots[memberIndex];
            var next = new List<FrontierState>(Math.Min(
                limits.FrontierLimit,
                frontier.Count * member.Candidates.Count));

            foreach (var state in frontier)
            {
                foreach (var candidateIndex in Enumerable.Range(0, member.Candidates.Count))
                {
                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        timedOut = true;
                        break;
                    }

                    var candidate = member.Candidates[candidateIndex];
                    if (!IsTeamWeaponAllowed(
                            candidate.PrimaryWeapon,
                            side,
                            buyMode,
                            preferredRifleExists,
                            state.LimitedSmgCount))
                        continue;
                    int cost = state.TotalCost + Math.Max(0, candidate.EstimatedCost);
                    if (cost > teamBudget)
                        continue;

                    int awpCount = state.AwpCount
                        + (candidate.PrimaryWeapon == "weapon_awp" ? 1 : 0);
                    if (awpCount > 1)
                        continue;

                    int minTier = Math.Min(state.MinTier, candidate.Tier);
                    int maxTier = Math.Max(state.MaxTier, candidate.Tier);
                    var indexes = new int[state.CandidateIndexes.Length + 1];
                    state.CandidateIndexes.CopyTo(indexes, 0);
                    indexes[^1] = candidateIndex;
                    next.Add(new FrontierState(
                        indexes,
                        cost,
                        minTier,
                        maxTier,
                        state.TierSum + candidate.Tier,
                        awpCount,
                        state.PreferredRifleCount
                            + (IsPreferredRifle(candidate.PrimaryWeapon, side) ? 1 : 0),
                        state.LimitedSmgCount + (IsLimitedSmg(candidate.PrimaryWeapon) ? 1 : 0),
                        state.UtilityCoverage + GetUtilityCoverage(candidate),
                        state.DefuserCount + (candidate.BuysDefuser ? 1 : 0)));
                }

                if (timedOut)
                    break;
            }

            frontier = Prune(next, limits.FrontierLimit, utilityCoverageTarget);
            maxFrontier = Math.Max(maxFrontier, frontier.Count);
            if (frontier.Count == 0 || timedOut)
                break;
        }

        bool usedFallback = timedOut || frontier.Count == 0;
        FrontierState? chosen = usedFallback
            ? BuildFallbackState(bots, teamBudget, side, buyMode, preferredRifleExists)
            : frontier
                .OrderByDescending(state => state.MinTier >= currentMinTier)
                .ThenByDescending(state => FrontierQualityScore(
                    state,
                    utilityCoverageTarget))
                .ThenByDescending(state => state.MinTier)
                .ThenByDescending(state => state.TierSum)
                // Keep the one-AWP structure when it does not lower the
                // team's combat floor; otherwise min/tier sum already wins.
                .ThenByDescending(state => state.AwpCount)
                .ThenByDescending(state => state.PreferredRifleCount)
                .ThenBy(state => ComputeHumanTierPenalty(
                    state,
                    bots,
                    humanObservations))
                .ThenBy(state => state.MaxTier - state.MinTier)
                .ThenBy(state => state.TotalCost)
                .FirstOrDefault();

        if (chosen is null)
        {
            return new BoundedTeamBuyResult(
                new TeamBuyPlan(
                    new Dictionary<int, PlayerBuyPlan>(),
                    humanObservations,
                    0,
                    0)
                {
                    Intent = purchaseIntent,
                    BuyMode = buyMode,
                    Reason = "no-affordable-team-plan",
                },
                new BoundedPlannerDiagnostics(
                    bots.Select(member => member.Candidates.Count).DefaultIfEmpty(0).Max(),
                    maxFrontier,
                    timedOut,
                    Stopwatch.GetTimestamp() - started));
        }

        var botPlans = new Dictionary<int, PlayerBuyPlan>(bots.Length);
        foreach (var (member, index) in bots.Select((member, index) => (member, index)))
        {
            int candidateIndex = chosen.CandidateIndexes.Length > index
                ? chosen.CandidateIndexes[index]
                : 0;
            botPlans[member.Member.Slot] = member.Candidates[candidateIndex];
        }

        var transferParticipants = bots
            .Select(member => new TransferParticipant(
                member.Member.Slot,
                IsBot: true,
                member.Member.Money,
                botPlans[member.Member.Slot],
                member.Member.CurrentPrimary))
            .ToArray();

        var transfers = TeamTransferPlanner.BuildTransfers(transferParticipants);
        var transferRecipients = transfers
            .Select(transfer => transfer.Recipient)
            .ToHashSet();
        foreach (var member in bots)
        {
            if (member.Member.CurrentPrimary is not null
                || !botPlans.TryGetValue(member.Member.Slot, out var selected)
                || selected.PrimaryWeapon is null && !selected.AcceptsTeamPrimary
                || transferRecipients.Contains(member.Member.Slot)
                || !selected.AcceptsTeamPrimary
                    && selected.EstimatedCost <= member.Member.Money)
                continue;

            var personalFallback = member.Member.Candidates
                .Where(candidate => !candidate.AcceptsTeamPrimary)
                .Where(candidate => candidate.EstimatedCost <= member.Member.Money)
                .OrderByDescending(candidate => candidate.Tier)
                .ThenBy(candidate => candidate.EstimatedCost)
                .FirstOrDefault();
            if (personalFallback is not null)
                botPlans[member.Member.Slot] = personalFallback;
        }

        // Rebuild transfers after removing any unbacked recipient plan. This
        // guarantees that an unaffordable bot never receives armor-only plus
        // an imaginary primary from a donor that was not selected.
        transferParticipants = bots
            .Select(member => new TransferParticipant(
                member.Member.Slot,
                IsBot: true,
                member.Member.Money,
                botPlans[member.Member.Slot],
                member.Member.CurrentPrimary))
            .ToArray();
        transfers = TeamTransferPlanner.BuildTransfers(transferParticipants);
        ApplyTransferredLoadouts(botPlans, transfers);
        var ordinaryPlans = botPlans.Values
            .Where(plan => plan.PrimaryWeapon != "weapon_awp")
            .ToArray();
        var balancePlans = ordinaryPlans.Length > 0 ? ordinaryPlans : botPlans.Values.ToArray();
        int min = balancePlans.Select(plan => plan.Tier).DefaultIfEmpty(0).Min();
        int max = balancePlans.Select(plan => plan.Tier).DefaultIfEmpty(0).Max();
        var purchaseCosts = BuildPurchaseCosts(bots, botPlans, transfers);
        var forecasts = rewards is null
            ? Array.Empty<NextRoundScenarioPrediction>()
            : NextRoundPredictor.Predict(
                bots.Select(member =>
                {
                    var selectedPlan = botPlans[member.Member.Slot];
                    return new ScenarioParticipant(
                        member.Member.Slot,
                        side,
                        Math.Max(0, member.Member.Money - purchaseCosts[member.Member.Slot]),
                        selectedPlan,
                        member.Member.Kills,
                        member.Member.IsPlanter,
                        member.Member.IsDefuser,
                        Math.Max(member.Member.SavedTier, selectedPlan.Tier));
                }).ToArray(),
                rewards,
                consecutiveLosses,
                members.Count,
                opponentPlayerCount);
        int humanTierPenalty = ComputeHumanTierPenalty(botPlans, humanObservations);
        var plan = new TeamBuyPlan(botPlans, humanObservations, min, max)
        {
            Transfers = transfers,
            Forecasts = forecasts,
            HumanTierPenalty = humanTierPenalty,
            Intent = purchaseIntent,
            BuyMode = buyMode,
            Reason = usedFallback ? "bounded-timeout-fallback" : "bounded-frontier",
        };

        return new BoundedTeamBuyResult(
            plan,
            new BoundedPlannerDiagnostics(
                bots.Select(member => member.Candidates.Count).DefaultIfEmpty(0).Max(),
                maxFrontier,
                timedOut,
                Stopwatch.GetTimestamp() - started));
    }

    private static IReadOnlyList<PlayerBuyPlan> TrimCandidates(
        TeamPlanningMember member,
        int limit)
    {
        var ordered = member.Candidates
            .Where(BuyPlanner.IsCombatLegal)
            .OrderByDescending(plan => PlanQualityScore(plan, member.IsAwper))
            .ThenByDescending(plan => plan.Tier)
            .ThenByDescending(GetUtilityCoverage)
            .ThenBy(plan => plan.EstimatedCost)
            .ThenBy(plan => plan.PrimaryWeapon, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        if (!member.IsAwper)
        {
            var recipient = member.Candidates
                .Where(candidate => candidate.AcceptsTeamPrimary)
                .Where(BuyPlanner.IsCombatLegal)
                .OrderByDescending(candidate => candidate.Tier)
                .ThenBy(candidate => candidate.EstimatedCost)
                .FirstOrDefault();
            if (recipient is null || ordered.Any(candidate => candidate.AcceptsTeamPrimary))
                return ordered;

            return ordered
                .Take(Math.Max(0, limit - 1))
                .Append(recipient)
                .ToArray();
        }

        var designatedAwp = member.Candidates
            .Where(plan => plan.PrimaryWeapon == "weapon_awp")
            .Where(BuyPlanner.IsCombatLegal)
            .OrderByDescending(plan => plan.Tier)
            .ThenBy(plan => plan.EstimatedCost)
            .FirstOrDefault();
        if (designatedAwp is null
            || ordered.Any(plan => plan.PrimaryWeapon == "weapon_awp"))
            return ordered;

        return ordered
            .Where(plan => plan.PrimaryWeapon != "weapon_awp")
            .Take(Math.Max(0, limit - 1))
            .Append(designatedAwp)
            .ToArray();
    }

    private static List<FrontierState> Prune(
        List<FrontierState> states,
        int limit,
        int utilityCoverageTarget)
    {
        var result = new List<FrontierState>(Math.Min(limit, states.Count));
        foreach (var state in states
                     // Quality is the admission order. Cost remains a
                     // tie-breaker, so a premium rifle/utility structure can
                     // enter the frontier before the cheap-but-shallow states
                     // consume the bounded slot budget.
                     .OrderByDescending(state => FrontierQualityScore(
                         state,
                         utilityCoverageTarget))
                     .ThenByDescending(state => state.MinTier)
                     .ThenByDescending(state => state.TierSum)
                     .ThenByDescending(state => state.PreferredRifleCount)
                     .ThenByDescending(state => state.AwpCount)
                     .ThenBy(state => state.MaxTier - state.MinTier))
        {
            bool dominated = result.Any(existing =>
                existing.TotalCost <= state.TotalCost
                && existing.MinTier >= state.MinTier
                && existing.TierSum >= state.TierSum
                && existing.PreferredRifleCount >= state.PreferredRifleCount
                && existing.UtilityCoverage >= state.UtilityCoverage
                && existing.DefuserCount >= state.DefuserCount
                && existing.MaxTier - existing.MinTier <= state.MaxTier - state.MinTier
                // AWP and rifle structures are intentionally separate Pareto
                // dimensions. A cheaper M4 must not erase the designated AWP
                // candidate before final team-structure scoring.
                && existing.AwpCount == state.AwpCount
                && existing.LimitedSmgCount <= state.LimitedSmgCount);
            if (dominated)
                continue;

            result.Add(state);
            if (result.Count >= limit)
                break;
        }

        return result;
    }

    private static int FrontierQualityScore(
        FrontierState state,
        int utilityCoverageTarget)
    {
        int utility = DiminishingCoverageScore(
            state.UtilityCoverage,
            utilityCoverageTarget,
            usefulWeight: 10,
            excessPenalty: 2);
        int defusers = DiminishingCoverageScore(
            state.DefuserCount,
            target: 1,
            usefulWeight: 24,
            excessPenalty: 8);
        int spreadPenalty = Math.Max(0, state.MaxTier - state.MinTier) * 14;
        int limitedSmgPenalty = state.LimitedSmgCount * 8;
        return state.MinTier * 1000
            + state.TierSum * 100
            + state.PreferredRifleCount * 28
            + state.AwpCount * 18
            + utility
            + defusers
            - spreadPenalty
            - limitedSmgPenalty;
    }

    private static int DiminishingCoverageScore(
        int coverage,
        int target,
        int usefulWeight,
        int excessPenalty)
        => Math.Min(coverage, target) * usefulWeight
            - Math.Max(0, coverage - target) * excessPenalty;

    private static Dictionary<int, int> BuildPurchaseCosts(
        IReadOnlyList<CandidateMember> members,
        IReadOnlyDictionary<int, PlayerBuyPlan> plans,
        IReadOnlyList<TransferPlan> transfers)
    {
        var costs = members.ToDictionary(
            member => member.Member.Slot,
            member => Math.Max(0, plans[member.Member.Slot].EstimatedCost));

        foreach (var transfer in transfers)
        {
            costs[transfer.Donor] += transfer.Cost;
        }

        return costs;
    }

    private static void ApplyTransferredLoadouts(
        IDictionary<int, PlayerBuyPlan> botPlans,
        IReadOnlyList<TransferPlan> transfers)
    {
        foreach (var transfer in transfers)
        {
            if (!botPlans.TryGetValue(transfer.Recipient, out var recipientPlan)
                || recipientPlan.ArmorLevel == ArmorLevel.None
                || recipientPlan.PrimaryWeapon is not null
                    && string.Equals(recipientPlan.PrimaryWeapon, transfer.Item, StringComparison.Ordinal))
                continue;

            int replacedCost = BuyPlanner.GetWeaponCost(recipientPlan.PrimaryWeapon);
            botPlans[transfer.Recipient] = recipientPlan with
            {
                PrimaryWeapon = transfer.Item,
                AcceptsTeamPrimary = false,
                RequestedPrimaryWeapon = null,
                EstimatedCost = Math.Max(0, recipientPlan.EstimatedCost - replacedCost),
                Tier = BuyPlanner.GetTier(
                    recipientPlan.ArmorLevel,
                    transfer.Item,
                    recipientPlan.SecondaryWeapon),
            };
        }
    }

    private static int ComputeHumanTierPenalty(
        IReadOnlyDictionary<int, PlayerBuyPlan> botPlans,
        IReadOnlyDictionary<int, PlayerBuyPlan> humanObservations)
        => ComputeHumanTierPenalty(botPlans.Values, humanObservations);

    private static int ComputeHumanTierPenalty(
        FrontierState state,
        IReadOnlyList<CandidateMember> members,
        IReadOnlyDictionary<int, PlayerBuyPlan> humanObservations)
        => ComputeHumanTierPenalty(
            members.Select((member, index) => member.Candidates[state.CandidateIndexes[index]]),
            humanObservations);

    private static int ComputeHumanTierPenalty(
        IEnumerable<PlayerBuyPlan> botPlans,
        IReadOnlyDictionary<int, PlayerBuyPlan> humanObservations)
    {
        if (humanObservations.Count == 0)
            return 0;

        return botPlans
            .Select(botPlan => humanObservations.Values
                .Select(humanPlan => Math.Max(0, Math.Abs(botPlan.Tier - humanPlan.Tier) - 1))
                .DefaultIfEmpty(0)
                .Min())
            .Sum();
    }

    private static FrontierState? BuildFallbackState(
        IReadOnlyList<CandidateMember> members,
        int teamBudget,
        TeamSide side,
        TeamBuyMode buyMode,
        bool preferredRifleExists)
    {
        var indexes = new int[members.Count];
        int totalCost = 0;
        int minTier = int.MaxValue;
        int maxTier = int.MinValue;
        int tierSum = 0;
        int awpCount = 0;
        int preferredRifleCount = 0;
        int limitedSmgCount = 0;
        int utilityCoverage = 0;
        int defuserCount = 0;

        for (int i = 0; i < members.Count; i++)
        {
            var allowedCandidates = members[i].Candidates
                .Select((candidate, candidateIndex) => (candidate, candidateIndex))
                .Where(entry => IsTeamWeaponAllowed(
                    entry.candidate.PrimaryWeapon,
                    side,
                    buyMode,
                    preferredRifleExists,
                    limitedSmgCount))
                .Where(entry => entry.candidate.PrimaryWeapon != "weapon_awp" || awpCount == 0)
                .Where(entry => totalCost + entry.candidate.EstimatedCost <= teamBudget)
                .ToArray();
            int? designatedAwpIndex = members[i].Member.IsAwper
                ? allowedCandidates
                    .Where(entry => entry.candidate.PrimaryWeapon == "weapon_awp")
                    .OrderByDescending(entry => entry.candidate.Tier)
                    .ThenBy(entry => entry.candidate.EstimatedCost)
                    .Select(entry => (int?)entry.candidateIndex)
                    .FirstOrDefault()
                : null;
            int? index = designatedAwpIndex ?? allowedCandidates
                .OrderByDescending(entry => entry.candidate.Tier)
                .ThenBy(entry => entry.candidate.EstimatedCost)
                .Select(entry => (int?)entry.candidateIndex)
                .FirstOrDefault();
            if (index is null)
                return null;
            indexes[i] = index.Value;
            var selected = members[i].Candidates[index.Value];
            totalCost += selected.EstimatedCost;
            minTier = Math.Min(minTier, selected.Tier);
            maxTier = Math.Max(maxTier, selected.Tier);
            tierSum += selected.Tier;
            awpCount += selected.PrimaryWeapon == "weapon_awp" ? 1 : 0;
            preferredRifleCount += IsPreferredRifle(selected.PrimaryWeapon, side) ? 1 : 0;
            limitedSmgCount += IsLimitedSmg(selected.PrimaryWeapon) ? 1 : 0;
            utilityCoverage += GetUtilityCoverage(selected);
            defuserCount += selected.BuysDefuser ? 1 : 0;
        }

        return new FrontierState(
            indexes,
            totalCost,
            minTier,
            maxTier,
            tierSum,
            awpCount,
            preferredRifleCount,
            limitedSmgCount,
            utilityCoverage,
            defuserCount);
    }

    private static bool IsTeamWeaponAllowed(
        string? weapon,
        TeamSide side,
        TeamBuyMode buyMode,
        bool preferredRifleExists,
        int limitedSmgCount)
    {
        if (weapon is "weapon_awp")
            return true;
        // FAMAS/Galil remain valid only for members whose candidate set has no
        // affordable M4/AK. Candidate generation already orders the preferred
        // rifle first; a global team-level ban would strand poor members when
        // one rich teammate happens to have an AK/M4 candidate.
        if (IsLimitedSmg(weapon)
            && buyMode is TeamBuyMode.Full or TeamBuyMode.MustWin
            && limitedSmgCount >= 1)
            return false;
        return true;
    }

    private static bool IsPreferredRifle(string? weapon, TeamSide side)
        => side == TeamSide.CounterTerrorist
            ? weapon is "weapon_m4a1" or "weapon_m4a1_silencer"
            : weapon == "weapon_ak47";

    private static bool IsLimitedSmg(string? weapon)
        => weapon is "weapon_mp9" or "weapon_mac10";

    private static int GetUtilityCoverage(PlayerBuyPlan plan)
        => plan.Utility
            .Select(utility => utility switch
            {
                "smoke" or "weapon_smokegrenade" => 2,
                "flash" or "weapon_flashbang" => 1,
                "he" or "weapon_hegrenade" => 1,
                "molotov" or "weapon_molotov" or "weapon_incgrenade" => 1,
                _ => 0,
            })
            .Sum();

    private static int PlanQualityScore(PlayerBuyPlan plan, bool designatedAwper)
    {
        int weaponScore = plan.PrimaryWeapon switch
        {
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" => 80,
            "weapon_awp" => designatedAwper ? 86 : 72,
            "weapon_aug" or "weapon_sg556" => 70,
            "weapon_galilar" or "weapon_famas" => 48,
            "weapon_mp9" or "weapon_mac10" => 30,
            null => 0,
            _ => 24,
        };
        int armorScore = plan.ArmorLevel switch
        {
            ArmorLevel.Full => 24,
            ArmorLevel.Half => 12,
            _ => 0,
        };
        int structureScore = (plan.BuysHelmet ? 4 : 0)
            + (plan.BuysDefuser ? 4 : 0)
            + GetUtilityCoverage(plan) * 3
            + (plan.AcceptsTeamPrimary ? -30 : 0);
        return weaponScore + armorScore + structureScore;
    }
}
