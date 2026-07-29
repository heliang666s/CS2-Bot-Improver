namespace CompetitiveBotCore;

public static class TacticalUtilityExecutionPolicy
{
    public const float MaxReleaseDistance = 1800f;

    public static bool CanClaim(
        TacticalPlanStage currentStage,
        TacticalPlanStage requestStage,
        float releaseDistance)
        => (currentStage == requestStage
                || requestStage == TacticalPlanStage.Contact
                    && currentStage == TacticalPlanStage.Execute)
            && float.IsFinite(releaseDistance)
            && releaseDistance >= 0f
            && releaseDistance <= MaxReleaseDistance;
}
