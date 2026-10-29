using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum TerrainSculptMode
{
    Add,
    Remove,
    Smooth,
    Flatten,
    Paint
}

public enum TerrainBrushVolume
{
    Sphere,
    Box,
    Cylinder
}

public sealed class TerrainSculptSettings
{
    public TerrainSculptMode Mode { get; set; } = TerrainSculptMode.Add;
    public TerrainBrushVolume Volume { get; set; } = TerrainBrushVolume.Sphere;
    public int Radius { get; set; } = 4;
    public float Strength { get; set; } = 1f;
    public int Material { get; set; } = 8;
    public Col Color { get; set; } = new(0.33f, 0.55f, 0.28f, 1f);
    public double CellSize { get; set; } = 1.0;
}

public sealed class TerrainSculptResult
{
    public int Added { get; init; }
    public int Removed { get; init; }
    public int Changed { get; init; }
    public int Columns { get; init; }
    public string Description { get; init; } = "Terrain";
    public string Summary => Count == 0
        ? Description + " — nothing changed"
        : Description + " — " + Count + " block(s) changed" + (Columns > 0 ? " across " + Columns + " column(s)" : "");

    public int Count => Added + Removed + Changed;
}

public enum TerrainFoundationMode
{
    None,
    LevelGround,
    CarveFoundation
}

public sealed class TerrainFoundationResult
{
    public int Columns { get; init; }
    public int BlocksChanged { get; init; }
    public string Description { get; init; } = "Foundation";
    public string Summary => Columns == 0
        ? Description + " — no terrain under the footprint"
        : Description + " — levelled " + Columns + " column(s), " + BlocksChanged + " block(s) changed";
}

/// <summary>
/// Column-based terrain editing. Terrain is stored as ordinary (often tall) cubes, so
/// every operation first expands the affected columns into cells, edits those cells,
/// and rebuilds only the affected runs. Everything the tool writes is still a normal
/// GRAB block and remains editable with the block brush.
/// </summary>
public static class TerrainSculptor
{
    private sealed class Column
    {
        public int X;
        public int Z;
        public readonly SortedDictionary<int, Cell> Cells = new();
        public readonly HashSet<LevelNode> Nodes = new();
        public LevelNode? Parent;
    }

    private readonly record struct Cell(int Material, Col Color);

    private static readonly Col DefaultTerrainColor = new(0.33f, 0.55f, 0.28f, 1f);

