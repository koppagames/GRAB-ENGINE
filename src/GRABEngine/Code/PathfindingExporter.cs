using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.Code;

public sealed class PathExportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public LevelNode? Block { get; init; }
    public GasmCompileResult? Compile { get; init; }
    public string Source { get; init; } = "";
}

/// <summary>
/// Turns a live editor path into a normal C# Code Block and compiles it to GASM.
/// GRAB has no runtime A* opcode, so the route is baked for the target's position
/// at export time. The generated block is a real GASM program: it has Position
/// connections to the finder and runs in the retail game, not just Play mode.
/// </summary>
public static class PathfindingExporter
{
    /// <summary>
    /// Bakes a live planner, not a frozen route. The generated block reads the
    /// current target and wall positions every frame and resolves its own movement
    /// against the live wall boxes, so a moved wall or a moving target is re-planned
    /// immediately inside the game. When the direct line is blocked it commits to that
    /// one wall and follows it to its end (choosing the end nearer the target once, at
    /// first contact), then rounds the corner and heads for the target again; it never
    /// re-derives the travel direction from the target offset, which is what used to
    /// make it reverse in the middle of a wall. GASM has no array
    /// indexing cheap enough for a grid search (a runtime index expands to a chain of
    /// comparisons), so this is a continuous planner instead of a BFS field. The
    /// game's runtime stores programs/connections/registers in dynamic arrays; only
    /// its editor UI caps them at 150/10/480, so this block is compiled in extended
    /// mode.
    /// </summary>
    public static PathExportResult BakeRuntime(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode target)
    {
        if (finder.Kind is NodeKind.Start or NodeKind.Finish)
            return new PathExportResult { Success = false, Message = "GRAB does not allow Code Blocks to connect to Start or Finish objects. Use a normal block as the finder." };
        if (target.Kind is NodeKind.Start or NodeKind.Finish)
            return new PathExportResult { Success = false, Message = "GRAB does not allow Code Blocks to connect to Start or Finish objects. Use a normal block as the target." };
        if (ReferenceEquals(finder, target))
            return new PathExportResult { Success = false, Message = "Finder and target must be different objects." };

        level.RebuildParents();
        level.EnsureNames();
        if (string.IsNullOrWhiteSpace(finder.Name)) finder.Name = finder.DisplayName;
        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = target.DisplayName;

        settings.Clamp();
        var walls = SelectBlockers(level, settings, finder, target);

        string? duplicate = new[] { finder, target }.Concat(walls)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate != null)
            return new PathExportResult
            {
                Success = false,
                Message = $"More than one object is named '{duplicate}'. The runtime planner binds objects with GameObject.Find, so names must be unique. Rename the duplicates and bake again."
            };

