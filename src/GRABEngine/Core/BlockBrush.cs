using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum BlockBrushMode
{
    None,
    Fill,
    Replace,
    Erase,
    Line,
    Box,
    Sphere
}

[Flags]
public enum BlockSymmetry
{
    None = 0,
    X = 1,
    Z = 2,
    Both = X | Z
}

public static class BlockBrushModeInfo
{
    public static string Display(BlockBrushMode mode) => mode switch
    {
        BlockBrushMode.Fill => "Fill",
        BlockBrushMode.Replace => "Replace",
        BlockBrushMode.Erase => "Erase",
        BlockBrushMode.Line => "Line",
        BlockBrushMode.Box => "Box",
        BlockBrushMode.Sphere => "Sphere",
        _ => "Brush"
    };

    public static string Hint(BlockBrushMode mode) => mode switch
    {
        BlockBrushMode.Fill => "Click a block to flood-fill its connected island with the selected material.",
        BlockBrushMode.Replace => "Click or drag over blocks to repaint their material.",
        BlockBrushMode.Erase => "Click or drag over blocks to remove them.",
        BlockBrushMode.Line => "Drag from the first block to the last to place a line.",
        BlockBrushMode.Box => "Drag a footprint. Layers controls how far it builds upward.",
        BlockBrushMode.Sphere => "Drag from the centre outward to set the radius.",
        _ => "Select a brush mode."
    };
}

/// <summary>A cell in the editor's voxel grid. Y is measured in block layers.</summary>
public readonly record struct VoxelCell(int X, int Y, int Z);

/// <summary>Result of a brush gesture, used for one undoable edit and the status bar.</summary>
public sealed class BlockBrushResult
{
    public string Description { get; init; } = "Brush";
    public int Count { get; init; }
    public bool Created { get; init; }
    public bool Truncated { get; init; }

    public string Status => Count == 0
        ? Description + " — nothing changed"
        : $"{Description} — {Count} block{(Count == 1 ? "" : "s")}" + (Truncated ? " (limit reached)" : "");
}

/// <summary>A capped list of cells produced by a brush outline.</summary>
public sealed class BrushPlan
{
    public List<VoxelCell> Cells { get; } = new();
    public bool Truncated { get; set; }
}

/// <summary>
/// Voxel-grid helpers shared by the editor brush and the self-test. Cell centres
/// are at (X * size, (Y + 0.5) * size, Z * size), so layer 0 rests on the y=0 grid
/// plane just like a cube placed at y=0.5.
/// </summary>
public static class BlockBrushMath
{
    public const int MaxCells = 4096;
    public const int MaxFillCells = 60000;
    public const int MaxFillBlocks = MaxFillCells;
    /// <summary>A single huge floor can cover more cells than one brush stroke. This
    /// guard only exists so a corrupted scale cannot lock up the editor.</summary>
    public const int MaxOccupiedCells = 250000;

    public static VoxelCell CellAt(Vec3 point, double size)
    {
        double cell = Math.Max(0.01, size);
        return new VoxelCell(
            (int)Math.Round(point.X / cell, MidpointRounding.AwayFromZero),
            (int)Math.Round((point.Y - cell * 0.5) / cell, MidpointRounding.AwayFromZero),
            (int)Math.Round(point.Z / cell, MidpointRounding.AwayFromZero));
    }

    public static Vec3 Center(VoxelCell cell, double size)
    {
        double cellSize = Math.Max(0.01, size);
        return new Vec3(
            (float)(cell.X * cellSize),
            (float)((cell.Y + 0.5) * cellSize),
            (float)(cell.Z * cellSize));
    }

