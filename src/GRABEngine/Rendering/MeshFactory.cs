using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GrabEngine.Rendering;

public sealed class MeshBuilder
{
    public Point3DCollection Positions { get; } = new();
    public Vector3DCollection Normals { get; } = new();
    public PointCollection Uvs { get; } = new();
    public Int32Collection Indices { get; } = new();

    public void Quad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal,
        Point uvA = default, Point uvB = default, Point uvC = default, Point uvD = default)
    {
        int i = Positions.Count;
        Positions.Add(a); Positions.Add(b); Positions.Add(c); Positions.Add(d);
        Normals.Add(normal); Normals.Add(normal); Normals.Add(normal); Normals.Add(normal);
        Uvs.Add(uvA); Uvs.Add(uvB); Uvs.Add(uvC); Uvs.Add(uvD);
        Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
        Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
    }

    public void Box(Point3D center, Vector3D size)
    {
        double hx = size.X / 2, hy = size.Y / 2, hz = size.Z / 2;
        var p = new Point3D[8];
        for (int i = 0; i < 8; i++)
            p[i] = new Point3D(center.X + ((i & 1) == 0 ? -hx : hx),
                center.Y + ((i & 2) == 0 ? -hy : hy),
                center.Z + ((i & 4) == 0 ? -hz : hz));
        // +X
        Quad(p[1], p[3], p[7], p[5], new Vector3D(1, 0, 0));
        // -X
        Quad(p[4], p[6], p[2], p[0], new Vector3D(-1, 0, 0));
        // +Y
        Quad(p[2], p[6], p[7], p[3], new Vector3D(0, 1, 0));
        // -Y
        Quad(p[0], p[1], p[5], p[4], new Vector3D(0, -1, 0));
        // +Z
        Quad(p[4], p[5], p[7], p[6], new Vector3D(0, 0, 1));
        // -Z
        Quad(p[0], p[2], p[3], p[1], new Vector3D(0, 0, -1));
    }

    public void Line(Point3D a, Point3D b, double thickness, double u0 = 0, double u1 = 1, double v0 = 0, double v1 = 1)
    {
        var dir = b - a;
        if (dir.LengthSquared < 1e-12) return;
        dir.Normalize();
        var up = Math.Abs(dir.Y) > 0.95 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var side = Vector3D.CrossProduct(dir, up);
        side.Normalize();
        side *= thickness / 2;
        var up2 = Vector3D.CrossProduct(side, dir);
        up2.Normalize();
        up2 *= thickness / 2;
        var n = Vector3D.CrossProduct(side, dir);
        n.Normalize();
        Quad(a - side - up2, a + side - up2, b + side + up2, b - side + up2, n,
            new Point(u0, v0), new Point(u0, v1), new Point(u1, v1), new Point(u1, v0));
        Quad(a - side - up2, b - side + up2, b + side + up2, a + side - up2, -n,
            new Point(u0, v0), new Point(u1, v0), new Point(u1, v1), new Point(u0, v1));
    }

    public void WireBox(Point3D center, Vector3D size, double thickness)
    {
        double hx = size.X / 2, hy = size.Y / 2, hz = size.Z / 2;
        Point3D P(int x, int y, int z) => new(center.X + (x == 0 ? -hx : hx), center.Y + (y == 0 ? -hy : hy), center.Z + (z == 0 ? -hz : hz));
        var c = new[]
        {
            (P(0,0,0),P(1,0,0)),(P(0,1,0),P(1,1,0)),(P(0,0,1),P(1,0,1)),(P(0,1,1),P(1,1,1)),
            (P(0,0,0),P(0,1,0)),(P(1,0,0),P(1,1,0)),(P(0,0,1),P(0,1,1)),(P(1,0,1),P(1,1,1)),
            (P(0,0,0),P(0,0,1)),(P(1,0,0),P(1,0,1)),(P(0,1,0),P(0,1,1)),(P(1,1,0),P(1,1,1))
        };
        foreach (var (a, b) in c) Line(a, b, thickness);
    }

    public void Circle(Point3D center, Vector3D normal, double radius, double thickness, int segments = 48)
    {
        normal.Normalize();
        var up = Math.Abs(normal.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var x = Vector3D.CrossProduct(up, normal); x.Normalize();
        var y = Vector3D.CrossProduct(normal, x); y.Normalize();
        var prev = center + x * radius;
        for (int i = 1; i <= segments; i++)
        {
            double a = i * Math.PI * 2 / segments;
            var p = center + x * (Math.Cos(a) * radius) + y * (Math.Sin(a) * radius);
            Line(prev, p, thickness);
            prev = p;
        }
    }

    /// <summary>
    /// Smooth torus in the plane perpendicular to <paramref name="axis"/>. The gizmo
    /// rings used to be built from straight Line ribbons, which produced a chain of
    /// differently oriented rectangles instead of a circle. A real torus reads as a
    /// continuous ring from every camera angle and gives the rotation gizmo a solid
    /// volume for hit-testing.
    /// </summary>
    public void Torus(Point3D center, Vector3D axis, double radius, double tubeRadius,
        int segments = 64, int tubeSegments = 10)
    {
        if (radius <= 1e-6 || tubeRadius <= 1e-6) return;
        axis.Normalize();
        var up = Math.Abs(axis.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var x = Vector3D.CrossProduct(up, axis); x.Normalize();
        var y = Vector3D.CrossProduct(axis, x); y.Normalize();

        Point3D Point(Vector3D radial, double tubeAngle) =>
            center + radial * (radius + tubeRadius * Math.Cos(tubeAngle)) + axis * (tubeRadius * Math.Sin(tubeAngle));
        Vector3D Normal(Vector3D radial, double tubeAngle) =>
            radial * Math.Cos(tubeAngle) + axis * Math.Sin(tubeAngle);

        for (int i = 0; i < segments; i++)
        {
            double a0 = i * Math.PI * 2 / segments;
            double a1 = (i + 1) * Math.PI * 2 / segments;
            var r0 = x * Math.Cos(a0) + y * Math.Sin(a0);
            var r1 = x * Math.Cos(a1) + y * Math.Sin(a1);
            for (int j = 0; j < tubeSegments; j++)
            {
                double b0 = j * Math.PI * 2 / tubeSegments;
                double b1 = (j + 1) * Math.PI * 2 / tubeSegments;
                int idx = Positions.Count;
                Positions.Add(Point(r0, b0)); Positions.Add(Point(r1, b0));
                Positions.Add(Point(r1, b1)); Positions.Add(Point(r0, b1));
                Normals.Add(Normal(r0, b0)); Normals.Add(Normal(r1, b0));
                Normals.Add(Normal(r1, b1)); Normals.Add(Normal(r0, b1));
                Uvs.Add(new Point((double)i / segments, (double)j / tubeSegments));
                Uvs.Add(new Point((double)(i + 1) / segments, (double)j / tubeSegments));
                Uvs.Add(new Point((double)(i + 1) / segments, (double)(j + 1) / tubeSegments));
                Uvs.Add(new Point((double)i / segments, (double)(j + 1) / tubeSegments));
                Indices.Add(idx); Indices.Add(idx + 1); Indices.Add(idx + 2);
                Indices.Add(idx); Indices.Add(idx + 2); Indices.Add(idx + 3);
            }
        }
    }

    public void Cylinder(Point3D start, Point3D end, double radius, int segments = 12)
    {
        var axis = end - start;
        double len = axis.Length;
        if (len < 1e-6) return;
        axis.Normalize();
        var up = Math.Abs(axis.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var x = Vector3D.CrossProduct(up, axis); x.Normalize();
        var y = Vector3D.CrossProduct(axis, x); y.Normalize();
        for (int i = 0; i < segments; i++)
        {
            double a0 = i * Math.PI * 2 / segments, a1 = (i + 1) * Math.PI * 2 / segments;
            var o0 = x * (Math.Cos(a0) * radius) + y * (Math.Sin(a0) * radius);
            var o1 = x * (Math.Cos(a1) * radius) + y * (Math.Sin(a1) * radius);
            var n = o0; n.Normalize();
            Quad(start + o0, start + o1, end + o1, end + o0, n);
        }
    }

    public void Cone(Point3D baseCenter, Point3D tip, double radius, int segments = 16)
    {
        var axis = tip - baseCenter;
        if (axis.LengthSquared < 1e-9) return;
        axis.Normalize();
        var up = Math.Abs(axis.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var x = Vector3D.CrossProduct(up, axis); x.Normalize();
        var y = Vector3D.CrossProduct(axis, x); y.Normalize();
        for (int i = 0; i < segments; i++)
        {
            double a0 = i * Math.PI * 2 / segments, a1 = (i + 1) * Math.PI * 2 / segments;
            var p0 = baseCenter + x * (Math.Cos(a0) * radius) + y * (Math.Sin(a0) * radius);
            var p1 = baseCenter + x * (Math.Cos(a1) * radius) + y * (Math.Sin(a1) * radius);
            int idx = Positions.Count;
            Positions.Add(p0); Positions.Add(p1); Positions.Add(tip);
            var n = Vector3D.CrossProduct(p1 - p0, tip - p0); n.Normalize();
            Normals.Add(n); Normals.Add(n); Normals.Add(n);
            Uvs.Add(new Point(0, 0)); Uvs.Add(new Point(1, 0)); Uvs.Add(new Point(0.5, 1));
            Indices.Add(idx); Indices.Add(idx + 1); Indices.Add(idx + 2);
        }
    }

    public MeshGeometry3D ToMesh()
    {
        var m = new MeshGeometry3D
        {
            Positions = Positions,
            Normals = Normals,
            TextureCoordinates = Uvs,
            TriangleIndices = Indices
        };
        m.Freeze();
        return m;
    }
}

public static class MeshFactory
{
    public static MeshGeometry3D Grid(int half, double spacing, double thickness)
    {
        var b = new MeshBuilder();
        for (int i = -half; i <= half; i++)
        {
            double v = i * spacing;
            b.Line(new Point3D(-half * spacing, 0, v), new Point3D(half * spacing, 0, v), i == 0 ? thickness * 1.6 : thickness);
            b.Line(new Point3D(v, 0, -half * spacing), new Point3D(v, 0, half * spacing), i == 0 ? thickness * 1.6 : thickness);
        }
        return b.ToMesh();
    }

    public static MeshGeometry3D Line(Point3D a, Point3D b, double thickness)
    {
        var m = new MeshBuilder();
        m.Line(a, b, thickness);
        return m.ToMesh();
    }

    public static MeshGeometry3D Arrow(Vector3D direction, double length, double radius)
    {
        var b = new MeshBuilder();
        direction.Normalize();
        var start = new Point3D(0, 0, 0);
        var shaftEnd = start + direction * (length * 0.72);
        var tip = start + direction * length;
        b.Cylinder(start, shaftEnd, radius);
        b.Cone(shaftEnd, tip, radius * 2.6);
        return b.ToMesh();
    }

    public static MeshGeometry3D Ring(Vector3D normal, double radius, double thickness)
    {
        var b = new MeshBuilder();
        b.Torus(new Point3D(0, 0, 0), normal, Math.Max(0.01, radius), Math.Max(0.008, thickness));
        return b.ToMesh();
    }

    public static MeshGeometry3D Quad(double width, double height, double z = 0)
    {
        var b = new MeshBuilder();
        double w = width / 2, h = height / 2;
        b.Quad(new Point3D(-w, -h, z), new Point3D(w, -h, z), new Point3D(w, h, z), new Point3D(-w, h, z),
            new Vector3D(0, 0, 1), new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0));
        return b.ToMesh();
    }

    public static MeshGeometry3D Sphere(double radius, int segments = 24, int rings = 16)
    {
        var b = new MeshBuilder();
        for (int y = 0; y < rings; y++)
        {
            double phi0 = Math.PI * y / rings, phi1 = Math.PI * (y + 1) / rings;
            for (int x = 0; x < segments; x++)
            {
                double th0 = 2 * Math.PI * x / segments, th1 = 2 * Math.PI * (x + 1) / segments;
                Point3D P(double phi, double th) => new(radius * Math.Sin(phi) * Math.Cos(th),
                    radius * Math.Cos(phi), radius * Math.Sin(phi) * Math.Sin(th));
                var p0 = P(phi0, th0); var p1 = P(phi0, th1); var p2 = P(phi1, th1); var p3 = P(phi1, th0);
                var idx = b.Positions.Count;
                b.Positions.Add(p0); b.Positions.Add(p1); b.Positions.Add(p2); b.Positions.Add(p3);
                foreach (var p in new[] { p0, p1, p2, p3 })
                {
                    var n = new Vector3D(p.X, p.Y, p.Z);
                    if (n.LengthSquared > 1e-9) n.Normalize();
                    b.Normals.Add(n);
                }
                b.Uvs.Add(new Point((double)x / segments, (double)y / rings));
                b.Uvs.Add(new Point((double)(x + 1) / segments, (double)y / rings));
                b.Uvs.Add(new Point((double)(x + 1) / segments, (double)(y + 1) / rings));
                b.Uvs.Add(new Point((double)x / segments, (double)(y + 1) / rings));
                b.Indices.Add(idx); b.Indices.Add(idx + 3); b.Indices.Add(idx + 2);
                b.Indices.Add(idx); b.Indices.Add(idx + 2); b.Indices.Add(idx + 1);
            }
        }
        return b.ToMesh();
    }
}
