namespace CompetitiveBotCore;

public static class FlashScanPolicy
{
    public const float LiveProjectileIntervalSeconds = 0.05f;
    public const float IdleIntervalSeconds = 0.50f;

    public static float NextInterval(bool hasLiveProjectile)
        => hasLiveProjectile
            ? LiveProjectileIntervalSeconds
            : IdleIntervalSeconds;
}
