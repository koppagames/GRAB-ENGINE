using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public sealed class SensorRigResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public LevelNode? Group { get; init; }
    public List<LevelNode> Nodes { get; } = new();
    public int Count => Nodes.Count;
}

/// <summary>
/// Places the trigger markers a baked agent uses for sensing.
///
/// GRAB's trigger runtime only reports player parts (hands, head, feet, grapple, held
/// block), so triggers cannot measure walls themselves. These markers are the authoring
/// rig: the code exporter reads their real world positions when it bakes and compiles
/// those points into the generated program. Move or delete a marker, bake again, and the
/// agent's sensing model changes with it.
///
/// Whisker rig: a ring (or two) of probes around the finder, used for 8-way collision
/// sensing instead of a single forward probe.
/// NavMesh rig: a surface of flat trigger tiles over every walkable cell reachable from
/// the finder (BFS), not just the current route, so a moved target still has ground
/// covered around it. Detail controls tile spacing.
/// </summary>
public static class PathSensorRig
{
    public const string WhiskerLabel = "rig:whisker";
    public const string NavMeshLabel = "rig:navmesh";
    public const string GroupLabel = "rig:group";
    public const string WhiskerGroupName = "Sensor Whiskers";
    public const string NavMeshGroupName = "NavMesh";

    public static bool IsRigNode(LevelNode node) =>
        node.EditorLabel is WhiskerLabel or NavMeshLabel or GroupLabel;

    /// <summary>The marker label the exporter looks for in each sensor mode.</summary>
    public static string LabelFor(PathSensorMode mode) => mode switch
    {
        PathSensorMode.Whiskers => WhiskerLabel,
        PathSensorMode.NavMesh => NavMeshLabel,
        _ => ""
    };

    /// <summary>Removes every previous sensor rig from the level.</summary>
    public static void Clear(GrabLevel level)
    {
        foreach (var node in level.Nodes.Where(IsRigNode).ToList())
            level.RemoveNode(node);
        foreach (var node in level.AllNodes().Where(IsRigNode).ToList())
            level.RemoveNode(node);
    }

    public static SensorRigResult Build(GrabLevel level, PathfindingSettings settings,
        LevelNode? finder, LevelNode? target, PathfindingResult? path)
    {
        settings.Clamp();
        if (finder == null)
            return new SensorRigResult { Success = false, Message = "Choose a finder object first." };
        // Live wall probing has no rig; switching back to it must not delete a rig the
        // user may want to keep for the other two modes.
        if (settings.SensorMode == PathSensorMode.LiveWalls)
            return new SensorRigResult
            {
                Success = true,
                Message = "Live wall probing does not need a rig: the baked agent reads the watched wall objects directly. Switch the sensor mode to place a rig."
            };
        Clear(level);
        return settings.SensorMode == PathSensorMode.Whiskers
            ? BuildWhiskers(level, settings, finder)
            : BuildNavMesh(level, settings, finder, target);
    }

    private static SensorRigResult BuildWhiskers(GrabLevel level, PathfindingSettings settings, LevelNode finder)
    {
        var foot = Pathfinder.FootPoint(finder);
        int directions = settings.WhiskerDirections;
        int rings = settings.WhiskerRadius > 2.6f ? 2 : 1;
        float outer = settings.WhiskerRadius;
        float inner = MathF.Max(0.35f, outer * 0.5f);
        var group = NewGroup(level, WhiskerGroupName);
        string distances = rings == 1 ? $"{outer:0.##} m" : $"{inner:0.##} m and {outer:0.##} m";
        var result = new SensorRigResult
        {
            Success = true,
            Group = group,
            Message = $"Whisker rig: {directions * rings} probe trigger(s) around '{finder.DisplayName}' at {distances}."
        };
        var index = 1;
        for (int ring = 0; ring < rings; ring++)
        {
            float radius = rings == 1 ? outer : (ring == 0 ? inner : outer);
            for (int i = 0; i < directions; i++)
            {
                float angle = MathF.PI * 2f * i / directions;
                var node = NewTrigger(group, "W" + index, WhiskerLabel,
                    new Vec3(foot.X + MathF.Cos(angle) * radius, foot.Y + 0.35f, foot.Z + MathF.Sin(angle) * radius),
                    new Vec3(0.3f, 0.3f, 0.3f));
                result.Nodes.Add(node);
                index++;
            }
        }
        if (result.Nodes.Count == 0)
            return new SensorRigResult { Success = false, Message = "The whisker ring produced no probes." };
        level.EnsureNames();
        level.IsDirty = true;
        return result;
    }

