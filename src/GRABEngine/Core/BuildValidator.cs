using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum BuildIssueKind { Error, Warning, Info }

public sealed class BuildIssue
{
    public LevelNode? Node { get; init; }
    public Vec3 Position { get; init; }
    public Vec3 Size { get; init; } = new(1, 1, 1);
    public BuildIssueKind Kind { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public override string ToString() => $"[{Kind}] {Category}: {Message}";
}

public sealed class BuildReport
{
    public List<BuildIssue> Issues { get; } = new();
    public int Errors => Issues.Count(i => i.Kind == BuildIssueKind.Error);
    public int Warnings => Issues.Count(i => i.Kind == BuildIssueKind.Warning);
    public bool Clean => Errors == 0 && Warnings == 0;
    public string Summary => Clean
        ? "No build problems found."
        : $"{Errors} error(s), {Warnings} warning(s), {Issues.Count(i => i.Kind == BuildIssueKind.Info)} note(s)";
}

/// <summary>
/// Level-wide checks that catch the mistakes which only show up after exporting:
/// floating geometry, overlaps, dead triggers, missing goals and a reachability
/// estimate using the play-mode jump height.
/// </summary>
public static class BuildValidator
{
    public static BuildReport Validate(GrabLevel level)
    {
        var report = new BuildReport();
        var nodes = level.AllNodes().ToList();
        var solid = nodes.Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && !n.IsPassable).ToList();

        if (!nodes.Any(n => n.Kind == NodeKind.Start))
            report.Issues.Add(new BuildIssue { Kind = BuildIssueKind.Error, Category = "Level", Message = "No Start point. The level cannot be spawned into." });
        if (!nodes.Any(n => n.Kind == NodeKind.Finish))
            report.Issues.Add(new BuildIssue { Kind = BuildIssueKind.Error, Category = "Level", Message = "No Finish point. The level can never be completed." });

        foreach (var (node, severity, message) in GrabLimits.ValidateLevel(level))
            report.Issues.Add(new BuildIssue
            {
                Node = node,
                Position = node?.WorldPosition ?? Vec3.Zero,
                Size = node != null ? SizeOf(node) : Vec3.One,
                Kind = severity == DiagnosticSeverity.Error ? BuildIssueKind.Error : BuildIssueKind.Warning,
                Category = "GRAB limits",
                Message = message
            });

        // Floating blocks: nothing solid underneath the footprint.
        int floating = 0;
        foreach (var n in solid)
        {
            if (n.Parent != null || n.Kind == NodeKind.Crumbling) continue;
            var (c, s) = Rendering.SceneBuilder.BoundsOf(n);
            float bottom = c.Y - MathF.Abs(s.Y) * 0.5f;
            if (bottom < 0.06f) continue;
            bool supported = false;
            foreach (var other in solid)
            {
                if (ReferenceEquals(n, other)) continue;
                var (oc, os) = Rendering.SceneBuilder.BoundsOf(other);
                if (MathF.Abs(oc.X - c.X) > (MathF.Abs(s.X) + MathF.Abs(os.X)) * 0.5f + 0.2f) continue;
                if (MathF.Abs(oc.Z - c.Z) > (MathF.Abs(s.Z) + MathF.Abs(os.Z)) * 0.5f + 0.2f) continue;
                float top = oc.Y + MathF.Abs(os.Y) * 0.5f;
                if (MathF.Abs(top - bottom) < 0.14f) { supported = true; break; }
            }
            if (!supported && floating++ < 40)
                report.Issues.Add(new BuildIssue
                {
                    Node = n, Position = n.WorldPosition, Size = SizeOf(n),
                    Kind = BuildIssueKind.Warning, Category = "Floating",
                    Message = $"'{n.DisplayName}' has nothing directly underneath."
                });
        }
        if (floating > 40)
            report.Issues.Add(new BuildIssue { Kind = BuildIssueKind.Info, Category = "Floating", Message = $"{floating - 40} more unsupported block(s) not listed." });

