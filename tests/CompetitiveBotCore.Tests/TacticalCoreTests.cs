using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class TacticalCoreTests
{
    [Fact]
    public void CTWithoutReliableInformation_HoldsAnchor()
    {
        var context = new RoundContext(3, 1, 1, 2, false, 2, false, null, 5, 5, RoundPhase.Live);

        var directive = new CompetitiveTacticalDirector().Decide(
            TeamSide.CounterTerrorist,
            context,
            hasReliableEnemyInfo: false,
            isInformationRole: false);

        Assert.Equal(TacticalDirective.HoldAnchor, directive);
    }

    [Fact]
    public void CTInformationRoleProbesButOtherBotsRotate()
    {
        var context = new RoundContext(3, 1, 1, 2, false, 2, false, null, 5, 4, RoundPhase.Live);
        var director = new CompetitiveTacticalDirector();

        Assert.Equal(
            TacticalDirective.ProbeAndFallBack,
            director.Decide(TeamSide.CounterTerrorist, context, true, true));
        Assert.Equal(
            TacticalDirective.Rotate,
            director.Decide(TeamSide.CounterTerrorist, context, true, false));
    }

    [Fact]
    public void BombPlantForcesRetakeOrPostPlant()
    {
        var context = new RoundContext(12, 1, 6, 6, true, 0, true, "A", 3, 2, RoundPhase.BombPlanted);
        var director = new CompetitiveTacticalDirector();

        Assert.Equal(TacticalDirective.Retake,
            director.Decide(TeamSide.CounterTerrorist, context, false, false));
        Assert.Equal(TacticalDirective.PostPlant,
            director.Decide(TeamSide.Terrorist, context, false, false));
    }

    [Fact]
    public void MemoryIsPerBotAndDecaysIntoHistoricalInformation()
    {
        var memory = new BotMemory();
        memory.Record(1, new EnemyMemory(7, 1, 2, 3, BotInfoSource.Sound, 10f, 1f, false));
        memory.Record(2, new EnemyMemory(7, 9, 8, 7, BotInfoSource.Visual, 10f, 1f, false));

        Assert.True(memory.TryGet(1, 7, 10f, out var botOne));
        Assert.True(memory.TryGet(2, 7, 12f, out var botTwo));
        Assert.Equal(1, botOne.X);
        Assert.Equal(9, botTwo.X);
        Assert.True(botTwo.IsHistorical);
        Assert.True(botTwo.Confidence < botOne.Confidence);
    }

    [Fact]
    public void AimOverrideRequiresEveryInformationGate()
    {
        var policy = new CompetitiveVisibilityPolicy();

        Assert.True(policy.CanOverrideAim(true, true, false, true, false));
        Assert.False(policy.CanOverrideAim(false, true, false, true, false));
        Assert.False(policy.CanOverrideAim(true, false, false, true, false));
        Assert.False(policy.CanOverrideAim(true, true, true, true, false));
        Assert.False(policy.CanOverrideAim(true, true, false, false, false));
        Assert.False(policy.CanOverrideAim(true, true, false, true, true));
    }

    [Fact]
    public void SmokeGeometryOnlyBlocksLinesThatCrossTheSmokeVolume()
    {
        Assert.True(VisibilityGeometry.SegmentIntersectsSphere(
            0f, 0f, 0f,
            1000f, 0f, 0f,
            500f, 0f, 0f,
            120f));
        Assert.False(VisibilityGeometry.SegmentIntersectsSphere(
            0f, 0f, 0f,
            1000f, 0f, 0f,
            500f, 250f, 0f,
            120f));
    }

    [Fact]
    public void SmokeVisibilityChecksTheActualAimPointNotOnlyEnemyCenter()
    {
        var smokes = new[]
        {
            new SmokeVolume(500f, 0f, 64f, 120f),
        };

        Assert.True(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 0f, 64f,
            1000f, 0f, 64f,
            smokes));
        Assert.False(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 0f, 200f,
            1000f, 0f, 200f,
            smokes));
    }

    [Fact]
    public void FinalPistolJitterPointIsRecheckedAgainstSmoke()
    {
        var smokes = new[]
        {
            new SmokeVolume(500f, 100f, 30f, 20f),
        };
        var original = new AimPoint(1040f, 0f, 64f);

        Assert.False(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 0f, 64f,
            original.X, original.Y, original.Z,
            smokes));

        var jittered = AimJitterPolicy.Apply(
            enemyOriginX: 1000f,
            enemyOriginY: 0f,
            target: original,
            targetJitterUnits: 200f,
            burstStability: 1f,
            jitterSeed: 0);

        Assert.True(VisibilityGeometry.SegmentIntersectsAnySmoke(
            0f, 0f, 64f,
            jittered.X, jittered.Y, jittered.Z,
            smokes));
    }
}
