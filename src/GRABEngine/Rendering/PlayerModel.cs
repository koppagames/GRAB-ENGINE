using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

/// <summary>
/// The retail GRAB player avatar: the real body.sgm and head.sgm meshes with the exact
/// material colours from the game's SGM materials. Play mode composes these around the
/// player capsule so the local player reads as a GRAB character instead of a marker.
/// </summary>
public static class PlayerModel
{
    public const float EyeHeight = 1.62f;

    private static Model3DGroup? _cached;

    public static Model3DGroup Create()
    {
        if (_cached != null) return _cached;
        var root = new Model3DGroup();
        AddSgm(root, "body", new Vector3D(0, 1.49, 0));
        AddSgm(root, "head", new Vector3D(0, 1.55, 0));
        AddShadow(root);
        _cached = root;
        return root;
    }

    private static void AddSgm(Model3DGroup root, string name, Vector3D offset)
    {
        string path = TextureLibrary.ModelPath(name);
        if (!System.IO.File.Exists(path)) return;
        foreach (var part in SgmLoader.LoadParts(path))
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Clamp(part.Color.Ri, 0, 255),
                (byte)Math.Clamp(part.Color.Gi, 0, 255),
                (byte)Math.Clamp(part.Color.Bi, 0, 255)));
            brush.Freeze();
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(brush));
            var glow = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
            glow.Freeze();
            material.Children.Add(new EmissiveMaterial(glow));
            material.Freeze();
            var model = new GeometryModel3D(part.Mesh, material) { BackMaterial = material };
            var transform = new Transform3DGroup();
            transform.Children.Add(new TranslateTransform3D(offset.X, offset.Y, offset.Z));
            transform.Freeze();
            model.Transform = transform;
            root.Children.Add(model);
        }
    }

    private static void AddShadow(Model3DGroup root)
    {
        var b = new MeshBuilder();
        b.Cylinder(new Point3D(0, 0.01, 0), new Point3D(0, 0.012, 0), 0.36, 28);
        var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        var shadow = new GeometryModel3D(b.ToMesh(), material) { BackMaterial = material };
        root.Children.Add(shadow);
    }
}
