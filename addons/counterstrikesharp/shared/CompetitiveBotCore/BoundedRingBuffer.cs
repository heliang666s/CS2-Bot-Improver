namespace CompetitiveBotCore;

public sealed class BoundedRingBuffer<T>
{
    private readonly T[] _items;
    private int _next;
    private int _count;

    public BoundedRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Count => _count;
    public int Capacity => _items.Length;

    public void Add(T item)
    {
        _items[_next] = item;
        _next = (_next + 1) % _items.Length;
        if (_count < _items.Length)
            _count++;
    }

    public T[] ToArray()
    {
        var result = new T[_count];
        int first = _count == _items.Length ? _next : 0;
        for (int i = 0; i < _count; i++)
            result[i] = _items[(first + i) % _items.Length];
        return result;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _next = 0;
        _count = 0;
    }
}
