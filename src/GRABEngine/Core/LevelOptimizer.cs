using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public sealed class LevelBudget
{
    public int Objects { get; init; }
    public int Blocks { get; init; }
    public int Groups { get; init; }
    public int PhysicsGroups { get; init; }
    public int TransparentBlocks { get; init; }
    public int Lights { get; init; }
    public int Triggers { get; init; }
    public int Signs { get; init; }
    public int Sounds { get; init; }
    public int ParticleEmitters { get; init; }
    public int CodeBlocks { get; init; }
    public int Gasms { get; init; }
    public int Instructions { get; init; }
    public int Connections { get; init; }
    public int SpawnPoints { get; init; }
    public int AnimationFrames { get; init; }
    public IReadOnlyList<(LevelNode? Node, DiagnosticSeverity Severity, string Message)> Issues { get; init; } =
        Array.Empty<(LevelNode?, DiagnosticSeverity, string)>();

    public int WarningCount => Issues.Count(i => i.Severity == DiagnosticSeverity.Warning);
    public int ErrorCount => Issues.Count(i => i.Severity == DiagnosticSeverity.Error);
}

public sealed class LevelOptimizeOptions
{
    public bool MergeAdjacentCubes { get; set; } = true;
    /// <summary>Merge across different block heights and sizes on a shared grid. This is
    /// what turns a terrain column stack into one larger block.</summary>
    public bool MergeMixedSizes { get; set; } = true;
    public double MixedSizeGrid { get; set; } = 1.0;
    public bool CullHiddenBlocks { get; set; } = true;
    public bool RemoveDuplicateBlocks { get; set; } = true;
    public bool RemoveDegenerateBlocks { get; set; } = true;
    public bool RemoveEmptyGroups { get; set; }
    public bool ProtectPhysics { get; set; } = true;
    public bool ProtectAnimated { get; set; } = true;
    public bool ProtectLocked { get; set; } = true;
}

public sealed class LevelOptimizeResult
{
    public int BeforeObjects { get; init; }
    public int AfterObjects { get; init; }
    public int MergedBlocks { get; init; }
    public int CulledBlocks { get; init; }
    public int DuplicateBlocks { get; init; }
    public int DegenerateBlocks { get; init; }
    public int EmptyGroups { get; init; }
    public string Summary => $"{BeforeObjects} → {AfterObjects} objects  ·  merged {MergedBlocks}  ·  removed {CulledBlocks} hidden, {DuplicateBlocks} duplicate(s), {DegenerateBlocks} zero-size, {EmptyGroups} empty group(s)";
}

/// <summary>
/// Conservative level geometry optimisation. Only axis-aligned, identical static cubes are
/// merged; anything with animation, scripts, a physics group or non-uniform scale is left
/// alone. Hidden-block culling also skips physics structures, so the game's collision does
/// not change.
/// </summary>
public static class LevelOptimizer
{
    public static LevelBudget Analyze(GrabLevel level)
    {
        level.RebuildParents();
        var nodes = level.AllNodes().ToList();
        var issues = GrabLimits.ValidateLevel(level);
        var counted = new HashSet<GasmProgram>();
        int instructions = 0, connections = 0;
        foreach (var n in nodes.Where(n => n.Kind == NodeKind.GASM))
        {
            if (n.Compiled != null)
            {
                void Count(GasmCompileResult result)
                {
                    if (counted.Add(result.Program)) instructions += result.Program.Instructions.Count;
                    foreach (var c in result.Continuations) Count(c);
                }
                Count(n.Compiled);
                connections += n.Compiled.Connections.Count;
            }
            else if (n.ImportedProgram != null && counted.Add(n.ImportedProgram))
            {
                instructions += n.ImportedProgram.Instructions.Count;
                connections += n.ImportedConnections.Count;
            }
        }
        return new LevelBudget
        {
            Objects = nodes.Count,
            Blocks = nodes.Count(n => n.Kind is NodeKind.Static or NodeKind.Crumbling),
            Groups = nodes.Count(n => n.Kind == NodeKind.Group),
            PhysicsGroups = nodes.Count(n => n.Kind == NodeKind.Group && (n.PhysicsObject || n.LocalPhysicsObject)),
            TransparentBlocks = nodes.Count(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && n.IsTransparent),
            Lights = nodes.Count(n => n.Kind == NodeKind.Light),
            Triggers = nodes.Count(n => n.Kind == NodeKind.Trigger),
            Signs = nodes.Count(n => n.Kind == NodeKind.Sign),
            Sounds = nodes.Count(n => n.Kind == NodeKind.Sound),
            ParticleEmitters = nodes.Count(n => n.Kind == NodeKind.ParticleEmitter),
            CodeBlocks = nodes.Count(n => n.Kind == NodeKind.GASM),
            Gasms = nodes.Count(n => n.Kind == NodeKind.GASM),
            Instructions = instructions,
            Connections = connections,
            SpawnPoints = nodes.Count(n => n.Kind == NodeKind.Start),
            AnimationFrames = nodes.Sum(n => n.Animations.Sum(a => a.Frames.Count)),
            Issues = issues
        };
    }

