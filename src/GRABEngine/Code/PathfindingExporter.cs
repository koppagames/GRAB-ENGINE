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

    /// <summary>
    /// Bakes the agent using the sensor rig placed around the finder (whiskers) or over
    /// the walkable ground (navmesh). The rig is authored in the editor as real trigger
    /// objects; their world positions are compiled into the generated program. GRAB's
    /// triggers cannot report arbitrary blocks, so the exporter samples the rig at bake
    /// time rather than reading trigger state at runtime.
    /// </summary>
    public static PathExportResult BakeSensors(GrabLevel level, PathfindingSettings settings,
        LevelNode finder, LevelNode target, PathfindingResult path)
    {
        if (finder.Kind is NodeKind.Start or NodeKind.Finish || target.Kind is NodeKind.Start or NodeKind.Finish)
            return new PathExportResult { Success = false, Message = "GRAB does not allow Code Blocks to connect to Start or Finish objects. Use a normal block as the finder and target." };
        if (ReferenceEquals(finder, target))
            return new PathExportResult { Success = false, Message = "Finder and target must be different objects." };

        level.RebuildParents();
        level.EnsureNames();
        settings.Clamp();
        var rigNodes = level.AllNodes()
            .Where(n => n.EditorLabel == PathSensorRig.LabelFor(settings.SensorMode))
            .ToList();
        if (rigNodes.Count == 0)
            return new PathExportResult
            {
                Success = false,
                Message = settings.SensorMode == PathSensorMode.Whiskers
                    ? "No whisker rig found. Press Place Sensor Rig to put probe triggers around the finder, then bake."
                    : "No navmesh rig found. Press Place Sensor Rig to lay the trigger surface over walkable ground, then bake."
            };

        var walls = SelectBlockers(level, settings, finder, target);
        // The sensor core is heavier per wall than the direct planner (a near/far probe
        // ring plus escape tests), so cap it lower; the nearest walls to the route win.
        if (walls.Count > 16) walls = walls.Take(16).ToList();
        string? duplicate = new[] { finder, target }.Concat(walls).Concat(rigNodes)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate != null)
            return new PathExportResult
            {
                Success = false,
                Message = $"More than one object is named '{duplicate}'. Rename the duplicates and bake again."
            };

        // The graph is built from the rig itself for the navmesh (its tiles are the
        // walkable surface), and from a walkable scan of the level for whiskers (the
        // ring is only a sensor, it paints no ground).
        IReadOnlyList<Vec3>? surface = settings.SensorMode == PathSensorMode.NavMesh
            ? rigNodes.Select(n => n.WorldPosition).ToList()
            : null;
        var graph = NavRouteGraph.Build(level, settings, finder, target, surface, settings.NavMeshNodes);
        if (graph == null || graph.Count < 2)
            return new PathExportResult
            {
                Success = false,
                Message = "Could not build a route graph: no walkable ground was found around the finder. Check that it stands on a floor and that the agent radius fits."
            };

        float far = settings.WhiskerRadius;
        if (settings.SensorMode == PathSensorMode.NavMesh)
        {
            far = MathF.Max(1.1f, settings.AgentRadius * 2.6f);
        }
        else if (rigNodes.Count > 0)
        {
            // Use the ring the user actually placed: the average marker distance becomes
            // the far probe radius, so moving the probes changes what the agent senses.
            var ringFoot = Pathfinder.FootPoint(finder);
            float sum = 0f;
            foreach (var probe in rigNodes)
            {
                float dx = probe.WorldPosition.X - ringFoot.X;
                float dz = probe.WorldPosition.Z - ringFoot.Z;
                sum += MathF.Sqrt(dx * dx + dz * dz);
            }
            far = MathF.Max(0.6f, sum / rigNodes.Count);
        }

        string source = BuildRouteSource(settings, finder, target, walls, graph, far);
        string kind = settings.SensorMode == PathSensorMode.NavMesh
            ? $"navmesh surface ({graph.Count} route nodes, {graph.ForcedLinks} joined)"
            : $"whisker ring ({graph.Count} route nodes)";

        string name = UniqueBlockName(level, settings.SensorMode == PathSensorMode.Whiskers ? "Whisker Nav " + finder.Name : "NavMesh " + finder.Name);
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
                Message = "The sensor planner was created, but its GASM did not compile:\n" + errors,
                Block = block,
                Compile = compile,
                Source = source
            };
        }

        return new PathExportResult
        {
            Success = true,
            Message = $"Baked {kind} into '{name}': {rigNodes.Count} rig trigger(s), {walls.Count} watched wall(s), " +
                      $"{compile.Program.Instructions.Count} GASM instructions, {compile.Connections.Count} connections. " +
                      "The rig positions are compiled into the block, so the exported .level moves and re-plans with them.",
            Block = block,
            Compile = compile,
            Source = source
        };
    }

    /// <summary>
    /// Compiles the baked route graph into the exported agent.
    ///
    /// GASM has no loops, so the search runs here, at bake time: the graph's all-pairs
    /// shortest paths become a next-hop table. The shipped block only has to find the
    /// node nearest the live target, look up where to walk next, and repeat - every
    /// frame. That is what makes it re-plan when the target (or a wall) moves, instead
    /// of walking a route that was frozen when the block was baked.
    /// </summary>
    private static string BuildRouteSource(PathfindingSettings settings, LevelNode finder, LevelNode target,
        IReadOnlyList<LevelNode> walls, NavRouteGraph graph, float far)
    {
        float speed = Math.Max(0.1f, settings.Speed);
        float radius = Math.Max(0.05f, settings.AgentRadius);
        float arrive = MathF.Max(0.45f, MathF.Min(1.1f, settings.NavMeshDetail * 0.8f));
        float near = MathF.Max(0.3f, far * 0.55f);
        var dirs = Directions(8);
        int n = graph.Count;

        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// GRAB ENGINE route planner. Compiled by GRAB ENGINE, runs in retail GRAB.");
        sb.AppendLine("// The navigation graph baked from the walkable surface is stored as a next-hop");
        sb.AppendLine("// table: for every route node and every goal node, the block knows which node");
        sb.AppendLine("// to walk to next. Each frame it re-reads the target, picks the node nearest");
        sb.AppendLine("// the target's LIVE position and follows the table, so a target that moves (or");
        sb.AppendLine("// a wall that is moved) is handled inside the game with no editor involved.");
        sb.AppendLine("// Whisker probes around the finder steer it around anything the table misses.");
        sb.AppendLine("public class PathRoute : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine($"    GameObject mover = GameObject.Find(\"{Escape(finder.Name)}\");");
        sb.AppendLine($"    GameObject goal = GameObject.Find(\"{Escape(target.Name)}\");");
        for (int i = 0; i < walls.Count; i++)
            sb.AppendLine($"    GameObject w{i} = GameObject.Find(\"{Escape(walls[i].Name)}\");");
        sb.AppendLine();
        sb.AppendLine($"    public float speed = {Number(speed)};");
        sb.AppendLine($"    public float arrive = {Number(arrive)};");
        sb.AppendLine("    // an = route node the agent is walking from, tn = node nearest the target.");
        sb.AppendLine("    float an = -1f;");
        sb.AppendLine("    float tn = -1f;");
        sb.AppendLine("    float detour = 0f;");
        sb.AppendLine("    float last = -1f;");
        sb.AppendLine("    float spin = 1f;");
        sb.AppendLine("    float stuck = 0f;");
        sb.AppendLine("    float side = 1f;");
        sb.AppendLine("    float follow = 0f;");
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
        sb.AppendLine();
        sb.AppendLine("        // 1. Which route node is the target standing on right now?");
        sb.AppendLine("        float newtn = 0f;");
        sb.AppendLine("        float nearest = 1000000f;");
        for (int i = 0; i < n; i++)
        {
            var p = graph.Nodes[i];
            sb.AppendLine($"        float q{i}x = gx - {Number(p.X)};");
            sb.AppendLine($"        float q{i}z = gz - {Number(p.Z)};");
            sb.AppendLine($"        float q{i} = q{i}x * q{i}x + q{i}z * q{i}z;");
            sb.AppendLine($"        if (q{i} < nearest) {{ nearest = q{i}; newtn = {i}f; }}");
        }
        sb.AppendLine("        if (newtn != tn)");
        sb.AppendLine("        {");
        sb.AppendLine("            // The target moved to another part of the surface: drop the old");
        sb.AppendLine("            // node and re-plan from wherever the agent is now.");
        sb.AppendLine("            tn = newtn;");
        sb.AppendLine("            an = -1f;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        // 2. Which node is the agent closest to?");
        sb.AppendLine("        if (an < -0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            float frombest = 1000000f;");
        sb.AppendLine("            float picked = -1f;");
        for (int i = 0; i < n; i++)
        {
            var p = graph.Nodes[i];
            sb.AppendLine($"            float r{i}x = px - {Number(p.X)};");
            sb.AppendLine($"            float r{i}z = pz - {Number(p.Z)};");
            sb.AppendLine($"            float r{i} = r{i}x * r{i}x + r{i}z * r{i}z;");
            sb.AppendLine($"            if (r{i} < frombest) {{ frombest = r{i}; picked = {i}f; }}");
        }
        sb.AppendLine("            an = picked;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        float wpx = ux;");
        sb.AppendLine("        float wpz = uz;");
        sb.AppendLine("        float jog = speed;");
        sb.AppendLine("        if (dist < arrive) { jog = 0f; }");
        sb.AppendLine("        float nb = -1f;");
        sb.AppendLine();
        sb.AppendLine("        if (detour > 0f)");
        sb.AppendLine("        {");
        sb.AppendLine("            // A moved wall cut the baked edge: let live wall following take over");
        sb.AppendLine("            // for a moment, then resume the graph.");
        sb.AppendLine("            detour = detour - Time.DeltaTime;");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            if (an > -0.5f)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (an != tn)");
        sb.AppendLine("                {");
        sb.AppendLine("                    float direct = 0f;");
        sb.AppendLine("                    if (dist < 3.5f) { direct = 1f; }");
        sb.AppendLine("                    if (direct < 0.5f)");
        sb.AppendLine("                    {");
        sb.AppendLine("                        if (Clear(px, pz, gx, gz)) { direct = 1f; }");
        sb.AppendLine("                    }");
        sb.AppendLine("                    if (direct < 0.5f)");
        sb.AppendLine("                    {");
        if (n > 1)
        {
            for (int i = 0; i < n; i++)
            {
                bool any = false;
                for (int j = 0; j < n; j++)
                    if (j != i && graph.Next[i, j] >= 0) any = true;
                if (!any) continue;
                sb.AppendLine($"                        if (an == {i}f)");
                sb.AppendLine("                        {");
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    int next = graph.Next[i, j];
                    if (next < 0) continue;
                    sb.AppendLine($"                            if (tn == {j}f) {{ nb = {next}f; }}");
                }
                sb.AppendLine("                        }");
            }
        }
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (nb > -0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            float wx = 0f;");
        sb.AppendLine("            float wz = 0f;");
        for (int i = 0; i < n; i++)
        {
            var p = graph.Nodes[i];
            sb.AppendLine($"            {(i == 0 ? "" : "else ")}if (nb == {i}f) {{ wx = {Number(p.X)}; wz = {Number(p.Z)}; }}");
        }
        sb.AppendLine("            float bx = wx - px;");
        sb.AppendLine("            float bz = wz - pz;");
        sb.AppendLine("            float bd = Mathf.Sqrt(bx * bx + bz * bz);");
        sb.AppendLine("            if (bd > 0.0001f)");
        sb.AppendLine("            {");
        sb.AppendLine("                wpx = bx / bd;");
        sb.AppendLine("                wpz = bz / bd;");
        sb.AppendLine("            }");
        sb.AppendLine("            if (bd < arrive) { an = nb; }");
        sb.AppendLine("        }");
        sb.AppendLine();
        AppendSteering(sb, dirs, near, far, "wpx", "wpz", "jog");
        AppendYAndFace(sb);
        sb.AppendLine("        if (stuck > 0.55f)");
        sb.AppendLine("        {");
        sb.AppendLine("            detour = 1.2f;");
        sb.AppendLine("            an = -1f;");
        sb.AppendLine("            tn = -1f;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // Eight samples along the straight line, tested with agent clearance.");
        sb.AppendLine("    bool Clear(float ax, float az, float bx, float bz)");
        sb.AppendLine("    {");
        for (int i = 1; i <= 8; i++)
        {
            float t = i / 9f;
            sb.AppendLine($"        if (Hit(ax + (bx - ax) * {Number(t)}, az + (bz - az) * {Number(t)})) {{ return false; }}");
        }
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();
        AppendHitHelper(sb, walls, radius);
        AppendBlockedHelper(sb, walls, radius);
        AppendFreeHelper(sb, walls);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static List<(float Dx, float Dz)> Directions(int count)
    {
        var dirs = new List<(float Dx, float Dz)>();
        for (int i = 0; i < count; i++)
        {
            float angle = MathF.PI * 2f * i / count;
            dirs.Add((MathF.Round(MathF.Cos(angle), 4), MathF.Round(MathF.Sin(angle), 4)));
        }
        return dirs;
    }

    /// <summary>
    /// Emits the shared sensor core: near/far probe ring, free-direction scoring biased
    /// toward the desired heading, hysteresis, and the movement step with an escape path
    /// for an agent that starts overlapping geometry.
    /// </summary>
    private static void AppendSteering(StringBuilder sb, IReadOnlyList<(float Dx, float Dz)> dirs,
        float near, float far, string desiredX, string desiredZ, string speed = "speed")
    {
        sb.AppendLine("        // Probe ring: near and far sample per direction.");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"        float n{i}x = px + {Number(ddx * near)}; float n{i}z = pz + {Number(ddz * near)};");
            sb.AppendLine($"        float f{i}x = px + {Number(ddx * far)}; float f{i}z = pz + {Number(ddz * far)};");
            sb.AppendLine($"        float h{i} = 0f;");
            sb.AppendLine($"        if (Hit(n{i}x, n{i}z)) {{ h{i} = 1f; }}");
            sb.AppendLine($"        if (Hit(f{i}x, f{i}z)) {{ h{i} = 1f; }}");
        }
        sb.AppendLine();
        sb.AppendLine("        // How clear is the wanted heading? The best free direction within reach.");
        sb.AppendLine("        float fw = -2f;");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"        if (h{i} < 0.5f) {{ fw = Mathf.Max(fw, {desiredX} * {Number(ddx)} + {desiredZ} * {Number(ddz)}); }}");
        }
        sb.AppendLine($"        float vx = {desiredX};");
        sb.AppendLine($"        float vz = {desiredZ};");
        sb.AppendLine("        if (fw < 0.6f)");
        sb.AppendLine("        {");
        sb.AppendLine("            // The wanted heading is blocked on this face. Commit to sliding along the");
        sb.AppendLine("            // obstacle in ONE direction and keep it until the target heading opens up");
        sb.AppendLine("            // again; re-scoring toward the target every frame is what used to make the");
        sb.AppendLine("            // agent rock back and forth in front of a wall instead of going round it.");
        sb.AppendLine("            if (follow <= 0.0001f)");
        sb.AppendLine("            {");
        sb.AppendLine("                float cpx = -vz * side;");
        sb.AppendLine("                float cpz = vx * side;");
        sb.AppendLine("                float ls = -2f;");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"                if (h{i} < 0.5f) {{ ls = Mathf.Max(ls, cpx * {Number(ddx)} + cpz * {Number(ddz)}); }}");
        }
        sb.AppendLine("                if (ls < 0.2f) { side = -side; }");
        sb.AppendLine("                follow = 0.001f;");
        sb.AppendLine("            }");
        sb.AppendLine("            follow = follow + Time.DeltaTime;");
        sb.AppendLine($"            vx = -{desiredZ} * side;");
        sb.AppendLine($"            vz = {desiredX} * side;");
        sb.AppendLine("            float ss = -2f;");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"            if (h{i} < 0.5f) {{ ss = Mathf.Max(ss, vx * {Number(ddx)} + vz * {Number(ddz)}); }}");
        }
        sb.AppendLine("            if (ss < 0.2f) { side = -side; vx = -vx; vz = -vz; }");
        sb.AppendLine("            if (follow > 5f) { follow = 0f; side = -side; }");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            follow = 0f;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        // Score free directions by how directly they follow the steering vector.");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"        float s{i} = -2f;");
            sb.AppendLine($"        if (h{i} < 0.5f) {{ s{i} = vx * {Number(ddx)} + vz * {Number(ddz)}; }}");
            sb.AppendLine($"        if (last == {i}f) {{ if (h{i} < 0.5f) {{ s{i} = s{i} + 0.25f; }} }}");
        }
        sb.AppendLine("        float pick = -1f;");
        sb.AppendLine("        float best = -3f;");
        for (int i = 0; i < dirs.Count; i++)
            sb.AppendLine($"        if (s{i} > best) {{ best = s{i}; pick = {i}f; }}");
        sb.AppendLine("        if (pick < -0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            // Enclosed on every side we can reach: back out along any free probe.");
        for (int i = 0; i < dirs.Count; i++)
            sb.AppendLine($"            {(i == 0 ? "" : "else ")}if (h{i} < 0.5f) {{ pick = {i}f; }}");
        sb.AppendLine("            spin = -spin;");
        sb.AppendLine("        }");
        sb.AppendLine("        float hx = vx;");
        sb.AppendLine("        float hz = vz;");
        for (int i = 0; i < dirs.Count; i++)
        {
            var (ddx, ddz) = dirs[i];
            sb.AppendLine($"        {(i == 0 ? "" : "else ")}if (pick == {i}f) {{ hx = {Number(ddx)}; hz = {Number(ddz)}; }}");
        }
        sb.AppendLine("        last = pick;");
        sb.AppendLine();
        sb.AppendLine($"        float stepLen = {speed} * Time.DeltaTime;");
        sb.AppendLine("        float nx = px + hx * stepLen;");
        sb.AppendLine("        float nz = pz + hz * stepLen;");
        sb.AppendLine("        float pinned = 0f;");
        sb.AppendLine("        if (Blocked(px, pz)) { pinned = 1f; }");
        sb.AppendLine("        if (pinned > 0.5f)");
        sb.AppendLine("        {");
        sb.AppendLine("            // Already overlapping a wall (block flush against it, or a group body):");
        sb.AppendLine("            // slide out with point tests instead of refusing every step forever.");
        sb.AppendLine("            if (Free(nx, nz))");
        sb.AppendLine("            {");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                if (Free(nx, pz)) { nz = pz; }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    if (Free(px, nz)) { nx = px; }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            if (Blocked(nx, nz))");
        sb.AppendLine("            {");
        sb.AppendLine("                if (!Blocked(nx, pz)) { nz = pz; }");
        sb.AppendLine("                else");
        sb.AppendLine("                {");
        sb.AppendLine("                    if (!Blocked(px, nz)) { nx = px; }");
        sb.AppendLine("                    else { nx = px; nz = pz; }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        mover.Position.X = nx;");
        sb.AppendLine("        mover.Position.Z = nz;");
        sb.AppendLine("        float moved = Mathf.Sqrt((nx - px) * (nx - px) + (nz - pz) * (nz - pz));");
        sb.AppendLine("        if (moved < stepLen * 0.4f)");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = stuck + Time.DeltaTime;");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = 0f;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (stuck > 0.8f)");
        sb.AppendLine("        {");
        sb.AppendLine("            stuck = 0f;");
        sb.AppendLine("            last = -1f;");
        sb.AppendLine("            side = -side;");
        sb.AppendLine("            spin = -spin;");
        sb.AppendLine("            if (pinned > 0.5f)");
        sb.AppendLine("            {");
        sb.AppendLine("                mover.Position.X = px + hx * 0.5f;");
        sb.AppendLine("                mover.Position.Z = pz + hz * 0.5f;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
    }

    private static void AppendYAndFace(StringBuilder sb)
    {
        sb.AppendLine("        float dy = goal.Position.Y - mover.Position.Y;");
        sb.AppendLine("        if (dy > 0.35f)");
        sb.AppendLine("        {");
        sb.AppendLine("            mover.Position.Y = mover.Position.Y + Mathf.Min(dy, 3f * Time.DeltaTime);");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            if (dy < -0.35f) { mover.Position.Y = mover.Position.Y - Mathf.Min(-dy, 3f * Time.DeltaTime); }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (hx * hx + hz * hz > 0.0001f) { mover.Rotation.Y = Mathf.Atan2(hx, hz) * 57.29578f; }");
    }

    /// <summary>
    /// Clearance test used by whisker probes and line samples. Inflated exactly like the
    /// movement test, so a direction scored as free is one the agent can really move in.
    /// </summary>
    private static void AppendHitHelper(StringBuilder sb, IReadOnlyList<LevelNode> walls, float radius)
    {
        sb.AppendLine("    bool Hit(float x, float z)");
        sb.AppendLine("    {");
        for (int i = 0; i < walls.Count; i++)
        {
            var (ex, _, ez) = Extents(walls[i]);
            float hx = Math.Abs(ex) + radius * 0.9f;
            float hz = Math.Abs(ez) + radius * 0.9f;
            sb.AppendLine($"        if (Mathf.Abs(x - w{i}.Position.X) < {Number(hx)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (Mathf.Abs(z - w{i}.Position.Z) < {Number(hz)}) {{ return true; }}");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>Agent-radius block test used by the movement step.</summary>
    private static void AppendBlockedHelper(StringBuilder sb, IReadOnlyList<LevelNode> walls, float radius)
    {
        sb.AppendLine("    bool Blocked(float x, float z)");
        sb.AppendLine("    {");
        for (int i = 0; i < walls.Count; i++)
        {
            var (ex, _, ez) = Extents(walls[i]);
            float bx = Math.Abs(ex) + radius;
            float bz = Math.Abs(ez) + radius;
            sb.AppendLine($"        if (Mathf.Abs(x - w{i}.Position.X) < {Number(bx)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (Mathf.Abs(z - w{i}.Position.Z) < {Number(bz)}) {{ return true; }}");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>Raw point test (no radius): the escape path out of an overlapping start.</summary>
    private static void AppendFreeHelper(StringBuilder sb, IReadOnlyList<LevelNode> walls)
    {
        sb.AppendLine("    bool Free(float x, float z)");
        sb.AppendLine("    {");
        for (int i = 0; i < walls.Count; i++)
        {
            var (ex, _, ez) = Extents(walls[i]);
            sb.AppendLine($"        if (Mathf.Abs(x - w{i}.Position.X) < {Number(Math.Abs(ex))})");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (Mathf.Abs(z - w{i}.Position.Z) < {Number(Math.Abs(ez))}) {{ return false; }}");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
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
        // The agent's own body (and the target's) must never count as an obstacle: a
        // group finder keeps its visible blocks as children, and a block standing against
        // a wall overlaps it at spawn. Either case used to pin the agent in place forever.
        var self = new HashSet<LevelNode>(finder.SelfAndDescendants());
        foreach (var n in target.SelfAndDescendants()) self.Add(n);
        return level.AllNodes()
            .Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling)
            .Where(n => !n.IsPassable && !self.Contains(n))
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
