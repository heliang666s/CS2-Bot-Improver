namespace CompetitiveBotCore;

/// <summary>
/// Bounded cache for immutable planning results. The caller owns the key and
/// must include every input version that can change the result.
/// </summary>
public sealed class PlanningResultCache<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _values = new();
    private readonly Queue<TKey> _insertionOrder = new();
    private readonly int _capacity;

    public PlanningResultCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
                return _values.Count;
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
            return _values.TryGetValue(key, out value!);
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_values.ContainsKey(key))
            {
                _values[key] = value;
                return;
            }

            _values[key] = value;
            _insertionOrder.Enqueue(key);
            while (_values.Count > _capacity && _insertionOrder.Count > 0)
                _values.Remove(_insertionOrder.Dequeue());
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
            return _values.Remove(key);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _insertionOrder.Clear();
        }
    }
}
