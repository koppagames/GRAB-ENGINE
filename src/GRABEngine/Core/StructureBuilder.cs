using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum StructureKind
{
    Stairs, Ramp, SpiralStairs, Bridge, Arch, Pillar, Doorway, Tower,
    GableRoof, Fence, Platform, Frame, SphereShell, Dome, Torus, CylinderShell
}

public sealed class StructureSettings
{
    public Vec3 Origin { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;
    public float Cell { get; set; } = 1f;
    public float Width { get; set; } = 4f;
    public float Length { get; set; } = 6f;
    public float Height { get; set; } = 3f;
    public float Thickness { get; set; } = 0.35f;
    public int Steps { get; set; } = 6;
    public int Sides { get; set; } = 12;
    public float Radius { get; set; } = 2.5f;
    public int Material { get; set; } = 8;
    public Col Color { get; set; } = new(0.72f, 0.72f, 0.72f, 1);
    public bool Railings { get; set; } = true;
    public bool Hollow { get; set; } = true;
    public int Depth { get; set; } = 1;
    public bool MirrorX { get; set; }
    public bool MirrorZ { get; set; }
    public Vec3 MirrorPivot { get; set; } = Vec3.Zero;
    public string GroupName { get; set; } = "";

    public StructureSettings Clone() => (StructureSettings)MemberwiseClone();
}

public sealed class StructurePlan
{
    public string Name { get; init; } = "";
    public List<LevelNode> Nodes { get; } = new();
    public string Summary => $"{Name}: {Nodes.Count} block(s)";
}

/// <summary>
/// Builds editable GRAB blocks for common level-building shapes. Everything returns
/// ordinary LevelNode objects so the result participates in undo, prefabs, layers and
/// the .level exporter exactly like hand-placed blocks.
/// </summary>
public static class StructureBuilder
{
    public static StructurePlan Build(StructureKind kind, StructureSettings s)
    {
        var plan = new StructurePlan { Name = kind.ToString() };
        var nodes = kind switch
        {
            StructureKind.Stairs => Stairs(s),
            StructureKind.Ramp => Ramp(s),
            StructureKind.SpiralStairs => SpiralStairs(s),
            StructureKind.Bridge => Bridge(s),
            StructureKind.Arch => Arch(s),
            StructureKind.Pillar => Pillar(s),
            StructureKind.Doorway => Doorway(s),
            StructureKind.Tower => Tower(s),
            StructureKind.GableRoof => GableRoof(s),
            StructureKind.Fence => Fence(s),
            StructureKind.Platform => Platform(s),
            StructureKind.Frame => Frame(s),
            StructureKind.SphereShell => VoxelSphere(s),
            StructureKind.Dome => Dome(s),
            StructureKind.Torus => Torus(s),
            StructureKind.CylinderShell => CylinderShell(s),
            _ => new List<LevelNode>()
        };
        foreach (var n in nodes) n.Material = s.Material;
        foreach (var n in nodes)
        {
            n.Color1 = s.Color;
            if (n.EditorLabel.Length == 0) n.EditorLabel = "structure";
        }
        var mirrored = new List<LevelNode>();
        if (s.MirrorX || s.MirrorZ)
            mirrored = Mirror(nodes, s.MirrorX, s.MirrorZ, s.MirrorPivot);
        plan.Nodes.AddRange(nodes);
        plan.Nodes.AddRange(mirrored);
        return plan;
    }

    private static LevelNode Block(Vec3 position, Vec3 scale, Quat? rotation = null)
        => new()
        {
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Position = position,
            Scale = ScaleAtLeast(scale),
            Rotation = rotation ?? Quat.Identity,
            Material = 8,
            Color1 = new Col(0.72f, 0.72f, 0.72f, 1)
        };

    private static Vec3 ScaleAtLeast(Vec3 s) => new(
        MathF.Max(0.02f, MathF.Abs(s.X)),
        MathF.Max(0.02f, MathF.Abs(s.Y)),
        MathF.Max(0.02f, MathF.Abs(s.Z)));

