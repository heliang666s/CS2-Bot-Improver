namespace CompetitiveBotCore;

public enum FreezeBuyStage
{
    TemporaryPlan,
    FinalCalibration,
    Execution,
    PostFreezeCheck,
}

public static class FreezeBuyPolicy
{
    public const float MinimumFreezeSeconds = 1f;
    public const float FinalCalibrationWindowSeconds = 1.5f;
    public const float ExecutionWindowSeconds = 0.8f;
    public const float MaximumDispatchDelaySeconds = 0.30f;

    public static float ExecutionAt(
        float roundStartAt,
        float freezeDurationSeconds)
        => roundStartAt
            + Math.Max(
                0f,
                Math.Max(MinimumFreezeSeconds, freezeDurationSeconds)
                    - ExecutionWindowSeconds);

    public static float EndAt(float roundStartAt, float freezeDurationSeconds)
        => roundStartAt + Math.Max(MinimumFreezeSeconds, freezeDurationSeconds);

    public static FreezeBuyStage Resolve(
        float elapsedSeconds,
        float freezeDurationSeconds)
    {
        float elapsed = Math.Max(0f, elapsedSeconds);
        float freeze = Math.Max(MinimumFreezeSeconds, freezeDurationSeconds);
        if (elapsed >= freeze)
            return FreezeBuyStage.PostFreezeCheck;

        if (elapsed >= Math.Max(0f, freeze - ExecutionWindowSeconds))
            return FreezeBuyStage.Execution;

        if (elapsed >= Math.Max(0f, freeze - FinalCalibrationWindowSeconds))
            return FreezeBuyStage.FinalCalibration;

        return FreezeBuyStage.TemporaryPlan;
    }

    public static bool ShouldAcceptPlan(
        FreezeBuyStage stage,
        bool finalCalibrationCompleted)
        => stage switch
        {
            FreezeBuyStage.TemporaryPlan => !finalCalibrationCompleted,
            FreezeBuyStage.FinalCalibration => true,
            FreezeBuyStage.Execution => false,
            FreezeBuyStage.PostFreezeCheck => false,
            _ => false,
        };

    public static bool HasExecutionBudget(
        float now,
        float freezeEndAt,
        float requiredSeconds)
        => freezeEndAt <= 0f
            || now + Math.Max(0f, requiredSeconds) < freezeEndAt;
}
