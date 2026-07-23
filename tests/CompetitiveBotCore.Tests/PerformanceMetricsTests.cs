using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class PerformanceMetricsTests
{
    [Fact]
    public void MetricsAggregatePercentilesWithoutKeepingPerTickLogRecords()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 16);
        foreach (double milliseconds in new[] { 0.1, 0.2, 0.3, 0.4, 1.0 })
            metrics.RecordMilliseconds(PerformancePhase.NormalTick, milliseconds);

        var snapshot = metrics.Snapshot(PerformancePhase.NormalTick);

        Assert.Equal(5, snapshot.Calls);
        Assert.InRange(snapshot.P50Milliseconds, 0.2, 0.4);
        Assert.InRange(snapshot.P95Milliseconds, 0.9, 1.01);
        Assert.InRange(snapshot.P99Milliseconds, 0.9, 1.01);
        Assert.Equal(1.0, snapshot.MaxMilliseconds, precision: 6);
        Assert.Equal(0, metrics.Snapshot(PerformancePhase.BuyPlanning).Calls);
    }

    [Fact]
    public void SampleRingIsBoundedWhileCallCountContinuesToAggregate()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 3);
        for (int index = 0; index < 10; index++)
            metrics.RecordMilliseconds(PerformancePhase.BuyExecution, index);

        var snapshot = metrics.Snapshot(PerformancePhase.BuyExecution);
        Assert.Equal(10, snapshot.Calls);
        Assert.Equal(9, snapshot.MaxMilliseconds, precision: 6);
        Assert.Equal(3, snapshot.SampleCount);
    }
}
