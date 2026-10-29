using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrabEngine.Core;
using GrabEngine.Formats;

namespace GrabEngine.UI;

/// <summary>
/// Produces a recognisable isometric thumbnail directly from a level or prefab file.
/// GRAB levels do not store screenshots, so drawing the real block layout is both faster
/// and more useful than a generic placeholder. Results are cached by path and timestamp.
/// </summary>
public static class LevelThumbnailRenderer
{
    private const string CacheVersion = "v2";

    public static string CacheRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GRAB ENGINE", "Thumbnails");

    public static BitmapSource? TryCached(string path)
    {
        try
        {
            var cache = CachePath(path);
            if (!File.Exists(cache)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(cache, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource RenderFile(string path, bool useCache = true)
    {
        if (useCache && TryCached(path) is { } cached) return cached;
        GrabLevel level;
        if (path.EndsWith(".level", StringComparison.OrdinalIgnoreCase))
            level = LevelIO.Load(path);
        else
            level = ProjectIO.Load(path);
        var image = Render(level);
        SaveCache(path, image);
        return image;
    }

    public static string CachePath(string path)
    {
        Directory.CreateDirectory(CacheRoot);
        string key = CacheVersion + "|" + Path.GetFullPath(path).ToLowerInvariant();
        try
        {
            var info = new FileInfo(path);
            key += "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch { }
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheRoot, Convert.ToHexString(hash)[..20] + ".png");
    }

    public static void SaveCache(string path, BitmapSource image)
    {
        try
        {
            var target = CachePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(target);
            encoder.Save(stream);
        }
        catch
        {
            // Thumbnail caching is an optimisation. A read-only profile should not break
            // opening the level library.
        }
    }

    public static BitmapSource Render(GrabLevel level, int width = 360, int height = 202)
    {
        width = Math.Max(64, width);
        height = Math.Max(48, height);

        var blocks = level.AllNodes()
            .Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && n.Shape is ShapeInfo.Cube or ShapeInfo.Sphere or ShapeInfo.Cylinder or ShapeInfo.Pyramid or ShapeInfo.Prism or ShapeInfo.Cone or ShapeInfo.PyramidSquare)
            .ToList();

        // Skip interior blocks and cap the work for very large imported models. The
        // thumbnail still communicates the level's shape.
        const int MaxDrawn = 4200;
        if (blocks.Count > MaxDrawn)
        {
            var stride = (double)blocks.Count / MaxDrawn;
            blocks = Enumerable.Range(0, MaxDrawn).Select(i => blocks[(int)(i * stride)]).ToList();
        }

        var bounds = Bounds(level, blocks);
        double spanX = Math.Max(0.5, bounds.Max.X - bounds.Min.X);
        double spanY = Math.Max(0.5, bounds.Max.Y - bounds.Min.Y);
        double spanZ = Math.Max(0.5, bounds.Max.Z - bounds.Min.Z);
        double cos = Math.Cos(Math.PI / 6);
        double sin = Math.Sin(Math.PI / 6);
        double projectedWidth = (spanX + spanZ) * cos;
        double projectedHeight = (spanX + spanZ) * sin + spanY;
        double scale = Math.Min((width - 34) / Math.Max(1, projectedWidth), (height - 26) / Math.Max(1, projectedHeight));
        scale = Math.Clamp(scale, 0.002, 80);
        var center = new Vec3((bounds.Min.X + bounds.Max.X) * 0.5f,
            (bounds.Min.Y + bounds.Max.Y) * 0.5f, (bounds.Min.Z + bounds.Max.Z) * 0.5f);

        double ToX(double x, double z) => width * 0.5 + (x - z) * cos * scale;
        double ToY(double x, double y, double z) => height * 0.64 + ((x + z) * sin - (y - center.Y)) * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(
                Color.FromRgb((byte)Math.Clamp(level.Ambience.SkyZenithColor.Ri, 140, 205),
                    (byte)Math.Clamp(level.Ambience.SkyZenithColor.Gi, 175, 225),
                    (byte)Math.Clamp(level.Ambience.SkyZenithColor.Bi, 220, 255)),
                Color.FromRgb(230, 240, 245), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, width, height));

            // Soft horizon and a ground haze behind the blocks.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null,
                new Rect(0, height * 0.62, width, height * 0.38));

            if (blocks.Count == 0)
            {
                var text = new FormattedText("EMPTY LEVEL", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13,
                    new SolidColorBrush(Color.FromRgb(100, 112, 122)), 1.0);
                dc.DrawText(text, new Point((width - text.Width) / 2, height * 0.53));
            }
            else
            {
                // Painter's algorithm: blocks further along the camera's depth direction
                // are drawn first. A small bias on Y keeps higher blocks on top.
                var ordered = blocks
                    .Select(n => (Node: n, Depth: n.WorldPosition.X + n.WorldPosition.Z + n.WorldPosition.Y * 0.25))
                    .OrderBy(x => x.Depth)
                    .ToList();
                foreach (var (node, _) in ordered)
                    DrawBlock(dc, node, ToX, ToY, scale);
            }

            var vignette = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.8,
                RadiusY = 0.8
            };
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(70, 0, 0, 0), 1));
            dc.DrawRectangle(vignette, null, new Rect(0, 0, width, height));

            var footer = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(210, 9, 13, 18), 90);
            dc.DrawRectangle(footer, null, new Rect(0, height - 44, width, 44));
            var caption = $"{level.AllNodes().Count()} objects  ·  {(level.Creators ?? "").Trim()}";
            if (caption.Length > 52) caption = caption[..52] + "…";
            var ft = new FormattedText(caption, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5,
                new SolidColorBrush(Color.FromRgb(205, 218, 230)), 1.0);
            dc.DrawText(ft, new Point(13, height - 25));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawBlock(DrawingContext dc, LevelNode n,
        Func<double, double, double> toX, Func<double, double, double, double> toY, double scale)
    {
        var p = n.WorldPosition;
        var s = n.WorldScale;
        double hx = Math.Abs(s.X) * 0.5;
        double hy = Math.Abs(s.Y) * 0.5;
        double hz = Math.Abs(s.Z) * 0.5;
        if (n.Shape is ShapeInfo.Sphere or ShapeInfo.Cylinder or ShapeInfo.Cone or ShapeInfo.Pyramid or ShapeInfo.Prism or ShapeInfo.PyramidSquare)
        {
            hx *= 0.82;
            hz *= 0.82;
        }
        double x0 = p.X - hx, x1 = p.X + hx;
        double z0 = p.Z - hz, z1 = p.Z + hz;
        double y0 = p.Y - hy, y1 = p.Y + hy;

        Point P(double x, double y, double z) => new(toX(x, z), toY(x, y, z));

        var top = new[] { P(x0, y1, z0), P(x1, y1, z0), P(x1, y1, z1), P(x0, y1, z1) };
        var left = new[] { P(x0, y1, z1), P(x0, y1, z0), P(x0, y0, z0), P(x0, y0, z1) };
        var right = new[] { P(x1, y1, z1), P(x1, y1, z0), P(x1, y0, z0), P(x1, y0, z1) };

        var color = ThumbnailColor(n);
        var topColor = Lighten(color, 1.18);
        var leftColor = Lighten(color, 0.78);
        var rightColor = Lighten(color, 0.61);
        var edge = Color.FromArgb(70, 10, 12, 15);
        var pen = new Pen(new SolidColorBrush(edge), Math.Clamp(scale * 0.09, 0.3, 1.1));
        pen.Freeze();

        dc.DrawGeometry(new SolidColorBrush(topColor), pen, Polygon(top));
        dc.DrawGeometry(new SolidColorBrush(leftColor), pen, Polygon(left));
        dc.DrawGeometry(new SolidColorBrush(rightColor), pen, Polygon(right));
    }

    private static StreamGeometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static (Vec3 Min, Vec3 Max) Bounds(GrabLevel level, IReadOnlyList<LevelNode> blocks)
    {
        if (blocks.Count == 0)
            return (new Vec3(-4, 0, -4), new Vec3(4, 3, 4));

        // Big workshop levels often contain one stray block far away from the playable
        // area. Framing the full AABB makes the actual level a few pixels wide, so for
        // large scenes use the 2nd-98th percentile of block centres and a small pad.
        if (blocks.Count >= 40)
        {
            var centreX = blocks.Select(n => n.WorldPosition.X).OrderBy(x => x).ToList();
            var centreY = blocks.Select(n => n.WorldPosition.Y).OrderBy(x => x).ToList();
            var centreZ = blocks.Select(n => n.WorldPosition.Z).OrderBy(x => x).ToList();
            int lo = Math.Max(0, (int)(blocks.Count * 0.02));
            int hi = Math.Min(blocks.Count - 1, (int)(blocks.Count * 0.98));
            float pad = Math.Max(1.5f, Math.Max(centreX[hi] - centreX[lo], Math.Max(centreY[hi] - centreY[lo], centreZ[hi] - centreZ[lo])) * 0.04f);
            return (new Vec3(centreX[lo] - pad, Math.Min(0, centreY[lo] - pad), centreZ[lo] - pad),
                new Vec3(centreX[hi] + pad, centreY[hi] + pad, centreZ[hi] + pad));
        }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var n in blocks)
        {
            var p = n.WorldPosition;
            var s = n.WorldScale;
            minX = Math.Min(minX, p.X - Math.Abs(s.X) * 0.5);
            minY = Math.Min(minY, p.Y - Math.Abs(s.Y) * 0.5);
            minZ = Math.Min(minZ, p.Z - Math.Abs(s.Z) * 0.5);
            maxX = Math.Max(maxX, p.X + Math.Abs(s.X) * 0.5);
            maxY = Math.Max(maxY, p.Y + Math.Abs(s.Y) * 0.5);
            maxZ = Math.Max(maxZ, p.Z + Math.Abs(s.Z) * 0.5);
        }
        minY = Math.Min(minY, 0);
        return (new Vec3((float)minX, (float)minY, (float)minZ), new Vec3((float)maxX, (float)maxY, (float)maxZ));
    }

    private static Color ThumbnailColor(LevelNode n)
    {
        if (MaterialInfo.IsColorable(n.Material)) return ToColor(n.Color1);
        return n.Material switch
        {
            0 => Color.FromRgb(172, 176, 184),
            1 => Color.FromRgb(206, 164, 96),
            2 => Color.FromRgb(116, 193, 228),
            3 => Color.FromRgb(218, 91, 40),
            4 => Color.FromRgb(154, 106, 61),
            5 => Color.FromRgb(83, 139, 183),
            6 => Color.FromRgb(185, 90, 48),
            7 => Color.FromRgb(164, 120, 85),
            8 => ToColor(n.Color1),
            9 => Color.FromRgb(205, 111, 164),
            10 => Color.FromRgb(219, 231, 239),
            11 => Color.FromRgb(105, 173, 145),
            _ => Color.FromRgb(160, 165, 174)
        };
    }

    private static Color ToColor(Col c) => Color.FromArgb(
        (byte)Math.Clamp(c.Ai, 0, 255), (byte)Math.Clamp(c.Ri, 0, 255),
        (byte)Math.Clamp(c.Gi, 0, 255), (byte)Math.Clamp(c.Bi, 0, 255));

    private static Color Lighten(Color c, double factor) => Color.FromArgb(c.A,
        (byte)Math.Clamp(c.R * factor, 0, 255),
        (byte)Math.Clamp(c.G * factor, 0, 255),
        (byte)Math.Clamp(c.B * factor, 0, 255));
}