        string source = BuildRuntimeSource(level, settings, finder, target, walls);
        string name = UniqueBlockName(level, "Live Path " + finder.Name);
        var block = new LevelNode
        {
            Kind = NodeKind.GASM,
            Name = name,
            Position = new Vec3(finder.WorldPosition.X, finder.WorldPosition.Y + 1.2f, finder.WorldPosition.Z),
            Script = source,
            StartActive = true
        };
        level.AddNode(block);
        level.EnsureNames();
        var compile = new CSharpCompiler { AllowExtendedGasm = true }.Compile(level, block, source);
        block.Compiled = compile;
        if (!compile.Success)
        {
            string errors = string.Join("\n", compile.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"line {d.Line}: {d.Message}"));
            return new PathExportResult
            {
                Success = false,
                Message = "The live planner was created, but its GASM did not compile:\n" + errors,
                Block = block,
                Compile = compile,
                Source = source
            };
        }

        return new PathExportResult
        {
            Success = true,
            Message = $"Baked live planner into '{name}': {walls.Count} wall object(s), {compile.Program.Instructions.Count} GASM instructions, {compile.Connections.Count} connections (one Code Block). It reads the real wall and target positions every frame, so moved walls and a moving target are re-planned inside the game.",
            Block = block,
            Compile = compile,
            Source = source
        };
    }

    public static PathExportResult Bake(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, PathfindingResult path, bool loop = false)
    {
        if (finder.Kind is NodeKind.Start or NodeKind.Finish)
            return new PathExportResult { Success = false, Message = "GRAB does not allow Code Blocks to connect to Start or Finish objects. Use a normal block as the finder." };
        if (!path.Success || path.Points.Count == 0)
            return new PathExportResult { Success = false, Message = "There is no route to bake. Find a path first." };

        level.EnsureNames();
        if (string.IsNullOrWhiteSpace(finder.Name))
            finder.Name = finder.DisplayName;
        int sameName = level.AllNodes().Count(n => string.Equals(n.Name, finder.Name, StringComparison.OrdinalIgnoreCase));
        if (sameName > 1)
            return new PathExportResult
            {
                Success = false,
                Message = $"'{finder.Name}' is not a unique object name, so the generated GameObject.Find would bind the wrong block. Rename one of the duplicates and bake again."
            };

        var points = CleanPoints(path.Points);
        if (points.Count == 0)
            return new PathExportResult { Success = false, Message = "The route has no usable waypoints." };

        string source = BuildSource(level, finder, points, settings, loop);
        string name = UniqueBlockName(level, "Path " + finder.Name);
        var block = new LevelNode
        {
            Kind = NodeKind.GASM,
            Name = name,
            Position = new Vec3(finder.WorldPosition.X, finder.WorldPosition.Y + 1.2f, finder.WorldPosition.Z),
            Script = source,
            StartActive = true
        };
        level.AddNode(block);
        level.EnsureNames();
        var compile = new CSharpCompiler { AllowExtendedGasm = EngineSettings.ExtendedGasm }.Compile(level, block, source);
        block.Compiled = compile;
        if (!compile.Success)
        {
            string errors = string.Join("\n", compile.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"line {d.Line}: {d.Message}"));
            return new PathExportResult
            {
                Success = false,
                Message = "The route was created, but its GASM did not compile:\n" + errors,
                Block = block,
                Compile = compile,
                Source = source
            };
        }

        int pages = 1 + compile.Continuations.Count;
        string limit = pages > 1
            ? $"{pages} Code Blocks with continuation pages (GRAB's per-block limits were respected)."
            : $"{compile.Program.Instructions.Count} instructions, {compile.Connections.Count} connections, one Code Block.";
        return new PathExportResult
        {
            Success = true,
            Message = $"Baked '{finder.DisplayName}' route into '{name}': {points.Count} waypoint(s), {limit}",
            Block = block,
            Compile = compile,
            Source = source
        };
    }

    /// <summary>
    /// The blocking objects a baked live planner watches: real walls inside the agent's
    /// vertical band, nearest to the straight line first. The count comes from
    /// <see cref="PathfindingSettings.BlockingObjects"/>; every extra object is another
    /// GASM connection (uncapped at runtime) and more per-frame instructions.
    /// </summary>
    public static List<LevelNode> SelectBlockers(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode target)
    {
        settings.Clamp();
        float feet = Pathfinder.FootPoint(finder).Y;
        float height = Math.Max(0.3f, settings.AgentHeight);
        int max = Math.Clamp(settings.BlockingObjects, 1, 96);
        return level.AllNodes()
            .Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling)
            .Where(n => !n.IsPassable && !ReferenceEquals(n, finder) && !ReferenceEquals(n, target))
            .Where(n => !n.Generated || !n.HideModel)
            .Where(n => Math.Abs(n.WorldScale.X) > 1e-4f && Math.Abs(n.WorldScale.Y) > 1e-4f && Math.Abs(n.WorldScale.Z) > 1e-4f)
            .Where(n =>
            {
                var (_, ey, _) = Extents(n);
                float y = n.WorldPosition.Y;
                return y + ey > feet + 0.12f && y - ey < feet + height;
            })
            .OrderBy(n => SegmentDistance(n.WorldPosition, finder.WorldPosition, target.WorldPosition))
            .Take(max)
            .ToList();
    }

    private static string BuildRuntimeSource(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode target, IReadOnlyList<LevelNode> blockers)
    {
        float speed = Math.Max(0.1f, settings.Speed);
        float radius = Math.Max(0.05f, settings.AgentRadius);
        float probe = Math.Clamp(settings.CellSize * 1.5f, 0.45f, 1f);
        // How far past a wall's end the agent walks before it lets go of that wall. It has
        // to clear the end plus the agent radius, or rounding the corner would put it back
        // inside the same wall and it would re-commit to the face it just left.
        float pad = radius + 0.5f;

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// GRAB ENGINE live planner. Compiled by GRAB ENGINE and executed by the retail game.");
        sb.AppendLine("// Every frame it reads the live positions of the target and of the walls. When the");
        sb.AppendLine("// straight line is blocked it commits to that wall and FOLLOWS IT TO ITS END: the");
        sb.AppendLine("// direction along the face is chosen once, from whichever end of that wall is closer");
        sb.AppendLine("// to the target, and is never re-derived from the target offset, so the agent cannot");
        sb.AppendLine("// rock back and forth on one face. Once it is past the end it rounds the corner and");
        sb.AppendLine("// heads straight for the target again.");
        sb.AppendLine("public class PathLive : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine($"    GameObject mover = GameObject.Find(\"{Escape(finder.Name)}\");");
        sb.AppendLine($"    GameObject goal = GameObject.Find(\"{Escape(target.Name)}\");");
        for (int i = 0; i < blockers.Count; i++)
            sb.AppendLine($"    GameObject w{i} = GameObject.Find(\"{Escape(blockers[i].Name)}\");");
        sb.AppendLine();
        sb.AppendLine($"    public float speed = {Number(speed)};");
        sb.AppendLine("    public bool face = true;");
        sb.AppendLine();
        sb.AppendLine("    // Wall-following state. wall < 0 means \"no wall committed, head for the goal\".");
        sb.AppendLine("    float wall = -1f;");
        sb.AppendLine("    float lastWall = -2f;");
        sb.AppendLine("    float axis = 0f;");
        sb.AppendLine("    float dir = 1f;");
        sb.AppendLine("    float endX = 0f;");
        sb.AppendLine("    float endZ = 0f;");
        sb.AppendLine("    float grace = 0f;");
        sb.AppendLine("    float stuck = 0f;");
        sb.AppendLine("    float lastHx = 0f;");
        sb.AppendLine("    float lastHz = 0f;");
        sb.AppendLine("    float rescan = 0f;");
        sb.AppendLine("    float jams = 0f;");
        sb.AppendLine("    float spin = 1f;");
        sb.AppendLine("    float bestDist = 100000f;");
        sb.AppendLine();
        sb.AppendLine("    void Start()");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        sb.AppendLine("        float px = mover.Position.X;");
        sb.AppendLine("        float pz = mover.Position.Z;");
        sb.AppendLine("        float gx = goal.Position.X;");
        sb.AppendLine("        float gz = goal.Position.Z;");
        sb.AppendLine("        float dx = gx - px;");
        sb.AppendLine("        float dz = gz - pz;");
        sb.AppendLine("        float dist = Mathf.Sqrt(dx * dx + dz * dz);");
        sb.AppendLine("        float ux = 0f;");
        sb.AppendLine("        float uz = 0f;");
        sb.AppendLine("        if (dist > 0.0001f)");
        sb.AppendLine("        {");
        sb.AppendLine("            ux = dx / dist;");
        sb.AppendLine("            uz = dz / dist;");
        sb.AppendLine("        }");
        sb.AppendLine($"        float ax = px + ux * {Number(probe)};");
        sb.AppendLine($"        float az = pz + uz * {Number(probe)};");
        // After a blocked step, look at the wall we actually bumped into instead of the
        // one on the straight line to the target, so an L-corner hands over to the other
        // wall instead of bouncing back along the first one.
        sb.AppendLine("        if (rescan > 0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine($"            ax = px + lastHx * {Number(probe)};");
        sb.AppendLine($"            az = pz + lastHz * {Number(probe)};");
        sb.AppendLine("            rescan = 0f;");
        sb.AppendLine("        }");
        sb.AppendLine("        // The wall straight ahead, if any: which face we hit decides the axis we can");
        sb.AppendLine("        // travel along while hugging it.");
        sb.AppendLine("        float hit = 0f;");
        sb.AppendLine("        float hitWall = -1f;");
        sb.AppendLine("        float tangent = 1f;");
        sb.AppendLine("        float hitX = 0f;");
        sb.AppendLine("        float hitZ = 0f;");
        sb.AppendLine("        float halfX = 0f;");
        sb.AppendLine("        float halfZ = 0f;");

        for (int i = 0; i < blockers.Count; i++)
        {
            var (ex, _, ez) = Extents(blockers[i]);
            float bx = Math.Abs(ex) + radius;
            float bz = Math.Abs(ez) + radius;
            sb.AppendLine($"        // {Escape(blockers[i].DisplayName)}");
            sb.AppendLine($"        float ox{i} = ax - w{i}.Position.X;");
            sb.AppendLine($"        float oz{i} = az - w{i}.Position.Z;");
            sb.AppendLine($"        float qx{i} = Mathf.Abs(ox{i});");
            sb.AppendLine($"        float qz{i} = Mathf.Abs(oz{i});");
            sb.AppendLine($"        if (qx{i} < {Number(bx)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (qz{i} < {Number(bz)})");
            sb.AppendLine("            {");
            sb.AppendLine("                if (hit < 0.5f)");
            sb.AppendLine("                {");
            sb.AppendLine("                    hit = 1f;");
            sb.AppendLine($"                    hitWall = {i}f;");
            sb.AppendLine($"                    hitX = w{i}.Position.X;");
            sb.AppendLine($"                    hitZ = w{i}.Position.Z;");
            sb.AppendLine($"                    halfX = {Number(Math.Abs(ex))};");
            sb.AppendLine($"                    halfZ = {Number(Math.Abs(ez))};");
            // The face we are closest to is the one we are about to walk into; the other
            // axis runs along that face, so that is the axis to follow.
            sb.AppendLine($"                    if (({Number(bx)} - qx{i}) < ({Number(bz)} - qz{i}))");
            sb.AppendLine("                    {");
            sb.AppendLine("                        tangent = 1f;");
            sb.AppendLine("                    }");
            sb.AppendLine("                    else");
            sb.AppendLine("                    {");
            sb.AppendLine("                        tangent = 0f;");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        sb.AppendLine();
        sb.AppendLine("        if (grace > 0f)");
        sb.AppendLine("        {");
        sb.AppendLine("            grace = grace - Time.DeltaTime;");
        sb.AppendLine("            if (grace < 0f)");
        sb.AppendLine("            {");
        sb.AppendLine("                grace = 0f;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (hit > 0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (wall < 0f)");
        sb.AppendLine("            {");
        // Right after letting go we are standing at a corner; re-committing to the same
        // wall there would recompute the direction towards the end we just passed.
        sb.AppendLine("                if (lastWall != hitWall || grace <= 0f)");
        sb.AppendLine("                {");
        sb.AppendLine("                    wall = hitWall;");
        sb.AppendLine("                    if (tangent > 0.5f)");
        sb.AppendLine("                    {");
        // The target of a follow is the CORNER of the wall: the end we chose, pushed out
        // along the face and also away from it. Walking only to the end leaves the agent
        // pressed against the face, and the straight line to a target on the far side
        // then drags it back along that same face (the reported "runs the wall the wrong
        // way" loop); the outward offset makes it round the corner and get clear.
        sb.AppendLine("                        axis = 1f;");
        sb.AppendLine($"                        float ez1 = hitZ - halfZ - {Number(pad)};");
        sb.AppendLine($"                        float ez2 = hitZ + halfZ + {Number(pad)};");
        sb.AppendLine("                        float cz1 = Mathf.Abs(pz - ez1) + Mathf.Abs(gz - ez1);");
        sb.AppendLine("                        float cz2 = Mathf.Abs(pz - ez2) + Mathf.Abs(gz - ez2);");
        sb.AppendLine("                        if (spin * (cz1 - cz2) < 0f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = -1f;");
        sb.AppendLine("                            endZ = ez1;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = 1f;");
        sb.AppendLine("                            endZ = ez2;");
        sb.AppendLine("                        }");
        // Never pick the corner we are already standing on: that is a zero-length follow
        // and the agent would jitter instead of walking around the wall.
        sb.AppendLine("                        if (Mathf.Abs(pz - ez1) < 0.7f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = 1f;");
        sb.AppendLine("                            endZ = ez2;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        if (Mathf.Abs(pz - ez2) < 0.7f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = -1f;");
        sb.AppendLine("                            endZ = ez1;");
        sb.AppendLine("                        }");
        sb.AppendLine($"                        float ox1 = hitX + halfX + {Number(pad)};");
        sb.AppendLine($"                        float ox2 = hitX - halfX - {Number(pad)};");
        sb.AppendLine("                        if (gx > hitX)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            endX = ox1;");
        sb.AppendLine("                            if (px > endX)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                endX = px;");
        sb.AppendLine("                            }");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else");
        sb.AppendLine("                        {");
        sb.AppendLine("                            endX = ox2;");
        sb.AppendLine("                            if (px < endX)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                endX = px;");
        sb.AppendLine("                            }");
        sb.AppendLine("                        }");
        sb.AppendLine("                    }");
        sb.AppendLine("                    else");
        sb.AppendLine("                    {");
        sb.AppendLine("                        axis = 0f;");
        sb.AppendLine($"                        float ex1 = hitX - halfX - {Number(pad)};");
        sb.AppendLine($"                        float ex2 = hitX + halfX + {Number(pad)};");
        sb.AppendLine("                        float cx1 = Mathf.Abs(px - ex1) + Mathf.Abs(gx - ex1);");
        sb.AppendLine("                        float cx2 = Mathf.Abs(px - ex2) + Mathf.Abs(gx - ex2);");
        sb.AppendLine("                        if (spin * (cx1 - cx2) < 0f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = -1f;");
        sb.AppendLine("                            endX = ex1;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = 1f;");
        sb.AppendLine("                            endX = ex2;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        if (Mathf.Abs(px - ex1) < 0.7f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = 1f;");
        sb.AppendLine("                            endX = ex2;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        if (Mathf.Abs(px - ex2) < 0.7f)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            dir = -1f;");
        sb.AppendLine("                            endX = ex1;");
        sb.AppendLine("                        }");
        sb.AppendLine($"                        float oz1 = hitZ + halfZ + {Number(pad)};");
        sb.AppendLine($"                        float oz2 = hitZ - halfZ - {Number(pad)};");
        sb.AppendLine("                        if (gz > hitZ)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            endZ = oz1;");
        sb.AppendLine("                            if (pz > endZ)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                endZ = pz;");
        sb.AppendLine("                            }");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else");
        sb.AppendLine("                        {");
        sb.AppendLine("                            endZ = oz2;");
        sb.AppendLine("                            if (pz < endZ)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                endZ = pz;");
        sb.AppendLine("                            }");
        sb.AppendLine("                        }");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        float hx = ux;");
        sb.AppendLine("        float hz = uz;");
        sb.AppendLine("        if (wall >= 0f)");
        sb.AppendLine("        {");
        // Keep the committed direction until we are standing on that corner: that is the
        // whole point of following rather than steering at the target.
        sb.AppendLine("            if (axis > 0.5f)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (dir * (pz - endZ) >= 0f)");
        sb.AppendLine("                {");
        sb.AppendLine("                    lastWall = wall;");
        sb.AppendLine("                    wall = -1f;");
        sb.AppendLine("                    grace = 0.5f;");
        sb.AppendLine("                }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    float fx = endX - px;");
        sb.AppendLine("                    float fz = endZ - pz;");
        sb.AppendLine("                    float fl = Mathf.Sqrt(fx * fx + fz * fz);");
        sb.AppendLine("                    if (fl > 0.0001f)");
        sb.AppendLine("                    {");
        sb.AppendLine("                        hx = fx / fl;");
        sb.AppendLine("                        hz = fz / fl;");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                if (dir * (px - endX) >= 0f)");
        sb.AppendLine("                {");
        sb.AppendLine("                    lastWall = wall;");
        sb.AppendLine("                    wall = -1f;");
        sb.AppendLine("                    grace = 0.5f;");
        sb.AppendLine("                }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    float fx = endX - px;");
        sb.AppendLine("                    float fz = endZ - pz;");
        sb.AppendLine("                    float fl = Mathf.Sqrt(fx * fx + fz * fz);");
        sb.AppendLine("                    if (fl > 0.0001f)");
        sb.AppendLine("                    {");
        sb.AppendLine("                        hx = fx / fl;");
        sb.AppendLine("                        hz = fz / fl;");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        float len = Mathf.Sqrt(hx * hx + hz * hz);");
        sb.AppendLine("        if (len > 0.0001f)");
        sb.AppendLine("        {");
        sb.AppendLine("            hx = hx / len;");
        sb.AppendLine("            hz = hz / len;");
        sb.AppendLine("        }");
        sb.AppendLine("        float step = speed * Time.DeltaTime;");
        sb.AppendLine("        float nx = px + hx * step;");
        sb.AppendLine("        float nz = pz + hz * step;");
        sb.AppendLine("        if (Blocked(nx, nz))");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!Blocked(nx, pz))");
        sb.AppendLine("            {");
        sb.AppendLine("                nz = pz;");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                if (!Blocked(px, nz))");
        sb.AppendLine("                {");
        sb.AppendLine("                    nx = px;");
        sb.AppendLine("                }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    nx = px;");
        sb.AppendLine("                    nz = pz;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        mover.Position.X = nx;");
        sb.AppendLine("        mover.Position.Z = nz;");
        // Stuck means "did not actually move": following a wall sideways is progress even
        // while the straight-line distance grows.
        sb.AppendLine("        float moved = Mathf.Sqrt((nx - px) * (nx - px) + (nz - pz) * (nz - pz));");
        sb.AppendLine("        if (moved < step * 0.4f)");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = stuck + Time.DeltaTime;");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = 0f;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (stuck > 0.45f)");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = 0f;");
        // Something blocked the step: look at THAT wall next frame and follow it instead
        // (this is how an L-corner hands over from one wall to the other). After a few
        // jams with no progress, try the other way around rather than standing still.
        sb.AppendLine("            rescan = 1f;");
        sb.AppendLine("            jams = jams + 1f;");
        sb.AppendLine("            if (jams > 3f)");
        sb.AppendLine("            {");
        sb.AppendLine("                jams = 0f;");
        sb.AppendLine("                spin = -spin;");
        sb.AppendLine("            }");
        sb.AppendLine("            if (wall >= 0f)");
        sb.AppendLine("            {");
        sb.AppendLine("                lastWall = wall;");
        sb.AppendLine("                wall = -1f;");
        sb.AppendLine("                grace = 0f;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        float dy = goal.Position.Y - mover.Position.Y;");
        sb.AppendLine("        if (dy > 0.35f)");
        sb.AppendLine("        {");
        sb.AppendLine("            mover.Position.Y = mover.Position.Y + Mathf.Min(dy, 3f * Time.DeltaTime);");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            if (dy < -0.35f) mover.Position.Y = mover.Position.Y - Mathf.Min(-dy, 3f * Time.DeltaTime);");
        sb.AppendLine("        }");
        sb.AppendLine("        if (dist < bestDist - 0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            bestDist = dist;");
        sb.AppendLine("            jams = 0f;");
        sb.AppendLine("        }");
        sb.AppendLine("        lastHx = hx;");
        sb.AppendLine("        lastHz = hz;");
        sb.AppendLine("        if (face && dist > 0.15f)");
        sb.AppendLine("        {");
        sb.AppendLine("            mover.Rotation.Y = Mathf.Atan2(dx, dz) * 57.29578f;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    bool Blocked(float x, float z)");
        sb.AppendLine("    {");
        for (int i = 0; i < blockers.Count; i++)
        {
            var (ex, _, ez) = Extents(blockers[i]);
            float bx = Math.Abs(ex) + radius;
            float bz = Math.Abs(ez) + radius;
            sb.AppendLine($"        float bx{i} = x - w{i}.Position.X;");
            sb.AppendLine($"        float bz{i} = z - w{i}.Position.Z;");
            sb.AppendLine($"        if (Mathf.Abs(bx{i}) < {Number(bx)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (Mathf.Abs(bz{i}) < {Number(bz)}) return true;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Distance from a point to the segment finder → target. Used to keep the
    /// walls that can actually stand in the agent's way.</summary>
    private static float SegmentDistance(Vec3 point, Vec3 a, Vec3 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float len = dx * dx + dz * dz;
        float t = len < 1e-6f ? 0f : Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / len, 0f, 1f);
        float cx = a.X + dx * t - point.X;
        float cz = a.Z + dz * t - point.Z;
        return MathF.Sqrt(cx * cx + cz * cz);
    }

    private static (float Ex, float Ey, float Ez) Extents(LevelNode node)
    {
        var scale = node.WorldScale;
        var rotation = node.WorldRotation;
        var hx = rotation.Rotate(new Vec3(Math.Abs(scale.X) * 0.5f, 0, 0));
        var hy = rotation.Rotate(new Vec3(0, Math.Abs(scale.Y) * 0.5f, 0));
        var hz = rotation.Rotate(new Vec3(0, 0, Math.Abs(scale.Z) * 0.5f));
        return (Math.Abs(hx.X) + Math.Abs(hy.X) + Math.Abs(hz.X),
            Math.Abs(hx.Y) + Math.Abs(hy.Y) + Math.Abs(hz.Y),
            Math.Abs(hx.Z) + Math.Abs(hy.Z) + Math.Abs(hz.Z));
    }

    private static List<Vec3> CleanPoints(IReadOnlyList<Vec3> input)
    {
        var result = new List<Vec3>();
        foreach (var p in input)
        {
            if (result.Count > 0 && (p - result[^1]).Length < 0.04f) continue;
            result.Add(p);
        }
        return result;
    }

    private static string UniqueBlockName(GrabLevel level, string baseName)
    {
        string name = baseName;
        for (int i = 2; level.AllNodes().Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = baseName + " " + i;
        return name;
    }

    private static string BuildSource(GrabLevel level, LevelNode finder, IReadOnlyList<Vec3> points,
        PathfindingSettings settings, bool loop)
    {
        bool fly = settings.Traversal == PathfindingTraversal.Fly;
        // Path points are feet positions. Finder.Position is the object's origin.
        float half = finder.Kind is NodeKind.Static or NodeKind.Crumbling
            ? Math.Abs(finder.WorldScale.Y) * 0.5f
            : 0.5f;
        float yOffset = half;
        float speed = Math.Max(0.1f, settings.Speed);
        float tolerance = Math.Max(0.05f, settings.WaypointTolerance);
        bool face = settings.FaceMovement;

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// GRAB ENGINE pathfinding bake.");
        sb.AppendLine("// The route was baked for the target's position when this Code Block was created.");
        sb.AppendLine("// Real GRAB has no runtime A* opcode, so every waypoint is a normal GASM move.");
        sb.AppendLine("public class PathRoute : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine($"    GameObject mover = GameObject.Find(\"{Escape(finder.Name)}\");");
        sb.AppendLine($"    public float speed = {Number(speed)};");
        sb.AppendLine($"    public float arrive = {Number(tolerance)};");
        sb.AppendLine($"    public bool loop = {(loop ? "true" : "false")};");
        sb.AppendLine("    private int step = 0;");
        sb.AppendLine();
        sb.AppendLine("    void Start()");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            float originY = p.Y + yOffset;
            sb.AppendLine($"        if (step == {i})");
            sb.AppendLine("        {");
            if (fly)
            {
                sb.AppendLine($"            float dx = {Number(p.X)} - mover.Position.X;");
                sb.AppendLine($"            float dy = {Number(originY)} - mover.Position.Y;");
                sb.AppendLine($"            float dz = {Number(p.Z)} - mover.Position.Z;");
                sb.AppendLine("            float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);");
                sb.AppendLine("            if (dist > arrive)");
                sb.AppendLine("            {");
                sb.AppendLine("                float move = Mathf.Min(dist, speed * Time.DeltaTime);");
                sb.AppendLine("                mover.Position.X = mover.Position.X + dx / dist * move;");
                sb.AppendLine("                mover.Position.Y = mover.Position.Y + dy / dist * move;");
                sb.AppendLine("                mover.Position.Z = mover.Position.Z + dz / dist * move;");
                if (face)
                    sb.AppendLine("                mover.Rotation.Y = Mathf.Atan2(dx, dz) * 57.29578f;");
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
            }
            else
            {
                sb.AppendLine($"            mover.Position.Y = {Number(originY)};");
                sb.AppendLine($"            float dx = {Number(p.X)} - mover.Position.X;");
                sb.AppendLine($"            float dz = {Number(p.Z)} - mover.Position.Z;");
                sb.AppendLine("            float flat = Mathf.Sqrt(dx * dx + dz * dz);");
                sb.AppendLine("            if (flat > arrive)");
                sb.AppendLine("            {");
                sb.AppendLine("                float move = Mathf.Min(flat, speed * Time.DeltaTime);");
                sb.AppendLine("                mover.Position.X = mover.Position.X + dx / flat * move;");
                sb.AppendLine("                mover.Position.Z = mover.Position.Z + dz / flat * move;");
                if (face)
                    sb.AppendLine("                mover.Rotation.Y = Mathf.Atan2(dx, dz) * 57.29578f;");
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
            }

            if (i == points.Count - 1)
            {
                sb.AppendLine("                if (loop)");
                sb.AppendLine("                {");
                sb.AppendLine("                    step = 0;");
                sb.AppendLine("                }");
                sb.AppendLine("                else");
                sb.AppendLine("                {");
                sb.AppendLine($"                    step = {i + 1};");
                sb.AppendLine("                }");
            }
            else
                sb.AppendLine($"                step = {i + 1};");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Number(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture) + "f";

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