        // Overlapping blocks. Only pairs whose centres are close are tested.
        int overlaps = 0;
        for (int i = 0; i < solid.Count && overlaps < 40; i++)
        {
            var a = solid[i];
            var (ac, asz) = Rendering.SceneBuilder.BoundsOf(a);
            var ah = new Vec3(MathF.Abs(asz.X) * 0.5f, MathF.Abs(asz.Y) * 0.5f, MathF.Abs(asz.Z) * 0.5f);
            for (int j = i + 1; j < solid.Count; j++)
            {
                var b = solid[j];
                var (bc, bsz) = Rendering.SceneBuilder.BoundsOf(b);
                var bh = new Vec3(MathF.Abs(bsz.X) * 0.5f, MathF.Abs(bsz.Y) * 0.5f, MathF.Abs(bsz.Z) * 0.5f);
                if (MathF.Abs(ac.X - bc.X) >= ah.X + bh.X - 0.04f) continue;
                if (MathF.Abs(ac.Y - bc.Y) >= ah.Y + bh.Y - 0.04f) continue;
                if (MathF.Abs(ac.Z - bc.Z) >= ah.Z + bh.Z - 0.04f) continue;
                // Blocks that merely touch face-to-face are not overlaps.
                int deep = 0;
                if (MathF.Abs(ac.X - bc.X) < ah.X + bh.X - 0.12f) deep++;
                if (MathF.Abs(ac.Y - bc.Y) < ah.Y + bh.Y - 0.12f) deep++;
                if (MathF.Abs(ac.Z - bc.Z) < ah.Z + bh.Z - 0.12f) deep++;
                if (deep < 3) continue;
                overlaps++;
                report.Issues.Add(new BuildIssue
                {
                    Node = b, Position = bc, Size = bsz,
                    Kind = BuildIssueKind.Info, Category = "Overlap",
                    Message = $"'{a.DisplayName}' and '{b.DisplayName}' occupy the same space."
                });
                if (overlaps >= 40) break;
            }
        }
        if (overlaps >= 40)
            report.Issues.Add(new BuildIssue { Kind = BuildIssueKind.Info, Category = "Overlap", Message = "Overlap scan stopped after 40 pairs." });

        foreach (var t in nodes.Where(n => n.Kind == NodeKind.Trigger))
        {
            if (t.TriggerSources.Count == 0 && t.TriggerTargets.Count == 0)
                report.Issues.Add(new BuildIssue
                {
                    Node = t, Position = t.WorldPosition, Size = SizeOf(t),
                    Kind = BuildIssueKind.Warning, Category = "Trigger",
                    Message = $"'{t.DisplayName}' has no sources and no targets."
                });
            else if (t.TriggerTargets.Count == 0)
                report.Issues.Add(new BuildIssue
                {
                    Node = t, Position = t.WorldPosition, Size = SizeOf(t),
                    Kind = BuildIssueKind.Warning, Category = "Trigger",
                    Message = $"'{t.DisplayName}' never activates anything."
                });
            else if (t.TriggerTargets.Count > GrabLimits.MaxTriggerTargets)
                report.Issues.Add(new BuildIssue
                {
                    Node = t, Position = t.WorldPosition, Size = SizeOf(t),
                    Kind = BuildIssueKind.Error, Category = "Trigger",
                    Message = $"'{t.DisplayName}' has {t.TriggerTargets.Count} targets; GRAB allows {GrabLimits.MaxTriggerTargets}."
                });
            foreach (var target in t.TriggerTargets)
            {
                if (target.ObjectId != 0) continue;
                if (!string.IsNullOrWhiteSpace(target.ObjectName) && level.FindByName(target.ObjectName) != null) continue;
                if (target.Kind is 6) continue;
                report.Issues.Add(new BuildIssue
                {
                    Node = t, Position = t.WorldPosition, Size = SizeOf(t),
                    Kind = BuildIssueKind.Warning, Category = "Trigger",
                    Message = $"'{t.DisplayName}' targets a missing object '{(string.IsNullOrWhiteSpace(target.ObjectName) ? "?" : target.ObjectName)}'."
                });
            }
        }

        foreach (var code in nodes.Where(n => n.Kind == NodeKind.GASM))
        {
            if (code.Compiled == null && code.ImportedProgram == null && string.IsNullOrWhiteSpace(code.Script))
                report.Issues.Add(new BuildIssue
                {
                    Node = code, Position = code.WorldPosition, Size = SizeOf(code),
                    Kind = BuildIssueKind.Warning, Category = "Script",
                    Message = $"'{code.DisplayName}' is an empty Code Block."
                });
            else if (code.Compiled is { Success: false })
                report.Issues.Add(new BuildIssue
                {
                    Node = code, Position = code.WorldPosition, Size = SizeOf(code),
                    Kind = BuildIssueKind.Error, Category = "Script",
                    Message = $"'{code.DisplayName}' has {code.Compiled.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} compile error(s)."
                });
        }

