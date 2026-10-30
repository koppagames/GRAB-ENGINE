using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using GrabEngine.Code;

namespace GrabEngine.Core;

/// <summary>How an agent is allowed to traverse the voxel level.</summary>
public enum PathfindingTraversal
{
    /// <summary>Walk on surfaces, climb small steps and drop off ledges.</summary>
    Ground,
    /// <summary>Move through free 3D space in any direction (still avoids solid blocks).</summary>
    Fly
}

/// <summary>
/// Editor-side pathfinding configuration. This is stored inside the .grabproj only;
/// real GRAB .level files have no pathfinding component, so exported levels use the
/// baked scene objects and scripts like before.
/// </summary>
public sealed class PathfindingSettings
{
    public string FinderId { get; set; } = "";
    public string TargetId { get; set; } = "";

    // Navigation grid.
    public float CellSize { get; set; } = 0.5f;
    public float AgentRadius { get; set; } = 0.45f;
    public float AgentHeight { get; set; } = 1.8f;
    public float MaxStepHeight { get; set; } = 0.75f;
    public float MaxDropHeight { get; set; } = 2.5f;
    public PathfindingTraversal Traversal { get; set; } = PathfindingTraversal.Ground;
    public bool AllowDiagonal { get; set; } = true;
    public bool AvoidCornerCutting { get; set; } = true;
    public bool SmoothPath { get; set; } = true;
    public float WallHugPenalty { get; set; } = 0.08f;

    // Replanning and following.
    public float RepathInterval { get; set; } = 0.2f;
    public float TargetPrediction { get; set; } = 0.18f;
    public float StopDistance { get; set; } = 0.3f;
    public float Speed { get; set; } = 4.5f;
    public float Acceleration { get; set; } = 20f;
    public float TurnSpeed { get; set; } = 720f;
    public bool FaceMovement { get; set; } = true;
    public float WaypointTolerance { get; set; } = 0.28f;

    // Play/preview.
    public bool AutoFollowInPlay { get; set; } = true;
    public bool ShowPathInEditor { get; set; } = true;
    public bool ShowPathInPlay { get; set; } = true;
    public bool ShowWaypoints { get; set; } = true;
    public float NavRebuildInterval { get; set; } = 0.75f;
    public int MaxIterations { get; set; } = 300000;
    public int MaxCells { get; set; } = 3_000_000;

    /// <summary>How many blocking objects a baked live planner watches. Every watched object
    /// costs GASM instructions in the exported block, so this is the knob that trades
    /// coverage for per-frame cost; the connections themselves are not capped.</summary>
    public int BlockingObjects { get; set; } = 24;

    /// <summary>Editor-only: draw the walkable navmesh and the objects the live planner
    /// watches in the scene view.</summary>
    public bool ShowNavMesh { get; set; }

    public PathfindingSettings Clone() => (PathfindingSettings)MemberwiseClone();

    public void Clamp()
    {
        CellSize = Math.Clamp(CellSize, 0.1f, 4f);
        AgentRadius = Math.Clamp(AgentRadius, 0.05f, 8f);
        AgentHeight = Math.Clamp(AgentHeight, 0.2f, 20f);
        MaxStepHeight = Math.Clamp(MaxStepHeight, 0f, 8f);
        MaxDropHeight = Math.Clamp(MaxDropHeight, 0f, 40f);
        RepathInterval = Math.Clamp(RepathInterval, 0.03f, 5f);
        TargetPrediction = Math.Clamp(TargetPrediction, 0f, 2f);
        StopDistance = Math.Clamp(StopDistance, 0.02f, 8f);
        Speed = Math.Clamp(Speed, 0.1f, 80f);
        Acceleration = Math.Clamp(Acceleration, 0.1f, 300f);
        TurnSpeed = Math.Clamp(TurnSpeed, 10f, 2160f);
        WaypointTolerance = Math.Clamp(WaypointTolerance, 0.05f, 3f);
        NavRebuildInterval = Math.Clamp(NavRebuildInterval, 0.15f, 10f);
        MaxIterations = Math.Clamp(MaxIterations, 1000, 2_000_000);
        MaxCells = Math.Clamp(MaxCells, 20_000, 8_000_000);
        WallHugPenalty = Math.Clamp(WallHugPenalty, 0f, 1f);
        BlockingObjects = Math.Clamp(BlockingObjects, 1, 96);
    }
}