    private static SensorRigResult BuildNavMesh(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode? target)
    {
        var startFoot = Pathfinder.FootPoint(finder);
        var goalFoot = target != null ? Pathfinder.FootPoint(target) : startFoot;
        var grid = NavigationGrid.Build(level, settings, startFoot, goalFoot, finder);
        var surface = WalkableSurface(grid, startFoot, settings.NavMeshArea);
        if (surface.Count == 0)
            return new SensorRigResult
            {
                Success = false,
                Message = "No walkable ground found around the finder. Check that it is standing on a floor and that the agent radius is small enough to fit."
            };

        float spacing = MathF.Max(0.35f, settings.NavMeshDetail);
        var picked = SampleSurface(surface, spacing, settings.NavMeshMaxTriggers, out float usedSpacing);
        if (picked.Count == 0)
            return new SensorRigResult { Success = false, Message = "The navmesh surface could not be sampled." };

        var group = NewGroup(level, NavMeshGroupName);
        float tile = MathF.Max(0.2f, usedSpacing * 0.72f);
        var placed = new List<LevelNode>();
        for (int i = 0; i < picked.Count; i++)
        {
            var center = picked[i];
            placed.Add(NewTrigger(group, "NV" + (i + 1), NavMeshLabel,
                new Vec3(center.X, center.Y + 0.06f, center.Z),
                new Vec3(tile, 0.08f, tile)));
        }
        level.EnsureNames();
        level.IsDirty = true;
        string resample = usedSpacing > spacing + 0.01f ? $" (spacing widened to {usedSpacing:0.##} m for the trigger cap)" : "";
        var result = new SensorRigResult
        {
            Success = true,
            Group = group,
            Message = $"NavMesh rig: {placed.Count} trigger tile(s) covering every walkable cell within {settings.NavMeshArea:0.#} m of the finder, at {usedSpacing:0.##} m detail{resample}."
        };
        result.Nodes.AddRange(placed);
        return result;
    }

    /// <summary>
    /// BFS over standable cells from the finder, honouring the agent's step-up and
    /// drop-down limits, so the surface covers the ground the agent can actually walk on
    /// (including every room the route did not happen to cross).
    /// </summary>
    public static List<Vec3> WalkableSurface(NavigationGrid grid, Vec3 origin, float maxDistance)
    {
        var cells = new List<Vec3>();
        int start = grid.FindStandableNear(origin, 5);
        if (start < 0) return cells;
        var visited = new bool[grid.Count];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        float maxSq = MathF.Max(1f, maxDistance) * MathF.Max(1f, maxDistance);
        float minY = origin.Y - MathF.Max(2f, maxDistance * 0.0f);
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            var (x, y, z) = grid.Cell(index);
            var center = grid.Center(x, y, z);
            float ox = center.X - origin.X, oz = center.Z - origin.Z;
            if (ox * ox + oz * oz > maxSq) continue;
            if (center.Y < minY - 12f) continue;
            cells.Add(center);
            for (int dir = 0; dir < 4; dir++)
            {
                int nx = x + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                int nz = z + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                if ((uint)nx >= (uint)grid.SizeX || (uint)nz >= (uint)grid.SizeZ) continue;
                for (int dy = -grid.DropCells; dy <= grid.StepCells; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= grid.SizeY) continue;
                    if (!grid.Standable(nx, ny, nz)) continue;
                    int next = grid.Index(nx, ny, nz);
                    if (visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
        }
        cells.Sort((a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Z.CompareTo(b.Z);
        });
        return cells;
    }

    /// <summary>
    /// Picks one tile per lattice cell of the requested size, widening the spacing until
    /// the result fits the trigger cap instead of dropping a random half of the surface.
    /// </summary>
    public static List<Vec3> SampleSurface(List<Vec3> cells, float spacing, int maxTriggers, out float usedSpacing)
    {
        usedSpacing = MathF.Max(0.25f, spacing);
        int max = Math.Max(8, maxTriggers);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var taken = new HashSet<(long, long, long)>();
            var picked = new List<Vec3>();
            foreach (var c in cells)
            {
                var key = ((long)MathF.Round(c.X / usedSpacing),
                    (long)MathF.Round(c.Y / 0.5f),
                    (long)MathF.Round(c.Z / usedSpacing));
                if (!taken.Add(key)) continue;
                picked.Add(c);
                if (picked.Count > max * 3) break;
            }
            if (picked.Count <= max) return picked;
            usedSpacing *= 1.28f;
        }
        return new List<Vec3>();
    }

