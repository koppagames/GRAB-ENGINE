using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

/// <summary>Loads Rayne .sgm meshes (the exact shapes GRAB uses).</summary>
public static class SgmLoader
{
    public sealed class SgmVertex
    {
        public float Px, Py, Pz, Nx, Ny, Nz, U, V;
        public int Index;
    }

    public sealed class SgmMesh
    {
        public List<SgmVertex> Vertices { get; } = new();
        public List<int> Indices { get; } = new();
        public int UvCount { get; set; }
    }

    public sealed class SgmPart
    {
        public MeshGeometry3D Mesh { get; init; } = null!;
        public Col Color { get; init; } = Col.White;
        public int MaterialId { get; init; }
    }

    private static readonly Dictionary<string, MeshGeometry3D> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<SgmPart>> PartCache = new(StringComparer.OrdinalIgnoreCase);

    public static MeshGeometry3D LoadMesh(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var mesh = Read(path);
        Cache[path] = mesh;
        return mesh;
    }

    private static Point ProjectUv(float px, float py, float pz, float nx, float ny, float nz)
    {
        float ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
        if (ax >= ay && ax >= az) return new Point(pz + 0.5f, 1.0 - (py + 0.5f));
        if (ay >= az) return new Point(px + 0.5f, 1.0 - (pz + 0.5f));
        return new Point(px + 0.5f, 1.0 - (py + 0.5f));
    }

    /// <summary>
    /// Player avatars are built from several material groups (body, visor, accents).
    /// This variant returns one mesh per material so the editor can render the real
    /// GRAB player instead of a placeholder sphere.
    /// </summary>
    public static IReadOnlyList<SgmPart> LoadParts(string path)
    {
        if (PartCache.TryGetValue(path, out var cache)) return cache;
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        uint magic = br.ReadUInt32();
        if (magic != 352658064) throw new InvalidDataException("Not a Rayne SGM file: " + path);
        byte version = br.ReadByte();
        if (version != 3) throw new InvalidDataException($"Unsupported SGM version {version}");
        int materialCount = br.ReadByte();
        var colors = new Dictionary<int, Col>();
        for (int m = 0; m < materialCount; m++)
        {
            int materialId = br.ReadByte();
            int uvSets = br.ReadByte();
            for (int u = 0; u < uvSets; u++)
            {
                int images = br.ReadByte();
                for (int i = 0; i < images; i++)
                {
                    br.ReadByte();
                    int len = br.ReadUInt16() - 1;
                    if (len > 0) br.ReadBytes(len);
                    br.ReadByte();
                }
            }
            int colorCount = br.ReadByte();
            for (int c = 0; c < colorCount; c++)
            {
                int colorId = br.ReadByte();
                var color = new Col(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                if (colorId == 0) colors[materialId] = color;
            }
        }

        var parts = new List<SgmPart>();
        int meshCount = br.ReadByte();
        for (int mi = 0; mi < meshCount; mi++)
        {
            br.ReadByte();
            int material = br.ReadByte();
            uint vertexCount = br.ReadUInt32();
            int uvCount = br.ReadByte();
            int colorCount = br.ReadByte();
            int hasTangents = br.ReadByte();
            int hasBones = br.ReadByte();
            var mesh = new MeshGeometry3D();
            for (int v = 0; v < vertexCount; v++)
            {
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float nx = br.ReadSingle(), ny = br.ReadSingle(), nz = br.ReadSingle();
                float u0 = 0, v0 = 0;
                for (int u = 0; u < uvCount; u++)
                {
                    float uu = br.ReadSingle(), vv = br.ReadSingle();
                    if (u == 0) { u0 = uu; v0 = vv; }
                }
                if (colorCount == 4) br.ReadBytes(16);
                if (hasTangents != 0) br.ReadBytes(16);
                if (hasBones != 0) br.ReadBytes(32);
                mesh.Positions.Add(new Point3D(px, py, pz));
                mesh.Normals.Add(new Vector3D(nx, ny, nz));
                mesh.TextureCoordinates.Add(uvCount > 0 ? new Point(u0, 1.0 - v0) : ProjectUv(px, py, pz, nx, ny, nz));
            }
            uint indexCount = br.ReadUInt32();
            int indexSize = br.ReadByte();
            for (int i = 0; i < indexCount; i++)
            {
                uint idx = indexSize == 4 ? br.ReadUInt32() : br.ReadUInt16();
                mesh.TriangleIndices.Add((int)idx);
            }
            mesh.Freeze();
            parts.Add(new SgmPart
            {
                Mesh = mesh,
                MaterialId = material,
                Color = colors.TryGetValue(material, out var color) ? color : Col.White
            });
        }
        PartCache[path] = parts;
        return parts;
    }

    public static MeshGeometry3D Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        uint magic = br.ReadUInt32();
        if (magic != 352658064) throw new InvalidDataException("Not a Rayne SGM file: " + path);
        byte version = br.ReadByte();
        if (version != 3) throw new InvalidDataException($"Unsupported SGM version {version}");
        int materialCount = br.ReadByte();
        for (int m = 0; m < materialCount; m++)
        {
            br.ReadByte(); // material id
            int uvSets = br.ReadByte();
            for (int u = 0; u < uvSets; u++)
            {
                int images = br.ReadByte();
                for (int i = 0; i < images; i++)
                {
                    br.ReadByte(); // usage hint
                    int len = br.ReadUInt16() - 1;
                    if (len > 0) br.ReadBytes(len);
                    br.ReadByte();
                }
            }
            int colors = br.ReadByte();
            for (int c = 0; c < colors; c++) { br.ReadByte(); br.ReadBytes(16); }
        }

        var geo = new MeshGeometry3D();
        var positions = geo.Positions;
        var normals = geo.Normals;
        var uvs = geo.TextureCoordinates;
        var indices = geo.TriangleIndices;
        int meshCount = br.ReadByte();
        int vertexOffset = 0;
        for (int mi = 0; mi < meshCount; mi++)
        {
            br.ReadByte(); // mesh id
            br.ReadByte(); // used material id
            uint vertexCount = br.ReadUInt32();
            int uvCount = br.ReadByte();
            int colorCount = br.ReadByte();
            int hasTangents = br.ReadByte();
            int hasBones = br.ReadByte();
            for (int v = 0; v < vertexCount; v++)
            {
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float nx = br.ReadSingle(), ny = br.ReadSingle(), nz = br.ReadSingle();
                float u0 = 0, v0 = 0;
                for (int u = 0; u < uvCount; u++)
                {
                    float uu = br.ReadSingle(), vv = br.ReadSingle();
                    if (u == 0) { u0 = uu; v0 = vv; }
                }
                if (colorCount == 4) br.ReadBytes(16);
                if (hasTangents != 0) br.ReadBytes(16);
                if (hasBones != 0) br.ReadBytes(32);
                positions.Add(new Point3D(px, py, pz));
                normals.Add(new Vector3D(nx, ny, nz));
                // GRAB's block meshes carry no UV set: the game projects textures in object
                // space. Do the same box projection so the real GRAB textures are visible.
                uvs.Add(uvCount > 0 ? new Point(u0, 1.0 - v0) : ProjectUv(px, py, pz, nx, ny, nz));
            }
            uint indexCount = br.ReadUInt32();
            int indexSize = br.ReadByte();
            for (int i = 0; i < indexCount; i++)
            {
                uint idx = indexSize == 4 ? br.ReadUInt32() : br.ReadUInt16();
                indices.Add(vertexOffset + (int)idx);
            }
            vertexOffset += (int)vertexCount;
        }
        geo.Freeze();
        return geo;
    }
}