public sealed class PathfindingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public List<Vec3> Points { get; init; } = new();
    public float Length { get; init; }
    public int Visited { get; init; }
    public int Iterations { get; init; }
    public float CellSize { get; init; }
    public double Milliseconds { get; init; }
    public Vec3 StartWorld { get; init; }
    public Vec3 EndWorld { get; init; }
    public Vec3 TargetWorld { get; init; }
    public int StartCell { get; init; } = -1;
    public int GoalCell { get; init; } = -1;
    public string Debug { get; init; } = "";

    /// <summary>Walkable cell centers around the route, filled only when the navmesh
    /// overlay is on so a normal preview costs nothing extra.</summary>
    public List<Vec3> NavCells { get; init; } = new();

    /// <summary>Blocking objects a baked live planner would watch for this route.</summary>
    public List<LevelNode> WatchWalls { get; init; } = new();

    public float NavCellSize { get; init; }

    public static PathfindingResult Fail(string message, double ms = 0, int visited = 0) => new()
    {
        Success = false,
        Message = message,
        Milliseconds = ms,
        Visited = visited
    };
}

/// <summary>
/// Dense voxel navigation grid. Blocks are inflated by the agent radius so A* paths
/// keep a real body-width corridor, and each standable cell carries the agent's full
/// height. A* runs on this grid with a stateless stamp array, so repeated live
/// repathing does not clear millions of cells every frame.
/// </summary>
public sealed class NavigationGrid
{
    public float CellSize { get; }
    public float MinX { get; }
    public float MinY { get; }
    public float MinZ { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public float AgentHeight { get; }
    public int HeightCells { get; }
    public int StepCells { get; }
    public int DropCells { get; }
    public bool Fly { get; }

    private readonly bool[] _solid;
    private readonly int[] _g;
    private readonly int[] _parent;
    private readonly int[] _seen;
    private readonly int[] _closed;
    private int _stamp;

    private NavigationGrid(float cell, float minX, float minY, float minZ,
        int nx, int ny, int nz, bool[] solid, PathfindingSettings settings)
    {
        CellSize = cell;
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        SizeX = nx;
        SizeY = ny;
        SizeZ = nz;
        _solid = solid;
        int total = nx * ny * nz;
        _g = new int[total];
        _parent = new int[total];
        _seen = new int[total];
        _closed = new int[total];
        AgentHeight = settings.AgentHeight;
        HeightCells = Math.Max(1, (int)Math.Ceiling(settings.AgentHeight / cell));
        // Floor, not round: a 0.75 m step limit must never silently become 1.0 m on
        // a 0.5 m grid. The UI promises a maximum, so the grid honours it.
        StepCells = Math.Max(0, (int)Math.Floor(settings.MaxStepHeight / cell + 1e-4f));
        DropCells = Math.Max(0, (int)Math.Floor(settings.MaxDropHeight / cell + 1e-4f));
        Fly = settings.Traversal == PathfindingTraversal.Fly;
    }

    public int Count => _solid.Length;

    public static NavigationGrid Build(GrabLevel level, PathfindingSettings settings, Vec3 start, Vec3 goal,
        LevelNode? ignored = null)
    {
        settings.Clamp();
        var ignoredNodes = ignored == null
            ? null
            : new HashSet<LevelNode>(ignored.SelfAndDescendants());
        var obstacles = new List<(Vec3 Min, Vec3 Max)>();
        float minX = Math.Min(start.X, goal.X), maxX = Math.Max(start.X, goal.X);
        float minY = Math.Min(start.Y, goal.Y), maxY = Math.Max(start.Y, goal.Y);
        float minZ = Math.Min(start.Z, goal.Z), maxZ = Math.Max(start.Z, goal.Z);

        foreach (var node in level.AllNodes())
        {
            if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) continue;
            if (ignoredNodes != null && ignoredNodes.Contains(node)) continue;
            if (node.IsPassable) continue;
            if (node.Generated && node.HideModel) continue;
            var scale = node.WorldScale;
            if (Math.Abs(scale.X) < 1e-4f || Math.Abs(scale.Y) < 1e-4f || Math.Abs(scale.Z) < 1e-4f) continue;
            var center = node.WorldPosition;
            var rotation = node.WorldRotation;
            var hx = rotation.Rotate(new Vec3(Math.Abs(scale.X) * 0.5f, 0, 0));
            var hy = rotation.Rotate(new Vec3(0, Math.Abs(scale.Y) * 0.5f, 0));
            var hz = rotation.Rotate(new Vec3(0, 0, Math.Abs(scale.Z) * 0.5f));
            float ex = Math.Abs(hx.X) + Math.Abs(hy.X) + Math.Abs(hz.X);
            float ey = Math.Abs(hx.Y) + Math.Abs(hy.Y) + Math.Abs(hz.Y);
            float ez = Math.Abs(hx.Z) + Math.Abs(hy.Z) + Math.Abs(hz.Z);
            float radius = settings.AgentRadius;
            var a = new Vec3(center.X - ex - radius, center.Y - ey, center.Z - ez - radius);
            var b = new Vec3(center.X + ex + radius, center.Y + ey, center.Z + ez + radius);
            obstacles.Add((a, b));
            minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, b.X);
            minY = Math.Min(minY, a.Y); maxY = Math.Max(maxY, b.Y);
            minZ = Math.Min(minZ, a.Z); maxZ = Math.Max(maxZ, b.Z);
        }