    private static LevelNode NewGroup(GrabLevel level, string name)
    {
        var group = new LevelNode
        {
            Kind = NodeKind.Group,
            Name = name,
            EditorLabel = GroupLabel,
            Position = Vec3.Zero,
            Scale = Vec3.One
        };
        level.AddNode(group);
        return group;
    }

    private static LevelNode NewTrigger(LevelNode group, string name, string label, Vec3 position, Vec3 scale)
    {
        var node = new LevelNode
        {
            Kind = NodeKind.Trigger,
            Shape = ShapeInfo.Cube,
            Name = name,
            EditorLabel = label,
            Position = position,
            Scale = scale,
            IsPassable = true,
            LayerId = group.LayerId
        };
        group.Children.Add(node);
        node.RebuildParents(group);
        return node;
    }

    /// <summary>Points every <paramref name="step"/> metres along a polyline.</summary>
    public static IEnumerable<Vec3> SampleAlong(IReadOnlyList<Vec3> points, float step)
    {
        step = MathF.Max(0.05f, step);
        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            float length = (b - a).Length;
            if (length < 1e-4f) continue;
            int count = Math.Max(1, (int)MathF.Ceiling(length / step));
            for (int k = 0; k < count; k++)
                yield return Vec3.Lerp(a, b, k / (float)count);
        }
        if (points.Count > 0) yield return points[^1];
    }

    /// <summary>
    /// Orders markers along a route by arc-length projection, drops markers that are not
    /// on the route corridor, and thins them so the baked chain has one waypoint per
    /// <paramref name="spacing"/> metres. With a ground-wide surface this is what turns
    /// hundreds of tiles into the chain the exporter can compile.
    /// </summary>
    public static List<LevelNode> OrderAlongPath(IReadOnlyList<LevelNode> nodes, IReadOnlyList<Vec3> points,
        float spacing, float corridor = 3f)
    {
        if (nodes.Count == 0 || points.Count < 2) return new List<LevelNode>();
        var cumulative = new float[points.Count];
        for (int i = 1; i < points.Count; i++)
            cumulative[i] = cumulative[i - 1] + (points[i] - points[i - 1]).Length;

        float limit = MathF.Max(2.5f, corridor);
        var scored = new List<(float T, LevelNode Node)>();
        foreach (var node in nodes)
        {
            var p = node.WorldPosition;
            float bestDistance = float.MaxValue;
            float bestT = 0;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var ab = b - a;
                float lenSq = ab.X * ab.X + ab.Z * ab.Z;
                if (lenSq < 1e-6f) continue;
                float t = Math.Clamp(((p.X - a.X) * ab.X + (p.Z - a.Z) * ab.Z) / lenSq, 0f, 1f);
                var closest = a + ab * t;
                float dx = p.X - closest.X;
                float dz = p.Z - closest.Z;
                float d = dx * dx + dz * dz;
                if (d >= bestDistance) continue;
                bestDistance = d;
                bestT = cumulative[i] + (b - a).Length * t;
            }
            if (bestDistance > limit * limit) continue;
            scored.Add((bestT, node));
        }
        scored.Sort((a, b) => a.T.CompareTo(b.T));
        var chain = new List<LevelNode>();
        float lastT = float.NegativeInfinity;
        foreach (var (t, node) in scored)
        {
            if (chain.Count > 0 && t - lastT < MathF.Max(0.25f, spacing)) continue;
            chain.Add(node);
            lastT = t;
        }
        // Always keep the trailing marker so the chain ends where the target is.
        if (scored.Count > 0 && chain.Count > 0 && scored[^1].Node != chain[^1])
            chain.Add(scored[^1].Node);
        return chain;
    }
}
