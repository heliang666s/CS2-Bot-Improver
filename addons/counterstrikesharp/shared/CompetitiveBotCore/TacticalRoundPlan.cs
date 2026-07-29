namespace CompetitiveBotCore;

public enum TTacticalIntent
{
    DefaultSplit,
    FastExecute,
    ContactExplode,
    FakeThenHit,
    LateRotate,
    LowUtilityDefault,
}

public enum TTacticalRole
{
    Entry,
    Support,
    Lurk,
    AWPer,
    CarrierSupport,
    LateRotator,
}

public enum TacticalUtilityType
{
    Smoke,
    Flash,
    He,
    Fire,
}

public enum TacticalPlanStage
{
    Stage,
    Probe,
    Execute,
    Split,
    Contact,
    Plant,
    PostPlant,
    Rotate,
    Search,
}

public sealed record TacticalRoundPlanContext(
    int RoundKey,
    string MapName,
    bool HasTwoBombSites,
    BuyPhase BuyPhase,
    int AliveT,
    int AliveCt,
    IReadOnlyList<int> BotSlots,
    int? HumanCarrierSlot,
    int? CurrentAwperSlot,
    IReadOnlyList<TTacticalIntent> PreviousIntents,
    IReadOnlySet<CtGambleSite>? AvailableSites = null);

public sealed record TacticalRoleAssignment(
    int Slot,
    TTacticalRole Role,
    int Group,
    CtGambleSite Site,
    bool IsCarrierSupport,
    bool IsHumanDependent);

public sealed record TacticalUtilityRequirement(
    TacticalUtilityType UtilityType,
    int RequiredCount,
    int OptionalCount,
    TacticalPlanStage Stage,
    CtGambleSite TargetSite,
    int? Slot = null);

public sealed record TacticalLineupRequest(
    string RequestId,
    int Slot,
    TacticalUtilityType UtilityType,
    TacticalPlanStage Stage,
    CtGambleSite TargetSite,
    bool Required);

public sealed record TacticalRoundPlan(
    int RoundKey,
    string PlanId,
    TTacticalIntent Intent,
    CtGambleSite PrimarySite,
    CtGambleSite FallbackSite,
    TacticalPlanStage Stage,
    IReadOnlyList<TacticalRoleAssignment> RoleAssignments,
    IReadOnlyList<TacticalUtilityRequirement> RequiredUtility,
    IReadOnlyList<TacticalLineupRequest> RequiredLineupRequests,
    int PlanSeed,
    bool IsFallback,
    bool UsesSharedTarget)
{
    public int RepetitionPenalty { get; init; }
}

// Kept as an init property so existing consumers can construct a plan without
// knowing how history is scored. The planner fills it for every candidate.
public static class TacticalRoundPlanHistory
{
    public static int Penalty(
        IReadOnlyList<TTacticalIntent> previousIntents,
        TTacticalIntent intent)
        => previousIntents.Count(previous => previous == intent) * 15;
}

public static class TacticalRoundPlanner
{
    private static readonly TTacticalIntent[] Intents =
    [
        TTacticalIntent.DefaultSplit,
        TTacticalIntent.FastExecute,
        TTacticalIntent.ContactExplode,
        TTacticalIntent.FakeThenHit,
        TTacticalIntent.LateRotate,
        TTacticalIntent.LowUtilityDefault,
    ];

    public static IReadOnlyList<TacticalRoundPlan> BuildCandidates(
        TacticalRoundPlanContext context)
    {
        var availableSites = ResolveAvailableSites(context);
        var slots = context.BotSlots
            .Distinct()
            .OrderBy(slot => slot)
            .ToArray();
        if (slots.Length == 0)
            return Array.Empty<TacticalRoundPlan>();

        return Intents
            .Where(intent => availableSites.Count >= 2
                || intent is TTacticalIntent.ContactExplode
                    or TTacticalIntent.LowUtilityDefault)
            .Select((intent, index) => BuildPlan(
                context,
                slots,
                intent,
                index,
                availableSites))
            .ToArray();
    }

    public static TacticalRoundPlan SelectPlan(
        IReadOnlyList<TacticalRoundPlan> candidates,
        int seed)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one tactical plan is required.", nameof(candidates));