        float cell = Math.Clamp(settings.CellSize, 0.1f, 4f);
        float padXZ = Math.Max(1.5f, settings.AgentRadius + cell * 2f);
        float padY = Math.Max(1f, cell * 2f);
        minX = (float)Math.Floor(minX - padXZ); maxX = (float)Math.Ceiling(maxX + padXZ);
        minY = (float)Math.Floor(minY - padY); maxY = (float)Math.Ceiling(maxY + padY + settings.AgentHeight);
        minZ = (float)Math.Floor(minZ - padXZ); maxZ = (float)Math.Ceiling(maxZ + padXZ);

        int nx = 1, ny = 1, nz = 1;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cell) + 1);
            ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cell) + 1);
            nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / cell) + 1);
            long total = (long)nx * ny * nz;
            if (total <= settings.MaxCells) break;
            cell *= 1.2f;
        }

        var solid = new bool[(long)nx * ny * nz > int.MaxValue ? 1 : nx * ny * nz];
        if ((long)nx * ny * nz <= int.MaxValue)
        {
            foreach (var (a, b) in obstacles)
            {
                int x0 = Math.Clamp((int)Math.Floor((a.X - minX) / cell + 0.0001), 0, nx - 1);
                int x1 = Math.Clamp((int)Math.Ceiling((b.X - minX) / cell - 0.0001) - 1, x0, nx - 1);
                int y0 = Math.Clamp((int)Math.Floor((a.Y - minY) / cell + 0.0001), 0, ny - 1);
                int y1 = Math.Clamp((int)Math.Ceiling((b.Y - minY) / cell - 0.0001) - 1, y0, ny - 1);
                int z0 = Math.Clamp((int)Math.Floor((a.Z - minZ) / cell + 0.0001), 0, nz - 1);
                int z1 = Math.Clamp((int)Math.Ceiling((b.Z - minZ) / cell - 0.0001) - 1, z0, nz - 1);
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        int row = (y * nz + z) * nx;
                        for (int x = x0; x <= x1; x++) solid[row + x] = true;
                    }
            }
        }
        return new NavigationGrid(cell, minX, minY, minZ, nx, ny, nz, solid, settings);
    }

    public int Index(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;

    public (int X, int Y, int Z) Cell(int index) =>
        (index % SizeX, index / (SizeX * SizeZ), (index / SizeX) % SizeZ);

    public Vec3 Center(int x, int y, int z) => new(
        MinX + (x + 0.5f) * CellSize,
        MinY + y * CellSize + 0.02f,
        MinZ + (z + 0.5f) * CellSize);

    public Vec3 Center(int index)
    {
        var (x, y, z) = Cell(index);
        return Center(x, y, z);
    }

    public bool Solid(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ) return true;
        return _solid[Index(x, y, z)];
    }

    public bool BodyFree(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) return false;
        if (y < 0 || y + HeightCells > SizeY) return false;
        for (int i = 0; i < HeightCells; i++)
            if (_solid[Index(x, y + i, z)]) return false;
        return true;
    }

    public bool Standable(int x, int y, int z)
    {
        if (!BodyFree(x, y, z) || y <= 0) return false;
        return Solid(x, y - 1, z);
    }

    public bool Contains(Vec3 world, float margin = -0.01f)
    {
        return world.X >= MinX - margin && world.X <= MinX + SizeX * CellSize + margin &&
               world.Y >= MinY - margin && world.Y <= MinY + SizeY * CellSize + margin &&
               world.Z >= MinZ - margin && world.Z <= MinZ + SizeZ * CellSize + margin;
    }

    public int CellX(float x) => Math.Clamp((int)Math.Floor((x - MinX) / CellSize), 0, SizeX - 1);
    public int CellY(float y) => Math.Clamp((int)Math.Floor((y - MinY) / CellSize), 0, SizeY - 1);
    public int CellZ(float z) => Math.Clamp((int)Math.Floor((z - MinZ) / CellSize), 0, SizeZ - 1);

    /// <summary>
    /// Walkable cell centers in a horizontal box around two points, limited to the agent's
    /// floor band so stacked floors do not turn into a wall of quads. The overlay caps the
    /// column count, so a huge level still builds one small merged mesh.
    /// </summary>
    public void CollectStandable(Vec3 from, Vec3 to, float radius, int maxColumns, float bandAbove, List<Vec3> into)
    {
        int x0 = CellX(Math.Min(from.X, to.X) - radius);
        int x1 = CellX(Math.Max(from.X, to.X) + radius);
        int z0 = CellZ(Math.Min(from.Z, to.Z) - radius);
        int z1 = CellZ(Math.Max(from.Z, to.Z) + radius);
        float floorY = Math.Min(from.Y, to.Y) + 0.4f;
        int yLow = Math.Max(0, (int)Math.Floor((floorY - 1.2f - MinY) / CellSize));
        int yHigh = Math.Min(SizeY - 1, (int)Math.Ceiling((floorY + bandAbove - MinY) / CellSize));
        int columns = 0;
        for (int x = x0; x <= x1 && columns < maxColumns; x++)
        {
            for (int z = z0; z <= z1 && columns < maxColumns; z++)
            {
                columns++;
                for (int y = yHigh; y >= yLow; y--)
                {
                    if (!Standable(x, y, z)) continue;
                    into.Add(Center(x, y, z));
                    break;
                }
            }
        }
    }

    public int FindStandableNear(Vec3 world, int radiusCells)
    {
        int cx = CellX(world.X), cy = CellY(world.Y), cz = CellZ(world.Z);
        int best = -1;
        float bestScore = float.MaxValue;
        int radius = Math.Max(1, radiusCells);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int x = cx + dx, z = cz + dz;
                if ((uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) continue;
                for (int y = 0; y < SizeY; y++)
                {
                    if (!Standable(x, y, z)) continue;
                    var p = Center(x, y, z);
                    float dh = (p.X - world.X) * (p.X - world.X) + (p.Z - world.Z) * (p.Z - world.Z);
                    float dv = p.Y - world.Y;
                    // Prefer the same floor, then nearby floors. A target floating in the
                    // air still resolves to the closest walkable surface below it.
                    float score = dh + dv * dv * (dv < 0 ? 0.25f : 2.2f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = Index(x, y, z);
                    }
                }
            }
        }
        if (best >= 0) return best;
        // Tiny or thin levels can put the only floor outside the ring. Scan the whole grid.
        for (int y = 0; y < SizeY; y++)
            for (int z = 0; z < SizeZ; z++)
                for (int x = 0; x < SizeX; x++)
                    if (Standable(x, y, z))
                    {
                        var p = Center(x, y, z);
                        float score = (p - world).Length * (p - world).Length;
                        if (score < bestScore) { bestScore = score; best = Index(x, y, z); }
                    }
        return best;
    }

    public int FindFreeNear(Vec3 world, int radiusCells)
    {
        int cx = CellX(world.X), cy = CellY(world.Y), cz = CellZ(world.Z);
        int best = -1;
        float bestScore = float.MaxValue;
        int radius = Math.Max(1, radiusCells);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int x = cx + dx, z = cz + dz;
                if ((uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) continue;
                for (int y = 0; y < SizeY; y++)
                {
                    if (!BodyFree(x, y, z)) continue;
                    var p = Center(x, y, z);
                    float score = (p - world).Length * (p - world).Length;
                    if (score < bestScore) { bestScore = score; best = Index(x, y, z); }
                }
            }
        }
        if (best >= 0) return best;
        for (int y = 0; y < SizeY; y++)
            for (int z = 0; z < SizeZ; z++)
                for (int x = 0; x < SizeX; x++)
                    if (BodyFree(x, y, z))
                    {
                        var p = Center(x, y, z);
                        float score = (p - world).Length * (p - world).Length;
                        if (score < bestScore) { bestScore = score; best = Index(x, y, z); }
                    }
        return best;
    }

    public string Describe(int start, int goal)
    {
        var s = start >= 0 ? Cell(start) : (-1, -1, -1);
        var g = goal >= 0 ? Cell(goal) : (-1, -1, -1);
        return $"grid {SizeX}x{SizeY}x{SizeZ} @ {CellSize:0.##}m; start {s} stand={Standable(s.Item1, s.Item2, s.Item3)} body={BodyFree(s.Item1, s.Item2, s.Item3)}; goal {g} stand={Standable(g.Item1, g.Item2, g.Item3)}";
    }

    public bool LineClear(Vec3 a, Vec3 b, PathfindingSettings settings)
    {
        var d = b - a;
        float horizontal = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        int steps = Math.Max(2, (int)Math.Ceiling(MathF.Max(horizontal, Math.Abs(d.Y)) / (CellSize * 0.45f)));
        for (int i = 0; i <= steps; i++)
        {
            var p = Vec3.Lerp(a, b, (float)i / steps);
            int x = CellX(p.X), z = CellZ(p.Z), y = CellY(p.Y);
            if (!BodyFree(x, y, z)) return false;
            if (!Fly)
            {
                bool floor = false;
                int maxDown = Math.Max(1, StepCells + 1);
                for (int down = 1; down <= maxDown; down++)
                    if (Solid(x, y - down, z)) { floor = true; break; }
                if (!floor) return false;
            }
        }
        return true;
    }

    public PathfindingResult FindPath(int start, int goal, PathfindingSettings settings)
    {
        settings.Clamp();
        var timer = Stopwatch.StartNew();
        if (start < 0 || goal < 0) return PathfindingResult.Fail("Finder or target is outside the walkable navigation area.");
        if (start == goal)
        {
            var p = Center(start);
            return new PathfindingResult
            {
                Success = true, Message = "Already at the target.", Points = { p },
                StartWorld = p, EndWorld = p, CellSize = CellSize, StartCell = start, GoalCell = goal
            };
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            _stamp = 1;
        }
        int stamp = _stamp;
        var open = new PriorityQueue<int, float>();
        _seen[start] = stamp;
        _g[start] = 0;
        _parent[start] = -1;
        open.Enqueue(start, Heuristic(start, goal));
        int visited = 0, iterations = 0;
        float bestH = Heuristic(start, goal);
        bool found = false;
        int dirCount = settings.AllowDiagonal ? 8 : 4;

        while (open.Count > 0 && iterations < settings.MaxIterations)
        {
            open.TryDequeue(out int current, out _);
            if (_closed[current] == stamp) continue;
            _closed[current] = stamp;
            visited++;
            iterations++;
            if (current == goal) { found = true; break; }
            var (cx, cy, cz) = Cell(current);

            for (int d = 0; d < dirCount; d++)
            {
                int dx, dz;
                if (settings.AllowDiagonal)
                {
                    dx = d switch { 0 => -1, 1 => 0, 2 => 1, 3 => -1, 4 => 1, 5 => -1, 6 => 0, 7 => 1, _ => 0 };
                    dz = d switch { 0 => -1, 1 => -1, 2 => -1, 3 => 0, 4 => 0, 5 => 1, 6 => 1, 7 => 1, _ => 0 };
                }
                else
                {
                    dx = d switch { 0 => 0, 1 => 0, 2 => -1, _ => 1 };
                    dz = d switch { 0 => -1, 1 => 1, 2 => 0, _ => 0 };
                }
                int tx = cx + dx, tz = cz + dz;
                if ((uint)tx >= (uint)SizeX || (uint)tz >= (uint)SizeZ) continue;

                int low = Fly ? -HeightCells : -DropCells;
                int high = Fly ? HeightCells : StepCells;
                for (int dy = low; dy <= high; dy++)
                {
                    int ty = cy + dy;
                    if ((uint)ty >= (uint)SizeY) continue;
                    if (!BodyFree(tx, ty, tz)) continue;
                    if (!Fly && !Standable(tx, ty, tz)) continue;
                    if (settings.AvoidCornerCutting && dx != 0 && dz != 0)
                    {
                        if (!BodyFree(cx + dx, ty, cz) || !BodyFree(cx, ty, cz + dz)) continue;
                    }
                    if (!Fly && dy > 0)
                    {
                        // Never step through a ceiling into a higher floor.
                        for (int check = 1; check <= dy; check++)
                            if (!BodyFree(cx, cy + check, cz)) { dy = high + 1; break; }
                        if (dy > high) continue;
                    }
                    int target = Index(tx, ty, tz);
                    if (_closed[target] == stamp) continue;
                    float moveCost = MathF.Sqrt(dx * dx + dz * dz) * CellSize + Math.Abs(dy) * CellSize * (dy < 0 ? 1.15f : 1.45f);
                    if (settings.WallHugPenalty > 0)
                    {
                        int blockedSides = 0;
                        if (Solid(tx - 1, ty, tz)) blockedSides++;
                        if (Solid(tx + 1, ty, tz)) blockedSides++;
                        if (Solid(tx, ty, tz - 1)) blockedSides++;
                        if (Solid(tx, ty, tz + 1)) blockedSides++;
                        if (blockedSides >= 3) moveCost += settings.WallHugPenalty * CellSize;
                    }
                    int tentative = _g[current] + Math.Max(1, (int)(moveCost * 1000));
                    if (_seen[target] != stamp || tentative < _g[target])
                    {
                        _seen[target] = stamp;
                        _g[target] = tentative;
                        _parent[target] = current;
                        float h = Heuristic(target, goal);
                        if (h < bestH) bestH = h;
                        open.Enqueue(target, tentative + h);
                    }
                }
            }
        }

        if (!found)
        {
            timer.Stop();
            string detail = visited == 0 ? "The navigation grid is empty." :
                Math.Abs(bestH) < CellSize * 4 ? "The target is blocked by a wall or has no walkable surface." :
                "No route reaches the target — check step/drop height, agent radius and sealed walls.";
            return PathfindingResult.Fail(detail, timer.Elapsed.TotalMilliseconds, visited);
        }

        var reversed = new List<int>();
        for (int at = goal; at >= 0; at = _parent[at])
        {
            reversed.Add(at);
            if (at == start) break;
            if (reversed.Count > Count + 2) break;
        }
        reversed.Reverse();
        if (reversed.Count == 0 || reversed[0] != start) reversed.Insert(0, start);

        var points = reversed.Select(Center).ToList();
        if (settings.SmoothPath && points.Count > 2)
            points = Simplify(points, settings);
        points[0] = Center(start);
        points[^1] = Center(goal);

        float length = 0;
        for (int i = 1; i < points.Count; i++) length += (points[i] - points[i - 1]).Length;
        timer.Stop();
        return new PathfindingResult
        {
            Success = true,
            Message = $"Path found: {length:0.00} m, {points.Count} waypoint(s).",
            Points = points,
            Length = length,
            Visited = visited,
            Iterations = iterations,
            CellSize = CellSize,
            Milliseconds = timer.Elapsed.TotalMilliseconds,
            StartWorld = Center(start),
            EndWorld = Center(goal),
            StartCell = start,
            GoalCell = goal
        };
    }

    private List<Vec3> Simplify(List<Vec3> points, PathfindingSettings settings)
    {
        var result = new List<Vec3> { points[0] };
        int at = 0;
        while (at < points.Count - 1)
        {
            int best = at + 1;
            for (int j = points.Count - 1; j > at + 1; j--)
            {
                if (LineClear(points[at], points[j], settings))
                {
                    best = j;
                    break;
                }
            }
            result.Add(points[best]);
            at = best;
        }
        return result;
    }

    private float Heuristic(int a, int b)
    {
        var (ax, ay, az) = Cell(a);
        var (bx, by, bz) = Cell(b);
        int dx = Math.Abs(ax - bx), dz = Math.Abs(az - bz), dy = Math.Abs(ay - by);
        float horizontal = MathF.Max(dx, dz) + (MathF.Sqrt(2f) - 1f) * MathF.Min(dx, dz);
        if (Fly)
        {
            int diag = Math.Min(dx, Math.Min(dy, dz));
            int straight = Math.Max(dx, Math.Max(dy, dz)) - diag;
            return (straight + diag * MathF.Sqrt(3f)) * CellSize;
        }
        return (horizontal + dy * 1.15f) * CellSize;
    }
}

