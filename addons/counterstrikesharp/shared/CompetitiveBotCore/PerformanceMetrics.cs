using System.Diagnostics;

namespace CompetitiveBotCore;

public enum PerformancePhase
{
    RoundStart,
    NormalTick,
    BombPlanted,
    FlashDetection,
    SoundMaintenance,
    CtTacticalPlanning,
    TPostPlantPlanning,
    BuyPlanning,
    BuyExecution,
    InventoryCalibration,
}

public readonly record struct PerformanceMetricSnapshot(
    long Calls,
    int SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds);

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

    private readonly PhaseSamples[] _phases;

    public PerformanceMetrics(int samplesPerPhase = 256)
    {
        int capacity = Math.Max(1, samplesPerPhase);
        _phases = Enum.GetValues<PerformancePhase>()
            .Select(_ => new PhaseSamples(capacity))
            .ToArray();
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

    public PerformanceMetricSnapshot Snapshot(PerformancePhase phase)
    {
        var samples = _phases[(int)phase];
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

    private void RecordTicks(PerformancePhase phase, long ticks)
    {
        var samples = _phases[(int)phase];
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
