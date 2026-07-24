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
    public const float MaximumDispatchDelaySeconds = 0.30f;
    public const float ConfirmationRetryIntervalSeconds = 0.05f;
    public const float ExecutionSafetyMarginSeconds = 0.15f;
    public const int MaximumDeferredConfirmationAttempts = 10;
    public static readonly float ExecutionWindowSeconds =
        RequiredExecutionWindowSeconds(
            MaximumDeferredConfirmationAttempts,
            MaximumDispatchDelaySeconds,
            ExecutionSafetyMarginSeconds);

    public static float RequiredExecutionWindowSeconds(
        int maxConfirmationAttempts,
        float maximumDispatchDelaySeconds,
        float safetyMarginSeconds)
        => Math.Max(0f, maximumDispatchDelaySeconds)
            + Math.Max(0, maxConfirmationAttempts - 1)
                * ConfirmationRetryIntervalSeconds
            + Math.Max(0f, safetyMarginSeconds);

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

    public static bool ShouldAbortPendingTransfer(
        bool executionOpen,
        bool retryable)
        => retryable && !executionOpen;
}