/// <summary>Public entry points used by the studio and by Play mode.</summary>
public static class Pathfinder
{
    public static PathfindingSettings SettingsFor(GrabLevel level) => level.Pathfinding ??= new PathfindingSettings();

    public static LevelNode? FindNode(GrabLevel level, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : level.FindByEditorId(id);

    public static Vec3 FootPoint(LevelNode node)
    {
        float half = node.Kind is NodeKind.Static or NodeKind.Crumbling
            ? Math.Abs(node.WorldScale.Y) * 0.5f
            : 0.5f;
        return node.WorldPosition - new Vec3(0, half, 0);
    }

    public static PathfindingResult Find(GrabLevel level, PathfindingSettings settings,
        LevelNode? finder, LevelNode? target, NavigationGrid? grid = null, Vec3? goalOverride = null)
    {
        if (finder == null) return PathfindingResult.Fail("Choose a finder object first.");
        if (target == null) return PathfindingResult.Fail("Choose a target object first.");
        if (ReferenceEquals(finder, target)) return PathfindingResult.Fail("Finder and target must be different objects.");
        bool fly = settings.Traversal == PathfindingTraversal.Fly;
        var startFoot = fly ? finder.WorldPosition : FootPoint(finder);
        var targetFoot = goalOverride ?? (fly ? target.WorldPosition : FootPoint(target));
        if (grid != null && (!grid.Contains(startFoot) || !grid.Contains(targetFoot)))
            return PathfindingResult.Fail("Target moved outside the navigation grid; repathing with a larger grid.");
        grid ??= NavigationGrid.Build(level, settings, startFoot, targetFoot, finder);
        int radius = Math.Max(4, (int)Math.Ceiling(Math.Max(settings.AgentRadius * 2f, 3f) / grid.CellSize));
        int start = fly ? grid.FindFreeNear(startFoot, radius) : grid.FindStandableNear(startFoot, radius);
        int goal = fly ? grid.FindFreeNear(targetFoot, radius) : grid.FindStandableNear(targetFoot, radius);
        var result = grid.FindPath(start, goal, settings);
        if (!result.Success)
        {
            return new PathfindingResult
            {
                Success = false,
                Message = result.Message,
                Visited = result.Visited,
                Iterations = result.Iterations,
                CellSize = grid.CellSize,
                Milliseconds = result.Milliseconds,
                StartCell = start,
                GoalCell = goal,
                TargetWorld = target.WorldPosition,
                Debug = grid.Describe(start, goal)
            };
        }
        var navCells = new List<Vec3>();
        var watch = new List<LevelNode>();
        if (settings.ShowNavMesh)
        {
            grid.CollectStandable(startFoot, targetFoot, 10f, 2600, 2.6f, navCells);
            watch = PathfindingExporter.SelectBlockers(level, settings, finder, target);
        }
        return new PathfindingResult
        {
            Success = result.Success,
            Message = result.Message,
            Points = result.Points,
            Length = result.Length,
            Visited = result.Visited,
            Iterations = result.Iterations,
            CellSize = result.CellSize,
            Milliseconds = result.Milliseconds,
            StartWorld = start >= 0 ? grid.Center(start) : startFoot,
            EndWorld = goal >= 0 ? grid.Center(goal) : targetFoot,
            TargetWorld = target.WorldPosition,
            StartCell = start,
            GoalCell = goal,
            NavCells = navCells,
            NavCellSize = grid.CellSize,
            WatchWalls = watch
        };
    }
}

/// <summary>
/// Live pathfinding follower used by Play mode. It continuously replans toward the
/// target's current position (with optional velocity prediction), then steers along the
/// smoothed path with acceleration and turn-rate limits. The level object is moved
/// through world space so hierarchy transforms keep working.
/// </summary>
public sealed class PathfinderRuntime
{
    private readonly GrabLevel _level;
    private readonly LevelNode _finder;
    private readonly LevelNode _target;
    private readonly PathfindingSettings _settings;
    private NavigationGrid? _grid;
    private PathfindingResult _path = PathfindingResult.Fail("No path computed yet.");
    private int _waypoint;
    private float _repathTimer;
    private float _navTimer;
    private float _velocityOffset;
    private Vec3 _lastTarget;
    private Vec3 _targetVelocity;
    private Vec3 _velocity;
    private double _stuckTimer;
    private Vec3 _lastFinder;