    public static int Distance(VoxelCell a, VoxelCell b) => Math.Max(
        Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

    public static BrushPlan Line(VoxelCell a, VoxelCell b)
    {
        var plan = new BrushPlan();
        int dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        int steps = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
        if (steps == 0)
        {
            plan.Cells.Add(a);
            return plan;
        }
        for (int i = 0; i <= steps; i++)
        {
            if (plan.Cells.Count >= MaxCells)
            {
                plan.Truncated = true;
                break;
            }
            var cell = new VoxelCell(
                a.X + (int)Math.Round(dx * (double)i / steps),
                a.Y + (int)Math.Round(dy * (double)i / steps),
                a.Z + (int)Math.Round(dz * (double)i / steps));
            if (plan.Cells.Count == 0 || plan.Cells[^1] != cell) plan.Cells.Add(cell);
        }
        return plan;
    }

    public static BrushPlan Box(VoxelCell a, VoxelCell b, int layers, bool hollow)
    {
        var plan = new BrushPlan();
        int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
        int minZ = Math.Min(a.Z, b.Z), maxZ = Math.Max(a.Z, b.Z);
        int minY = Math.Min(a.Y, b.Y);
        int maxY = Math.Max(a.Y, b.Y) + Math.Max(0, layers - 1);
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            if (hollow && x > minX && x < maxX && y > minY && y < maxY && z > minZ && z < maxZ)
                continue;
            if (plan.Cells.Count >= MaxCells)
            {
                plan.Truncated = true;
                return plan;
            }
            plan.Cells.Add(new VoxelCell(x, y, z));
        }
        return plan;
    }

    public static BrushPlan Sphere(VoxelCell centre, int radius, bool hollow)
    {
        var plan = new BrushPlan();
        int r = Math.Clamp(radius, 1, 64);
        int inner = hollow ? Math.Max(0, r - 1) : -1;
        double outerSq = r * r + r * 0.6;
        // Hollow keeps a one-cell shell: include the outer radius but skip everything
        // inside the next radius down. Keeping the >= test used to leave only the centre.
        double innerSq = inner >= 0 ? inner * inner : -1;
        for (int y = -r; y <= r; y++)
        for (int z = -r; z <= r; z++)
        for (int x = -r; x <= r; x++)
        {
            int distanceSq = x * x + y * y + z * z;
            if (distanceSq > outerSq) continue;
            if (hollow && distanceSq < innerSq) continue;
            if (plan.Cells.Count >= MaxCells)
            {
                plan.Truncated = true;
                return plan;
            }
            plan.Cells.Add(new VoxelCell(centre.X + x, centre.Y + y, centre.Z + z));
        }
        return plan;
    }

    /// <summary>
    /// Mirrors a brush plan across the selected world axes. The pivot is snapped to
    /// the voxel grid so a wall built on one side of the pivot has an exact twin.
    /// </summary>
    public static BrushPlan Mirror(BrushPlan source, BlockSymmetry symmetry, Vec3 pivot, double cellSize)
    {
        if (symmetry == BlockSymmetry.None) return source;
        double cell = Math.Max(0.01, cellSize);
        var pivotCell = CellAt(pivot, cell);
        var result = new BrushPlan();
        var seen = new HashSet<VoxelCell>();
        void Add(VoxelCell c)
        {
            if (!seen.Add(c)) return;
            if (result.Cells.Count >= MaxCells)
            {
                result.Truncated = true;
                return;
            }
            result.Cells.Add(c);
        }
        foreach (var c in source.Cells)
        {
            Add(c);
            if (symmetry.HasFlag(BlockSymmetry.X)) Add(new VoxelCell(2 * pivotCell.X - c.X, c.Y, c.Z));
            if (symmetry.HasFlag(BlockSymmetry.Z)) Add(new VoxelCell(c.X, c.Y, 2 * pivotCell.Z - c.Z));
            if (symmetry == BlockSymmetry.Both)
                Add(new VoxelCell(2 * pivotCell.X - c.X, c.Y, 2 * pivotCell.Z - c.Z));
        }
        result.Truncated |= source.Truncated;
        return result;
    }

