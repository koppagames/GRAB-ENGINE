using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GrabEngine.Rendering;

/// <summary>
/// WPF's fixed-function lighting is Gouraud shaded: each triangle is lit at its
/// vertices and interpolated. GRAB lights every pixel, so a large floor with only
/// four corner vertices cannot show the circular falloff of a point light. Splitting
/// each triangle into a small lattice adds interior vertices without moving the
/// surface, changing normals, or altering UVs.
/// </summary>
public static class MeshTessellator
{
    public static MeshGeometry3D Subdivide(MeshGeometry3D source, int divisions)
    {
        if (divisions <= 1) return source;

        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var uvs = new PointCollection();
        var indices = new Int32Collection();
        bool hasNormals = source.Normals.Count == source.Positions.Count;
        bool hasUvs = source.TextureCoordinates.Count == source.Positions.Count;

        var sourcePositions = source.Positions;
        var sourceNormals = source.Normals;
        var sourceUvs = source.TextureCoordinates;
        var sourceIndices = source.TriangleIndices;

        for (int t = 0; t + 2 < sourceIndices.Count; t += 3)
        {
            int ia = sourceIndices[t], ib = sourceIndices[t + 1], ic = sourceIndices[t + 2];
            if ((uint)ia >= (uint)sourcePositions.Count ||
                (uint)ib >= (uint)sourcePositions.Count ||
                (uint)ic >= (uint)sourcePositions.Count) continue;

            Point3D a = sourcePositions[ia], b = sourcePositions[ib], c = sourcePositions[ic];
            Vector3D na = hasNormals ? sourceNormals[ia] : default;
            Vector3D nb = hasNormals ? sourceNormals[ib] : default;
            Vector3D nc = hasNormals ? sourceNormals[ic] : default;
            Point ua = hasUvs ? sourceUvs[ia] : default;
            Point ub = hasUvs ? sourceUvs[ib] : default;
            Point uc = hasUvs ? sourceUvs[ic] : default;

            int baseIndex = positions.Count;
            var lattice = new int[divisions + 1, divisions + 1];

            for (int i = 0; i <= divisions; i++)
            {
                for (int j = 0; j <= divisions - i; j++)
                {
                    double wb = (double)i / divisions;
                    double wc = (double)j / divisions;
                    double wa = 1.0 - wb - wc;

                    lattice[i, j] = positions.Count;
                    positions.Add(new Point3D(
                        a.X * wa + b.X * wb + c.X * wc,
                        a.Y * wa + b.Y * wb + c.Y * wc,
                        a.Z * wa + b.Z * wb + c.Z * wc));

                    if (hasNormals)
                    {
                        var n = new Vector3D(
                            na.X * wa + nb.X * wb + nc.X * wc,
                            na.Y * wa + nb.Y * wb + nc.Y * wc,
                            na.Z * wa + nb.Z * wb + nc.Z * wc);
                        if (n.LengthSquared > 1e-12) n.Normalize();
                        normals.Add(n);
                    }
                    if (hasUvs)
                        uvs.Add(new Point(ua.X * wa + ub.X * wb + uc.X * wc,
                            ua.Y * wa + ub.Y * wb + uc.Y * wc));
                }
            }

            for (int i = 0; i < divisions; i++)
            {
                for (int j = 0; j < divisions - i; j++)
                {
                    indices.Add(lattice[i, j]);
                    indices.Add(lattice[i + 1, j]);
                    indices.Add(lattice[i, j + 1]);

                    if (i + j <= divisions - 2)
                    {
                        indices.Add(lattice[i + 1, j]);
                        indices.Add(lattice[i + 1, j + 1]);
                        indices.Add(lattice[i, j + 1]);
                    }
                }
            }
        }

        var result = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        if (hasNormals) result.Normals = normals;
        if (hasUvs) result.TextureCoordinates = uvs;
        result.Freeze();
        return result;
    }
}
