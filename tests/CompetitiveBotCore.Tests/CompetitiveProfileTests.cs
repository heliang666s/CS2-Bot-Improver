using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class CompetitiveProfileTests
{
    [Fact]
    public void CompetitiveProfile_DowngradesUnlimitedNadesToNormal()
    {
        Assert.Equal("normal", ProfilePolicy.NormalizeNadeMode(BotMatchProfile.Competitive, "max"));
        Assert.Equal("more", ProfilePolicy.NormalizeNadeMode(BotMatchProfile.Arcade, "more"));
    }

    [Fact]
    public void FFAOrScoutsSignal_UsesArcadeOnlyWhenProfileWasDefaultCompetitive()
    {
        Assert.Equal(BotMatchProfile.Arcade,
            ProfilePolicy.Resolve(BotMatchProfile.Competitive, entertainmentMode: true));
        Assert.Equal(BotMatchProfile.Legacy,
            ProfilePolicy.Resolve(BotMatchProfile.Legacy, entertainmentMode: true));
    }
}