    public PathfinderRuntime(GrabLevel level, PathfindingSettings settings, LevelNode finder, LevelNode target)
    {
        _level = level;
        _settings = settings.Clone();
        _settings.Clamp();
        _finder = finder;
        _target = target;
        _lastTarget = target.WorldPosition;
        _lastFinder = finder.WorldPosition;
        RebuildPath(force: true);
    }

    public PathfindingResult Path => _path;
    public LevelNode Finder => _finder;
    public LevelNode Target => _target;
    public bool ReachedTarget { get; private set; }
    public string Status => _path.Message;

    public void Update(float dt)
    {
        if (dt <= 0) return;
        dt = Math.Clamp(dt, 0.001f, 0.1f);
        _repathTimer -= dt;
        _navTimer -= dt;

        var targetNow = _target.WorldPosition;
        if (dt > 1e-5f)
        {
            var instant = (targetNow - _lastTarget) / dt;
            var t = Math.Clamp(dt * 12f, 0f, 1f);
            _targetVelocity = Vec3.Lerp(_targetVelocity, instant, t);
        }
        _lastTarget = targetNow;

        float moved = (_path.TargetWorld - targetNow).Length;
        bool outsideGrid = _grid != null && (!_grid.Contains(_finder.WorldPosition) || !_grid.Contains(_target.WorldPosition));
        if (outsideGrid || _repathTimer <= 0 || _path.StartCell < 0 || moved > Math.Max(0.25f, _settings.CellSize * 0.6f))
            RebuildPath(force: false);

        if (!_path.Success || _path.Points.Count == 0)
        {
            _velocity = Vec3.Zero;
            return;
        }

        var finderNow = _finder.WorldPosition;
        while (_waypoint < _path.Points.Count - 1)
        {
            var wp = _path.Points[_waypoint] + new Vec3(0, _velocityOffset, 0);
            var flat = new Vec3(wp.X - finderNow.X, 0, wp.Z - finderNow.Z);
            if (flat.Length <= _settings.WaypointTolerance &&
                Math.Abs(wp.Y - finderNow.Y) <= Math.Max(0.35f, _settings.CellSize * 1.5f))
            {
                _waypoint++;
                finderNow = _finder.WorldPosition;
            }
            else break;
        }

        if (_waypoint >= _path.Points.Count - 1)
        {
            var final = _path.Points[^1] + new Vec3(0, _velocityOffset, 0);
            if ((finderNow - final).Length <= _settings.StopDistance)
            {
                ReachedTarget = true;
                _velocity = Vec3.Zero;
                return;
            }
        }
        ReachedTarget = false;

        var aim = _path.Points[Math.Clamp(_waypoint, 0, _path.Points.Count - 1)] + new Vec3(0, _velocityOffset, 0);
        var delta = aim - finderNow;
        var desiredVelocity = delta.Length < 1e-4f ? Vec3.Zero : delta.Normalized * _settings.Speed;
        if (_settings.Acceleration > 0)
        {
            var diff = desiredVelocity - _velocity;
            float maxDelta = _settings.Acceleration * dt;
            _velocity = diff.Length <= maxDelta ? desiredVelocity : _velocity + diff.Normalized * maxDelta;
        }
        else _velocity = desiredVelocity;

        var before = _finder.WorldPosition;
        var next = before + _velocity * dt;
        SetWorldPosition(_finder, next);
        if (_settings.FaceMovement)
        {
            var face = new Vec3(_velocity.X, 0, _velocity.Z);
            if (face.Length > 0.05f)
            {
                float yaw = MathF.Atan2(face.X, face.Z) * 180f / MathF.PI;
                SetWorldYaw(_finder, yaw, dt);
            }
        }

        // If scripted motion or a moving wall blocks progress, force an immediate replan.
        if ((next - _lastFinder).Length < _settings.Speed * dt * 0.12f && _velocity.Length > 0.2f)
            _stuckTimer += dt;
        else _stuckTimer = 0;
        _lastFinder = _finder.WorldPosition;
        if (_stuckTimer > 0.8)
        {
            _stuckTimer = 0;
            _repathTimer = 0;
            _navTimer = 0;
        }
    }