    public static TerrainSculptResult Apply(GrabLevel level, Vec3 centre, TerrainSculptSettings settings)
    {
        if (level == null) return new TerrainSculptResult { Description = "Terrain" };
        double grid = Math.Clamp(settings.CellSize, 0.1, 4.0);
        int radius = Math.Clamp(settings.Radius, 1, 16);
        var (cx, cy, cz) = CellAt(centre, grid);
        var columns = BuildColumns(level, grid);
        var brush = BrushCells(cx, cy, cz, radius, settings.Volume);
        var brushSet = brush.ToHashSet();
        var rebuild = new HashSet<(int X, int Z)>();
        int added = 0, removed = 0, changed = 0;

        if (settings.Mode == TerrainSculptMode.Add)
        {
            foreach (var cell in brush)
            {
                if (!columns.TryGetValue((cell.X, cell.Z), out var column))
                {
                    column = new Column { X = cell.X, Z = cell.Z };
                    columns[columnKey(cell.X, cell.Z)] = column;
                }
                if (column.Cells.ContainsKey(cell.Y)) continue;
                column.Cells[cell.Y] = new Cell(settings.Material, settings.Color);
                column.Parent = column.Parent ?? FindOrCreateTerrainGroup(level);
                added++;
                rebuild.Add((cell.X, cell.Z));
            }
            foreach (var key in rebuild)
                if (columns.TryGetValue(key, out var col)) EnsureParent(level, col);
            Rebuild(level, columns, rebuild, grid);
            return Result("Add terrain", added, removed, changed, rebuild.Count, level);
        }

        // Removal, smoothing, flattening and painting all rebuild the columns that
        // contain terrain inside the brush.
        foreach (var cell in brush)
        {
            if (!columns.TryGetValue((cell.X, cell.Z), out var column)) continue;
            if (!column.Cells.ContainsKey(cell.Y)) continue;
            rebuild.Add((cell.X, cell.Z));
        }

        if (settings.Mode == TerrainSculptMode.Remove)
        {
            foreach (var key in rebuild)
            {
                var column = columns[key];
                foreach (var y in column.Cells.Keys.Where(y => brushSet.Contains((column.X, y, column.Z))).ToList())
                {
                    column.Cells.Remove(y);
                    removed++;
                }
            }
        }
        else if (settings.Mode == TerrainSculptMode.Paint)
        {
            foreach (var key in rebuild)
            {
                var column = columns[key];
                foreach (var y in column.Cells.Keys.Where(y => brushSet.Contains((column.X, y, column.Z))).ToList())
                {
                    var old = column.Cells[y];
                    if (old.Material == settings.Material && old.Color.Hex == settings.Color.Hex) continue;
                    column.Cells[y] = new Cell(settings.Material, settings.Color);
                    changed++;
                }
            }
        }
        else if (settings.Mode is TerrainSculptMode.Smooth or TerrainSculptMode.Flatten)
        {
            // Smoothing follows the surface instead of carving the inside of a hill.
            if (settings.Mode == TerrainSculptMode.Flatten)
            {
                foreach (var key in rebuild)
                {
                    var column = columns[key];
                    if (column.Cells.Count == 0) continue;
                    int target = cy;
                    changed += SetColumnTop(column, target);
                }
            }
            else
            {
                var source = rebuild
                    .Select(key => (Key: key, Top: columns[key].Cells.Count == 0 ? int.MinValue : columns[key].Cells.Keys.Max()))
                    .Where(x => x.Top != int.MinValue)
                    .ToList();
                float strength = Math.Clamp(settings.Strength, 0.05f, 1f);
                var updates = new List<(Column Column, int Target)>();
                foreach (var (key, top) in source)
                {
                    var column = columns[key];
                    var neighbours = source.Where(other =>
                    {
                        int dx = other.Key.X - key.X;
                        int dz = other.Key.Z - key.Z;
                        return dx * dx + dz * dz <= radius * radius;
                    }).ToList();
                    if (neighbours.Count == 0) continue;
                    int average = (int)Math.Round(neighbours.Average(n => n.Top));
                    int target = (int)Math.Round(top + (average - top) * strength);
                    if (target == top) continue;
                    updates.Add((column, target));
                }
                foreach (var update in updates) changed += SetColumnTop(update.Column, update.Target);
            }
        }

        // Expand the rebuild set to every column touched by a node that crosses into
        // the brush. This prevents a wide terrain block from being partly rebuilt.
        ExpandForSharedNodes(columns, rebuild);
        foreach (var key in rebuild)
            if (columns.TryGetValue(key, out var col)) EnsureParent(level, col);
        Rebuild(level, columns, rebuild, grid);
        string description = settings.Mode switch
        {
            TerrainSculptMode.Remove => "Subtract terrain",
            TerrainSculptMode.Paint => "Paint terrain",
            TerrainSculptMode.Smooth => "Smooth terrain",
            TerrainSculptMode.Flatten => "Flatten terrain",
            _ => "Terrain"
        };
        return Result(description, added, removed, changed, rebuild.Count, level);
    }