    public static LevelOptimizeResult Optimize(GrabLevel level, bool mergeCubes, bool cullHidden,
        bool protectPhysics = true)
        => Optimize(level, new LevelOptimizeOptions
        {
            MergeAdjacentCubes = mergeCubes,
            MergeMixedSizes = false,
            CullHiddenBlocks = cullHidden,
            ProtectPhysics = protectPhysics,
            RemoveDuplicateBlocks = false,
            RemoveEmptyGroups = false
        });

    public static LevelOptimizeResult Optimize(GrabLevel level, LevelOptimizeOptions options)
    {
        options ??= new LevelOptimizeOptions();
        int before = level.AllNodes().Count();
        int merged = 0, culled = 0, duplicates = 0, degenerate = 0, emptyGroups = 0;
        level.RebuildParents();
        var parents = new List<(LevelNode? Parent, List<LevelNode> List)>
        {
            (null, level.Nodes)
        };
        foreach (var group in level.AllNodes().Where(n => n.Kind == NodeKind.Group))
            parents.Add((group, group.Children));

        // Remove invisible zero-size leftovers first; they can never contribute shape
        // and they otherwise poison duplicate/merge grouping keys.
        if (options.RemoveDegenerateBlocks)
        {
            foreach (var (_, list) in parents)
                degenerate += RemoveDegenerate(list, options);
        }

        // Exact duplicates are removed before merging so the grid pass never has to
        // deal with two blocks occupying the same cells.
        if (options.RemoveDuplicateBlocks)
        {
            foreach (var (parent, list) in parents)
                duplicates += RemoveDuplicates(parent, list, options);
        }

        // Merge first. Mixed-size merging rebuilds one appearance group at a time, so
        // a 1×4 column and a 1×1 cap can become a single clean block.
        if (options.MergeMixedSizes)
        {
            foreach (var (parent, list) in parents)
                merged += MergeMixedList(level, parent, list, options);
        }
        else if (options.MergeAdjacentCubes)
        {
            foreach (var (parent, list) in parents)
                merged += MergeList(level, parent, list, options.ProtectPhysics);
        }

        // Merging first lets a solid region collapse into one large block. Culling after
        // the merge removes remaining one-cell shells that are completely enclosed.
        if (options.CullHiddenBlocks)
        {
            foreach (var (parent, list) in parents)
                culled += CullList(parent, list, options);
        }

        if (options.RemoveEmptyGroups)
        {
            foreach (var group in level.AllNodes().Where(n => n.Kind == NodeKind.Group).ToList())
            {
                if (group.Children.Count != 0 || group.Animations.Count > 0 || group.Compiled != null ||
                    group.ImportedProgram != null || group.PhysicsObject || group.LocalPhysicsObject ||
                    !string.IsNullOrWhiteSpace(group.ComponentHostId) || group.IsLocked && options.ProtectLocked)
                    continue;
                level.RemoveNode(group);
                emptyGroups++;
            }
        }

        level.RebuildParents();
        level.EnsureNames();
        level.IsDirty = true;
        return new LevelOptimizeResult
        {
            BeforeObjects = before,
            AfterObjects = level.AllNodes().Count(),
            MergedBlocks = merged,
            CulledBlocks = culled,
            DuplicateBlocks = duplicates,
            DegenerateBlocks = degenerate,
            EmptyGroups = emptyGroups
        };
    }

