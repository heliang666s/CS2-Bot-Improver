namespace CompetitiveBotCore;

public static class TacticalUtilityTimeoutPolicy
{
    // Covers the real-inventory confirmation retries while still releasing a
    // request quickly enough for the current tactical stage to downgrade.
    public const float ConfirmationBudgetSeconds = 2f;

    public static bool ShouldExpire(float now, float issuedAt)
        => now - issuedAt >= ConfirmationBudgetSeconds;
}
