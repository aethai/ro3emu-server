namespace Ro3Emu.Server.Maps;

/// <summary>
/// The client's walkability grid of one map (<c>*_dynamicmapdata.bytes</c>): 1 m cells, world x,z =
/// cell index, each cell walkable or not with a height; a few maps carry extra height layers. Little-endian, uncompressed:
/// a 12 x int32 header, a block table (30x30-cell blocks), 4 bytes per cell (byte0 bit7 walkable, int16 height in cm)
/// and 8-byte extra layers {cell, u16 x, u16 z}.
/// </summary>
public sealed class NavGrid
{
    /// <summary>The client accepts a walkable layer within this height of the queried y (SceneSpaceDynamic._isStaticWalkable).</summary>
    public const float WalkHeightTolerance = 2f;

    private const int HeaderSize = 48;
    private readonly byte[] _data;
    private readonly int _blockSize;
    private readonly int _blocksX;

    public int Width { get; }
    public int Height { get; }

    private NavGrid(byte[] data)
    {
        _data = data;
        Width = BitConverter.ToInt32(data, 20);
        Height = BitConverter.ToInt32(data, 24);
        _blockSize = BitConverter.ToInt32(data, 28);
        _blocksX = (Width + _blockSize - 1) / _blockSize;
    }

    public static NavGrid Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Height layers of cell floor(x), floor(z): (walkable, height in m). A struct sequence: every walk,
    /// teleport and monster step asks, and the iterator object was a steady share of the loop's allocation (#484).</summary>
    public LayerSet Layers(float x, float z) => new(this, (int)MathF.Floor(x), (int)MathF.Floor(z));