        var bestScore = candidates.Max(plan => Score(plan));
        var nearOptimal = candidates
            .Where(plan => bestScore - Score(plan) <= 8)
            .OrderBy(plan => plan.PlanId, StringComparer.Ordinal)
            .ToArray();
        int index = (int)((uint)seed % (uint)nearOptimal.Length);
        return nearOptimal[index] with { PlanSeed = seed };
    }

    public static TacticalRoundPlan MarkFallback(
        TacticalRoundPlan plan,
        string reason)
        => plan with
        {
            PlanId = $"{plan.PlanId}:fallback:{reason}",
            IsFallback = true,
        };

    private static TacticalRoundPlan BuildPlan(
        TacticalRoundPlanContext context,
        IReadOnlyList<int> slots,
        TTacticalIntent intent,
        int ordinal,
        IReadOnlyList<CtGambleSite> availableSites)
    {
        CtGambleSite primary = availableSites[ordinal % availableSites.Count];
        CtGambleSite fallback = availableSites.Count == 1
            ? primary
            : availableSites[(ordinal + 1) % availableSites.Count];
        var assignments = BuildAssignments(context, slots, intent, primary, fallback);
        var utility = BuildUtility(intent, primary);
        var requests = utility
            .Where(requirement => requirement.RequiredCount > 0
                && requirement.Slot is null)
            .SelectMany(requirement => Enumerable.Range(0, requirement.RequiredCount)
                .Select(index =>
                {
                    var assignment = assignments[index % assignments.Count];
                    return new TacticalLineupRequest(
                        $"r{context.RoundKey}-{intent}-{requirement.UtilityType}-{index}-{assignment.Slot}",
                        assignment.Slot,
                        requirement.UtilityType,
                        requirement.Stage,
                        assignment.Site,
                        Required: true);
                }))
            .ToArray();

        return new TacticalRoundPlan(
            context.RoundKey,
            $"r{context.RoundKey}:{intent}:{primary}",
            intent,
            primary,
            fallback,
            TacticalPlanStage.Stage,
            assignments,
            utility,
            requests,
            PlanSeed: context.RoundKey * 31 + ordinal,
            IsFallback: false,
            UsesSharedTarget: false)
        {
            RepetitionPenalty = TacticalRoundPlanHistory.Penalty(
                context.PreviousIntents,
                intent),
        };
    }

    private static IReadOnlyList<CtGambleSite> ResolveAvailableSites(
        TacticalRoundPlanContext context)
    {
        var configured = context.AvailableSites?
            .Where(site => site is CtGambleSite.A or CtGambleSite.B)
            .Distinct()
            .OrderBy(site => site)
            .ToArray();
        if (configured is { Length: > 0 })
            return configured;

        return context.HasTwoBombSites
            ? [CtGambleSite.A, CtGambleSite.B]
            : [CtGambleSite.A];
    }

    private static IReadOnlyList<TacticalRoleAssignment> BuildAssignments(
        TacticalRoundPlanContext context,
        IReadOnlyList<int> slots,
        TTacticalIntent intent,
        CtGambleSite primary,
        CtGambleSite fallback)
    {
        int splitPoint = Math.Max(1, (slots.Count + 1) / 2);
        var assignments = new List<TacticalRoleAssignment>(slots.Count);
        for (int index = 0; index < slots.Count; index++)
        {
            int slot = slots[index];
            bool isAwper = context.CurrentAwperSlot == slot;
            bool supportsHuman = context.HumanCarrierSlot.HasValue
                && index == 0;
            TTacticalRole role = supportsHuman
                ? TTacticalRole.CarrierSupport
                : isAwper
                    ? TTacticalRole.AWPer
                    : index == 0
                        ? TTacticalRole.Entry
                        : index == slots.Count - 1
                            ? TTacticalRole.Lurk
                            : intent == TTacticalIntent.LateRotate
                                ? TTacticalRole.LateRotator
                                : TTacticalRole.Support;

            CtGambleSite site = intent switch
            {
                TTacticalIntent.FakeThenHit when index == 0 => fallback,
                TTacticalIntent.LateRotate when index == slots.Count - 1 => fallback,
                TTacticalIntent.FastExecute or TTacticalIntent.ContactExplode
                    when index == slots.Count - 1 => fallback,
                TTacticalIntent.DefaultSplit or TTacticalIntent.LowUtilityDefault
                    when index >= splitPoint => fallback,
                _ => primary,
            };
            int group = intent switch
            {
                TTacticalIntent.FastExecute or TTacticalIntent.ContactExplode
                    when index == slots.Count - 1 => 1,
                TTacticalIntent.FastExecute or TTacticalIntent.ContactExplode => 0,
                _ when index >= splitPoint => 1,
                _ => 0,
            };
            assignments.Add(new TacticalRoleAssignment(
                slot,
                role,
                group,
                site,
                supportsHuman,
                supportsHuman));
        }

        return assignments;
    }

    private static IReadOnlyList<TacticalUtilityRequirement> BuildUtility(
        TTacticalIntent intent,
        CtGambleSite site)
    {
        (int Smoke, int Flash, int He, int Fire) demand = intent switch
        {
            TTacticalIntent.FastExecute => (3, 4, 1, 1),
            TTacticalIntent.ContactExplode => (1, 3, 1, 1),
            TTacticalIntent.FakeThenHit => (2, 2, 0, 1),
            TTacticalIntent.LateRotate => (2, 2, 1, 1),
            TTacticalIntent.LowUtilityDefault => (1, 1, 0, 0),
            _ => (2, 2, 1, 1),
        };

        return
        [
            new(TacticalUtilityType.Smoke, demand.Smoke, 0,
                TacticalPlanStage.Execute, site),
            new(TacticalUtilityType.Flash, demand.Flash, 1,
                TacticalPlanStage.Execute, site),
            new(TacticalUtilityType.He, demand.He, 1,
                TacticalPlanStage.Contact, site),
            new(TacticalUtilityType.Fire, demand.Fire, 1,
                TacticalPlanStage.Execute, site),
        ];
    }

    private static int Score(TacticalRoundPlan plan)
        => plan.Intent switch
        {
            TTacticalIntent.DefaultSplit => 100,
            TTacticalIntent.ContactExplode => 98,
            TTacticalIntent.FastExecute => 96,
            TTacticalIntent.FakeThenHit => 94,
            TTacticalIntent.LateRotate => 92,
            TTacticalIntent.LowUtilityDefault => 70,
            _ => 0,
        } - plan.RepetitionPenalty;
}
