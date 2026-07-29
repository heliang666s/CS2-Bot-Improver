using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalReplanPolicyTests
{
    [Fact]
    public void DroppedBombHoldsStageAndNeverRotates()
    {
        var decision = TacticalReplanPolicy.Resolve(new TacticalReplanContext(
            HasCarrier: false,
            CarrierIsHuman: false,
            ContactConfirmed: false,
            RouteFailed: false,
            FailedGroup: -1,
            RequiredUtilityFailed: false,
            HumanDeviation: false,
            AliveT: 4,
            AliveCt: 5,
            CurrentIntent: TTacticalIntent.FastExecute));

        Assert.Equal(TacticalReplanAction.HoldStageSearch, decision.Action);
        Assert.Equal(TacticalPlanStage.Stage, decision.Stage);
        Assert.False(decision.ShouldRotateWholeTeam);
    }

    [Fact]
    public void RouteFailureOnlyRotatesTheAffectedGroup()
    {
        var decision = TacticalReplanPolicy.Resolve(new TacticalReplanContext(
            HasCarrier: true,
            CarrierIsHuman: false,
            ContactConfirmed: false,
            RouteFailed: true,
            FailedGroup: 1,
            RequiredUtilityFailed: false,
            HumanDeviation: false,
            AliveT: 5,
            AliveCt: 5,
            CurrentIntent: TTacticalIntent.DefaultSplit));

        Assert.Equal(TacticalReplanAction.RotateAffectedGroup, decision.Action);
        Assert.Equal(1, decision.AffectedGroup);
        Assert.False(decision.ShouldRotateWholeTeam);
    }

    [Fact]
    public void RequiredUtilityFailureFallsBackToLowerDemandIntent()
    {
        var decision = TacticalReplanPolicy.Resolve(new TacticalReplanContext(
            HasCarrier: true,
            CarrierIsHuman: false,
            ContactConfirmed: false,
            RouteFailed: false,
            FailedGroup: -1,
            RequiredUtilityFailed: true,
            HumanDeviation: false,
            AliveT: 5,
            AliveCt: 5,
            CurrentIntent: TTacticalIntent.FastExecute));

        Assert.Equal(TacticalReplanAction.FallbackTactic, decision.Action);
        Assert.Equal(TTacticalIntent.ContactExplode, decision.FallbackIntent);
    }

    [Fact]
    public void HumanDeviationDegradesWithoutWaitingForHuman()
    {
        var decision = TacticalReplanPolicy.Resolve(new TacticalReplanContext(
            HasCarrier: true,
            CarrierIsHuman: true,
            ContactConfirmed: false,
            RouteFailed: false,
            FailedGroup: -1,
            RequiredUtilityFailed: false,
            HumanDeviation: true,
            AliveT: 5,
            AliveCt: 5,
            CurrentIntent: TTacticalIntent.DefaultSplit));

        Assert.Equal(TacticalReplanAction.DegradeForHumanDeviation, decision.Action);
        Assert.Equal(TacticalPlanStage.Split, decision.Stage);
        Assert.False(decision.ShouldWaitForHuman);
    }

    [Fact]
    public void ConfirmedContactAdvancesToExecute()
    {
        var decision = TacticalReplanPolicy.Resolve(new TacticalReplanContext(
            HasCarrier: true,
            CarrierIsHuman: false,
            ContactConfirmed: true,
            RouteFailed: false,
            FailedGroup: -1,
            RequiredUtilityFailed: false,
            HumanDeviation: false,
            AliveT: 5,
            AliveCt: 3,
            CurrentIntent: TTacticalIntent.ContactExplode));

        Assert.Equal(TacticalReplanAction.Execute, decision.Action);
        Assert.Equal(TacticalPlanStage.Execute, decision.Stage);
    }
}
