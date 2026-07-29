namespace CompetitiveBotCore;

/// <summary>
/// Runtime signals that are safe to derive from the live round state.  The
/// policy deliberately stays independent of CounterStrikeSharp entities so it
/// can be tested and used by other tactical runtimes.
/// </summary>
public sealed record TacticalReplanContext(
    bool HasCarrier,
    bool CarrierIsHuman,
    bool ContactConfirmed,
    bool RouteFailed,
    int FailedGroup,
    bool RequiredUtilityFailed,
    bool HumanDeviation,
    int AliveT,
    int AliveCt,
    TTacticalIntent CurrentIntent);

public enum TacticalReplanAction
{
    NoChange,
    HoldStageSearch,
    RotateAffectedGroup,
    FallbackTactic,
    DegradeForHumanDeviation,
    Execute,
}

public sealed record TacticalReplanDecision(
    TacticalReplanAction Action,
    TacticalPlanStage Stage,
    int AffectedGroup,
    TTacticalIntent? FallbackIntent,
    bool ShouldRotateWholeTeam,
    bool ShouldWaitForHuman,
    string Reason);

/// <summary>
/// Resolves live exceptions in priority order.  Recoverable local failures
/// must not reset the entire team, while a missing carrier must never be
/// interpreted as a blocked bomb site.
/// </summary>
public static class TacticalReplanPolicy
{
    public static TacticalReplanDecision Resolve(TacticalReplanContext context)
    {
        if (!context.HasCarrier)
        {
            return new TacticalReplanDecision(
                TacticalReplanAction.HoldStageSearch,
                TacticalPlanStage.Stage,
                AffectedGroup: -1,
                FallbackIntent: null,
                ShouldRotateWholeTeam: false,
                ShouldWaitForHuman: true,
                Reason: "no-carrier-search-or-wait");
        }

        if (context.RequiredUtilityFailed)
        {
            TTacticalIntent fallback = context.CurrentIntent switch
            {
                TTacticalIntent.FastExecute => TTacticalIntent.ContactExplode,
                TTacticalIntent.ContactExplode => TTacticalIntent.LowUtilityDefault,
                TTacticalIntent.FakeThenHit => TTacticalIntent.LowUtilityDefault,
                TTacticalIntent.LateRotate => TTacticalIntent.LowUtilityDefault,
                _ => TTacticalIntent.LowUtilityDefault,
            };
            return new TacticalReplanDecision(
                TacticalReplanAction.FallbackTactic,
                TacticalPlanStage.Stage,
                AffectedGroup: -1,
                FallbackIntent: fallback,
                ShouldRotateWholeTeam: false,
                ShouldWaitForHuman: false,
                Reason: $"required-utility-failed:{context.CurrentIntent}");
        }

        if (context.RouteFailed && context.FailedGroup >= 0)
        {
            return new TacticalReplanDecision(
                TacticalReplanAction.RotateAffectedGroup,
                TacticalPlanStage.Rotate,
                context.FailedGroup,
                FallbackIntent: null,
                ShouldRotateWholeTeam: false,
                ShouldWaitForHuman: false,
                Reason: $"route-failed-group:{context.FailedGroup}");
        }

        if (context.HumanDeviation)
        {
            return new TacticalReplanDecision(
                TacticalReplanAction.DegradeForHumanDeviation,
                TacticalPlanStage.Split,
                AffectedGroup: -1,
                FallbackIntent: TTacticalIntent.LowUtilityDefault,
                ShouldRotateWholeTeam: false,
                ShouldWaitForHuman: false,
                Reason: "human-deviated-from-plan");
        }

        if (context.ContactConfirmed)
        {
            return new TacticalReplanDecision(
                TacticalReplanAction.Execute,
                TacticalPlanStage.Execute,
                AffectedGroup: -1,
                FallbackIntent: null,
                ShouldRotateWholeTeam: false,
                ShouldWaitForHuman: false,
                Reason: "contact-confirmed");
        }

        return new TacticalReplanDecision(
            TacticalReplanAction.NoChange,
            TacticalPlanStage.Probe,
            AffectedGroup: -1,
            FallbackIntent: null,
            ShouldRotateWholeTeam: false,
            ShouldWaitForHuman: false,
            Reason: "plan-stable");
    }
}
