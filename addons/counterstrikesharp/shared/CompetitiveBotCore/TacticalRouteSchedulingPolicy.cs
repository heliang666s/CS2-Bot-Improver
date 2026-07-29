namespace CompetitiveBotCore;

public static class TacticalRouteSchedulingPolicy
{
    public static bool ShouldRebuild(
        bool planChanged,
        bool carrierChanged,
        bool rosterChanged,
        bool routeFailed,
        bool forced)
        => planChanged
            || carrierChanged
            || rosterChanged
            || routeFailed
            || forced;

    public static int[] SelectGoalBatch(
        IReadOnlyList<int> slots,
        int cursor,
        int maximumBatchSize,
        out int nextCursor)
    {
        if (slots.Count == 0 || maximumBatchSize <= 0)
        {
            nextCursor = 0;
            return Array.Empty<int>();
        }

        int start = Math.Clamp(cursor, 0, slots.Count - 1);
        int count = Math.Min(maximumBatchSize, slots.Count - start);
        var result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = slots[start + i];

        nextCursor = start + count >= slots.Count ? 0 : start + count;
        return result;
    }
}
