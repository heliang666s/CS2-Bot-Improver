using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class PerformanceSchedulingTests
{
    [Fact]
    public void FreezePolicyObservesAtTenSecondsButExecutesBeforeFreezeEnds()
    {
        Assert.Equal(10f, FreezeBuyPolicy.ObservationAt(0f, 15f));
        Assert.True(
            FreezeBuyPolicy.ExecutionAt(0f, 15f)
            < FreezeBuyPolicy.EndAt(0f, 15f));
        Assert.True(
            FreezeBuyPolicy.ExecutionAt(0f, 15f)
            >= FreezeBuyPolicy.ObservationAt(0f, 15f));
    }

    [Fact]
    public void InventoryObservationRefreshesOnlyWhenRevisionChanges()
    {
        Assert.True(FreezeBuyPolicy.ShouldRefreshObservation(11L, long.MinValue));
        Assert.False(FreezeBuyPolicy.ShouldRefreshObservation(11L, 11L));
    }

    [Fact]
    public void StableTacticalRouteDoesNotRebuildWithoutInvalidation()
    {
        Assert.False(TacticalRouteSchedulingPolicy.ShouldRebuild(
            planChanged: false,
            carrierChanged: false,
            rosterChanged: false,
            routeFailed: false,
            forced: false));
        Assert.True(TacticalRouteSchedulingPolicy.ShouldRebuild(
            planChanged: false,
            carrierChanged: false,
            rosterChanged: false,
            routeFailed: true,
            forced: false));
    }

    [Fact]
    public void GoalWritesAreDistributedAcrossBatches()
    {
        int[] slots = [1, 2, 3, 4, 5];

        int[] first = TacticalRouteSchedulingPolicy.SelectGoalBatch(
            slots,
            cursor: 0,
            maximumBatchSize: 2,
            out int nextCursor);
        int[] second = TacticalRouteSchedulingPolicy.SelectGoalBatch(
            slots,
            nextCursor,
            maximumBatchSize: 2,
            out _);

        Assert.Equal([1, 2], first);
        Assert.Equal([3, 4], second);
    }

    [Fact]
    public void BoundedBufferNeverExceedsConfiguredCapacity()
    {
        var buffer = new BoundedRingBuffer<int>(capacity: 2);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(2, buffer.Count);
        Assert.Equal([2, 3], buffer.ToArray());
    }

    [Fact]
    public void PerformanceMetricsTrackRoutePhaseAndCountersWithoutGrowingSamples()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 2);
        long startedAt = metrics.Start();
        metrics.Stop(PerformancePhase.RouteBuild, startedAt);
        metrics.Stop(PerformancePhase.RouteBuild, startedAt);
        metrics.Stop(PerformancePhase.RouteBuild, startedAt);
        metrics.Increment(PerformanceCounter.NavQueries, 3);

        var snapshot = metrics.Snapshot(PerformancePhase.RouteBuild);

        Assert.Equal(3, snapshot.Calls);
        Assert.Equal(2, snapshot.SampleCount);
        Assert.Equal(3, metrics.ReadCounter(PerformanceCounter.NavQueries));
    }

    [Fact]
    public void FlashScanSlowsDownWhenNoProjectileIsAlive()
    {
        Assert.Equal(0.50f, FlashScanPolicy.NextInterval(hasLiveProjectile: false));
        Assert.Equal(0.05f, FlashScanPolicy.NextInterval(hasLiveProjectile: true));
    }

    [Fact]
    public void PerformanceMetricsCanResetAtRoundBoundary()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 2);
        long startedAt = metrics.Start();
        metrics.Stop(PerformancePhase.RoundStart, startedAt);
        metrics.Increment(PerformanceCounter.EntityScans, 2);

        metrics.Reset();

        Assert.Equal(0, metrics.Snapshot(PerformancePhase.RoundStart).Calls);
        Assert.Equal(0, metrics.ReadCounter(PerformanceCounter.EntityScans));
    }

    [Fact]
    public void PerformanceMetricsCaptureMemoryAndGcAtRoundBoundary()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 2);

        var snapshot = metrics.CaptureMemorySnapshot();

        Assert.True(snapshot.ManagedHeapBytes >= 0);
        Assert.True(snapshot.WorkingSetBytes >= 0);
        Assert.True(snapshot.Gen0Collections >= 0);
        Assert.True(snapshot.Gen1Collections >= 0);
        Assert.True(snapshot.Gen2Collections >= 0);
    }

    [Fact]
    public void RoundWorkSchedulerRunsDueWorkOnceAndDropsStaleRoundWork()
    {
        var scheduler = new RoundWorkScheduler();
        scheduler.Reset(roundKey: 7);
        scheduler.Schedule(7, RoundWorkItem.Observe, dueAt: 1f);
        scheduler.Schedule(7, RoundWorkItem.DraftPlan, dueAt: 2f);
        scheduler.Schedule(6, RoundWorkItem.ExecuteBuy, dueAt: 0f);

        Assert.False(scheduler.TryDequeue(7, now: 0.5f, out _));
        Assert.True(scheduler.TryDequeue(7, now: 1f, out var first));
        Assert.Equal(RoundWorkItem.Observe, first);
        Assert.False(scheduler.TryDequeue(7, now: 1f, out _));

        scheduler.Reset(roundKey: 8);
        Assert.False(scheduler.TryDequeue(8, now: 3f, out _));
    }

    [Fact]
    public void RoundWorkSchedulerSupportsStagedTacticalBootstrap()
    {
        var scheduler = new RoundWorkScheduler();
        scheduler.Reset(roundKey: 4);
        scheduler.Schedule(4, RoundWorkItem.ActivateRoutes, dueAt: 1f);
        scheduler.Schedule(4, RoundWorkItem.PrepareTactical, dueAt: 2f);
        scheduler.Schedule(4, RoundWorkItem.LockPlan, dueAt: 3f);
        scheduler.Schedule(4, RoundWorkItem.InitializeTactical, dueAt: 4f);

        Assert.True(scheduler.TryDequeue(4, now: 1f, out var activate));
        Assert.Equal(RoundWorkItem.ActivateRoutes, activate);
        Assert.True(scheduler.TryDequeue(4, now: 2f, out var prepare));
        Assert.Equal(RoundWorkItem.PrepareTactical, prepare);
        Assert.True(scheduler.TryDequeue(4, now: 3f, out var lockPlan));
        Assert.Equal(RoundWorkItem.LockPlan, lockPlan);
        Assert.True(scheduler.TryDequeue(4, now: 4f, out var initialize));
        Assert.Equal(RoundWorkItem.InitializeTactical, initialize);
    }

    [Fact]
    public void BoundedResultQueueKeepsNewestResultsWithinCapacity()
    {
        var queue = new BoundedResultQueue<int>(capacity: 2);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedCount);
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(2, first);
        Assert.Equal(3, second);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void PlanningResultCacheSeparatesEquipmentAndTacticalVersions()
    {
        var cache = new PlanningResultCache<string, string>(capacity: 2);
        cache.Set("round=3;equipment=10;tactic=1", "full-buy");

        Assert.True(cache.TryGet("round=3;equipment=10;tactic=1", out var value));
        Assert.Equal("full-buy", value);
        Assert.False(cache.TryGet("round=3;equipment=11;tactic=1", out _));

        cache.Set("round=3;equipment=11;tactic=1", "fallback-buy");
        cache.Set("round=3;equipment=11;tactic=2", "new-tactic");

        Assert.False(cache.TryGet("round=3;equipment=10;tactic=1", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void PerformanceMetricsKeepWindowSamplesSeparate()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 4);
        metrics.RecordMilliseconds(
            PerformancePhase.TacticPlanning,
            PerformanceWindow.Startup,
            8d);
        metrics.RecordMilliseconds(
            PerformancePhase.TacticPlanning,
            PerformanceWindow.Stable,
            1d);

        Assert.Equal(
            8d,
            metrics.Snapshot(
                PerformancePhase.TacticPlanning,
                PerformanceWindow.Startup).P50Milliseconds,
            precision: 3);
        Assert.Equal(
            1d,
            metrics.Snapshot(
                PerformancePhase.TacticPlanning,
                PerformanceWindow.Stable).P50Milliseconds,
            precision: 3);
    }

    [Fact]
    public void SpatialLineupIndexMatchesFullScanForNearbyCandidates()
    {
        var index = new SpatialLineupIndex<int>(cellSize: 100f);
        index.Add(1, 0f, 0f);
        index.Add(2, 95f, 0f);
        index.Add(4, 150f, 0f);
        index.Add(3, 500f, 500f);

        var indexed = new List<int>();
        index.CopyCandidates(20f, 0f, 100f, indexed);

        Assert.Equal([1, 2], indexed.OrderBy(value => value));
    }

    [Fact]
    public void PerformanceMetricsTrackMemoryByWindow()
    {
        var metrics = new PerformanceMetrics(samplesPerPhase: 2);

        metrics.CaptureMemorySnapshot(PerformanceWindow.Startup);
        metrics.CaptureMemorySnapshot(PerformanceWindow.Stable);

        Assert.Equal(1, metrics.MemorySnapshot(PerformanceWindow.Startup).Samples);
        Assert.Equal(1, metrics.MemorySnapshot(PerformanceWindow.Stable).Samples);
        Assert.True(
            metrics.MemorySnapshot(PerformanceWindow.Startup).LatestManagedHeapBytes >= 0);
    }
}