    public readonly struct LayerSet(NavGrid grid, int cx, int cz) : IEnumerable<(bool Walkable, float Height)>
    {
        public LayerEnumerator GetEnumerator() => new(grid, cx, cz);
        IEnumerator<(bool Walkable, float Height)> IEnumerable<(bool Walkable, float Height)>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The cell's own layer first, then the extra layers recorded for the same cell.</summary>
    public struct LayerEnumerator : IEnumerator<(bool Walkable, float Height)>
    {
        private readonly byte[] _data;
        private readonly int _cx, _cz, _cell, _otherPos, _otherCount;
        private int _next; // -1 the cell's own layer, then extra layer indexes; past the end when done

        internal LayerEnumerator(NavGrid grid, int cx, int cz)
        {
            _data = grid._data;
            _cx = cx; _cz = cz;
            Current = default;
            if (cx < 0 || cz < 0 || cx >= grid.Width || cz >= grid.Height)
            {
                _cell = _otherPos = _otherCount = 0;
                _next = int.MaxValue;
                return;
            }
            int block = cz / grid._blockSize * grid._blocksX + cx / grid._blockSize;
            int entry = HeaderSize + block * 16;
            int normalPos = BitConverter.ToInt32(_data, entry);
            _otherPos = BitConverter.ToInt32(_data, entry + 8);
            _otherCount = BitConverter.ToInt32(_data, entry + 12);
            _cell = normalPos + (cz % grid._blockSize * grid._blockSize + cx % grid._blockSize) * 4;
            _next = -1;
        }

        public (bool Walkable, float Height) Current { get; private set; }
        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_next == -1)
            {
                _next = 0;
                Current = ((_data[_cell] & 0x80) != 0, BitConverter.ToInt16(_data, _cell + 2) * 0.01f);
                return true;
            }
            while (_next >= 0 && _next < _otherCount)
            {
                int p = _otherPos + _next++ * 8;
                if (BitConverter.ToUInt16(_data, p + 4) == _cx && BitConverter.ToUInt16(_data, p + 6) == _cz)
                {
                    Current = ((_data[p] & 0x80) != 0, BitConverter.ToInt16(_data, p + 2) * 0.01f);
                    return true;
                }
            }
            _next = int.MaxValue;
            return false;
        }

        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }

    public bool IsWalkable(float x, float y, float z)
    {
        foreach (var (walkable, h) in Layers(x, z))
            if (walkable && MathF.Abs(h - y) <= WalkHeightTolerance) return true;
        return false;
    }

    /// <summary>Height of the walkable layer nearest to <paramref name="yHint"/>, or null when the cell is not walkable.</summary>
    public float? WalkHeight(float x, float z, float yHint)
    {
        float? best = null;
        foreach (var (walkable, h) in Layers(x, z))
            if (walkable && (best == null || MathF.Abs(h - yHint) < MathF.Abs(best.Value - yHint))) best = h;
        return best;
    }

    /// <summary>
    /// The point itself on its walkable layer nearest to <paramref name="yHint"/>, else the centre of the nearest walkable cell
    /// at most <paramref name="cells"/> cells away (any height), else null. A client standing at the edge of a wall can report a
    /// position in a cell the 1 m grid marks blocked, and its y need not match the layer (inferred).
    /// </summary>
    public (float X, float Y, float Z)? NearestWalkable(float x, float z, float yHint, int cells = 1)
    {
        if (WalkHeight(x, z, yHint) is { } own) return (x, own, z);
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        (float X, float Y, float Z)? best = null;
        float bestDistance = float.MaxValue;
        for (int dx = -cells; dx <= cells; dx++)
            for (int dz = -cells; dz <= cells; dz++)
            {
                float px = cx + dx + 0.5f, pz = cz + dz + 0.5f, d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d < bestDistance && WalkHeight(px, pz, yHint) is { } h) { best = (px, h, pz); bestDistance = d; }
            }
        return best;
    }

    /// <summary>Length of a route <see cref="FindPath"/> returns, from a along its legs.</summary>
    public static float RouteLength(float ax, float az, IReadOnlyList<(float X, float Y, float Z)> route)
    {
        float length = 0, x = ax, z = az;
        foreach (var p in route)
        {
            length += MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
            (x, z) = (p.X, p.Z);
        }
        return length;
    }

    /// <summary>
    /// The walkable layers reachable on foot from the seeds (inferred): a flood fill over the 8 neighbouring cells,
    /// stepping onto a walkable layer within <see cref="WalkHeightTolerance"/> of the current one, as <see cref="CanWalk"/>
    /// follows heights. A seed that stands on no walkable layer adds nothing.
    /// </summary>
    public NavReach Reachable(IEnumerable<(float X, float Y, float Z)> seeds)
    {
        var reached = new HashSet<long>();
        var open = new Queue<(int X, int Z, float Height)>();
        foreach (var (x, y, z) in seeds)
            if (Layer(x, y, z) is { } seed && reached.Add(NavReach.Key(seed.X, seed.Z, seed.Index, Width)))
                open.Enqueue((seed.X, seed.Z, seed.Height));
        while (open.TryDequeue(out var at))
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (Layer(at.X + dx + 0.5f, at.Height, at.Z + dz + 0.5f) is { } next
                        && reached.Add(NavReach.Key(next.X, next.Z, next.Index, Width)))
                        open.Enqueue((next.X, next.Z, next.Height));
        return new NavReach(this, reached);
    }

    /// <summary>The walkable layer of cell floor(x), floor(z) nearest to y within <see cref="WalkHeightTolerance"/>.</summary>
    internal (int X, int Z, int Index, float Height)? Layer(float x, float y, float z)
    {
        (int X, int Z, int Index, float Height)? best = null;
        int index = 0;
        foreach (var (walkable, h) in Layers(x, z))
        {
            if (walkable && MathF.Abs(h - y) <= WalkHeightTolerance && (best == null || MathF.Abs(h - y) < MathF.Abs(best.Value.Height - y)))
                best = ((int)MathF.Floor(x), (int)MathF.Floor(z), index, h);
            index++;
        }
        return best;
    }

    /// <summary>
    /// Whether the straight 2D segment from a to b crosses only walkable cells (sampled every half metre, heights
    /// followed cell by cell like the client's direct-walk check).
    /// </summary>
    public bool CanWalk(float ax, float ay, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.5f));
        float y = ay;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = ax + dx * t, z = az + dz * t;
            if (WalkHeight(x, z, y) is not { } h || MathF.Abs(h - y) > WalkHeightTolerance) return false;
            y = h;
        }
        return true;
    }

    /// <summary>
    /// A walkable route from a to b: A* over the 1 m cells (8 neighbours, no corner cutting, heights followed as
    /// <see cref="CanWalk"/> does), pulled into straight legs of at most <paramref name="maxLeg"/> m that
    /// <see cref="CanWalk"/> accepts from the end of the leg before (the first from a itself). The points to walk to in
    /// order, the last one b; null when b is not reached within <paramref name="maxNodes"/> expanded cells. A leg is
    /// checked between exactly the points it is walked between: <see cref="CanWalk"/> samples a line every half metre,
    /// so a part of a checked line that grazes a wall can fail where the whole passed. The client data has no route for
    /// server-driven units, so a monster sent across a map takes the shortest one (inferred).
    /// </summary>
    public List<(float X, float Y, float Z)>? FindPath(float ax, float ay, float az, float bx, float bz,
        float maxLeg = float.MaxValue, int maxNodes = 60000)
    {
        int sx = (int)MathF.Floor(ax), sz = (int)MathF.Floor(az), gx = (int)MathF.Floor(bx), gz = (int)MathF.Floor(bz);
        if (!Inside(sx, sz) || !Inside(gx, gz) || WalkHeight(bx, bz, ay) is not { } by) return null;
        if (Within(ax, az, bx, bz, maxLeg) && CanWalk(ax, ay, az, bx, bz)) return [(bx, by, bz)];
        int start = sz * Width + sx, goal = gz * Width + gx;
        var scratch = Interlocked.Exchange(ref _scratch, null) ?? new SearchScratch();
        try { return Search(scratch, ax, ay, az, bx, by, bz, sx, sz, gx, gz, start, goal, maxLeg, maxNodes); }
        finally
        {
            // A search that expanded many cells keeps large tables: let them go instead of holding them per map.
            if (scratch.Cost.Count <= ScratchKeepCells) { scratch.Clear(); Volatile.Write(ref _scratch, scratch); }
        }
    }

    /// <summary>The A* tables of one search, reused by the next search on this grid (monsters path every few seconds; new
    /// tables per search were a steady share of the loop's allocation, #484). Taken with an exchange, so a concurrent
    /// caller simply gets its own.</summary>
    private sealed class SearchScratch
    {
        public readonly Dictionary<int, int> Parent = [];
        public readonly Dictionary<int, float> Cost = [];
        public readonly Dictionary<int, float> Height = [];
        public readonly HashSet<int> Closed = [];
        public readonly PriorityQueue<int, float> Open = new();

        public void Clear() { Parent.Clear(); Cost.Clear(); Height.Clear(); Closed.Clear(); Open.Clear(); }
    }

    private const int ScratchKeepCells = 8192;
    private SearchScratch? _scratch;

    private List<(float X, float Y, float Z)>? Search(SearchScratch scratch, float ax, float ay, float az, float bx, float by,
        float bz, int sx, int sz, int gx, int gz, int start, int goal, float maxLeg, int maxNodes)
    {
        var (parent, cost, height, closed, open) = (scratch.Parent, scratch.Cost, scratch.Height, scratch.Closed, scratch.Open);
        parent[start] = -1;
        cost[start] = 0;
        height[start] = ay;
        float Heuristic(int x, int z)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return Math.Max(dx, dz) + 0.41421357f * Math.Min(dx, dz);
        }
        bool Step(int x, int z, float y, out float h)
        {
            h = 0;
            if (!Inside(x, z) || WalkHeight(x + 0.5f, z + 0.5f, y) is not { } found || MathF.Abs(found - y) > WalkHeightTolerance) return false;
            h = found;
            return true;
        }
        open.Enqueue(start, Heuristic(sx, sz));
        while (open.TryDequeue(out int cell, out _))
        {
            if (!closed.Add(cell)) continue;
            if (cell == goal) return Pull(ax, ay, az, (bx, by, bz), cell, parent, height, maxLeg);
            if (closed.Count > maxNodes) return null;
            int cx = cell % Width, cz = cell / Width;
            float g = cost[cell], y = height[cell];
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    bool diagonal = dx != 0 && dz != 0;
                    if (diagonal && (!Step(cx + dx, cz, y, out _) || !Step(cx, cz + dz, y, out _))) continue;
                    if (!Step(cx + dx, cz + dz, y, out float ny)) continue;
                    int next = (cz + dz) * Width + cx + dx;
                    float ng = g + (diagonal ? 1.41421357f : 1f);
                    if (closed.Contains(next) || cost.TryGetValue(next, out float old) && old <= ng) continue;
                    cost[next] = ng;
                    parent[next] = cell;
                    height[next] = ny;
                    open.Enqueue(next, ng + Heuristic(cx + dx, cz + dz));
                }
        }
        return null;
    }

    private bool Inside(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

    private static bool Within(float ax, float az, float bx, float bz, float distance) =>
        (bx - ax) * (bx - ax) + (bz - az) * (bz - az) <= distance * distance;

    /// <summary>
    /// The cell chain back to the start as cell centres, then each leg stretched to the farthest one in a straight walk.
    /// A start whose straight line to the next cell grazes a wall goes through its own cell's centre first.
    /// </summary>
    private List<(float X, float Y, float Z)> Pull(float ax, float ay, float az, (float X, float Y, float Z) end, int goal,
        Dictionary<int, int> parent, Dictionary<int, float> height, float maxLeg)
    {
        var cells = new List<(float X, float Y, float Z)> { end };
        for (int cell = parent[goal]; cell >= 0 && parent[cell] >= 0; cell = parent[cell])
            cells.Add((cell % Width + 0.5f, height[cell], cell / Width + 0.5f));
        cells.Reverse();
        var legs = new List<(float X, float Y, float Z)>();
        var from = (X: ax, Y: ay, Z: az);
        if (!CanWalk(ax, ay, az, cells[0].X, cells[0].Z))
        {
            from = (MathF.Floor(ax) + 0.5f, ay, MathF.Floor(az) + 0.5f);
            legs.Add(from);
        }
        for (int i = 0; i < cells.Count;)
        {
            int j = i;
            while (j + 1 < cells.Count && Within(from.X, from.Z, cells[j + 1].X, cells[j + 1].Z, maxLeg)
                && CanWalk(from.X, from.Y, from.Z, cells[j + 1].X, cells[j + 1].Z)) j++;
            legs.Add(cells[j]);
            from = cells[j];
            i = j + 1;
        }
        return legs;
    }
}

/// <summary>A set of walkable layers of one <see cref="NavGrid"/>, from <see cref="NavGrid.Reachable"/>.</summary>
public sealed class NavReach(NavGrid nav, HashSet<long> layers)
{
    public bool IsEmpty => layers.Count == 0;

    /// <summary>Whether the walkable layer under (x, y, z) is in the set.</summary>
    public bool Contains(float x, float y, float z) =>
        nav.Layer(x, y, z) is { } at && layers.Contains(Key(at.X, at.Z, at.Index, nav.Width));

    internal static long Key(int x, int z, int index, int width) => ((long)z * width + x) << 8 | (uint)index;
}
