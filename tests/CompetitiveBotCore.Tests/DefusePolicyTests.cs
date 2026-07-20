using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class DefusePolicyTests
{
    [Theory]
    [InlineData(BotMatchProfile.Competitive, true, true, 0.05, true)]
    [InlineData(BotMatchProfile.Competitive, true, true, 0.10, false)]
    [InlineData(BotMatchProfile.Competitive, true, false, 0.65, true)]
    [InlineData(BotMatchProfile.Competitive, true, false, 0.66, false)]
    [InlineData(BotMatchProfile.Competitive, false, true, 0.0, false)]
    [InlineData(BotMatchProfile.Arcade, true, true, 0.05, true)]
    [InlineData(BotMatchProfile.Arcade, true, false, 0.65, true)]
    public void ProfilesUseLimitedFakeDefuseDuringThreat(
        BotMatchProfile profile,
        bool hasLiveEnemy,
        bool hasDefuser,
        double randomRoll,
        bool expected)
    {
        Assert.Equal(expected,
            DefuseDecisionPolicy.ShouldFakeDefuse(
                profile, hasLiveEnemy, hasDefuser, randomRoll));
    }

    [Theory]
    [InlineData(BotMatchProfile.Competitive, true, false, 1.0, true)]
    [InlineData(BotMatchProfile.Competitive, false, true, 0.0, false)]
    [InlineData(BotMatchProfile.Arcade, false, true, 1.0, true)]
    [InlineData(BotMatchProfile.Arcade, false, false, 0.32, true)]
    [InlineData(BotMatchProfile.Arcade, false, false, 0.33, false)]
    public void CompetitiveProfileUsesOwnedSmokeDuringDefuse(
        BotMatchProfile profile,
        bool hasSmoke,
        bool hasDefuser,
        double randomRoll,
        bool expected)
    {
        Assert.Equal(expected,
            DefuseDecisionPolicy.ShouldDeployDefuseSmoke(
                profile, hasSmoke, hasDefuser, randomRoll));
    }
}
