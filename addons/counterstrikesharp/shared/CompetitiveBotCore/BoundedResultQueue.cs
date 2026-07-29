namespace CompetitiveBotCore;

/// <summary>
/// Thread-safe, fixed-capacity FIFO for worker results. If the game thread is
/// delayed, the oldest result is discarded so stale work cannot consume memory
/// without bound.
/// </summary>
public sealed class BoundedResultQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items;
    private readonly int _capacity;
    private long _droppedCount;

    public BoundedResultQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public long DroppedCount
    {
        get
        {
            lock (_gate)
                return _droppedCount;
        }
    }

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            if (_items.Count == _capacity)
            {
                _items.Dequeue();
                _droppedCount++;
            }

            _items.Enqueue(item);
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            item = _items.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }
}
