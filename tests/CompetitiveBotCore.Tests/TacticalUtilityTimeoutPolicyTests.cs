using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalUtilityTimeoutPolicyTests
{
    [Fact]
    public void RequestIsNotExpiredBeforeConfirmationBudget()
    {
        Assert.False(TacticalUtilityTimeoutPolicy.ShouldExpire(1.99f, 0f));
    }

    [Fact]
    public void RequestExpiresAtConfirmationBudget()
    {
        Assert.True(TacticalUtilityTimeoutPolicy.ShouldExpire(2f, 0f));
        Assert.True(TacticalUtilityTimeoutPolicy.ShouldExpire(3f, 0.5f));
    }
}
