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

    /// <summary>Height layers of cell floor(x), floor(z): (walkable, height in m).</summary>
    public IEnumerable<(bool Walkable, float Height)> Layers(float x, float z)
    {
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) yield break;
        int block = cz / _blockSize * _blocksX + cx / _blockSize;
        int entry = HeaderSize + block * 16;
        int normalPos = BitConverter.ToInt32(_data, entry);
        int otherPos = BitConverter.ToInt32(_data, entry + 8);
        int otherCount = BitConverter.ToInt32(_data, entry + 12);
        int cell = normalPos + (cz % _blockSize * _blockSize + cx % _blockSize) * 4;
        yield return ((_data[cell] & 0x80) != 0, BitConverter.ToInt16(_data, cell + 2) * 0.01f);
        for (int k = 0; k < otherCount; k++)
        {
            int p = otherPos + k * 8;
            if (BitConverter.ToUInt16(_data, p + 4) == cx && BitConverter.ToUInt16(_data, p + 6) == cz)
                yield return ((_data[p] & 0x80) != 0, BitConverter.ToInt16(_data, p + 2) * 0.01f);
        }
    }

    public bool IsWalkable(float x, float y, float z) =>
        Layers(x, z).Any(l => l.Walkable && MathF.Abs(l.Height - y) <= WalkHeightTolerance);

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
        var parent = new Dictionary<int, int> { [start] = -1 };
        var cost = new Dictionary<int, float> { [start] = 0 };
        var height = new Dictionary<int, float> { [start] = ay };
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
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