    public void RebuildPath(bool force)
    {
        bool rebuildNav = force || _grid == null || _navTimer <= 0;
        var targetWorld = _target.WorldPosition;
        var predicted = targetWorld + _targetVelocity * _settings.TargetPrediction;
        bool fly = _settings.Traversal == PathfindingTraversal.Fly;
        var start = fly ? _finder.WorldPosition : Pathfinder.FootPoint(_finder);
        var goal = (fly ? _target.WorldPosition : Pathfinder.FootPoint(_target)) + (predicted - targetWorld);
        if (rebuildNav)
        {
            _grid = NavigationGrid.Build(_level, _settings, start, goal, _finder);
            _navTimer = _settings.NavRebuildInterval;
        }
        var next = Pathfinder.Find(_level, _settings, _finder, _target, _grid, goal);
        // The prediction shifts only the query goal. If it made the goal invalid, retry
        // with the real target before giving up.
        if (!next.Success && !rebuildNav)
        {
            _grid = NavigationGrid.Build(_level, _settings, start, fly ? _target.WorldPosition : Pathfinder.FootPoint(_target), _finder);
            _navTimer = _settings.NavRebuildInterval;
            next = Pathfinder.Find(_level, _settings, _finder, _target, _grid);
        }
        bool newPath = !_path.Success || _path.GoalCell != next.GoalCell || !_path.Points.SequenceEqual(next.Points);
        _path = next;
        if (next.Success && next.Points.Count > 0 && newPath)
        {
            _waypoint = 0;
            _velocityOffset = fly ? 0 : Math.Clamp(_finder.WorldPosition.Y - next.Points[0].Y, -256f, 256f);
        }
        _repathTimer = _settings.RepathInterval;
    }

    private static void SetWorldPosition(LevelNode node, Vec3 world)
    {
        if (node.Parent == null) { node.Position = world; return; }
        var p = node.Parent.WorldPosition;
        var local = node.Parent.WorldRotation.Inverse().Rotate(world - p);
        var scale = node.Parent.WorldScale;
        node.Position = new Vec3(local.X / Math.Max(1e-5f, Math.Abs(scale.X)),
            local.Y / Math.Max(1e-5f, Math.Abs(scale.Y)),
            local.Z / Math.Max(1e-5f, Math.Abs(scale.Z)));
    }

    private void SetWorldYaw(LevelNode node, float yaw, float dt)
    {
        var current = node.WorldRotation.ToEulerDegrees();
        float delta = ((yaw - current.Y + 540f) % 360f) - 180f;
        float maxTurn = _settings.TurnSpeed * dt;
        if (Math.Abs(delta) > maxTurn) yaw = current.Y + Math.Sign(delta) * maxTurn;
        var target = Quat.FromEulerDegrees(new Vec3(current.X, yaw, current.Z));
        node.Rotation = node.Parent == null ? target : node.Parent.WorldRotation.Inverse() * target;
    }
}
