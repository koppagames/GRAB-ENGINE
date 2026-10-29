using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrabEngine.Core;

namespace GrabEngine.Formats;

public enum GlbImportMode
{
    Faces,
    Voxels
}

public sealed class GlbImportSettings
{
    /// <summary>Faces builds thin panels per face; Voxels samples the surface into a cube grid.</summary>
    public GlbImportMode Mode { get; set; } = GlbImportMode.Faces;

    /// <summary>1 (fewer faces / coarse voxels) to 10 (all faces / fine voxels).</summary>
    public int Detail { get; set; } = 6;

    public double Scale { get; set; } = 1.0;
    public double Thickness { get; set; } = 0.06;
    public int Material { get; set; } = 8;
    public bool UseModelColors { get; set; } = true;
    public bool UseModelTextures { get; set; } = true;
    public bool UseVertexColors { get; set; } = true;
    public bool DetectQuads { get; set; } = true;
    public bool SolidVoxels { get; set; }
    public bool CenterOnOrigin { get; set; } = true;
    public bool DropToGround { get; set; } = true;

    public GlbImportSettings Clone() => (GlbImportSettings)MemberwiseClone();
}

public sealed class GlbImportResult
{
    public LevelNode Group { get; init; } = null!;
    public List<LevelNode> Nodes { get; init; } = new();
    public int SourceTriangles { get; init; }
    public int QuadFaces { get; init; }
    public int TriangleFaces { get; init; }
    public int VoxelCount { get; init; }
    public int MaterialCount { get; init; }
    public int MeshCount { get; init; }
    public bool Truncated { get; init; }
    public List<string> Warnings { get; init; } = new();
    public string Summary => $"{SourceTriangles} source triangles  •  {QuadFaces} quads  •  {TriangleFaces} triangles"
        + (VoxelCount > 0 ? $"  •  {VoxelCount} surface voxels" : "")
        + (Truncated ? "  •  output capped" : "");
}

internal sealed class GlbTriangle
{
    public Vec3 A, B, C;
    public Col Color;
    public int Material;
}

internal sealed class GlbScene
{
    public List<GlbTriangle> Triangles { get; } = new();
    public List<GlbMaterial> Materials { get; } = new();
    public List<GlbTexture?> Textures { get; } = new();
    public int MeshCount { get; set; }
    public int MaterialCount { get; set; }
    public List<string> Warnings { get; } = new();
}

internal sealed class GlbMaterial
{
    public Col Color { get; set; } = Col.White;
    public int Texture { get; set; } = -1;
}

/// <summary>A decoded base-colour texture. glTF stores images as PNG/JPEG blobs; the
/// importer samples them through TEXCOORD_0 so a textured model becomes coloured blocks.</summary>
internal sealed class GlbTexture
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();

    public Col Sample(float u, float v)
    {
        if (Width <= 0 || Height <= 0 || Bgra.Length < Width * Height * 4) return Col.White;
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);
        int x = Math.Clamp((int)(u * Width), 0, Width - 1);
        int y = Math.Clamp((int)(v * Height), 0, Height - 1);
        int i = (y * Width + x) * 4;
        return new Col(Bgra[i + 2] / 255f, Bgra[i + 1] / 255f, Bgra[i] / 255f, Bgra[i + 3] / 255f);
    }
}

internal sealed class GlbFace
{
    public List<Vec3> Points { get; init; } = new();
    public Col Color { get; init; }
    public bool IsQuad { get; init; }
    public double Area { get; set; }
}

