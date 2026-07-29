namespace CompetitiveBotCore;

public enum RoundWorkItem
{
    Observe,
    DraftPlan,
    ReconcileInventory,
    LockPlan,
    ExecuteBuy,
    ActivateRoutes,
    PrepareTactical,
    InitializeTactical,
    ExecuteUtility,
    LiveReplan,
    PostPlant,
    RoundEndCleanup,
}

/// <summary>
/// Small deterministic scheduler for round-scoped work. It coalesces duplicate
/// requests, never releases work from an old round, and returns at most one due
/// item per call so callers can spread expensive work across ticks.
/// </summary>
public sealed class RoundWorkScheduler
{
    private readonly float[] _dueAt = new float[Enum.GetValues<RoundWorkItem>().Length];
    private readonly bool[] _pending = new bool[Enum.GetValues<RoundWorkItem>().Length];
    private int _roundKey = -1;

    public int RoundKey => _roundKey;

    public void Reset(int roundKey)
    {
        _roundKey = roundKey;
        Array.Clear(_dueAt);
        Array.Clear(_pending);
    }

    public bool Schedule(int roundKey, RoundWorkItem item, float dueAt)
    {
        if (roundKey != _roundKey || !float.IsFinite(dueAt))
            return false;

        int index = (int)item;
        if (_pending[index])
        {
            _dueAt[index] = MathF.Min(_dueAt[index], dueAt);
            return false;
        }

        _pending[index] = true;
        _dueAt[index] = dueAt;
        return true;
    }

    public bool TryDequeue(int roundKey, float now, out RoundWorkItem item)
    {
        item = default;
        if (roundKey != _roundKey || !float.IsFinite(now))
            return false;

        int selected = -1;
        float selectedDueAt = float.MaxValue;
        for (int index = 0; index < _pending.Length; index++)
        {
            if (!_pending[index] || _dueAt[index] > now)
                continue;

            if (_dueAt[index] < selectedDueAt)
            {
                selected = index;
                selectedDueAt = _dueAt[index];
            }
        }

        if (selected < 0)
            return false;

        _pending[selected] = false;
        item = (RoundWorkItem)selected;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_dueAt);
        Array.Clear(_pending);
    }
}