    private static int MergeList(GrabLevel level, LevelNode? parent, List<LevelNode> list, bool protectPhysics)
    {
        if (list.Count < 2) return 0;
        if (protectPhysics && parent != null && (parent.PhysicsObject || parent.LocalPhysicsObject)) return 0;
        // Nested groups with non-unit scale/rotation would need a full local-space
        // transform for every merged box. Leave those groups untouched.
        if (parent != null && (Math.Abs(parent.WorldScale.X - 1) > 0.001f ||
            Math.Abs(parent.WorldScale.Y - 1) > 0.001f || Math.Abs(parent.WorldScale.Z - 1) > 0.001f ||
            Math.Abs(parent.WorldRotation.X) > 0.0005f || Math.Abs(parent.WorldRotation.Y) > 0.0005f ||
            Math.Abs(parent.WorldRotation.Z) > 0.0005f)) return 0;
        var candidates = list.Where(n => CanMerge(n, protectPhysics)).ToList();
        if (candidates.Count < 2) return 0;
        int removed = 0;

        foreach (var group in candidates.GroupBy(n => MergeKey(n)))
        {
            var nodes = group.ToList();
            if (nodes.Count < 2) continue;
            double cell = Math.Abs(nodes[0].Scale.X);
            var byCell = new Dictionary<(int X, int Y, int Z), LevelNode>();
            double ox = nodes.Min(n => n.WorldPosition.X - cell * 0.5);
            double oy = nodes.Min(n => n.WorldPosition.Y - cell * 0.5);
            double oz = nodes.Min(n => n.WorldPosition.Z - cell * 0.5);
            foreach (var n in nodes)
            {
                var p = n.WorldPosition;
                var key = ((int)Math.Round((p.X - cell * 0.5 - ox) / cell),
                    (int)Math.Round((p.Y - cell * 0.5 - oy) / cell),
                    (int)Math.Round((p.Z - cell * 0.5 - oz) / cell));
                if (byCell.ContainsKey(key)) continue;
                byCell[key] = n;
            }
            var visited = new HashSet<(int X, int Y, int Z)>();
            foreach (var start in byCell.Keys.OrderBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.Z))
            {
                if (visited.Contains(start) || !byCell.ContainsKey(start)) continue;
                // Grow a rectangular run: X, then Z, then Y. A failed extension leaves the
                // already accepted rectangle untouched.
                int nx = 1;
                while (byCell.ContainsKey((start.X + nx, start.Y, start.Z)) && !visited.Contains((start.X + nx, start.Y, start.Z))) nx++;
                int nz = 1;
                while (true)
                {
                    int z = start.Z + nz;
                    bool all = true;
                    for (int x = 0; x < nx; x++)
                        if (!byCell.ContainsKey((start.X + x, start.Y, z)) || visited.Contains((start.X + x, start.Y, z)))
                        { all = false; break; }
                    if (!all) break;
                    nz++;
                }
                int ny = 1;
                while (true)
                {
                    int y = start.Y + ny;
                    bool all = true;
                    for (int x = 0; x < nx && all; x++)
                        for (int z = 0; z < nz && all; z++)
                            if (!byCell.ContainsKey((start.X + x, y, start.Z + z)) ||
                                visited.Contains((start.X + x, y, start.Z + z))) all = false;
                    if (!all) break;
                    ny++;
                }
                if (nx * ny * nz <= 1) { visited.Add(start); continue; }

                var first = byCell[start];
                var mergedNode = first.Clone(newIds: false);
                mergedNode.Name = first.Name;
                mergedNode.Scale = new Vec3((float)(cell * nx), (float)(cell * ny), (float)(cell * nz));
                mergedNode.Position = new Vec3(
                    (float)(ox + cell * (start.X + nx * 0.5)),
                    (float)(oy + cell * (start.Y + ny * 0.5)),
                    (float)(oz + cell * (start.Z + nz * 0.5)));
                // Parent-local coordinates: candidates can be nested, so convert the
                // world centre back into the parent's local space.
                if (parent != null)
                {
                    var inv = parent.WorldRotation.Inverse();
                    var ps = parent.WorldScale;
                    var local = inv.Rotate(mergedNode.Position - parent.WorldPosition);
                    mergedNode.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f),
                        local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
                    mergedNode.Rotation = inv * first.WorldRotation;
                }

                foreach (var key in Expand(start, nx, ny, nz))
                {
                    if (!visited.Add(key)) continue;
                    if (byCell.TryGetValue(key, out var old)) list.Remove(old);
                }
                list.Add(mergedNode);
                removed += nx * ny * nz - 1;
            }
        }
        return removed;
    }

    private static IEnumerable<(int X, int Y, int Z)> Expand((int X, int Y, int Z) start, int nx, int ny, int nz)
    {
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    yield return (start.X + x, start.Y + y, start.Z + z);
    }

    private static bool CanMerge(LevelNode n, bool protectPhysics)
    {
        if (n.Kind != NodeKind.Static || n.Shape != ShapeInfo.Cube) return false;
        if (n.Generated || n.ScriptCreated || n.Children.Count > 0) return false;
        if (n.Animations.Count > 0 || n.PhysicsObject || n.LocalPhysicsObject) return false;
        if (protectPhysics && n.IsLocked) return false;
        var s = n.Scale;
        if (s.X <= 0.001f || Math.Abs(s.X - s.Y) > 0.002f || Math.Abs(s.X - s.Z) > 0.002f) return false;
        var q = n.Rotation;
        if (Math.Abs(q.X) > 0.0005f || Math.Abs(q.Y) > 0.0005f || Math.Abs(q.Z) > 0.0005f) return false;
        return true;
    }

    private static string MergeKey(LevelNode n, bool includeScale = true) => string.Join("|",
        includeScale ? Math.Round(Math.Abs(n.Scale.X), 4).ToString(System.Globalization.CultureInfo.InvariantCulture) : "mixed",
        n.Material,
        n.Color1.Hex,
        n.IsTransparent, n.IsNeon, n.IsAdditive, n.IsPassable, n.IsGrabbable, n.IsGrapplable,
        n.IsLocal, n.EditorLabel);

    private static int RemoveDuplicates(LevelNode? parent, List<LevelNode> list, LevelOptimizeOptions options)
    {
        if (options.ProtectPhysics && parent != null && (parent.PhysicsObject || parent.LocalPhysicsObject)) return 0;
        int removed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list.ToList())
        {
            if (!CanRemoveDuplicate(node, options)) continue;
            var p = node.WorldPosition;
            var q = node.WorldRotation;
            var s = node.WorldScale;
            string key = string.Join("|",
                node.Kind, node.Shape,
                Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4),
                Math.Round(q.X, 5), Math.Round(q.Y, 5), Math.Round(q.Z, 5), Math.Round(q.W, 5),
                Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4),
                node.Material, node.Color1.Hex, node.IsTransparent, node.IsNeon,
                node.IsAdditive, node.IsPassable, node.IsGrabbable, node.IsGrapplable);
            if (seen.Add(key)) continue;
            list.Remove(node);
            removed++;
        }
        return removed;
    }

    private static int RemoveDegenerate(List<LevelNode> list, LevelOptimizeOptions options)
    {
        int removed = 0;
        foreach (var node in list.ToList())
        {
            if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) continue;
            if (node.Children.Count > 0 || node.Generated || node.ScriptCreated) continue;
            if (node.PhysicsObject || node.LocalPhysicsObject) continue;
            if (options.ProtectLocked && node.IsLocked) continue;
            if (options.ProtectAnimated && node.Animations.Count > 0) continue;
            var s = node.Scale;
            if (Math.Abs(s.X) > 0.001f && Math.Abs(s.Y) > 0.001f && Math.Abs(s.Z) > 0.001f) continue;
            list.Remove(node);
            removed++;
        }
        return removed;
    }

    private static bool CanRemoveDuplicate(LevelNode n, LevelOptimizeOptions options)
    {
        if (n.Kind is not (NodeKind.Static or NodeKind.Crumbling)) return false;
        if (n.Children.Count > 0 || n.Generated || n.ScriptCreated) return false;
        if (n.PhysicsObject || n.LocalPhysicsObject) return false;
        if (options.ProtectLocked && n.IsLocked) return false;
        if (options.ProtectAnimated && n.Animations.Count > 0) return false;
        return true;
    }

    /// <summary>
    /// Rebuilds one appearance group on a shared voxel grid. Unlike the conservative
    /// cube merge this can combine a tall terrain column with a one-block cap, which is
    /// where terrain levels usually get their object count back.
    /// </summary>
    private static int MergeMixedList(GrabLevel level, LevelNode? parent, List<LevelNode> list, LevelOptimizeOptions options)
    {
        if (list.Count < 2) return 0;
        if (options.ProtectPhysics && parent != null && (parent.PhysicsObject || parent.LocalPhysicsObject)) return 0;
        if (parent != null && (Math.Abs(parent.WorldScale.X - 1) > 0.001f ||
            Math.Abs(parent.WorldScale.Y - 1) > 0.001f || Math.Abs(parent.WorldScale.Z - 1) > 0.001f ||
            Math.Abs(parent.WorldRotation.X) > 0.0005f || Math.Abs(parent.WorldRotation.Y) > 0.0005f ||
            Math.Abs(parent.WorldRotation.Z) > 0.0005f)) return 0;

        // Any grid the user types; CellsFor skips a block that would need too many cells.
        double grid = Math.Clamp(options.MixedSizeGrid, 0.05, 32.0);
        int removed = 0;
        var candidates = list.Where(n => CanMergeMixed(n, options)).ToList();
        foreach (var group in candidates.GroupBy(n => MergeKey(n, includeScale: false)))
        {
            var nodes = group.ToList();
            if (nodes.Count < 2) continue;
            var cells = new Dictionary<(int X, int Y, int Z), LevelNode>();
            bool valid = true;
            foreach (var node in nodes)
            {
                var nodeCells = CellsFor(node, grid);
                if (nodeCells.Count == 0) { valid = false; break; }
                foreach (var cell in nodeCells)
                {
                    if (!cells.TryAdd(cell, node)) { valid = false; break; }
                }
                if (!valid) break;
            }
            if (!valid || cells.Count < 2) continue;

            var visited = new HashSet<(int X, int Y, int Z)>();
            var plans = new List<(int X, int Y, int Z, int NX, int NY, int NZ)>();
            foreach (var start in cells.Keys.OrderBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.Z))
            {
                if (visited.Contains(start)) continue;
                int nx = 1;
                while (cells.ContainsKey((start.X + nx, start.Y, start.Z)) &&
                       !visited.Contains((start.X + nx, start.Y, start.Z))) nx++;
                int nz = 1;
                while (true)
                {
                    int z = start.Z + nz;
                    bool all = true;
                    for (int x = 0; x < nx; x++)
                    {
                        var key = (start.X + x, start.Y, z);
                        if (!cells.ContainsKey(key) || visited.Contains(key)) { all = false; break; }
                    }
                    if (!all) break;
                    nz++;
                }
                int ny = 1;
                while (true)
                {
                    int y = start.Y + ny;
                    bool all = true;
                    for (int x = 0; x < nx && all; x++)
                        for (int z = 0; z < nz && all; z++)
                        {
                            var key = (start.X + x, y, start.Z + z);
                            if (!cells.ContainsKey(key) || visited.Contains(key)) all = false;
                        }
                    if (!all) break;
                    ny++;
                }
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        for (int z = 0; z < nz; z++)
                            visited.Add((start.X + x, start.Y + y, start.Z + z));
                plans.Add((start.X, start.Y, start.Z, nx, ny, nz));
            }
            if (plans.Count >= nodes.Count) continue;

            var sample = nodes[0];
            foreach (var node in nodes) list.Remove(node);
            foreach (var plan in plans)
            {
                var merged = CreateMergedNode(parent, sample, plan.X, plan.Y, plan.Z, plan.NX, plan.NY, plan.NZ, grid);
                list.Add(merged);
            }
            removed += nodes.Count - plans.Count;
        }
        return removed;
    }

    private static bool CanMergeMixed(LevelNode n, LevelOptimizeOptions options)
    {
        if (n.Kind != NodeKind.Static || n.Shape != ShapeInfo.Cube) return false;
        if (n.Generated || n.ScriptCreated || n.Children.Count > 0) return false;
        if (options.ProtectAnimated && n.Animations.Count > 0) return false;
        if (options.ProtectPhysics && (n.PhysicsObject || n.LocalPhysicsObject)) return false;
        if (options.ProtectLocked && n.IsLocked) return false;
        var s = n.WorldScale;
        return s.X > 0.001f && s.Y > 0.001f && s.Z > 0.001f;
    }

    private static List<(int X, int Y, int Z)> CellsFor(LevelNode node, double grid)
    {
        var cells = new List<(int X, int Y, int Z)>();
        var rotation = node.WorldRotation;
        if (Math.Abs(rotation.X) > 0.0005f || Math.Abs(rotation.Y) > 0.0005f || Math.Abs(rotation.Z) > 0.0005f)
            return cells;
        var p = node.WorldPosition;
        var s = node.WorldScale;
        int nx = Math.Max(1, (int)Math.Round(Math.Abs(s.X) / grid));
        int ny = Math.Max(1, (int)Math.Round(Math.Abs(s.Y) / grid));
        int nz = Math.Max(1, (int)Math.Round(Math.Abs(s.Z) / grid));
        if (Math.Abs(Math.Abs(s.X) - nx * grid) > 0.01 ||
            Math.Abs(Math.Abs(s.Y) - ny * grid) > 0.01 ||
            Math.Abs(Math.Abs(s.Z) - nz * grid) > 0.01) return cells;
        double minX = p.X - Math.Abs(s.X) * 0.5;
        double minY = p.Y - Math.Abs(s.Y) * 0.5;
        double minZ = p.Z - Math.Abs(s.Z) * 0.5;
        int startX = (int)Math.Round(minX / grid);
        int startY = (int)Math.Round(minY / grid);
        int startZ = (int)Math.Round(minZ / grid);
        if (Math.Abs(minX / grid - startX) > 0.01 ||
            Math.Abs(minY / grid - startY) > 0.01 ||
            Math.Abs(minZ / grid - startZ) > 0.01) return cells;
        if ((long)nx * ny * nz > 100000) return cells;
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    cells.Add((startX + x, startY + y, startZ + z));
        return cells;
    }

    private static LevelNode CreateMergedNode(LevelNode? parent, LevelNode sample,
        int startX, int startY, int startZ, int nx, int ny, int nz, double grid)
    {
        var merged = sample.Clone(newIds: false);
        merged.Name = sample.Name;
        merged.Scale = new Vec3((float)(nx * grid), (float)(ny * grid), (float)(nz * grid));
        var world = new Vec3(
            (float)((startX + nx * 0.5) * grid),
            (float)((startY + ny * 0.5) * grid),
            (float)((startZ + nz * 0.5) * grid));
        if (parent != null)
        {
            var rel = world - parent.WorldPosition;
            var local = parent.WorldRotation.Inverse().Rotate(rel);
            var ps = parent.WorldScale;
            merged.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f),
                local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
            merged.Rotation = parent.WorldRotation.Inverse() * sample.WorldRotation;
        }
        else
        {
            merged.Position = world;
            merged.Rotation = sample.WorldRotation;
        }
        return merged;
    }

    private static int CullList(LevelNode? parent, List<LevelNode> list, LevelOptimizeOptions options)
    {
        if (options.ProtectPhysics && parent != null && (parent.PhysicsObject || parent.LocalPhysicsObject)) return 0;
        var candidates = list.Where(n => n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Cube &&
            n.Children.Count == 0 && n.Animations.Count == 0 && !n.IsTransparent && !n.IsNeon &&
            !n.IsAdditive && !n.Generated && !n.ScriptCreated && !n.PhysicsObject && !n.LocalPhysicsObject &&
            !(options.ProtectLocked && n.IsLocked) &&
            Math.Abs(n.Scale.X - n.Scale.Y) < 0.002f && Math.Abs(n.Scale.X - n.Scale.Z) < 0.002f &&
            Math.Abs(n.Rotation.X) < 0.0005f && Math.Abs(n.Rotation.Y) < 0.0005f &&
            Math.Abs(n.Rotation.Z) < 0.0005f).ToList();
        if (candidates.Count < 7) return 0;
        double cell = Math.Abs(candidates[0].Scale.X);
        var cells = new HashSet<(int X, int Y, int Z)>();
        foreach (var n in candidates)
        {
            var p = n.WorldPosition;
            cells.Add(((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell), (int)Math.Floor(p.Z / cell)));
        }
        int removed = 0;
        foreach (var n in candidates)
        {
            var p = n.WorldPosition;
            var c = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell), (int)Math.Floor(p.Z / cell));
            bool hidden =
                cells.Contains((c.Item1 + 1, c.Item2, c.Item3)) &&
                cells.Contains((c.Item1 - 1, c.Item2, c.Item3)) &&
                cells.Contains((c.Item1, c.Item2 + 1, c.Item3)) &&
                cells.Contains((c.Item1, c.Item2 - 1, c.Item3)) &&
                cells.Contains((c.Item1, c.Item2, c.Item3 + 1)) &&
                cells.Contains((c.Item1, c.Item2, c.Item3 - 1));
            if (!hidden) continue;
            list.Remove(n);
            removed++;
        }
        return removed;
    }
}
