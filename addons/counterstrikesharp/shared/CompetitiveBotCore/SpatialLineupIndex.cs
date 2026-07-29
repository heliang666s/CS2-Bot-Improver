namespace CompetitiveBotCore;

/// <summary>
/// Two-dimensional spatial index for static lineup trigger points. Entries are
/// stored once by cell and copied into a caller-owned buffer during queries, so
/// the normal tick path does not allocate or scan the complete map catalog.
/// </summary>
public sealed class SpatialLineupIndex<T>
{
    private readonly record struct SpatialEntry(T Item, float X, float Y);
    private readonly float _cellSize;
    private readonly Dictionary<(int X, int Y), List<SpatialEntry>> _cells = new();

    public SpatialLineupIndex(float cellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        _cellSize = cellSize;
    }

    public int CellCount => _cells.Count;

    public void Add(T item, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return;

        var key = GetCell(x, y);
        if (!_cells.TryGetValue(key, out var items))
        {
            items = new List<SpatialEntry>();
            _cells[key] = items;
        }
        items.Add(new SpatialEntry(item, x, y));
    }

    public void CopyCandidates(
        float x,
        float y,
        float radius,
        List<T> destination)
    {
        destination.Clear();
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || !float.IsFinite(radius)
            || radius < 0f)
            return;

        int cellRadius = Math.Max(0, (int)MathF.Ceiling(radius / _cellSize));
        var center = GetCell(x, y);
        float radiusSquared = radius * radius;
        for (int cellX = center.X - cellRadius;
             cellX <= center.X + cellRadius;
             cellX++)
        {
            for (int cellY = center.Y - cellRadius;
                 cellY <= center.Y + cellRadius;
                 cellY++)
            {
                if (!_cells.TryGetValue((cellX, cellY), out var items))
                    continue;

                foreach (var item in items)
                {
                    float dx = x - item.X;
                    float dy = y - item.Y;
                    if (dx * dx + dy * dy <= radiusSquared)
                        destination.Add(item.Item);
                }
            }
        }
    }

    public void Clear()
        => _cells.Clear();

    private (int X, int Y) GetCell(float x, float y)
        => (
            (int)MathF.Floor(x / _cellSize),
            (int)MathF.Floor(y / _cellSize));
}
