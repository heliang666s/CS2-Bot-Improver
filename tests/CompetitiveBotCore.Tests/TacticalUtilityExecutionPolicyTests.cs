using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalUtilityExecutionPolicyTests
{
    [Fact]
    public void ExecuteRequestWaitsForExecuteStageAndNearbyReleasePosition()
    {
        Assert.False(TacticalUtilityExecutionPolicy.CanClaim(
            TacticalPlanStage.Probe,
            TacticalPlanStage.Execute,
            releaseDistance: 100f));

        Assert.True(TacticalUtilityExecutionPolicy.CanClaim(
            TacticalPlanStage.Execute,
            TacticalPlanStage.Execute,
            releaseDistance: 100f));

        Assert.True(TacticalUtilityExecutionPolicy.CanClaim(
            TacticalPlanStage.Execute,
            TacticalPlanStage.Contact,
            releaseDistance: 100f));

        Assert.False(TacticalUtilityExecutionPolicy.CanClaim(
            TacticalPlanStage.Execute,
            TacticalPlanStage.Execute,
            releaseDistance: TacticalUtilityExecutionPolicy.MaxReleaseDistance + 1f));
    }
}
