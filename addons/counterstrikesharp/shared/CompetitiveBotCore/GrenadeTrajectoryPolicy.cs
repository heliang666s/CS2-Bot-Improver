namespace CompetitiveBotCore;

public readonly record struct GrenadeThrowTrajectory(
    float OriginX,
    float OriginY,
    float OriginZ,
    float VelocityX,
    float VelocityY,
    float VelocityZ);

public static class GrenadeTrajectoryPolicy
{
    public static bool TryCreate(
        float originX,
        float originY,
        float originZ,
        float velocityX,
        float velocityY,
        float velocityZ,
        out GrenadeThrowTrajectory trajectory)
    {
        if (!float.IsFinite(originX)
            || !float.IsFinite(originY)
            || !float.IsFinite(originZ)
            || !float.IsFinite(velocityX)
            || !float.IsFinite(velocityY)
            || !float.IsFinite(velocityZ))
        {
            trajectory = default;
            return false;
        }

        trajectory = new GrenadeThrowTrajectory(
            originX,
            originY,
            originZ,
            velocityX,
            velocityY,
            velocityZ);
        return true;
    }

    public static bool ShouldRetargetEngineProjectile(
        BotMatchProfile profile,
        bool engineConfirmedThrow,
        GrenadeThrowTrajectory? trajectory)
        => profile == BotMatchProfile.Competitive
            && engineConfirmedThrow
            && trajectory.HasValue;
}

public static class InventoryCalibrationPolicy
{
    public const float FallbackAuditIntervalSeconds = 0.25f;

    public static bool ShouldScan(
        bool finalWindowOpen,
        bool inventoryDirty,
        float now,
        float nextAuditAt)
        => finalWindowOpen
            && now >= nextAuditAt;

    public static float NextAuditAt(float now)
        => Math.Max(0f, now) + FallbackAuditIntervalSeconds;
}