    public static List<LevelNode> Stairs(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int steps = Math.Clamp(s.Steps, 1, 64);
        float width = MathF.Max(0.5f, s.Width);
        float depth = MathF.Max(0.15f, s.Length / steps);
        float rise = MathF.Max(0.05f, s.Height / steps);
        for (int i = 0; i < steps; i++)
        {
            float top = rise * (i + 1);
            var local = new Vec3(0, top * 0.5f, depth * (i + 0.5f));
            var node = Block(s.Origin + s.Rotation.Rotate(local), new Vec3(width, top, depth), s.Rotation);
            nodes.Add(node);
        }
        if (s.Railings)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                for (int i = 0; i < steps; i++)
                {
                    float top = rise * (i + 1);
                    var local = new Vec3(side * (width * 0.5f + 0.06f), top + 0.55f, depth * (i + 0.5f));
                    nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.1f, 1.1f, 0.1f), s.Rotation));
                }
                // Hand rail as a thin sloped slab following the steps.
                float angle = MathF.Atan2(s.Height, s.Length) * 180f / MathF.PI;
                var railLocal = new Vec3(side * (width * 0.5f + 0.06f), s.Height * 0.5f + 1.05f, s.Length * 0.5f);
                var rot = s.Rotation * Quat.FromAxisAngle(Vec3.Right, -angle);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(railLocal), new Vec3(0.1f, 0.1f, MathF.Sqrt(s.Length * s.Length + s.Height * s.Height)), rot));
            }
        }
        return nodes;
    }

    public static List<LevelNode> Ramp(StructureSettings s)
    {
        float length = MathF.Max(0.5f, s.Length);
        float height = MathF.Max(0.05f, s.Height);
        float angle = MathF.Atan2(height, length) * 180f / MathF.PI;
        var rot = s.Rotation * Quat.FromAxisAngle(Vec3.Right, -angle);
        var local = new Vec3(0, height * 0.5f, length * 0.5f);
        var nodes = new List<LevelNode>
        {
            Block(s.Origin + s.Rotation.Rotate(local), new Vec3(MathF.Max(0.5f, s.Width), MathF.Max(0.06f, s.Thickness),
                MathF.Sqrt(length * length + height * height)), rot)
        };
        // A low side kerb makes the ramp readable from the side and less likely to
        // be missed when the camera is looking along it.
        if (s.Railings)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                var kerb = new Vec3(side * (s.Width * 0.5f + 0.1f), height * 0.5f + 0.18f, length * 0.5f);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(kerb), new Vec3(0.12f, 0.22f,
                    MathF.Sqrt(length * length + height * height)), rot));
            }
        }
        return nodes;
    }

    public static List<LevelNode> SpiralStairs(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int steps = Math.Clamp(s.Steps, 3, 96);
        float radius = MathF.Max(0.6f, s.Radius);
        float rise = MathF.Max(0.05f, s.Height / steps);
        float cell = MathF.Max(0.3f, s.Cell);
        for (int i = 0; i < steps; i++)
        {
            float angle = i * (360f / MathF.Max(6, s.Sides));
            float rad = angle * MathF.PI / 180f;
            float top = rise * (i + 1);
            var local = new Vec3(MathF.Cos(rad) * radius, top * 0.5f, MathF.Sin(rad) * radius);
            var yaw = Quat.FromAxisAngle(Vec3.Up, -angle);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(cell, top, cell), s.Rotation * yaw));
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(MathF.Cos(rad) * radius, top + 0.55f, MathF.Sin(rad) * radius)),
                new Vec3(0.08f, 1.1f, 0.08f), s.Rotation));
        }
        // Central newel post.
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, s.Height * 0.5f, 0)),
            new Vec3(cell * 0.65f, s.Height + 1.1f, cell * 0.65f), s.Rotation));
        return nodes;
    }

    public static List<LevelNode> Bridge(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float length = MathF.Max(1, s.Length);
        float width = MathF.Max(1, s.Width);
        float thickness = MathF.Max(0.15f, s.Thickness);
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, -thickness * 0.5f, length * 0.5f)),
            new Vec3(width, thickness, length), s.Rotation));
        if (s.Railings)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                var rail = s.Origin + s.Rotation.Rotate(new Vec3(side * (width * 0.5f + 0.08f), 0.55f, length * 0.5f));
                nodes.Add(Block(rail, new Vec3(0.1f, 1.1f, length), s.Rotation));
                for (int i = 0; i <= 4; i++)
                {
                    var post = s.Origin + s.Rotation.Rotate(new Vec3(side * (width * 0.5f + 0.08f), 0.1f, length * i / 4f));
                    nodes.Add(Block(post, new Vec3(0.14f, 1.3f, 0.14f), s.Rotation));
                }
            }
        }
        // Support piers at both ends and the middle.
        foreach (var t in new[] { 0.08f, 0.5f, 0.92f })
        {
            float z = length * t;
            foreach (var side in new[] { -1f, 1f })
            {
                var local = new Vec3(side * (width * 0.5f - 0.25f), -s.Height * 0.5f - thickness, z);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.4f, MathF.Max(0.5f, s.Height), 0.4f), s.Rotation));
            }
        }
        return nodes;
    }

    public static List<LevelNode> Arch(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int sides = Math.Clamp(s.Sides, 5, 48);
        float radius = MathF.Max(0.6f, s.Radius);
        float blockW = MathF.Max(0.15f, (MathF.PI * radius) / sides * 1.08f);
        float blockH = MathF.Max(0.12f, MathF.Min(blockW, s.Thickness * 1.6f));
        float depth = MathF.Max(0.2f, s.Thickness * 2.2f);
        for (int i = 0; i <= sides; i++)
        {
            float angle = 180f * i / sides;
            float rad = angle * MathF.PI / 180f;
            var local = new Vec3(MathF.Cos(rad) * radius, MathF.Sin(rad) * radius, 0);
            var rot = s.Rotation * Quat.FromAxisAngle(Vec3.Forward, angle);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(blockW, blockH, depth), rot));
        }
        // Legs down to the ground.
        foreach (var side in new[] { -1f, 1f })
        {
            float x = side * radius;
            var leg = new Vec3(x, -s.Height * 0.5f, 0);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(leg), new Vec3(blockW * 1.4f, MathF.Max(0.2f, s.Height), depth), s.Rotation));
        }
        return nodes;
    }

    public static List<LevelNode> Pillar(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float height = MathF.Max(0.4f, s.Height);
        float radius = MathF.Max(0.3f, s.Radius * 0.5f);
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, 0.12f, 0)), new Vec3(radius * 2.4f, 0.24f, radius * 2.4f), s.Rotation));
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, height * 0.5f, 0)), new Vec3(radius * 1.6f, height, radius * 1.6f), s.Rotation));
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, height + 0.16f, 0)), new Vec3(radius * 2.4f, 0.32f, radius * 2.4f), s.Rotation));
        return nodes;
    }

    public static List<LevelNode> Doorway(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float height = MathF.Max(1.2f, s.Height);
        float width = MathF.Max(1f, s.Width);
        float depth = MathF.Max(0.25f, s.Thickness);
        foreach (var side in new[] { -1f, 1f })
        {
            var local = new Vec3(side * (width * 0.5f + 0.12f), height * 0.5f, 0);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.24f, height, depth), s.Rotation));
        }
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, height + 0.16f, 0)),
            new Vec3(width + 0.7f, 0.32f, depth), s.Rotation));
        return nodes;
    }

    public static List<LevelNode> Tower(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int sides = Math.Clamp(s.Sides, 6, 32);
        int layers = Math.Max(1, (int)MathF.Round(s.Height / MathF.Max(0.3f, s.Cell)));
        float radius = MathF.Max(1f, s.Radius);
        float cell = MathF.Max(0.3f, s.Cell);
        for (int y = 0; y < layers; y++)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = 360f * i / sides;
                float rad = a * MathF.PI / 180f;
                var local = new Vec3(MathF.Cos(rad) * radius, y * cell + cell * 0.5f, MathF.Sin(rad) * radius);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(cell, cell, cell),
                    s.Rotation * Quat.FromAxisAngle(Vec3.Up, -a)));
            }
        }
        // Walkway/parapet and a roof ring.
        for (int i = 0; i < sides; i++)
        {
            float a = 360f * i / sides;
            float rad = a * MathF.PI / 180f;
            var local = new Vec3(MathF.Cos(rad) * radius, layers * cell + 0.5f, MathF.Sin(rad) * radius);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(cell * 1.4f, 1f, cell * 0.4f),
                s.Rotation * Quat.FromAxisAngle(Vec3.Up, -a)));
        }
        return nodes;
    }

    public static List<LevelNode> GableRoof(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float width = MathF.Max(1, s.Width);
        float length = MathF.Max(1, s.Length);
        float height = MathF.Max(0.5f, s.Height);
        float angle = MathF.Atan2(height, width * 0.5f) * 180f / MathF.PI;
        float slope = MathF.Sqrt(height * height + width * width * 0.25f);
        foreach (var side in new[] { -1f, 1f })
        {
            var local = new Vec3(side * width * 0.25f, height * 0.5f, length * 0.5f);
            var rot = s.Rotation * Quat.FromAxisAngle(Vec3.Forward, side * angle);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(slope, 0.22f, length), rot));
        }
        foreach (var side in new[] { -1f, 1f })
        {
            var local = new Vec3(0, height * 0.5f, length * (side > 0 ? 1 : 0));
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(width, height, 0.18f), s.Rotation));
        }
        return nodes;
    }

    public static List<LevelNode> Fence(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float length = MathF.Max(1, s.Length);
        int posts = Math.Max(2, (int)MathF.Round(length / 2f) + 1);
        float height = MathF.Max(0.6f, s.Height);
        for (int i = 0; i < posts; i++)
        {
            var local = new Vec3(0, height * 0.5f, length * i / (posts - 1f));
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.16f, height, 0.16f), s.Rotation));
        }
        foreach (var y in new[] { 0.35f, 0.75f })
        {
            var local = new Vec3(0, height * y, length * 0.5f);
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.09f, 0.09f, length), s.Rotation));
        }
        return nodes;
    }

    public static List<LevelNode> Platform(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float w = MathF.Max(1, s.Width), l = MathF.Max(1, s.Length), t = MathF.Max(0.15f, s.Thickness);
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, -t * 0.5f, l * 0.5f)), new Vec3(w, t, l), s.Rotation));
        foreach (var x in new[] { -0.4f, 0.4f })
            foreach (var z in new[] { 0.12f, 0.88f })
            {
                var local = new Vec3(w * x, -MathF.Max(0.5f, s.Height) * 0.5f - t, l * z);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(0.28f, MathF.Max(0.5f, s.Height), 0.28f), s.Rotation));
            }
        return nodes;
    }

    public static List<LevelNode> Frame(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float w = MathF.Max(1, s.Width), h = MathF.Max(1, s.Height), l = MathF.Max(0.2f, s.Thickness);
        float t = 0.2f;
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, 0, 0)), new Vec3(w, t, l), s.Rotation));
        nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(0, h, 0)), new Vec3(w, t, l), s.Rotation));
        foreach (var side in new[] { -1f, 1f })
            nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(side * w * 0.5f, h * 0.5f, 0)), new Vec3(t, h, l), s.Rotation));
        return nodes;
    }

    public static List<LevelNode> VoxelSphere(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float radius = MathF.Max(0.5f, s.Radius);
        float cell = MathF.Max(0.25f, s.Cell);
        int n = Math.Clamp((int)MathF.Ceiling(radius / cell), 2, 24);
        for (int x = -n; x <= n; x++)
            for (int y = -n; y <= n; y++)
                for (int z = -n; z <= n; z++)
                {
                    float px = x * cell, py = y * cell, pz = z * cell;
                    float d = MathF.Sqrt(px * px + py * py + pz * pz);
                    if (d > radius) continue;
                    // A hollow shell keeps the inside usable; the band is one cell thick
                    // so the result still reads as a sphere from outside.
                    if (s.Hollow && d < radius - cell) continue;
                    if (nodes.Count >= 3000) break;
                    nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(px, py + radius, pz)), new Vec3(cell, cell, cell), s.Rotation));
                }
        return nodes;
    }

    public static List<LevelNode> Dome(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        float radius = MathF.Max(1f, s.Radius);
        float cell = MathF.Max(0.3f, s.Cell);
        int n = Math.Clamp((int)MathF.Ceiling(radius / cell), 2, 28);
        for (int x = -n; x <= n; x++)
            for (int y = 0; y <= n; y++)
                for (int z = -n; z <= n; z++)
                {
                    float px = x * cell, py = y * cell, pz = z * cell;
                    float d = MathF.Sqrt(px * px + py * py + pz * pz);
                    if (d > radius || (s.Hollow && d < radius - cell * 1.05f)) continue;
                    if (nodes.Count >= 3000) break;
                    nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(px, py, pz)), new Vec3(cell, cell, cell), s.Rotation));
                }
        return nodes;
    }

    public static List<LevelNode> Torus(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int major = Math.Clamp(s.Sides, 8, 48);
        int minor = Math.Clamp(s.Steps, 4, 20);
        float radius = MathF.Max(1f, s.Radius);
        float tube = MathF.Max(0.15f, s.Thickness);
        float cell = MathF.Max(0.2f, s.Cell * 0.6f);
        for (int i = 0; i < major; i++)
        {
            float a = 360f * i / major * MathF.PI / 180f;
            for (int j = 0; j < minor; j++)
            {
                float b = 360f * j / minor * MathF.PI / 180f;
                float x = (radius + tube * MathF.Cos(b)) * MathF.Cos(a);
                float z = (radius + tube * MathF.Cos(b)) * MathF.Sin(a);
                float y = tube * MathF.Sin(b);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(new Vec3(x, y, z)), new Vec3(cell, cell, cell), s.Rotation));
            }
        }
        return nodes;
    }

    public static List<LevelNode> CylinderShell(StructureSettings s)
    {
        var nodes = new List<LevelNode>();
        int sides = Math.Clamp(s.Sides, 8, 64);
        int layers = Math.Max(1, (int)MathF.Round(s.Height / MathF.Max(0.25f, s.Cell)));
        float cell = MathF.Max(0.25f, s.Cell);
        float radius = MathF.Max(0.6f, s.Radius);
        float circumference = 2 * MathF.PI * radius;
        float arcBlock = MathF.Max(0.2f, circumference / sides * 1.08f);
        for (int y = 0; y < layers; y++)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = 360f * i / sides;
                float rad = a * MathF.PI / 180f;
                var local = new Vec3(MathF.Cos(rad) * radius, y * cell + cell * 0.5f, MathF.Sin(rad) * radius);
                nodes.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(arcBlock, cell, cell * 1.15f),
                    s.Rotation * Quat.FromAxisAngle(Vec3.Up, -a)));
            }
        }
        return nodes;
    }

    // ---------------- silhouette / curve / free shapes ----------------

    /// <summary>Extrudes a closed XZ polygon into blocks. Useful for block letters, logos and custom footprints.</summary>
    public static List<LevelNode> ExtrudePolygon(IReadOnlyList<(float X, float Z)> polygon, StructureSettings s)
    {
        var result = new List<LevelNode>();
        if (polygon.Count < 3) return result;
        float cell = MathF.Max(0.15f, s.Cell);
        float minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
        float minZ = polygon.Min(p => p.Z), maxZ = polygon.Max(p => p.Z);
        int layers = Math.Clamp(s.Depth, 1, 64);
        for (float x = minX; x <= maxX; x += cell)
            for (float z = minZ; z <= maxZ; z += cell)
            {
                if (!PointInPolygon(x + cell * 0.5f, z + cell * 0.5f, polygon)) continue;
                for (int y = 0; y < layers; y++)
                {
                    var local = new Vec3(x + cell * 0.5f, y * cell + cell * 0.5f, z + cell * 0.5f);
                    result.Add(Block(s.Origin + s.Rotation.Rotate(local), new Vec3(cell, cell, cell), s.Rotation));
                    if (result.Count >= 6000) return result;
                }
            }
        return result;
    }

    private static bool PointInPolygon(float x, float z, IReadOnlyList<(float X, float Z)> p)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            if ((p[i].Z > z) != (p[j].Z > z) &&
                x < (p[j].X - p[i].X) * (z - p[i].Z) / (p[j].Z - p[i].Z) + p[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Places a line of blocks through a series of world-space points.</summary>
    public static List<LevelNode> Curve(IReadOnlyList<Vec3> points, StructureSettings s)
    {
        var result = new List<LevelNode>();
        if (points.Count < 2) return result;
        float cell = MathF.Max(0.1f, s.Cell);
        float radius = MathF.Max(cell * 0.5f, s.Thickness * 0.5f);
        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            float dist = (b - a).Length;
            if (dist < 1e-4f) continue;
            int steps = Math.Clamp((int)MathF.Ceiling(dist / cell), 1, 2000);
            for (int k = 0; k <= steps; k++)
            {
                var p = Vec3.Lerp(a, b, k / (float)steps);
                result.Add(Block(p, new Vec3(radius * 2, radius * 2, radius * 2), s.Rotation));
                if (result.Count >= 5000) return result;
            }
        }
        return result;
    }

    /// <summary>Voxelises a sphere/box/cylinder/cone selection into blocks of a given cell size.</summary>
    public static List<LevelNode> VoxelShape(int shape, Vec3 center, Vec3 size, float cell, int material, Col color)
    {
        var result = new List<LevelNode>();
        cell = MathF.Max(0.1f, cell);
        var half = new Vec3(MathF.Max(0.05f, size.X) * 0.5f, MathF.Max(0.05f, size.Y) * 0.5f, MathF.Max(0.05f, size.Z) * 0.5f);
        int nx = Math.Clamp((int)MathF.Ceiling(size.X / cell), 1, 96);
        int ny = Math.Clamp((int)MathF.Ceiling(size.Y / cell), 1, 96);
        int nz = Math.Clamp((int)MathF.Ceiling(size.Z / cell), 1, 96);
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++)
                {
                    float lx = (x + 0.5f) * cell / MathF.Max(cell, half.X * 2);
                    float ly = (y + 0.5f) * cell / MathF.Max(cell, half.Y * 2);
                    float lz = (z + 0.5f) * cell / MathF.Max(cell, half.Z * 2);
                    bool inside = shape switch
                    {
                        ShapeInfo.Sphere => Sq(lx * 2 - 1) + Sq(ly * 2 - 1) + Sq(lz * 2 - 1) <= 1f,
                        ShapeInfo.Cylinder => Sq(lx * 2 - 1) + Sq(lz * 2 - 1) <= 1f,
                        ShapeInfo.Cone => Sq(lz * 2 - 1) + Sq(lx * 2 - 1) <= (1 - ly) * (1 - ly),
                        ShapeInfo.Pyramid or ShapeInfo.PyramidSquare => MathF.Abs(lx * 2 - 1) <= 1 - ly && MathF.Abs(lz * 2 - 1) <= 1 - ly,
                        _ => true
                    };
                    if (!inside) continue;
                    var p = new Vec3(center.X - half.X + (x + 0.5f) * cell,
                        center.Y - half.Y + (y + 0.5f) * cell,
                        center.Z - half.Z + (z + 0.5f) * cell);
                    var n = Block(p, new Vec3(cell, cell, cell));
                    n.Shape = ShapeInfo.Cube;
                    n.Material = material;
                    n.Color1 = color;
                    result.Add(n);
                    if (result.Count >= 8000) return result;
                }
        return result;
    }

    private static float Sq(float v) => v * v;

    /// <summary>Clones a set of nodes mirrored across the requested world axes.</summary>
    public static List<LevelNode> Mirror(IReadOnlyList<LevelNode> nodes, bool x, bool z, Vec3 pivot)
    {
        var result = new List<LevelNode>();
        foreach (var n in nodes)
        {
            var c = n.Clone();
            var p = n.WorldPosition;
            c.Position = new Vec3(
                x ? pivot.X - (p.X - pivot.X) : p.X,
                p.Y,
                z ? pivot.Z - (p.Z - pivot.Z) : p.Z);
            // Mirroring flips handedness: negate the yaw (and roll) so ramps and
            // rotated structures face the mirrored way instead of staying lopsided.
            var e = n.Rotation.ToEulerDegrees();
            c.Rotation = Quat.FromEulerDegrees(new Vec3(e.X, x != z ? -e.Y : e.Y, z ? -e.Z : e.Z));
            c.Name = "";
            c.EditorId = Guid.NewGuid().ToString("N");
            if (n.Parent == null) result.Add(c);
        }
        return result;
    }

    public static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<LevelNode> nodes)
    {
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        foreach (var n in nodes)
        {
            var (c, s) = Rendering.SceneBuilder.BoundsOf(n);
            var h = new Vec3(MathF.Abs(s.X) * 0.5f, MathF.Abs(s.Y) * 0.5f, MathF.Abs(s.Z) * 0.5f);
            min = new Vec3(MathF.Min(min.X, c.X - h.X), MathF.Min(min.Y, c.Y - h.Y), MathF.Min(min.Z, c.Z - h.Z));
            max = new Vec3(MathF.Max(max.X, c.X + h.X), MathF.Max(max.Y, c.Y + h.Y), MathF.Max(max.Z, c.Z + h.Z));
            any = true;
        }
        return any ? (min, max) : (Vec3.Zero, Vec3.Zero);
    }

    public sealed class Measurement
    {
        public Vec3 Start { get; init; }
        public Vec3 End { get; init; }
        public float Distance => (End - Start).Length;
        public Vec3 Delta => End - Start;
        public float Horizontal => new Vec3(Delta.X, 0, Delta.Z).Length;
        public float AngleDegrees => Horizontal < 1e-4f ? 90f : MathF.Atan2(Delta.Y, Horizontal) * 180f / MathF.PI;
        public float Width { get; init; }
        public float Height { get; init; }
        public bool PlayerFitsWidth => Width >= 0.64f;
        public bool PlayerFitsHeight => Height >= 1.7f;
    }

    public static Measurement Measure(Vec3 a, Vec3 b) => new() { Start = a, End = b };

    /// <summary>
    /// Checks a player-sized box at the midpoint of a measurement against solid blocks.
    /// This is the quick "can the player actually get through here?" check.
    /// </summary>
    public static (bool Fits, string Reason, float Width, float Height) Clearance(GrabLevel level, Vec3 a, Vec3 b, float playerRadius = 0.35f, float playerHeight = 1.7f)
    {
        var mid = (a + b) * 0.5f;
        var axis = b - a;
        var flat = new Vec3(axis.X, 0, axis.Z);
        float heading = flat.Length < 1e-4f ? 0 : MathF.Atan2(flat.X, flat.Z);
        var right = new Vec3(MathF.Cos(heading), 0, -MathF.Sin(heading));
        float maxWidth = 0, maxHeight = 0;
        foreach (var n in level.AllNodes())
        {
            if (n.Kind is not (NodeKind.Static or NodeKind.Crumbling) || n.IsPassable) continue;
            var (c, s) = Rendering.SceneBuilder.BoundsOf(n);
            var half = new Vec3(MathF.Abs(s.X) * 0.5f, MathF.Abs(s.Y) * 0.5f, MathF.Abs(s.Z) * 0.5f);
            var d = c - mid;
            if (MathF.Abs(d.X) > half.X + 3 || MathF.Abs(d.Y) > half.Y + 3 || MathF.Abs(d.Z) > half.Z + 3) continue;
            if (MathF.Abs(d.X) < half.X + playerRadius && MathF.Abs(d.Z) < half.Z + playerRadius && MathF.Abs(d.Y) < half.Y + playerHeight)
            {
                maxWidth += half.X * 2;
                maxHeight = MathF.Max(maxHeight, c.Y + half.Y - mid.Y);
            }
        }
        bool fits = maxWidth < 0.5f && maxHeight < playerHeight * 0.9f;
        string reason = fits ? "Player-sized opening" : "Blocked by geometry at the midpoint";
        return (fits, reason, maxWidth, maxHeight);
    }
}