/// <summary>
/// Minimal, dependency-free glTF 2.0 binary (.glb) importer. It supports the
/// subset used by exported models: scenes, node transforms, meshes, indexed and
/// non-indexed triangles, strips/fans, material base colours and COLOR_0.
/// glTF itself is triangle-only, so quads are recovered by pairing two coplanar
/// triangles that share an edge.
/// </summary>
public static class GlbImporter
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const int MaxImportNodes = 120000;
    private const int MaxTexturePixels = 4096 * 4096;

    public static GlbImportResult ImportFile(string path, GlbImportSettings settings)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        byte[] bytes = File.ReadAllBytes(path);
        return Import(bytes, settings, name);
    }

    public static GlbImportResult Import(byte[] glbBytes, GlbImportSettings settings, string name)
    {
        settings = settings.Clone();
        var scene = Read(glbBytes, settings);
        settings.Detail = Math.Clamp(settings.Detail, 1, 10);
        settings.Scale = Math.Clamp(settings.Scale, 0.001, 1000);
        settings.Thickness = Math.Clamp(settings.Thickness, 0.001, 100);

        var faces = settings.Mode == GlbImportMode.Faces
            ? BuildFaces(scene, settings)
            : BuildFaceTriangles(scene);
        var bounds = BoundsOf(faces);
        var normalized = new List<GlbFace>(faces.Count);
        foreach (var face in faces)
        {
            var points = face.Points.Select(p => Normalize(p, bounds, settings)).ToList();
            normalized.Add(new GlbFace
            {
                Points = points,
                Color = face.Color,
                IsQuad = face.IsQuad,
                Area = face.Area * settings.Scale * settings.Scale
            });
        }

        var group = new LevelNode
        {
            Kind = NodeKind.Group,
            Name = "GLB " + (string.IsNullOrWhiteSpace(name) ? "Model" : name),
            Position = Vec3.Zero,
            Scale = Vec3.One
        };
        var nodes = new List<LevelNode>();
        bool truncated = false;
        int quads = 0, tris = 0, voxels = 0;

        if (settings.Mode == GlbImportMode.Faces)
        {
            double minArea = normalized.Count == 0
                ? 0
                : Math.Pow(Math.Max(0.001, BoundsExtent(normalized)), 2) * 0.00002 * Math.Pow(10 - settings.Detail, 2);
            foreach (var face in normalized)
            {
                if (face.Area < minArea) continue;
                if (nodes.Count >= MaxImportNodes)
                {
                    truncated = true;
                    break;
                }
                LevelNode? node = face.IsQuad
                    ? BuildQuadPanel(face, settings)
                    : BuildTriPrism(face, settings);
                if (node == null) continue;
                node.Parent = group;
                group.Children.Add(node);
                nodes.Add(node);
                if (face.IsQuad) quads++;
                else tris++;
            }
        }
        else
        {
            voxels = BuildVoxels(normalized, settings, group, nodes, out truncated);
            quads = normalized.Count(f => f.IsQuad);
            tris = normalized.Count(f => !f.IsQuad);
        }

        return new GlbImportResult
        {
            Group = group,
            Nodes = nodes,
            SourceTriangles = scene.Triangles.Count,
            QuadFaces = quads,
            TriangleFaces = tris,
            VoxelCount = voxels,
            MaterialCount = scene.MaterialCount,
            MeshCount = scene.MeshCount,
            Truncated = truncated,
            Warnings = scene.Warnings.ToList()
        };
    }

    // ================= GLB container / glTF accessors =================

    private static GlbScene Read(byte[] glbBytes, GlbImportSettings settings)
    {
        using var ms = new MemoryStream(glbBytes, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        if (br.ReadUInt32() != GlbMagic) throw new InvalidDataException("Not a GLB file.");
        uint version = br.ReadUInt32();
        if (version != 2) throw new InvalidDataException($"Unsupported GLB version {version}.");
        uint totalLength = br.ReadUInt32();
        if (totalLength > glbBytes.Length) throw new InvalidDataException("The GLB header is longer than the file.");
        string? json = null;
        byte[]? bin = null;
        while (ms.Position < totalLength)
        {
            uint chunkLength = br.ReadUInt32();
            uint chunkType = br.ReadUInt32();
            if (chunkLength > glbBytes.Length - ms.Position) throw new InvalidDataException("Truncated GLB chunk.");
            var data = br.ReadBytes((int)chunkLength);
            if (chunkType == JsonChunk) json = Encoding.UTF8.GetString(data).TrimEnd('\0', ' ');
            else if (chunkType == BinChunk) bin = data;
        }
        if (json == null) throw new InvalidDataException("The GLB has no JSON chunk.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new GlbScene();
        var triangles = result.Triangles;
        var buffers = new Dictionary<int, byte[]>();
        if (root.TryGetProperty("buffers", out var buffersJson))
        {
            int i = 0;
            foreach (var buffer in buffersJson.EnumerateArray())
            {
                if (buffer.TryGetProperty("uri", out _))
                    result.Warnings.Add("External buffer URIs are not supported; only the embedded BIN chunk is used.");
                else if (bin != null)
                    buffers[i] = bin;
                i++;
            }
        }
        if (buffers.Count == 0 && bin != null) buffers[0] = bin;

        result.Textures.AddRange(ReadGlbTextures(root, buffers, result.Warnings));
        result.Materials.AddRange(ReadMaterials(root));
        if (root.TryGetProperty("meshes", out var meshesJson)) result.MeshCount = meshesJson.GetArrayLength();
        result.MaterialCount = result.Materials.Count;

        JsonElement? scenes = Prop(root, "scenes");
        var roots = new List<int>();
        if (scenes is { ValueKind: JsonValueKind.Array } sc && sc.GetArrayLength() > 0)
        {
            int sceneIndex = propInt(root, "scene", 0);
            var scene = sc[Math.Clamp(sceneIndex, 0, sc.GetArrayLength() - 1)];
            if (scene.TryGetProperty("nodes", out var sceneNodes))
                foreach (var n in sceneNodes.EnumerateArray()) roots.Add(n.GetInt32());
        }
        if (roots.Count == 0 && root.TryGetProperty("nodes", out var allNodes))
        {
            var childIds = new HashSet<int>();
            foreach (var node in allNodes.EnumerateArray())
                if (node.TryGetProperty("children", out var children))
                    foreach (var c in children.EnumerateArray()) childIds.Add(c.GetInt32());
            for (int i = 0; i < allNodes.GetArrayLength(); i++)
                if (!childIds.Contains(i)) roots.Add(i);
        }

        var visited = new HashSet<int>();
        foreach (int rootIndex in roots)
            WalkNode(rootIndex, Mat4.Identity, root, buffers, result.Materials, result.Textures, triangles, visited, settings);

        if (triangles.Count == 0)
            result.Warnings.Add("No triangle geometry was found. Points, lines and non-triangle primitives are skipped.");
        return result;
    }

    private static void WalkNode(int index, Mat4 parent, JsonElement root, Dictionary<int, byte[]> buffers,
        List<GlbMaterial> materials, List<GlbTexture?> textures, List<GlbTriangle> triangles,
        HashSet<int> visited, GlbImportSettings settings)
    {
        if (!visited.Add(index)) return;
        if (!root.TryGetProperty("nodes", out var nodes) || index < 0 || index >= nodes.GetArrayLength()) return;
        var node = nodes[index];
        var local = ReadNodeMatrix(node);
        var world = parent * local;

        int meshIndex = propInt(node, "mesh", -1);
        if (meshIndex >= 0 && root.TryGetProperty("meshes", out var meshes) && meshIndex < meshes.GetArrayLength())
        {
            var mesh = meshes[meshIndex];
            if (mesh.TryGetProperty("primitives", out var primitives))
            {
                foreach (var primitive in primitives.EnumerateArray())
                    AddPrimitive(primitive, world, root, buffers, materials, textures, triangles, settings);
            }
        }

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                WalkNode(child.GetInt32(), world, root, buffers, materials, textures, triangles, visited, settings);
    }

    private static Mat4 ReadNodeMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix) && matrix.GetArrayLength() == 16)
        {
            var m = new float[16];
            int i = 0;
            foreach (var v in matrix.EnumerateArray()) m[i++] = (float)v.GetDouble();
            // glTF stores matrices column-major.
            return new Mat4
            {
                M11 = m[0], M12 = m[4], M13 = m[8], M14 = m[12],
                M21 = m[1], M22 = m[5], M23 = m[9], M24 = m[13],
                M31 = m[2], M32 = m[6], M33 = m[10], M34 = m[14],
                M41 = m[3], M42 = m[7], M43 = m[11], M44 = m[15]
            };
        }
        var t = propVec3(node, "translation", Vec3.Zero);
        var r = propQuat(node, "rotation", Quat.Identity);
        var s = propVec3(node, "scale", Vec3.One);
        return Mat4.Translate(t) * Mat4.Rotate(r) * Mat4.Scale(s);
    }

    private static void AddPrimitive(JsonElement primitive, Mat4 world, JsonElement root,
        Dictionary<int, byte[]> buffers, List<GlbMaterial> materials, List<GlbTexture?> textures,
        List<GlbTriangle> triangles, GlbImportSettings settings)
    {
        var mode = propInt(primitive, "mode", 4);
        if (mode is not (4 or 5 or 6)) return;
        if (!primitive.TryGetProperty("attributes", out var attrs)) return;
        if (!attrs.TryGetProperty("POSITION", out var posAccessor)) return;
        int materialIndex = propInt(primitive, "material", -1);
        var material = materialIndex >= 0 && materialIndex < materials.Count
            ? materials[materialIndex] : new GlbMaterial();
        Col materialColor = material.Color;
        GlbTexture? texture = material.Texture >= 0 && material.Texture < textures.Count ? textures[material.Texture] : null;
        List<float> uvs = new();
        int uvComponents = 0;
        if (texture != null && attrs.TryGetProperty("TEXCOORD_0", out var uvAccessor))
            uvs = ReadAccessor(root, buffers, uvAccessor.GetInt32(), 2, out uvComponents);

        var positions = ReadAccessor(root, buffers, posAccessor.GetInt32(), 3, out _);
        if (positions.Count < 3) return;
        var colors = new List<Col>();
        if (attrs.TryGetProperty("COLOR_0", out var colorAccessor))
        {
            if (settings.UseVertexColors)
            {
                var raw = ReadAccessor(root, buffers, colorAccessor.GetInt32(), 4, out int colorComponents);
                int step = Math.Max(3, colorComponents);
                for (int i = 0; i + 2 < raw.Count; i += step)
                    colors.Add(new Col(LinearToSrgb(raw[i]), LinearToSrgb(raw[i + 1]),
                        LinearToSrgb(raw[i + 2]), step >= 4 ? Math.Clamp(raw[i + 3], 0f, 1f) : 1f));
            }
        }

        var indices = new List<int>();
        if (primitive.TryGetProperty("indices", out var indexAccessor))
            indices = ReadIndices(root, buffers, indexAccessor.GetInt32());
        else
            for (int i = 0; i < positions.Count / 3; i++) indices.Add(i);
        if (indices.Count < 3) return;

        void Add(int i0, int i1, int i2)
        {
            int max = positions.Count / 3;
            if ((uint)i0 >= (uint)max || (uint)i1 >= (uint)max || (uint)i2 >= (uint)max) return;
            var a = world.Transform(new Vec3(positions[i0 * 3], positions[i0 * 3 + 1], positions[i0 * 3 + 2]));
            var b = world.Transform(new Vec3(positions[i1 * 3], positions[i1 * 3 + 1], positions[i1 * 3 + 2]));
            var c = world.Transform(new Vec3(positions[i2 * 3], positions[i2 * 3 + 1], positions[i2 * 3 + 2]));
            var normal = Vec3Cross(b - a, c - a);
            if (normal.Length < 1e-8f) return;
            Col color = materialColor;
            if (settings.UseModelTextures && texture != null && uvs.Count >= 6 && uvComponents >= 2)
            {
                // Average the three UV samples: one block per face keeps the model
                // inside GRAB's object budget while preserving its painted look.
                int vc = uvs.Count / uvComponents;
                var t0 = texture.Sample(UvAt(i0, 0), UvAt(i0, 1));
                var t1 = texture.Sample(UvAt(i1, 0), UvAt(i1, 1));
                var t2 = texture.Sample(UvAt(i2, 0), UvAt(i2, 1));
                color = new Col((t0.R + t1.R + t2.R) / 3f, (t0.G + t1.G + t2.G) / 3f,
                    (t0.B + t1.B + t2.B) / 3f, (t0.A + t1.A + t2.A) / 3f);
                color = new Col(color.R * materialColor.R, color.G * materialColor.G,
                    color.B * materialColor.B, color.A * materialColor.A);

                float UvAt(int vertex, int component)
                {
                    int index = Math.Min(Math.Max(vertex, 0), Math.Max(0, vc - 1)) * uvComponents + component;
                    return index >= 0 && index < uvs.Count ? uvs[index] : 0f;
                }
            }
            if (colors.Count > 0)
            {
                color = new Col(
                    color.R * (colors[Math.Min(i0, colors.Count - 1)].R + colors[Math.Min(i1, colors.Count - 1)].R + colors[Math.Min(i2, colors.Count - 1)].R) / 3f,
                    color.G * (colors[Math.Min(i0, colors.Count - 1)].G + colors[Math.Min(i1, colors.Count - 1)].G + colors[Math.Min(i2, colors.Count - 1)].G) / 3f,
                    color.B * (colors[Math.Min(i0, colors.Count - 1)].B + colors[Math.Min(i1, colors.Count - 1)].B + colors[Math.Min(i2, colors.Count - 1)].B) / 3f,
                    color.A);
            }
            triangles.Add(new GlbTriangle { A = a, B = b, C = c, Color = color, Material = materialIndex });
        }

        if (mode == 4)
            for (int i = 0; i + 2 < indices.Count; i += 3) Add(indices[i], indices[i + 1], indices[i + 2]);
        else if (mode == 5)
            for (int i = 0; i + 2 < indices.Count; i++)
                Add(indices[i], indices[i + 1], indices[i + 2]);
        else
            for (int i = 1; i + 1 < indices.Count; i++) Add(indices[0], indices[i], indices[i + 1]);
    }

    private static List<GlbMaterial> ReadMaterials(JsonElement root)
    {
        var result = new List<GlbMaterial>();
        if (!root.TryGetProperty("materials", out var materials)) return result;
        foreach (var material in materials.EnumerateArray())
        {
            var info = new GlbMaterial();
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.GetArrayLength() >= 3)
                {
                    // glTF baseColorFactor is linear; WPF/GRAB Color1 is sRGB.
                    info.Color = new Col(
                        LinearToSrgb((float)factor[0].GetDouble()),
                        LinearToSrgb((float)factor[1].GetDouble()),
                        LinearToSrgb((float)factor[2].GetDouble()),
                        factor.GetArrayLength() > 3 ? Math.Clamp((float)factor[3].GetDouble(), 0f, 1f) : 1f);
                }
                if (pbr.TryGetProperty("baseColorTexture", out var baseTexture) &&
                    baseTexture.TryGetProperty("index", out var textureIndex))
                    info.Texture = textureIndex.GetInt32();
            }
            result.Add(info);
        }
        return result;
    }

    private static List<GlbTexture?> ReadGlbTextures(JsonElement root, Dictionary<int, byte[]> buffers,
        List<string> warnings)
    {
        var images = new List<GlbTexture?>();
        if (!root.TryGetProperty("images", out var imageArray)) return images;
        foreach (var image in imageArray.EnumerateArray())
        {
            byte[]? bytes = null;
            try
            {
                if (image.TryGetProperty("bufferView", out var bvIndex) &&
                    root.TryGetProperty("bufferViews", out var views) &&
                    bvIndex.GetInt32() >= 0 && bvIndex.GetInt32() < views.GetArrayLength())
                {
                    var view = views[bvIndex.GetInt32()];
                    if (buffers.TryGetValue(propInt(view, "buffer", 0), out var buffer))
                    {
                        int offset = propInt(view, "byteOffset", 0);
                        int length = propInt(view, "byteLength", 0);
                        if (offset >= 0 && length > 0 && offset + length <= buffer.Length)
                            bytes = buffer.AsSpan(offset, length).ToArray();
                    }
                }
                else if (image.TryGetProperty("uri", out var uri) &&
                         uri.GetString() is { } data && data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = data.IndexOf(',');
                    if (comma > 0) bytes = Convert.FromBase64String(data[(comma + 1)..]);
                }
                else if (image.TryGetProperty("uri", out _))
                    warnings.Add("External texture files are not embedded; those materials use their base colour.");

                if (bytes == null) { images.Add(null); continue; }
                using var stream = new MemoryStream(bytes);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (frame.PixelWidth * (long)frame.PixelHeight > MaxTexturePixels)
                {
                    warnings.Add("A texture is larger than 4096x4096; using its base colour instead.");
                    images.Add(null);
                    continue;
                }
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                images.Add(new GlbTexture { Width = converted.PixelWidth, Height = converted.PixelHeight, Bgra = pixels });
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read a model texture: " + ex.Message);
                images.Add(null);
            }
        }

        // textures[] maps a sampler/view onto an image; -1 means no image.
        var textures = new List<GlbTexture?>(images);
        if (root.TryGetProperty("textures", out var textureArray))
        {
            textures.Clear();
            foreach (var texture in textureArray.EnumerateArray())
            {
                int source = propInt(texture, "source", -1);
                textures.Add(source >= 0 && source < images.Count ? images[source] : null);
            }
        }
        return textures;
    }

    private static List<float> ReadAccessor(JsonElement root, Dictionary<int, byte[]> buffers, int accessorIndex,
        int requestedComponents, out int components)
    {
        components = requestedComponents;
        var result = new List<float>();
        if (!root.TryGetProperty("accessors", out var accessors) || accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
            return result;
        var accessor = accessors[accessorIndex];
        int count = propInt(accessor, "count", 0);
        string type = propString(accessor, "type");
        int actualComponents = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => requestedComponents };
        components = actualComponents;
        int componentType = propInt(accessor, "componentType", 5126);
        int size = ComponentSize(componentType);
        bool normalized = accessor.TryGetProperty("normalized", out var n) && n.ValueKind == JsonValueKind.True;
        if (!accessor.TryGetProperty("bufferView", out var bvElement)) return result;
        int bufferViewIndex = bvElement.GetInt32();
        if (!root.TryGetProperty("bufferViews", out var bufferViews) || bufferViewIndex < 0 || bufferViewIndex >= bufferViews.GetArrayLength())
            return result;
        var view = bufferViews[bufferViewIndex];
        int bufferIndex = propInt(view, "buffer", 0);
        if (!buffers.TryGetValue(bufferIndex, out var buffer)) return result;
        int viewOffset = propInt(view, "byteOffset", 0);
        int accessorOffset = propInt(accessor, "byteOffset", 0);
        int stride = propInt(view, "byteStride", actualComponents * size);
        if (stride <= 0) stride = actualComponents * size;
        long start = viewOffset + accessorOffset;
        for (int i = 0; i < count; i++)
        {
            long row = start + (long)i * stride;
            for (int c = 0; c < actualComponents; c++)
            {
                long offset = row + (long)c * size;
                if (offset < 0 || offset + size > buffer.Length) break;
                result.Add(ReadComponent(buffer, (int)offset, componentType, normalized));
            }
        }
        return result;
    }

    private static List<int> ReadIndices(JsonElement root, Dictionary<int, byte[]> buffers, int accessorIndex)
    {
        var result = new List<int>();
        if (!root.TryGetProperty("accessors", out var accessors) || accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
            return result;
        var accessor = accessors[accessorIndex];
        if (!accessor.TryGetProperty("bufferView", out var bvElement)) return result;
        int viewIndex = bvElement.GetInt32();
        if (!root.TryGetProperty("bufferViews", out var views) || viewIndex < 0 || viewIndex >= views.GetArrayLength())
            return result;
        var view = views[viewIndex];
        if (!buffers.TryGetValue(propInt(view, "buffer", 0), out var buffer)) return result;
        int count = propInt(accessor, "count", 0);
        int componentType = propInt(accessor, "componentType", 5123);
        int size = ComponentSize(componentType);
        int stride = propInt(view, "byteStride", size);
        if (stride <= 0) stride = size;
        long start = propInt(view, "byteOffset", 0) + propInt(accessor, "byteOffset", 0);
        for (int i = 0; i < count; i++)
        {
            long offset = start + (long)i * stride;
            if (offset < 0 || offset + size > buffer.Length) break;
            result.Add((int)ReadUnsigned(buffer, (int)offset, componentType));
        }
        return result;
    }

    private static float ReadComponent(byte[] buffer, int offset, int componentType, bool normalized)
    {
        switch (componentType)
        {
            case 5120:
            {
                sbyte v = unchecked((sbyte)buffer[offset]);
                return normalized ? Math.Max(v / 127f, -1f) : v;
            }
            case 5121:
                return normalized ? buffer[offset] / 255f : buffer[offset];
            case 5122:
            {
                short v = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2));
                return normalized ? Math.Max(v / 32767f, -1f) : v;
            }
            case 5123:
                return normalized ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2)) / 65535f
                    : BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
            case 5125:
                return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            default:
                return BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset, 4));
        }
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => 4
    };

    private static uint ReadUnsigned(byte[] buffer, int offset, int componentType) => componentType switch
    {
        5120 => (uint)(sbyte)buffer[offset],
        5121 => buffer[offset],
        5123 => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2)),
        5125 => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4)),
        _ => 0
    };

    // ================= face reconstruction =================

    private static List<GlbFace> BuildFaces(GlbScene scene, GlbImportSettings settings)
    {
        var faces = new List<GlbFace>();
        var triangles = scene.Triangles;
        if (!settings.DetectQuads)
            return BuildFaceTriangles(scene);

        var used = new bool[triangles.Count];
        var edges = new Dictionary<EdgeKey, List<int>>();
        double extent = Math.Max(0.0001, BoundsExtentOfTriangles(triangles));
        double tolerance = Math.Max(1e-5, extent * 1e-5);
        for (int i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            AddEdge(edges, t.A, t.B, i, tolerance);
            AddEdge(edges, t.B, t.C, i, tolerance);
            AddEdge(edges, t.C, t.A, i, tolerance);
        }

        foreach (var pair in edges.Values)
        {
            if (pair.Count < 2) continue;
            for (int i = 0; i < pair.Count; i++)
            {
                if (used[pair[i]]) continue;
                for (int j = i + 1; j < pair.Count; j++)
                {
                    if (used[pair[j]]) continue;
                    var t1 = triangles[pair[i]];
                    var t2 = triangles[pair[j]];
                    if (!ColorsClose(t1.Color, t2.Color)) continue;
                    if (!TryMakeQuad(t1, t2, tolerance, out var quad)) continue;
                    used[pair[i]] = used[pair[j]] = true;
                    quad.Area = PolygonArea(quad.Points);
                    faces.Add(quad);
                    break;
                }
            }
        }
        for (int i = 0; i < triangles.Count; i++)
        {
            if (used[i]) continue;
            var t = triangles[i];
            var face = new GlbFace { Points = new List<Vec3> { t.A, t.B, t.C }, Color = t.Color, IsQuad = false };
            face.Area = PolygonArea(face.Points);
            faces.Add(face);
        }
        return faces;
    }

    private static List<GlbFace> BuildFaceTriangles(GlbScene scene)
    {
        var faces = new List<GlbFace>();
        foreach (var t in scene.Triangles)
        {
            var face = new GlbFace { Points = new List<Vec3> { t.A, t.B, t.C }, Color = t.Color, IsQuad = false };
            face.Area = PolygonArea(face.Points);
            faces.Add(face);
        }
        return faces;
    }

    private static bool TryMakeQuad(GlbTriangle t1, GlbTriangle t2, double tolerance, out GlbFace quad)
    {
        quad = null!;
        var p1 = new[] { t1.A, t1.B, t1.C };
        var p2 = new[] { t2.A, t2.B, t2.C };
        var shared = new List<Vec3>();
        var unique1 = new List<Vec3>();
        var unique2 = new List<Vec3>();
        foreach (var a in p1)
        {
            if (p2.Any(b => DistanceSquared(a, b) <= tolerance * tolerance)) shared.Add(a);
            else unique1.Add(a);
        }
        foreach (var b in p2)
            if (!p1.Any(a => DistanceSquared(a, b) <= tolerance * tolerance)) unique2.Add(b);
        if (shared.Count != 2 || unique1.Count != 1 || unique2.Count != 1) return false;
        var n1 = Vec3Cross(t1.B - t1.A, t1.C - t1.A).Normalized;
        var n2 = Vec3Cross(t2.B - t2.A, t2.C - t2.A).Normalized;
        if (Vec3Dot(n1, n2) < 0.995f) return false;
        var v = unique2[0];
        if (Math.Abs(Vec3Dot(v - shared[0], n1)) > tolerance * 20) return false;

        var order = new List<Vec3> { shared[0], unique1[0], shared[1], unique2[0] };
        var area = PolygonArea(order);
        if (area < tolerance * tolerance) return false;
        quad = new GlbFace { Points = order, Color = Average(t1.Color, t2.Color), IsQuad = true };
        return true;
    }

    // ================= output builders =================

    private static LevelNode? BuildQuadPanel(GlbFace face, GlbImportSettings settings)
    {
        if (face.Points.Count < 4) return null;
        var p0 = face.Points[0];
        var p1 = face.Points[1];
        var p2 = face.Points[2];
        var p3 = face.Points[3];
        var normal = Vec3Cross(p1 - p0, p2 - p0).Normalized;
        var x = (p1 - p0).Normalized;
        if (normal.Length < 1e-6f || x.Length < 1e-6f) return null;
        var y = Vec3Cross(normal, x).Normalized;
        if (y.Length < 1e-6f) return null;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in face.Points)
        {
            double px = Vec3Dot(p - p0, x);
            double py = Vec3Dot(p - p0, y);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }
        double width = maxX - minX, height = maxY - minY;
        if (width < 0.005 || height < 0.005) return null;
        var centre = p0 + x * (float)((minX + maxX) * 0.5) + y * (float)((minY + maxY) * 0.5);
        var rotation = QuatFromBasis(x, y, normal);
        return new LevelNode
        {
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = settings.Material,
            Position = centre,
            Rotation = rotation,
            Scale = new Vec3((float)width, (float)height, (float)Math.Max(0.005, settings.Thickness)),
            Color1 = settings.UseModelColors ? face.Color : Col.Gray
        };
    }

    private static LevelNode? BuildTriPrism(GlbFace face, GlbImportSettings settings)
    {
        if (face.Points.Count < 3) return null;
        var a = face.Points[0];
        var b = face.Points[1];
        var c = face.Points[2];
        var edges = new[]
        {
            (A: a, B: b, C: c, Len: (b - a).Length),
            (A: b, B: c, C: a, Len: (c - b).Length),
            (A: c, B: a, C: b, Len: (a - c).Length)
        };
        var edge = edges.OrderByDescending(e => e.Len).First();
        double baseLength = edge.Len;
        if (baseLength < 1e-5) return null;
        var edgeDir = (edge.B - edge.A).Normalized;
        var baseMid = (edge.A + edge.B) * 0.5f;
        var tip = edge.C;
        var tipDir = tip - baseMid;
        tipDir = tipDir - edgeDir * Vec3Dot(tipDir, edgeDir);
        double height = tipDir.Length;
        if (height < 1e-5) return null;
        var z = tipDir.Normalized;
        var y = Vec3Cross(z, edgeDir).Normalized;
        if (y.Length < 1e-6f) return null;
        var x = edgeDir;
        var rotation = QuatFromBasis(x, y, z);
        var centre = (a + b + c) * (1f / 3f);
        // The GRAB prism's triangle is 0.866 wide and 0.75 tall, extruded ±0.5 in Y.
        return new LevelNode
        {
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Prism,
            Material = settings.Material,
            Position = centre,
            Rotation = rotation,
            Scale = new Vec3(
                (float)(baseLength / 0.86602540378),
                (float)Math.Max(0.005, settings.Thickness),
                (float)(height / 0.75)),
            Color1 = settings.UseModelColors ? face.Color : Col.Gray
        };
    }

    private static int BuildVoxels(List<GlbFace> faces, GlbImportSettings settings,
        LevelNode group, List<LevelNode> nodes, out bool truncated)
    {
        truncated = false;
        if (faces.Count == 0) return 0;
        var bounds = BoundsOf(faces);
        var size = Math.Max(0.001, Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y, bounds.Size.Z)));
        int resolution = Math.Clamp(18 + settings.Detail * 9, 20, 110);
        double voxel = size / resolution;
        if (voxel < 1e-4) return 0;
        var origin = new Vec3(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        var surface = new Dictionary<Cell, ColorSum>();
        double tolerance = voxel * 0.72;

        foreach (var face in faces)
        {
            for (int i = 1; i + 1 < face.Points.Count; i++)
                RasterizeTriangle(face.Points[0], face.Points[i], face.Points[i + 1], face.Color, origin, voxel, tolerance, surface);
        }
        if (surface.Count == 0) return 0;

        var all = new Dictionary<Cell, Col>(surface.Count);
        foreach (var pair in surface) all[pair.Key] = pair.Value.ToCol();

        if (settings.SolidVoxels)
        {
            var interior = InteriorCells(surface);
            var colored = new Dictionary<Cell, Col>();
            foreach (var pair in surface) colored[pair.Key] = pair.Value.ToCol();
            var queue = new Queue<Cell>(colored.Keys);
            var dirs = new[] { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 1, 0), new Cell(0, -1, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) };
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var color = colored[cell];
                foreach (var d in dirs)
                {
                    var next = new Cell(cell.X + d.X, cell.Y + d.Y, cell.Z + d.Z);
                    if (!interior.Contains(next) || colored.ContainsKey(next)) continue;
                    colored[next] = color;
                    queue.Enqueue(next);
                }
            }
            all = colored;
        }

        var cells = all.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.Z).ThenBy(p => p.Key.X).ToList();
        foreach (var pair in cells)
        {
            if (nodes.Count >= MaxImportNodes)
            {
                truncated = true;
                break;
            }
            var center = new Vec3(
                origin.X + (pair.Key.X + 0.5f) * (float)voxel,
                origin.Y + (pair.Key.Y + 0.5f) * (float)voxel,
                origin.Z + (pair.Key.Z + 0.5f) * (float)voxel);
            var node = new LevelNode
            {
                Kind = NodeKind.Static,
                Shape = ShapeInfo.Cube,
                Material = settings.Material,
                Position = center,
                Scale = new Vec3((float)(voxel * 0.98), (float)(voxel * 0.98), (float)(voxel * 0.98)),
                Color1 = settings.UseModelColors ? pair.Value : Col.Gray
            };
            node.Parent = group;
            group.Children.Add(node);
            nodes.Add(node);
        }
        return nodes.Count;
    }

    private static void RasterizeTriangle(Vec3 a, Vec3 b, Vec3 c, Col color, Vec3 origin, double voxel,
        double tolerance, Dictionary<Cell, ColorSum> surface)
    {
        double minX = Math.Min(a.X, Math.Min(b.X, c.X)) - tolerance;
        double maxX = Math.Max(a.X, Math.Max(b.X, c.X)) + tolerance;
        double minY = Math.Min(a.Y, Math.Min(b.Y, c.Y)) - tolerance;
        double maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y)) + tolerance;
        double minZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)) - tolerance;
        double maxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z)) + tolerance;
        int x0 = (int)Math.Floor((minX - origin.X) / voxel);
        int x1 = (int)Math.Ceiling((maxX - origin.X) / voxel);
        int y0 = (int)Math.Floor((minY - origin.Y) / voxel);
        int y1 = (int)Math.Ceiling((maxY - origin.Y) / voxel);
        int z0 = (int)Math.Floor((minZ - origin.Z) / voxel);
        int z1 = (int)Math.Ceiling((maxZ - origin.Z) / voxel);
        double tol2 = tolerance * tolerance;
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vec3(
                origin.X + (x + 0.5f) * (float)voxel,
                origin.Y + (y + 0.5f) * (float)voxel,
                origin.Z + (z + 0.5f) * (float)voxel);
            var closest = ClosestPointOnTriangle(p, a, b, c);
            if (DistanceSquared(closest, p) > tol2) continue;
            var key = new Cell(x, y, z);
            if (surface.TryGetValue(key, out var sum))
            {
                sum.R += color.R; sum.G += color.G; sum.B += color.B; sum.Count++;
                surface[key] = sum;
            }
            else
            {
                surface[key] = new ColorSum { R = color.R, G = color.G, B = color.B, Count = 1 };
            }
        }
    }

    private static HashSet<Cell> InteriorCells(Dictionary<Cell, ColorSum> surface)
    {
        int minX = surface.Keys.Min(c => c.X) - 1, maxX = surface.Keys.Max(c => c.X) + 1;
        int minY = surface.Keys.Min(c => c.Y) - 1, maxY = surface.Keys.Max(c => c.Y) + 1;
        int minZ = surface.Keys.Min(c => c.Z) - 1, maxZ = surface.Keys.Max(c => c.Z) + 1;
        var outside = new HashSet<Cell>();
        var queue = new Queue<Cell>();
        void Seed(Cell c)
        {
            if (surface.ContainsKey(c) || !outside.Add(c)) return;
            queue.Enqueue(c);
        }
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        {
            Seed(new Cell(minX, y, z));
            Seed(new Cell(maxX, y, z));
        }
        for (int x = minX; x <= maxX; x++)
        for (int z = minZ; z <= maxZ; z++)
        {
            Seed(new Cell(x, minY, z));
            Seed(new Cell(x, maxY, z));
        }
        for (int x = minX; x <= maxX; x++)
        for (int y = minY; y <= maxY; y++)
        {
            Seed(new Cell(x, y, minZ));
            Seed(new Cell(x, y, maxZ));
        }
        var dirs = new[] { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 1, 0), new Cell(0, -1, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) };
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var d in dirs)
            {
                var next = new Cell(c.X + d.X, c.Y + d.Y, c.Z + d.Z);
                if (next.X < minX || next.X > maxX || next.Y < minY || next.Y > maxY || next.Z < minZ || next.Z > maxZ)
                    continue;
                if (surface.ContainsKey(next)) continue;
                if (outside.Add(next)) queue.Enqueue(next);
            }
        }
        var interior = new HashSet<Cell>();
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            var c = new Cell(x, y, z);
            if (!surface.ContainsKey(c) && !outside.Contains(c)) interior.Add(c);
        }
        return interior;
    }

    // ================= math / helpers =================

    private static Vec3 Normalize(Vec3 p, Bounds bounds, GlbImportSettings settings)
    {
        double cx = settings.CenterOnOrigin ? (bounds.Min.X + bounds.Max.X) * 0.5 : 0;
        double cz = settings.CenterOnOrigin ? (bounds.Min.Z + bounds.Max.Z) * 0.5 : 0;
        double cy = settings.DropToGround ? bounds.Min.Y : 0;
        double s = settings.Scale;
        return new Vec3((float)((p.X - cx) * s), (float)((p.Y - cy) * s), (float)((p.Z - cz) * s));
    }

    private static Bounds BoundsOf(IEnumerable<GlbFace> faces)
    {
        var bounds = new Bounds
        {
            Min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
            Max = new Vec3(float.MinValue, float.MinValue, float.MinValue)
        };
        foreach (var face in faces)
        foreach (var p in face.Points)
        {
            bounds.Min = new Vec3(MathF.Min(bounds.Min.X, p.X), MathF.Min(bounds.Min.Y, p.Y), MathF.Min(bounds.Min.Z, p.Z));
            bounds.Max = new Vec3(MathF.Max(bounds.Max.X, p.X), MathF.Max(bounds.Max.Y, p.Y), MathF.Max(bounds.Max.Z, p.Z));
        }
        if (bounds.Min.X > bounds.Max.X) { bounds.Min = Vec3.Zero; bounds.Max = Vec3.One; }
        return bounds;
    }

    private static Bounds BoundsOf(GlbScene scene)
        => BoundsOf(scene.Triangles.Select(t => new GlbFace { Points = new List<Vec3> { t.A, t.B, t.C } }));

    private static double BoundsExtentOfTriangles(List<GlbTriangle> triangles)
    {
        if (triangles.Count == 0) return 1;
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var t in triangles)
        foreach (var p in new[] { t.A, t.B, t.C })
        {
            min = new Vec3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
            max = new Vec3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
        }
        var size = max - min;
        return Math.Max(size.X, Math.Max(size.Y, size.Z));
    }

    private static double BoundsExtent(IEnumerable<GlbFace> faces)
    {
        var b = BoundsOf(faces);
        return Math.Max(b.Size.X, Math.Max(b.Size.Y, b.Size.Z));
    }

    private static double BoundsExtent(GlbScene scene) => BoundsOf(scene).Size.Length;

    private static double PolygonArea(List<Vec3> points)
    {
        if (points.Count < 3) return 0;
        var a = points[0];
        double area = 0;
        for (int i = 1; i + 1 < points.Count; i++)
            area += Vec3Cross(points[i] - a, points[i + 1] - a).Length * 0.5;
        return area;
    }

    private static bool ColorsClose(Col a, Col b)
        => Math.Abs(a.R - b.R) < 0.03f && Math.Abs(a.G - b.G) < 0.03f && Math.Abs(a.B - b.B) < 0.03f;

    private static Col Average(Col a, Col b) => new((a.R + b.R) * 0.5f, (a.G + b.G) * 0.5f, (a.B + b.B) * 0.5f, 1f);

    /// <summary>glTF stores colour factors and COLOR_0 in linear space. The rest of
    /// GRAB ENGINE (and WPF materials) uses sRGB bytes, so convert once at import.</summary>
    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static void AddEdge(Dictionary<EdgeKey, List<int>> edges, Vec3 a, Vec3 b, int index, double tolerance)
    {
        var ka = PosKey.From(a, tolerance);
        var kb = PosKey.From(b, tolerance);
        var key = EdgeKey.Create(ka, kb);
        if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>();
        if (!list.Contains(index)) list.Add(index);
    }

    private static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = Vec3Dot(ab, ap);
        double d2 = Vec3Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        double d3 = Vec3Dot(ab, bp);
        double d4 = Vec3Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (float)(d1 / (d1 - d3));
        var cp = p - c;
        double d5 = Vec3Dot(ab, cp);
        double d6 = Vec3Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (float)(d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * (float)((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double denom = 1.0 / (va + vb + vc);
        double v = vb * denom;
        double w = vc * denom;
        return a + ab * (float)v + ac * (float)w;
    }

    private static Quat QuatFromBasis(Vec3 x, Vec3 y, Vec3 z)
    {
        // Rotation matrix with x/y/z as its columns, converted to a quaternion.
        double m00 = x.X, m01 = y.X, m02 = z.X;
        double m10 = x.Y, m11 = y.Y, m12 = z.Y;
        double m20 = x.Z, m21 = y.Z, m22 = z.Z;
        double trace = m00 + m11 + m22;
        double qx, qy, qz, qw;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            qw = 0.25 * s;
            qx = (m21 - m12) / s;
            qy = (m02 - m20) / s;
            qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            qw = (m21 - m12) / s;
            qx = 0.25 * s;
            qy = (m01 + m10) / s;
            qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            qw = (m02 - m20) / s;
            qx = (m01 + m10) / s;
            qy = 0.25 * s;
            qz = (m12 + m21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            qw = (m10 - m01) / s;
            qx = (m02 + m20) / s;
            qy = (m12 + m21) / s;
            qz = 0.25 * s;
        }
        double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (len < 1e-9) return Quat.Identity;
        return new Quat((float)(qx / len), (float)(qy / len), (float)(qz / len), (float)(qw / len));
    }

    private static float Vec3Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Vec3Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float DistanceSquared(Vec3 a, Vec3 b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return x * x + y * y + z * z;
    }

    private static JsonElement? Prop(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : null;
    private static int propInt(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    private static string propString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static Vec3 propVec3(JsonElement e, string name, Vec3 fallback)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3) return fallback;
        return new Vec3((float)v[0].GetDouble(), (float)v[1].GetDouble(), (float)v[2].GetDouble());
    }
    private static Quat propQuat(JsonElement e, string name, Quat fallback)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 4) return fallback;
        return new Quat((float)v[0].GetDouble(), (float)v[1].GetDouble(), (float)v[2].GetDouble(), (float)v[3].GetDouble());
    }

    private readonly record struct PosKey(long X, long Y, long Z) : IComparable<PosKey>
    {
        public int CompareTo(PosKey other)
        {
            int c = X.CompareTo(other.X);
            if (c != 0) return c;
            c = Y.CompareTo(other.Y);
            return c != 0 ? c : Z.CompareTo(other.Z);
        }
        public static PosKey From(Vec3 p, double tolerance)
        {
            tolerance = Math.Max(1e-7, tolerance);
            return new PosKey((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance), (long)Math.Round(p.Z / tolerance));
        }
    }

    private readonly record struct EdgeKey(PosKey A, PosKey B)
    {
        public static EdgeKey Create(PosKey a, PosKey b) => a.CompareTo(b) <= 0 ? new EdgeKey(a, b) : new EdgeKey(b, a);
    }

    private readonly record struct Cell(int X, int Y, int Z);

    private struct ColorSum
    {
        public float R, G, B;
        public int Count;
        public readonly Col ToCol() => new(R / Count, G / Count, B / Count, 1f);
    }

    private struct Bounds
    {
        public Vec3 Min;
        public Vec3 Max;
        public readonly Vec3 Size => Max - Min;
    }

    private struct Mat4
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;
        public static Mat4 Identity => new() { M11 = 1, M22 = 1, M33 = 1, M44 = 1 };
        public static Mat4 Translate(Vec3 t) => new() { M11 = 1, M22 = 1, M33 = 1, M44 = 1, M14 = t.X, M24 = t.Y, M34 = t.Z };
        public static Mat4 Scale(Vec3 s) => new() { M11 = s.X, M22 = s.Y, M33 = s.Z, M44 = 1 };
        public static Mat4 Rotate(Quat q)
        {
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            float xx = x * x, yy = y * y, zz = z * z;
            return new Mat4
            {
                M11 = 1 - 2 * (yy + zz), M12 = 2 * (x * y - w * z), M13 = 2 * (x * z + w * y),
                M21 = 2 * (x * y + w * z), M22 = 1 - 2 * (xx + zz), M23 = 2 * (y * z - w * x),
                M31 = 2 * (x * z - w * y), M32 = 2 * (y * z + w * x), M33 = 1 - 2 * (xx + yy),
                M44 = 1
            };
        }
        public Vec3 Transform(Vec3 p) => new(
            M11 * p.X + M12 * p.Y + M13 * p.Z + M14,
            M21 * p.X + M22 * p.Y + M23 * p.Z + M24,
            M31 * p.X + M32 * p.Y + M33 * p.Z + M34);
        public static Mat4 operator *(Mat4 a, Mat4 b) => new()
        {
            M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
            M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
            M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
            M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
            M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
            M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
            M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
            M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
            M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
            M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
            M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
            M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
            M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
            M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
            M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
            M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44
        };
    }
}
