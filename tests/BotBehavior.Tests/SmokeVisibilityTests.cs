using BotBehaviorPolicy;
using Xunit;

namespace BotBehavior.Tests;

public sealed class SmokeVisibilityTests
{
    [Fact]
    public void SegmentThroughSmokeIsBlocked()
    {
        var smokes = new[] { new SmokeVolume(500f, 0f, 64f, 120f) };

        Assert.True(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 0f, 64f,
            1000f, 0f, 64f,
            smokes));
    }

    [Fact]
    public void SegmentAroundSmokeRemainsVisible()
    {
        var smokes = new[] { new SmokeVolume(500f, 0f, 64f, 120f) };

        Assert.False(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 300f, 64f,
            1000f, 300f, 64f,
            smokes));
    }
}
