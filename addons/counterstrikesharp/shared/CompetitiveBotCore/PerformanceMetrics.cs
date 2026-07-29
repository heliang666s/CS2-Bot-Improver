using System.Diagnostics;

namespace CompetitiveBotCore;

public enum PerformancePhase
{
    RoundStart,
    FreezeBuy,
    RoundFreezeEnd,
    NormalTick,
    BombPlanted,
    FlashDetection,
    SoundMaintenance,
    TacticPlanning,
    RouteBuild,
    NavQuery,
    GoalRepath,
    NadePlanning,
    NadeExecution,
    Replan,
    CtTacticalPlanning,
    TPostPlantPlanning,
    BuyPlanning,
    BuyExecution,
    InventoryCalibration,
}

public enum PerformanceWindow
{
    Startup,
    FreezeBuy,
    Stable,
    Endgame,
    Unspecified,
}

public enum PerformanceCounter
{
    EntityScans,
    NavQueries,
    GoalWrites,
    NadeCandidates,
    PendingTransactions,
    TacticalQueueLength,
}

public readonly record struct PerformanceMetricSnapshot(
    long Calls,
    int SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds);

public readonly record struct PerformanceMemorySnapshot(
    long ManagedHeapBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

public readonly record struct PerformanceMemoryWindowSnapshot(
    long Samples,
    long LatestManagedHeapBytes,
    long LatestWorkingSetBytes,
    long PeakManagedHeapBytes,
    long PeakWorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Fixed-size, allocation-free-on-recording phase metrics. Percentiles are
/// calculated only when a diagnostic snapshot is requested, so the Tick hot
/// path does not format or retain one log entry per invocation.
/// </summary>
public sealed class PerformanceMetrics
{
    private sealed class PhaseSamples
    {
        public PhaseSamples(int capacity)
        {
            Values = new long[capacity];
        }

        public readonly long[] Values;
        public long Calls;
        public long WriteIndex;
        public long MaxTicks;
    }

    private sealed class MemoryWindowState
    {
        public long Samples;
        public PerformanceMemorySnapshot Latest;
        public long PeakManagedHeapBytes;
        public long PeakWorkingSetBytes;
    }

    private readonly PhaseSamples[] _phases;
    private readonly PhaseSamples[][] _windowPhases;
    private readonly MemoryWindowState[] _memoryWindows;
    private readonly long[] _counters;
    private PerformanceMemorySnapshot _roundStartMemory;
    private PerformanceMemorySnapshot _latestMemory;

    public PerformanceMetrics(int samplesPerPhase = 256)
    {
        int capacity = Math.Max(1, samplesPerPhase);
        var phases = Enum.GetValues<PerformancePhase>();
        _phases = phases
            .Select(_ => new PhaseSamples(capacity))
            .ToArray();
        _windowPhases = Enum.GetValues<PerformanceWindow>()
            .Select(_ => phases
                .Select(_ => new PhaseSamples(capacity))
                .ToArray())
            .ToArray();
        _memoryWindows = Enum.GetValues<PerformanceWindow>()
            .Select(_ => new MemoryWindowState())
            .ToArray();
        _counters = new long[Enum.GetValues<PerformanceCounter>().Length];
    }

    public long Start() => Stopwatch.GetTimestamp();

    public void Stop(PerformancePhase phase, long startedAt)
        => RecordTicks(phase, Math.Max(0, Stopwatch.GetTimestamp() - startedAt));

    public void RecordMilliseconds(PerformancePhase phase, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0d)
            return;

        RecordTicks(
            phase,
                Math.Max(0L, (long)Math.Round(
                milliseconds * Stopwatch.Frequency / 1000d)));
    }

    public void Stop(
        PerformancePhase phase,
        PerformanceWindow window,
        long startedAt)
        => RecordWindowTicks(
            phase,
            window,
            Math.Max(0, Stopwatch.GetTimestamp() - startedAt));

    public void RecordMilliseconds(
        PerformancePhase phase,
        PerformanceWindow window,
        double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0d)
            return;

        RecordWindowTicks(
            phase,
            window,
            Math.Max(0L, (long)Math.Round(
                milliseconds * Stopwatch.Frequency / 1000d)));
    }

    public void Increment(PerformanceCounter counter, long delta = 1)
    {
        if (delta <= 0)
            return;
        Interlocked.Add(ref _counters[(int)counter], delta);
    }

    public void Set(PerformanceCounter counter, long value)
        => Interlocked.Exchange(ref _counters[(int)counter], Math.Max(0L, value));

    public long ReadCounter(PerformanceCounter counter)
        => Volatile.Read(ref _counters[(int)counter]);

    public PerformanceMemorySnapshot CaptureMemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var snapshot = new PerformanceMemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
        _latestMemory = snapshot;
        return snapshot;
    }

    public PerformanceMemorySnapshot CaptureMemorySnapshot(PerformanceWindow window)
    {
        var snapshot = CaptureMemorySnapshot();
        var state = _memoryWindows[(int)window];
        state.Samples++;
        state.Latest = snapshot;
        state.PeakManagedHeapBytes = Math.Max(
            state.PeakManagedHeapBytes,
            snapshot.ManagedHeapBytes);
        state.PeakWorkingSetBytes = Math.Max(
            state.PeakWorkingSetBytes,
            snapshot.WorkingSetBytes);
        return snapshot;
    }

    public void CaptureRoundStartMemory()
        => _roundStartMemory = CaptureMemorySnapshot(PerformanceWindow.Startup);

    public PerformanceMemorySnapshot RoundStartMemory => _roundStartMemory;
    public PerformanceMemorySnapshot LatestMemory => _latestMemory;

    public PerformanceMemoryWindowSnapshot MemorySnapshot(PerformanceWindow window)
    {
        var state = _memoryWindows[(int)window];
        return new PerformanceMemoryWindowSnapshot(
            state.Samples,
            state.Latest.ManagedHeapBytes,
            state.Latest.WorkingSetBytes,
            state.PeakManagedHeapBytes,
            state.PeakWorkingSetBytes,
            state.Latest.Gen0Collections,
            state.Latest.Gen1Collections,
            state.Latest.Gen2Collections);
    }

    public void ResetCounters()
    {
        for (int i = 0; i < _counters.Length; i++)
            Volatile.Write(ref _counters[i], 0L);
    }

    public void Reset()
    {
        foreach (var samples in _phases)
            ResetSamples(samples);
        foreach (var window in _windowPhases)
            foreach (var samples in window)
                ResetSamples(samples);
        foreach (var window in _memoryWindows)
        {
            window.Samples = 0;
            window.Latest = default;
            window.PeakManagedHeapBytes = 0;
            window.PeakWorkingSetBytes = 0;
        }
        ResetCounters();
        _roundStartMemory = default;
        _latestMemory = default;
    }

    public PerformanceMetricSnapshot Snapshot(PerformancePhase phase)
        => Snapshot(_phases[(int)phase]);

    public PerformanceMetricSnapshot Snapshot(
        PerformancePhase phase,
        PerformanceWindow window)
        => Snapshot(_windowPhases[(int)window][(int)phase]);

    private PerformanceMetricSnapshot Snapshot(PhaseSamples samples)
    {
        long calls = Volatile.Read(ref samples.Calls);
        int count = (int)Math.Min(calls, samples.Values.Length);
        if (count == 0)
            return default;

        var values = new long[count];
        long writeIndex = Volatile.Read(ref samples.WriteIndex);
        long first = Math.Max(0L, writeIndex - count);
        for (int i = 0; i < count; i++)
            values[i] = Volatile.Read(
                ref samples.Values[(int)((first + i) % samples.Values.Length)]);
        Array.Sort(values);

        return new PerformanceMetricSnapshot(
            calls,
            count,
            ToMilliseconds(values[PercentileIndex(count, 0.50)]),
            ToMilliseconds(values[PercentileIndex(count, 0.95)]),
            ToMilliseconds(values[PercentileIndex(count, 0.99)]),
            ToMilliseconds(Volatile.Read(ref samples.MaxTicks)));
    }

    /// <summary>
    /// Formats one diagnostic snapshot for an operator-facing command or log.
    /// This is intentionally outside the recording path: percentile sorting and
    /// string allocation only happen when someone asks for observability.
    /// </summary>
    public string FormatSnapshot(PerformancePhase phase)
    {
        var snapshot = Snapshot(phase);
        return $"{phase} calls={snapshot.Calls} samples={snapshot.SampleCount} "
            + $"p50={snapshot.P50Milliseconds:F3}ms "
            + $"p95={snapshot.P95Milliseconds:F3}ms "
            + $"p99={snapshot.P99Milliseconds:F3}ms "
            + $"max={snapshot.MaxMilliseconds:F3}ms";
    }

    public string FormatSnapshot(
        PerformancePhase phase,
        PerformanceWindow window)
    {
        var snapshot = Snapshot(phase, window);
        return $"{window}/{phase} calls={snapshot.Calls} samples={snapshot.SampleCount} "
            + $"p50={snapshot.P50Milliseconds:F3}ms "
            + $"p95={snapshot.P95Milliseconds:F3}ms "
            + $"p99={snapshot.P99Milliseconds:F3}ms "
            + $"max={snapshot.MaxMilliseconds:F3}ms";
    }

    private void RecordTicks(PerformancePhase phase, long ticks)
        => RecordTicks(_phases[(int)phase], ticks);

    private void RecordWindowTicks(
        PerformancePhase phase,
        PerformanceWindow window,
        long ticks)
        => RecordTicks(_windowPhases[(int)window][(int)phase], ticks);

    private static void ResetSamples(PhaseSamples samples)
    {
        Array.Clear(samples.Values);
        Volatile.Write(ref samples.Calls, 0L);
        Volatile.Write(ref samples.WriteIndex, 0L);
        Volatile.Write(ref samples.MaxTicks, 0L);
    }

    private static void RecordTicks(PhaseSamples samples, long ticks)
    {
        long writeIndex = Interlocked.Increment(ref samples.WriteIndex) - 1;
        samples.Values[(int)(writeIndex % samples.Values.Length)] = ticks;
        Interlocked.Increment(ref samples.Calls);

        long currentMax;
        do
        {
            currentMax = Volatile.Read(ref samples.MaxTicks);
            if (currentMax >= ticks)
                break;
        }
        while (Interlocked.CompareExchange(ref samples.MaxTicks, ticks, currentMax) != currentMax);
    }

    private static int PercentileIndex(int count, double percentile)
        => Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private static double ToMilliseconds(long ticks)
        => ticks * 1000d / Stopwatch.Frequency;
}
