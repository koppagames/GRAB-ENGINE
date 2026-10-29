using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

public sealed class SceneBuildResult
{
    public Model3DGroup Group { get; } = new();
    public Dictionary<GeometryModel3D, LevelNode> NodeOfModel { get; } = new();
    public Dictionary<LevelNode, Model3DGroup> Visuals { get; } = new();
}

public static class SceneBuilder
{
    private static readonly Dictionary<string, MeshGeometry3D> ShapeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MeshGeometry3D> LitShapeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Material> MaterialCache = new();
    private static (MeshGeometry3D White, MeshGeometry3D Black)? _startFinishMeshes;
    private static Material? _padWhite;
    private static Material? _padBlack;
    private static Material? _startGreen;

    public static Material BlockMaterial(LevelNode n)
    {
        string texture = MaterialInfo.Texture(n.Material);
        var worldScale = n.WorldScale;
        double tile = Math.Clamp(Math.Max(worldScale.X, Math.Max(worldScale.Y, worldScale.Z)), 1, 12);
        // GRAB only multiplies Color1 into "Default Colored". Ice, lava, wood and the
        // other materials sample their texture directly, so tinting them here made the
        // editor show colours the real game never renders (dark ice, brown wood, ...).
        var tint = MaterialInfo.IsColorable(n.Material) ? n.Color1 : Col.White;
        string key = texture + "|" + tint.Hex + "|" + n.IsNeon + "|" + n.IsTransparent + "|" + n.IsAdditive + "|" + (int)(n.SpecularBrightness * 10) + "|" + tile.ToString("0.##");
        if (MaterialCache.TryGetValue(key, out var cached)) return cached;
        var brush = TextureLibrary.GetBrush(texture, tint, !n.IsTransparent, tile);
        var group = new MaterialGroup();
        if (n.IsNeon || n.IsAdditive)
        {
            group.Children.Add(new EmissiveMaterial(brush));
            if (!n.IsAdditive)
                group.Children.Add(new DiffuseMaterial(brush));
        }
        else
        {
            group.Children.Add(new DiffuseMaterial(brush));
            if (n.SpecularBrightness > 0.01f && !n.IsTransparent)
            {
                var spec = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(n.SpecularBrightness * 32, 0, 255), 255, 255, 255));
                spec.Freeze();
                group.Children.Add(new SpecularMaterial(spec, 28));
            }
        }
        if (n.IsTransparent) group.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))));
        group.Freeze();
        MaterialCache[key] = group;
        return group;
    }

    /// <summary>
    /// WPF shades lights per vertex. A 10x10 floor only has four corner vertices, so a
    /// nearby point light either lit the whole face or none of it. Subdividing large
    /// blocks gives the light somewhere to fall off and produces the soft pool of light
    /// GRAB has, without changing texture coordinates or the block's silhouette.
    /// </summary>
    public static MeshGeometry3D BlockMesh(LevelNode n, IReadOnlyList<LevelNode>? lights = null)
    {
        string name = ShapeInfo.ModelName(n.Shape);
        int divisions = BlockDivisions(n, lights);
        if (divisions <= 1) return ShapeMesh(n.Shape);
        string key = name + "|" + divisions;
        if (LitShapeCache.TryGetValue(key, out var mesh)) return mesh;
        mesh = MeshTessellator.Subdivide(ShapeMesh(n.Shape), divisions);
        LitShapeCache[key] = mesh;
        return mesh;
    }

    private static int BlockDivisions(LevelNode n, IReadOnlyList<LevelNode>? lights)
    {
        var s = n.WorldScale;
        double largest = Math.Max(Math.Abs(s.X), Math.Max(Math.Abs(s.Y), Math.Abs(s.Z)));
        if (largest <= 1.2) return 1;
        if (lights == null || !NearAnyLight(n, lights)) return 1;
        // roughly one new vertex every 45 cm of the block's largest side; 24 is enough
        // to hide the triangle bands of a 12 m floor while staying cheap to rebuild
        int divisions = (int)Math.Ceiling(largest / 0.45);
        divisions = Math.Clamp(divisions, 2, 24);
        // Curved shapes are already dense. Four times their triangles is plenty; a cube
        // gets the full grid because its flat faces were the visible problem.
        if (n.Shape != ShapeInfo.Cube) divisions = Math.Min(divisions, 4);
        return divisions;
    }

    private static bool NearAnyLight(LevelNode n, IReadOnlyList<LevelNode> lights)
    {
        var p = n.WorldPosition;
        var half = new Vec3(Math.Abs(n.WorldScale.X) * 0.5f, Math.Abs(n.WorldScale.Y) * 0.5f, Math.Abs(n.WorldScale.Z) * 0.5f);
        foreach (var l in lights)
        {
            var lp = l.WorldPosition;
            double dx = Math.Max(0, Math.Abs(lp.X - p.X) - half.X);
            double dy = Math.Max(0, Math.Abs(lp.Y - p.Y) - half.Y);
            double dz = Math.Max(0, Math.Abs(lp.Z - p.Z) - half.Z);
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance <= Math.Max(0.1, l.LightRange)) return true;
        }
        return false;
    }

    public static MeshGeometry3D ShapeMesh(int shape)
    {
        string name = ShapeInfo.ModelName(shape);
        if (ShapeCache.TryGetValue(name, out var m)) return m;
        var path = TextureLibrary.ModelPath(name);
        if (!System.IO.File.Exists(path)) path = TextureLibrary.ModelPath("cube");
        m = SgmLoader.LoadMesh(path);
        ShapeCache[name] = m;
        return m;
    }

    public static MeshGeometry3D SpecialMesh(string name)
    {
        if (ShapeCache.TryGetValue(name, out var m)) return m;
        var path = TextureLibrary.ModelPath(name);
        m = System.IO.File.Exists(path) ? SgmLoader.LoadMesh(path) : ShapeMesh(ShapeInfo.Cube);
        ShapeCache[name] = m;
        return m;
    }

    public static Transform3D TransformFor(Vec3 pos, Quat rot, Vec3 scale)
    {
        var g = new Transform3DGroup();
        g.Children.Add(new ScaleTransform3D(scale.X == 0 ? 1 : scale.X, scale.Y == 0 ? 1 : scale.Y, scale.Z == 0 ? 1 : scale.Z));
        g.Children.Add(new RotateTransform3D(new QuaternionRotation3D(new Quaternion(rot.X, rot.Y, rot.Z, rot.W))));
        g.Children.Add(new TranslateTransform3D(pos.X, pos.Y, pos.Z));
        g.Freeze();
        return g;
    }

    public static SceneBuildResult Build(GrabLevel level, LevelNode? selected, bool showGrid = true)
        => Build(level, selected == null ? null : new[] { selected }, selected, showGrid);

    public static SceneBuildResult Build(GrabLevel level, IReadOnlyCollection<LevelNode>? selected,
        LevelNode? active = null, bool showGrid = true)
    {
        var result = new SceneBuildResult();
        if (showGrid) AddGrid(result.Group);
        level.EnsureLayers();
        var lightNodes = level.AllNodes()
            .Where(n => n.Kind == NodeKind.Light && level.IsLayerVisible(n)).ToList();
        int lights = 0;
        foreach (var n in level.AllNodes().Where(n => !n.Generated))
        {
            if (n.Kind == NodeKind.Group || !level.IsLayerVisible(n)) continue;
            var model = CreateNode(n, level, result, lightNodes);
            if (model == null) continue;
            result.Group.Children.Add(model);
            result.Visuals[n] = model;
            if (n.Kind == NodeKind.Light && lights++ < 20)
                AddLight(result.Group, n);
        }
        if (selected != null)
            foreach (var node in selected.Distinct().Where(level.IsLayerVisible))
                AddSelection(result.Group, node, ReferenceEquals(node, active ?? selected.LastOrDefault()));
        return result;
    }

    private static Model3DGroup? CreateNode(LevelNode n, GrabLevel level, SceneBuildResult result, IReadOnlyList<LevelNode> lights)
    {
        var group = new Model3DGroup();
        var pos = n.WorldPosition;
        var rot = n.WorldRotation;
        var scale = n.WorldScale;
        switch (n.Kind)
        {
            case NodeKind.Static:
            case NodeKind.Crumbling:
            {
                var geo = new GeometryModel3D(BlockMesh(n, lights), BlockMaterial(n));
                geo.Transform = TransformFor(pos, rot, scale);
                geo.BackMaterial = geo.Material;
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                break;
            }
            case NodeKind.Start:
            case NodeKind.Finish:
            {
                // GRAB's start_finish.sgm is a 32-sided drum whose faces alternate between
                // the two material colours (white/black), like a finish-line pedestal. The
                // old flat-colour version lost that and looked nothing like the real game.
                float radius = Math.Max(0.15f, n.Radius);
                float height = Math.Max(0.01f, n.WorldScale.Y);
                var padScale = new Vec3(radius * 2 * Math.Max(n.WorldScale.X, 0.01f), height, radius * 2 * Math.Max(n.WorldScale.Z, 0.01f));
                var padTransform = TransformFor(pos, rot, padScale);
                var pads = StartFinishMeshes();
                var white = new GeometryModel3D(pads.White, PadWhiteMaterial()) { BackMaterial = PadWhiteMaterial(), Transform = padTransform };
                var black = new GeometryModel3D(pads.Black, PadBlackMaterial()) { BackMaterial = PadBlackMaterial(), Transform = padTransform };
                group.Children.Add(white);
                group.Children.Add(black);
                result.NodeOfModel[white] = n;
                result.NodeOfModel[black] = n;

                // White top disc so the open drum doesn't look hollow from above.
                var top = new GeometryModel3D(ShapeMesh(ShapeInfo.Cylinder), PadWhiteMaterial())
                {
                    BackMaterial = PadWhiteMaterial(),
                    Transform = TransformFor(pos + new Vec3(0, height, 0), rot, new Vec3(radius * 2 * 0.985f, 0.04f, radius * 2 * 0.985f))
                };
                group.Children.Add(top);
                result.NodeOfModel[top] = n;

                var accent = StartGreenMaterial();
                // start_direction sits at local y=0; start_end sits at local y=0.5.
                // Offset each model by its own local height so the marker lands on the pad.
                float markerLift = height + 0.04f - (n.Kind == NodeKind.Finish ? 0.5f : 0f);
                var marker = new GeometryModel3D(SpecialMesh(n.Kind == NodeKind.Start ? "start_direction" : "start_end"), accent)
                {
                    BackMaterial = accent,
                    Transform = TransformFor(pos + new Vec3(0, markerLift, 0), rot, new Vec3(radius * 2, 1, radius * 2))
                };
                group.Children.Add(marker);
                result.NodeOfModel[marker] = n;

                var rim = new GeometryModel3D(MeshFactory.Ring(new Vector3D(0, 1, 0), radius * 0.86, 0.025), accent)
                {
                    BackMaterial = accent,
                    Transform = TransformFor(pos + new Vec3(0, height + 0.045f, 0), rot, new Vec3(1, 1, 1))
                };
                group.Children.Add(rim);
                result.NodeOfModel[rim] = n;
                break;
            }
            case NodeKind.Sign:
            {
                var brush = TextureLibrary.GetBrush("default", n.SignColor);
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(brush));
                if (n.SignNeon) mat.Children.Add(new EmissiveMaterial(brush));
                mat.Freeze();
                var geo = new GeometryModel3D(SpecialMesh("sign"), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, new Vec3(n.SignScale, n.SignScale, n.SignScale));
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                if (!n.HideModel)
                {
                    var textMat = new MaterialGroup();
                    var tb = TextureLibrary.SignBrush(n.Text, n.SignColor, n.SignWeight);
                    textMat.Children.Add(new DiffuseMaterial(tb));
                    if (n.SignNeon) textMat.Children.Add(new EmissiveMaterial(tb));
                    textMat.Freeze();
                    var textMesh = MeshFactory.Quad(0.62 * n.SignScale, 0.5 * n.SignScale);
                    var offset = rot.Rotate(new Vec3(0, -0.18f * n.SignScale, 0.025f * n.SignScale));
                    var textGeo = new GeometryModel3D(textMesh, textMat)
                    {
                        BackMaterial = textMat,
                        Transform = TransformFor(pos + offset, rot, new Vec3(1, 1, 1))
                    };
                    group.Children.Add(textGeo);
                    result.NodeOfModel[textGeo] = n;
                }
                break;
            }
            case NodeKind.GASM:
            {
                var brush = TextureLibrary.GetBrush("code", new Col(0.55f, 0.75f, 1f, 1f));
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Children.Add(new EmissiveMaterial(TextureLibrary.GetBrush("code", new Col(0.4f, 0.55f, 0.9f, 0.6f))));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(ShapeInfo.Cube), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, new Vec3(Math.Max(0.35f, scale.X), Math.Max(0.35f, scale.Y), Math.Max(0.35f, scale.Z)));
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                break;
            }
            case NodeKind.Light:
            {
                var brush = new SolidColorBrush(Color.FromRgb((byte)n.LightColor.Ri, (byte)n.LightColor.Gi, (byte)n.LightColor.Bi));
                brush.Freeze();
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Children.Add(new EmissiveMaterial(brush));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(ShapeInfo.Sphere), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, new Vec3(0.18f, 0.18f, 0.18f));
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                var rays = new MeshBuilder();
                for (int i = 0; i < 6; i++)
                {
                    double ang = i * Math.PI / 3;
                    var d = new Vector3D(Math.Cos(ang), 0, Math.Sin(ang));
                    rays.Line(new Point3D(d.X * 0.24, 0, d.Z * 0.24), new Point3D(d.X * 0.42, 0, d.Z * 0.42), 0.012);
                    rays.Line(new Point3D(0, d.X * 0.24, d.Z * 0.24), new Point3D(0, d.X * 0.42, d.Z * 0.42), 0.012);
                }
                var rayGeo = new GeometryModel3D(rays.ToMesh(), Emissive(n.LightColor)) { BackMaterial = Emissive(n.LightColor) };
                rayGeo.Transform = TransformFor(pos, rot, new Vec3(1, 1, 1));
                group.Children.Add(rayGeo);
                result.NodeOfModel[rayGeo] = n;
                break;
            }
            case NodeKind.Sound:
            {
                var mat = new MaterialGroup();
                var brush = TextureLibrary.GetBrush("sound", new Col(0.8f, 0.8f, 0.8f));
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Children.Add(new EmissiveMaterial(TextureLibrary.GetBrush("sound", new Col(0.35f, 0.35f, 0.35f))));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(ShapeInfo.Sphere), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, new Vec3(0.3f, 0.3f, 0.3f));
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                break;
            }
            case NodeKind.ParticleEmitter:
            {
                var pc = n.StartColor.Ai < 20 ? new Col(0.35f, 0.9f, 1f) : n.StartColor;
                var core = new SolidColorBrush(Color.FromArgb(230, (byte)pc.Ri, (byte)pc.Gi, (byte)pc.Bi));
                core.Freeze();
                var coreMat = new MaterialGroup();
                coreMat.Children.Add(new EmissiveMaterial(core));
                coreMat.Children.Add(new DiffuseMaterial(core));
                coreMat.Freeze();
                var ball = new GeometryModel3D(ShapeMesh(ShapeInfo.Sphere), coreMat) { BackMaterial = coreMat };
                ball.Transform = TransformFor(pos, rot, new Vec3(0.3f, 0.3f, 0.3f));
                group.Children.Add(ball);
                result.NodeOfModel[ball] = n;

                // The live plume is simulated by ParticleSystem; here we only draw the
                // emitter gizmo so it stays selectable in the editor.
                var wire = new MeshBuilder();
                wire.WireBox(new Point3D(0, 0, 0), new Vector3D(1, 1, 1), 0.035);
                var wireGeo = new GeometryModel3D(wire.ToMesh(), Emissive(new Col(1f, 0.55f, 0.15f)))
                {
                    BackMaterial = Emissive(new Col(1f, 0.55f, 0.15f)),
                    Transform = TransformFor(pos, rot, new Vec3(Math.Max(0.75f, scale.X), Math.Max(0.75f, scale.Y), Math.Max(0.75f, scale.Z)))
                };
                group.Children.Add(wireGeo);
                result.NodeOfModel[wireGeo] = n;
                var ring = new MeshBuilder();
                ring.Circle(new Point3D(0, 0, 0), new Vector3D(0, 1, 0), 0.55, 0.02);
                var ringGeo = new GeometryModel3D(ring.ToMesh(), Emissive(new Col(1f, 0.55f, 0.15f)))
                {
                    BackMaterial = Emissive(new Col(1f, 0.55f, 0.15f)),
                    Transform = TransformFor(pos, rot, new Vec3(1, 1, 1))
                };
                group.Children.Add(ringGeo);
                result.NodeOfModel[ringGeo] = n;
                break;
            }
            case NodeKind.LobbyTerminal:
            {
                var mat = new MaterialGroup();
                var brush = TextureLibrary.GetBrush("code", new Col(0.7f, 0.7f, 0.7f));
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Children.Add(new EmissiveMaterial(TextureLibrary.GetBrush("code", new Col(0.25f, 0.25f, 0.25f))));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(ShapeInfo.Cube), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, new Vec3(1, 1, 1));
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                break;
            }
            case NodeKind.Trigger:
            {
                var tint = new Col(0.2f, 0.75f, 1f, 0.16f);
                var brush = new SolidColorBrush(Color.FromArgb(38, 45, 170, 255));
                brush.Freeze();
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(n.Shape == 0 ? ShapeInfo.Cube : n.Shape), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, scale);
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                var wire = new MeshBuilder();
                wire.WireBox(new Point3D(0, 0, 0), new Vector3D(1, 1, 1), 0.025);
                var wireGeo = new GeometryModel3D(wire.ToMesh(), Emissive(new Col(0.2f, 0.8f, 1f, 0.8f))) { BackMaterial = Emissive(new Col(0.2f, 0.8f, 1f, 0.4f)) };
                wireGeo.Transform = TransformFor(pos, rot, scale);
                group.Children.Add(wireGeo);
                result.NodeOfModel[wireGeo] = n;
                break;
            }
            case NodeKind.Gravity:
            {
                var brush = new SolidColorBrush(Color.FromArgb(45, 180, 120, 255));
                brush.Freeze();
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(brush));
                mat.Freeze();
                var geo = new GeometryModel3D(ShapeMesh(ShapeInfo.Cube), mat) { BackMaterial = mat };
                geo.Transform = TransformFor(pos, rot, scale);
                group.Children.Add(geo);
                result.NodeOfModel[geo] = n;
                var arrow = new MeshBuilder();
                var dir = n.GravityDirection;
                dir = dir.Length < 0.01f ? new Vec3(0, -1, 0) : dir;
                var end = new Point3D(dir.X, dir.Y, dir.Z);
                arrow.Cylinder(new Point3D(0, 0, 0), end, 0.03);
                var coneDir = new Vector3D(dir.X, dir.Y, dir.Z);
                var tip = new Point3D(dir.X * 1.15, dir.Y * 1.15, dir.Z * 1.15);
                arrow.Cone(end, tip, 0.08);
                var ageo = new GeometryModel3D(arrow.ToMesh(), Emissive(new Col(0.7f, 0.45f, 1f))) { BackMaterial = Emissive(new Col(0.7f, 0.45f, 1f)) };
                ageo.Transform = TransformFor(pos, rot, scale);
                group.Children.Add(ageo);
                result.NodeOfModel[ageo] = n;
                break;
            }
            default:
                return null;
        }
        // Do not freeze node groups: the animation timeline previews a key by setting
        // the transform on the live visual, and WPF throws when a frozen Model3DGroup
        // is modified. The child meshes and materials stay frozen for performance.
        return group;
    }

    private static (MeshGeometry3D White, MeshGeometry3D Black) StartFinishMeshes()
    {
        if (_startFinishMeshes is { } cached) return cached;
        var source = SpecialMesh("start_finish");
        var whitePos = new Point3DCollection();
        var whiteNor = new Vector3DCollection();
        var whiteIdx = new Int32Collection();
        var blackPos = new Point3DCollection();
        var blackNor = new Vector3DCollection();
        var blackIdx = new Int32Collection();

        for (int i = 0; i + 2 < source.TriangleIndices.Count; i += 3)
        {
            int a = source.TriangleIndices[i], b = source.TriangleIndices[i + 1], c = source.TriangleIndices[i + 2];
            var pa = source.Positions[a];
            var pb = source.Positions[b];
            var pc = source.Positions[c];
            double cx = (pa.X + pb.X + pc.X) / 3;
            double cy = (pa.Y + pb.Y + pc.Y) / 3;
            double cz = (pa.Z + pb.Z + pc.Z) / 3;
            double angle = Math.Atan2(cx, cz);
            int band = Math.Clamp((int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * 32), 0, 31);
            // 32 columns x 2 rows of quads: alternate in both directions, the classic
            // black/white checkered finish pedestal from the real game.
            bool isWhite = ((band + (cy >= 0.5 ? 1 : 0)) & 1) == 0;
            var pos = isWhite ? whitePos : blackPos;
            var nor = isWhite ? whiteNor : blackNor;
            var idx = isWhite ? whiteIdx : blackIdx;
            int start = pos.Count;
            pos.Add(pa); pos.Add(pb); pos.Add(pc);
            nor.Add(source.Normals[a]); nor.Add(source.Normals[b]); nor.Add(source.Normals[c]);
            idx.Add(start); idx.Add(start + 1); idx.Add(start + 2);
        }

        var white = new MeshGeometry3D { Positions = whitePos, Normals = whiteNor, TriangleIndices = whiteIdx };
        var black = new MeshGeometry3D { Positions = blackPos, Normals = blackNor, TriangleIndices = blackIdx };
        white.Freeze();
        black.Freeze();
        _startFinishMeshes = (white, black);
        return (white, black);
    }

    private static Material PadWhiteMaterial()
    {
        if (_padWhite != null) return _padWhite;
        var b = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        b.Freeze();
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(b));
        g.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(36, 36, 36))));
        var s = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
        s.Freeze();
        g.Children.Add(new SpecularMaterial(s, 40));
        g.Freeze();
        _padWhite = g;
        return g;
    }

    private static Material PadBlackMaterial()
    {
        if (_padBlack != null) return _padBlack;
        var b = new SolidColorBrush(Color.FromRgb(28, 28, 30));
        b.Freeze();
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(b));
        g.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(10, 10, 12))));
        g.Freeze();
        _padBlack = g;
        return g;
    }

    private static Material StartGreenMaterial()
    {
        if (_startGreen != null) return _startGreen;
        var c = new Col(0.04f, 1f, 0.12f, 1);
        var b = new SolidColorBrush(Color.FromRgb((byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
        b.Freeze();
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(b));
        g.Children.Add(new EmissiveMaterial(b));
        g.Freeze();
        _startGreen = g;
        return g;
    }

    private static Material Emissive(Col c)
    {
        var m = new MaterialGroup();
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(c.Ai, 0, 255), (byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
        b.Freeze();
        m.Children.Add(new EmissiveMaterial(b));
        m.Freeze();
        return m;
    }

    private static void AddGrid(Model3DGroup group)
    {
        var grid = new GeometryModel3D(MeshFactory.Grid(50, 1, 0.012), Emissive(new Col(0.25f, 0.25f, 0.25f, 0.9f)))
        {
            BackMaterial = Emissive(new Col(0.18f, 0.18f, 0.18f, 0.5f))
        };
        group.Children.Add(grid);
        var xAxis = new GeometryModel3D(MeshFactory.Line(new Point3D(-50, 0, 0), new Point3D(50, 0, 0), 0.025), Emissive(new Col(0.75f, 0.18f, 0.18f)))
        { BackMaterial = Emissive(new Col(0.5f, 0.1f, 0.1f)) };
        var zAxis = new GeometryModel3D(MeshFactory.Line(new Point3D(0, 0, -50), new Point3D(0, 0, 50), 0.025), Emissive(new Col(0.18f, 0.4f, 0.9f)))
        { BackMaterial = Emissive(new Col(0.1f, 0.25f, 0.6f)) };
        group.Children.Add(xAxis);
        group.Children.Add(zAxis);
    }

    private static void AddSelection(Model3DGroup group, LevelNode n, bool active)
    {
        Vec3 size;
        Vec3 pos = n.WorldPosition;
        if (n.IsGroup)
        {
            var bounds = BoundsOf(n);
            size = bounds.Size;
            pos = bounds.Center;
        }
        else
        {
            size = n.Kind switch
            {
                NodeKind.Trigger => n.WorldScale,
                NodeKind.Static or NodeKind.Crumbling or NodeKind.Gravity => n.WorldScale,
                NodeKind.Start or NodeKind.Finish => new Vec3(Math.Max(0.2f, n.Radius * 2), Math.Max(0.1f, n.WorldScale.Y), Math.Max(0.2f, n.Radius * 2)),
                NodeKind.Sign => new Vec3(0.75f, 1.1f, 0.2f) * n.SignScale,
                NodeKind.Light or NodeKind.Sound or NodeKind.ParticleEmitter => new Vec3(0.5f, 0.5f, 0.5f),
                NodeKind.GASM => new Vec3(0.6f, 0.6f, 0.6f),
                _ => new Vec3(1, 1, 1)
            };
        }
        size = new Vec3(Math.Max(size.X, 0.08f), Math.Max(size.Y, 0.08f), Math.Max(size.Z, 0.08f));
        var b = new MeshBuilder();
        b.WireBox(new Point3D(0, 0, 0), new Vector3D(size.X, size.Y, size.Z), 0.018);
        // The active object gets GRAB ENGINE's bright cyan; additional selected
        // objects use a dimmer outline so the whole selection is readable at once.
        var color = active ? new Col(0.2f, 0.85f, 1f) : new Col(0.55f, 0.68f, 0.72f);
        var geo = new GeometryModel3D(b.ToMesh(), Emissive(color))
        {
            BackMaterial = Emissive(color),
            Transform = new TranslateTransform3D(pos.X, pos.Y, pos.Z)
        };
        group.Children.Add(geo);
    }

    public static (Vec3 Center, Vec3 Size) BoundsOf(LevelNode n)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var d in n.SelfAndDescendants())
        {
            if (d.Kind == NodeKind.Group) continue;
            var p = d.WorldPosition;
            var s = d.WorldScale;
            if (d.Kind is NodeKind.Start or NodeKind.Finish) s = new Vec3(Math.Max(0.2f, d.Radius * 2), Math.Max(0.1f, d.WorldScale.Y), Math.Max(0.2f, d.Radius * 2));
            if (d.Kind == NodeKind.Light || d.Kind == NodeKind.Sound || d.Kind == NodeKind.ParticleEmitter) s = new Vec3(0.5f, 0.5f, 0.5f);
            if (d.Kind == NodeKind.Sign) s = new Vec3(0.75f, 1.1f, 0.2f) * d.SignScale;
            var q = d.WorldRotation;
            for (int i = 0; i < 8; i++)
            {
                var local = new Vec3(((i & 1) == 0 ? -0.5f : 0.5f) * s.X,
                    ((i & 2) == 0 ? -0.5f : 0.5f) * s.Y,
                    ((i & 4) == 0 ? -0.5f : 0.5f) * s.Z);
                var w = p + q.Rotate(local);
                min = new Vec3(MathF.Min(min.X, w.X), MathF.Min(min.Y, w.Y), MathF.Min(min.Z, w.Z));
                max = new Vec3(MathF.Max(max.X, w.X), MathF.Max(max.Y, w.Y), MathF.Max(max.Z, w.Z));
            }
        }
        if (min.X > max.X) return (n.WorldPosition, new Vec3(1, 1, 1));
        var size = max - min;
        return (new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2), size);
    }

    private static void AddLight(Model3DGroup group, LevelNode n)
    {
        double boost = Math.Clamp(n.LightIntensity / 2.3, 0.05, 4);
        var c = Color.FromRgb((byte)Math.Clamp(n.LightColor.Ri * boost, 0, 255),
            (byte)Math.Clamp(n.LightColor.Gi * boost, 0, 255), (byte)Math.Clamp(n.LightColor.Bi * boost, 0, 255));
        var wp = n.WorldPosition;
        var pos = new Point3D(wp.X, wp.Y, wp.Z);
        // GRAB's shader cuts a light off exactly at LightRange. WPF measures Range in
        // world units too, so scaling it (the old *4) was what flooded entire rooms.
        double range = Math.Max(0.05, n.LightRange);
        if (n.LightType == 1)
        {
            var d = n.WorldRotation.Rotate(new Vec3(0, 0, -1));
            double cone = Math.Clamp(n.LightAngle, 1, 89);
            var l = new SpotLight(c, pos, new Vector3D(d.X, d.Y, d.Z), cone, 0)
            {
                Range = range
            };
            group.Children.Add(l);
        }
        else
        {
            var l = new PointLight(c, pos)
            {
                Range = range
            };
            group.Children.Add(l);
        }
    }
}