    /// <summary>
    /// Prepares the terrain under a building footprint. LevelGround cuts or fills each
    /// column to the structure's base; CarveFoundation removes material below the base
    /// and leaves a solid floor so the structure has a clean foundation pit.
    /// </summary>
    public static TerrainFoundationResult PrepareFoundation(GrabLevel level,
        IReadOnlyList<LevelNode> placed, TerrainFoundationMode mode, float depth = 4f,
        double cellSize = 1.0)
    {
        if (level == null || placed == null || placed.Count == 0 || mode == TerrainFoundationMode.None)
            return new TerrainFoundationResult { Description = "Foundation" };
        double grid = Math.Clamp(cellSize, 0.1, 4.0);
        var (min, max) = Bounds(placed);
        var centre = (min + max) * 0.5f;
        var columns = BuildColumns(level, grid);
        int minX = (int)Math.Floor(min.X / grid - 0.5);
        int maxX = (int)Math.Ceiling(max.X / grid - 0.5);
        int minZ = (int)Math.Floor(min.Z / grid - 0.5);
        int maxZ = (int)Math.Ceiling(max.Z / grid - 0.5);
        int baseTop = (int)Math.Round(min.Y / grid) - 1;
        int targetTop = mode == TerrainFoundationMode.LevelGround
            ? baseTop
            : baseTop - Math.Max(1, (int)Math.Round(depth / grid));
        var affected = new HashSet<(int X, int Z)>();
        int changed = 0;
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                var key = (x, z);
                if (!columns.TryGetValue(key, out var column))
                {
                    if (mode != TerrainFoundationMode.LevelGround) continue;
                    column = new Column { X = x, Z = z };
                    columns[key] = column;
                }
                affected.Add(key);
                int existingTop = column.Cells.Count == 0 ? int.MinValue : column.Cells.Keys.Max();
                if (mode == TerrainFoundationMode.LevelGround)
                {
                    changed += SetColumnTop(column, targetTop);
                    if (column.Cells.Count == 0)
                    {
                        // A completely empty column under the footprint still needs a
                        // floor; otherwise the structure appears to float over a hole.
                        for (int y = targetTop - 1; y <= targetTop; y++)
                            column.Cells[y] = new Cell(8, DefaultTerrainColor);
                        changed += 2;
                    }
                }
                else
                {
                    int cut = Math.Max(targetTop, existingTop);
                    foreach (var y in column.Cells.Keys.Where(y => y > targetTop).ToList())
                    {
                        column.Cells.Remove(y);
                        changed++;
                    }
                    // Leave a one-block foundation floor so the pit is walkable.
                    if (!column.Cells.ContainsKey(targetTop))
                    {
                        column.Cells[targetTop] = new Cell(8, DefaultTerrainColor);
                        changed++;
                    }
                }
                column.Parent ??= FindOrCreateTerrainGroup(level);
            }
        }

        ExpandForSharedNodes(columns, affected);
        foreach (var key in affected)
            if (columns.TryGetValue(key, out var col)) EnsureParent(level, col);
        Rebuild(level, columns, affected, grid);
        return new TerrainFoundationResult
        {
            Columns = affected.Count,
            BlocksChanged = changed,
            Description = mode == TerrainFoundationMode.LevelGround ? "Levelled ground" : "Carved foundation"
        };
    }

    private static TerrainSculptResult Result(string description, int added, int removed, int changed,
        int columns, GrabLevel level)
    {
        if (added + removed + changed > 0) level.IsDirty = true;
        return new TerrainSculptResult
        {
            Description = description,
            Added = added,
            Removed = removed,
            Changed = changed,
            Columns = columns
        };
    }

    private static (int X, int Y, int Z) CellAt(Vec3 point, double grid)
    {
        var cell = BlockBrushMath.CellAt(point, grid);
        return (cell.X, cell.Y, cell.Z);
    }

    private static (int X, int Z) columnKey(int x, int z) => (x, z);

    private static IEnumerable<(int X, int Y, int Z)> BrushCells(int cx, int cy, int cz, int radius, TerrainBrushVolume volume)
    {
        int r = Math.Max(1, radius);
        int yRadius = volume == TerrainBrushVolume.Cylinder ? r : r;
        for (int y = -yRadius; y <= yRadius; y++)
        {
            for (int z = -r; z <= r; z++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (volume == TerrainBrushVolume.Sphere)
                    {
                        if (x * x + y * y + z * z > r * r + r * 0.6) continue;
                    }
                    else if (volume == TerrainBrushVolume.Cylinder)
                    {
                        if (x * x + z * z > r * r + r * 0.6) continue;
                    }
                    yield return (cx + x, cy + y, cz + z);
                }
            }
        }
    }

    private static Dictionary<(int X, int Z), Column> BuildColumns(GrabLevel level, double grid)
    {
        var columns = new Dictionary<(int X, int Z), Column>();
        foreach (var node in level.AllNodes())
        {
            if (!IsTerrainBlock(node)) continue;
            var scale = node.WorldScale;
            if (Math.Abs(node.WorldRotation.X) > 0.0005f || Math.Abs(node.WorldRotation.Y) > 0.0005f ||
                Math.Abs(node.WorldRotation.Z) > 0.0005f) continue;
            int cellsX = Math.Max(1, (int)Math.Round(Math.Abs(scale.X) / grid));
            int cellsY = Math.Max(1, (int)Math.Round(Math.Abs(scale.Y) / grid));
            int cellsZ = Math.Max(1, (int)Math.Round(Math.Abs(scale.Z) / grid));
            var centre = node.WorldPosition;
            int centreX = (int)Math.Round(centre.X / grid);
            int centreZ = (int)Math.Round(centre.Z / grid);
            int centreY = (int)Math.Round((centre.Y - grid * 0.5) / grid);
            int minX = centreX - (cellsX - 1) / 2;
            int minY = centreY - (cellsY - 1) / 2;
            int minZ = centreZ - (cellsZ - 1) / 2;
            for (int x = 0; x < cellsX; x++)
            {
                for (int z = 0; z < cellsZ; z++)
                {
                    var key = (minX + x, minZ + z);
                    if (!columns.TryGetValue(key, out var column))
                    {
                        column = new Column { X = key.Item1, Z = key.Item2 };
                        columns[key] = column;
                    }
                    column.Nodes.Add(node);
                    column.Parent ??= node.Parent;
                    for (int y = 0; y < cellsY; y++)
                        column.Cells[minY + y] = new Cell(node.Material, node.Color1);
                }
            }
        }
        return columns;
    }

    private static void ExpandForSharedNodes(Dictionary<(int X, int Z), Column> columns, HashSet<(int X, int Z)> rebuild)
    {
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 8)
        {
            changed = false;
            var nodes = new HashSet<LevelNode>();
            foreach (var key in rebuild)
                if (columns.TryGetValue(key, out var column))
                    foreach (var node in column.Nodes) nodes.Add(node);
            foreach (var column in columns.Values.ToList())
            {
                if (!column.Nodes.Overlaps(nodes)) continue;
                if (rebuild.Add((column.X, column.Z))) changed = true;
            }
        }
    }

    private static int SetColumnTop(Column column, int targetTop)
    {
        if (column.Cells.Count == 0) return 0;
        int top = column.Cells.Keys.Max();
        int changed = 0;
        if (targetTop < top)
        {
            foreach (var y in column.Cells.Keys.Where(y => y > targetTop).ToList())
            {
                column.Cells.Remove(y);
                changed++;
            }
        }
        else if (targetTop > top)
        {
            var sample = column.Cells[top];
            for (int y = top + 1; y <= targetTop; y++)
            {
                column.Cells[y] = sample;
                changed++;
            }
        }
        return changed;
    }

    private static void EnsureParent(GrabLevel level, Column column)
    {
        if (column.Parent != null && level.AllNodes().Contains(column.Parent)) return;
        column.Parent = FindOrCreateTerrainGroup(level);
    }

    private static LevelNode FindOrCreateTerrainGroup(GrabLevel level)
    {
        var group = level.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Group &&
            string.Equals(n.EditorLabel, "terrain", StringComparison.OrdinalIgnoreCase));
        if (group != null) return group;
        group = new LevelNode
        {
            Kind = NodeKind.Group,
            Name = "Terrain",
            EditorLabel = "terrain",
            Position = Vec3.Zero,
            Scale = Vec3.One,
            LayerId = level.ActiveLayerId,
            Generated = true
        };
        level.AddNode(group);
        return group;
    }

    private static void Rebuild(GrabLevel level, Dictionary<(int X, int Z), Column> columns,
        HashSet<(int X, int Z)> keys, double grid)
    {
        var remove = new HashSet<LevelNode>();
        foreach (var key in keys)
            if (columns.TryGetValue(key, out var column))
                remove.UnionWith(column.Nodes);
        foreach (var node in remove) level.RemoveNode(node);

        foreach (var key in keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            if (!columns.TryGetValue(key, out var column)) continue;
            var parent = column.Parent ?? FindOrCreateTerrainGroup(level);
            if (!column.Cells.Any()) continue;
            var ordered = column.Cells.OrderBy(kv => kv.Key).ToList();
            int runStart = ordered[0].Key;
            Cell runCell = ordered[0].Value;
            int previous = runStart;
            void Flush(int start, int end, Cell cell)
            {
                if (end < start) return;
                var node = new LevelNode
                {
                    Kind = NodeKind.Static,
                    Shape = ShapeInfo.Cube,
                    Material = cell.Material,
                    Color1 = cell.Color,
                    Position = new Vec3(
                        (float)(column.X * grid),
                        (float)((start + (end - start + 1) * 0.5) * grid),
                        (float)(column.Z * grid)),
                    Scale = new Vec3((float)grid, (float)((end - start + 1) * grid), (float)grid),
                    LayerId = parent.LayerId,
                    EditorLabel = "terrain"
                };
                parent.Children.Add(node);
                node.RebuildParents(parent);
            }
            foreach (var (y, cell) in ordered.Skip(1))
            {
                bool continues = y == previous + 1 && cell.Material == runCell.Material && cell.Color.Hex == runCell.Color.Hex;
                if (continues)
                {
                    previous = y;
                    continue;
                }
                Flush(runStart, previous, runCell);
                runStart = y;
                previous = y;
                runCell = cell;
            }
            Flush(runStart, previous, runCell);
        }
        level.RebuildParents();
        level.EnsureNames();
        if (keys.Count > 0) level.IsDirty = true;
    }

    private static bool IsTerrainBlock(LevelNode node)
    {
        if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) return false;
        if (string.Equals(node.EditorLabel, "terrain", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.EditorLabel, "terrain-water", StringComparison.OrdinalIgnoreCase)) return true;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (string.Equals(parent.EditorLabel, "terrain", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static (Vec3 Min, Vec3 Max) Bounds(IReadOnlyList<LevelNode> nodes)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var node in nodes)
        {
            var (centre, size) = GrabEngine.Rendering.SceneBuilder.BoundsOf(node);
            minX = Math.Min(minX, centre.X - Math.Abs(size.X) * 0.5);
            minY = Math.Min(minY, centre.Y - Math.Abs(size.Y) * 0.5);
            minZ = Math.Min(minZ, centre.Z - Math.Abs(size.Z) * 0.5);
            maxX = Math.Max(maxX, centre.X + Math.Abs(size.X) * 0.5);
            maxY = Math.Max(maxY, centre.Y + Math.Abs(size.Y) * 0.5);
            maxZ = Math.Max(maxZ, centre.Z + Math.Abs(size.Z) * 0.5);
        }
        return (new Vec3((float)minX, (float)minY, (float)minZ),
            new Vec3((float)maxX, (float)maxY, (float)maxZ));
    }
}