    /// <summary>
    /// Maps every cell covered by a solid block. Large floors therefore reserve all
    /// their cells, which stops a brush from burying new blocks inside existing geometry.
    /// </summary>
    public static Dictionary<VoxelCell, LevelNode> Occupancy(GrabLevel level, double cellSize)
    {
        var map = new Dictionary<VoxelCell, LevelNode>();
        foreach (var node in level.AllNodes())
        {
            if (node.Generated) continue;
            if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) continue;
            foreach (var cell in CellsCovered(node, cellSize)) map[cell] = node;
        }
        return map;
    }

    public static IEnumerable<VoxelCell> CellsCovered(LevelNode node, double cellSize)
    {
        var p = node.WorldPosition;
        var s = node.WorldScale;
        double cell = Math.Max(0.01, cellSize);
        // X/Z cell centres are on integer multiples of the cell size; Y centres sit
        // half a cell above the layer line. Use the inclusive centre range so a floor
        // at y=-0.5 reserves exactly layer -1 instead of bleeding into nearby layers.
        int minX = (int)Math.Ceiling((p.X - Math.Abs(s.X) * 0.5f) / cell);
        int maxX = (int)Math.Floor((p.X + Math.Abs(s.X) * 0.5f) / cell);
        int minY = (int)Math.Ceiling((p.Y - Math.Abs(s.Y) * 0.5f) / cell - 0.5);
        int maxY = (int)Math.Floor((p.Y + Math.Abs(s.Y) * 0.5f) / cell - 0.5);
        int minZ = (int)Math.Ceiling((p.Z - Math.Abs(s.Z) * 0.5f) / cell);
        int maxZ = (int)Math.Floor((p.Z + Math.Abs(s.Z) * 0.5f) / cell);
        if (minX > maxX || minY > maxY || minZ > maxZ) yield break;
        int guard = 0;
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            if (++guard > MaxOccupiedCells) yield break;
            yield return new VoxelCell(x, y, z);
        }
    }

    /// <summary>
    /// Flood-fills from a block through touching blocks of the same material. Only
    /// existing blocks are returned; the caller changes their material.
    /// </summary>
    public static List<LevelNode> ConnectedIsland(GrabLevel level, LevelNode start, double cellSize, int maxBlocks = MaxFillBlocks)
        => ConnectedIsland(level, start, cellSize, maxBlocks, out _);

    public static List<LevelNode> ConnectedIsland(GrabLevel level, LevelNode start, double cellSize, int maxBlocks, out bool truncated)
    {
        var result = new List<LevelNode>();
        truncated = false;
        if (start.Kind is not (NodeKind.Static or NodeKind.Crumbling)) return result;
        var map = Occupancy(level, cellSize);
        var startCells = CellsCovered(start, cellSize).ToList();
        if (startCells.Count == 0) return result;
        var visited = new HashSet<VoxelCell>();
        var found = new HashSet<LevelNode> { start };
        var queue = new Queue<VoxelCell>();
        foreach (var cell in startCells)
        {
            if (visited.Add(cell)) queue.Enqueue(cell);
        }
        var dirs = new[]
        {
            new VoxelCell(1, 0, 0), new VoxelCell(-1, 0, 0),
            new VoxelCell(0, 1, 0), new VoxelCell(0, -1, 0),
            new VoxelCell(0, 0, 1), new VoxelCell(0, 0, -1)
        };
        int guard = 0;
        while (queue.Count > 0 && guard++ < MaxFillCells && found.Count < maxBlocks)
        {
            var cell = queue.Dequeue();
            foreach (var dir in dirs)
            {
                var next = new VoxelCell(cell.X + dir.X, cell.Y + dir.Y, cell.Z + dir.Z);
                if (!visited.Add(next)) continue;
                if (!map.TryGetValue(next, out var node)) continue;
                if (node.Material != start.Material) continue;
                found.Add(node);
                queue.Enqueue(next);
                if (found.Count >= maxBlocks)
                {
                    truncated = true;
                    break;
                }
            }
        }
        if (guard >= MaxFillCells) truncated = true;
        result.AddRange(found);
        return result;
    }
}