        // Tiny invisible slivers are almost always leftover brush cells.
        foreach (var n in solid)
        {
            var s = n.WorldScale;
            if (MathF.Abs(s.X) < 0.02f || MathF.Abs(s.Y) < 0.02f || MathF.Abs(s.Z) < 0.02f)
                report.Issues.Add(new BuildIssue
                {
                    Node = n, Position = n.WorldPosition, Size = SizeOf(n),
                    Kind = BuildIssueKind.Info, Category = "Geometry",
                    Message = $"'{n.DisplayName}' is effectively zero-sized."
                });
        }

        AddReachability(level, nodes, report);
        return report;
    }

    private static Vec3 SizeOf(LevelNode n)
    {
        var (_, s) = Rendering.SceneBuilder.BoundsOf(n);
        return new Vec3(MathF.Max(0.1f, MathF.Abs(s.X)), MathF.Max(0.1f, MathF.Abs(s.Y)), MathF.Max(0.1f, MathF.Abs(s.Z)));
    }

    /// <summary>
    /// Coarse platform graph: a player can step up 1.2 m and jump roughly 2.6 m across.
    /// If the finish is not in the connected component of the spawn, the level is very
    /// likely impossible; the check is deliberately conservative and reports it as a
    /// warning rather than an error.
    /// </summary>
    private static void AddReachability(GrabLevel level, List<LevelNode> nodes, BuildReport report)
    {
        var start = nodes.FirstOrDefault(n => n.Kind == NodeKind.Start);
        var finish = nodes.FirstOrDefault(n => n.Kind == NodeKind.Finish);
        if (start == null || finish == null) return;
        var tops = new List<(LevelNode Node, Vec3 Center, float Top, float Radius)>();
        foreach (var n in nodes.Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && !n.IsPassable))
        {
            var (c, s) = Rendering.SceneBuilder.BoundsOf(n);
            if (MathF.Abs(s.Y) < 0.15f) continue;
            float radius = MathF.Max(MathF.Abs(s.X), MathF.Abs(s.Z)) * 0.5f;
            if (radius < 0.12f) continue;
            tops.Add((n, new Vec3(c.X, c.Y + MathF.Abs(s.Y) * 0.5f, c.Z), c.Y + MathF.Abs(s.Y) * 0.5f, radius));
            if (tops.Count > 1200) break;
        }
        if (tops.Count == 0 || tops.Count > 1200) return;

        int seed = -1;
        float best = float.MaxValue;
        for (int i = 0; i < tops.Count; i++)
        {
            var d = tops[i].Center - start.WorldPosition;
            float dist = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            if (dist < best) { best = dist; seed = i; }
        }
        if (seed < 0) return;
        var seen = new bool[tops.Count];
        var queue = new Queue<int>();
        seen[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            var a = tops[i];
            for (int j = 0; j < tops.Count; j++)
            {
                if (seen[j]) continue;
                var b = tops[j];
                var d = b.Center - a.Center;
                float horizontal = MathF.Sqrt(d.X * d.X + d.Z * d.Z) - a.Radius - b.Radius;
                if (horizontal > 2.7f) continue;
                if (d.Y > 1.25f || d.Y < -12f) continue;
                seen[j] = true;
                queue.Enqueue(j);
            }
        }
        bool finishReached = false;
        for (int i = 0; i < tops.Count; i++)
        {
            if (!seen[i]) continue;
            var d = tops[i].Center - finish.WorldPosition;
            if (MathF.Sqrt(d.X * d.X + d.Z * d.Z) <= tops[i].Radius + MathF.Max(0.6f, finish.Radius) + 0.4f &&
                MathF.Abs(d.Y) < 2.4f)
            {
                finishReached = true;
                break;
            }
        }
        if (!finishReached)
            report.Issues.Add(new BuildIssue
            {
                Node = finish, Position = finish.WorldPosition, Size = Vec3.One,
                Kind = BuildIssueKind.Warning, Category = "Reachability",
                Message = "The finish may be unreachable: no 2.7 m jump / 1.25 m step path connects it to the start (estimate)."
            });
    }
}
