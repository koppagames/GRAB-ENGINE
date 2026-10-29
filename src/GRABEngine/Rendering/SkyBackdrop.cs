using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

/// <summary>
/// Paints the GRAB sky behind the 3D viewport: the vertical zenith/horizon
/// gradient, the sun disc with its corona, the star field and a deck of drifting
/// cumulus clouds. Everything is projected from the camera basis, so the sky
/// pans and tilts with the view exactly like a real skybox, but it is drawn with
/// 2D compositing (WPF's 3D pipeline cannot alpha-blend overlapping sky quads).
/// </summary>
public sealed class SkyBackdrop : FrameworkElement
{
    private AmbienceSettings? _ambience;
    private PerspectiveCamera? _camera;
    private double _time;

    private readonly List<Star> _stars = new();
    private readonly List<Puff> _puffs = new();
    private string _seedKey = "";
    private int _starSeed = int.MinValue;

    private BitmapSource? _starSprite;
    private readonly Dictionary<string, BitmapSource> _cloudSprites = new();

    private const double Deg = Math.PI / 180.0;

    private readonly struct Star
    {
        public readonly Vec3 Direction;
        public readonly double Size;
        public readonly double Brightness;
        public Star(Vec3 dir, double size, double brightness) { Direction = dir; Size = size; Brightness = brightness; }
    }

    private readonly struct Puff
    {
        public readonly double Azimuth;
        public readonly double Elevation;
        public readonly double HalfAngle;
        public readonly int Variant;
        public readonly double Opacity;
        public Puff(double az, double el, double halfAngle, int variant, double opacity)
        {
            Azimuth = az; Elevation = el; HalfAngle = halfAngle; Variant = variant; Opacity = opacity;
        }
    }

    public int CloudPuffCount => _puffs.Count;
    public int StarCount => _stars.Count;

    public void Update(AmbienceSettings? ambience, PerspectiveCamera? camera)
    {
        _ambience = ambience;
        _camera = camera;
        var a = ambience ?? new AmbienceSettings();
        EnsureStars(a);
        EnsureClouds(a);
        InvalidateVisual();
    }

    public void Advance(double dt)
    {
        _time += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        var a = _ambience ?? new AmbienceSettings();
        var view = BuildView(w, h, a);

        DrawGradient(dc, a, view, w, h);

        if (_stars.Count > 0) DrawStars(dc, a, view);

        DrawSun(dc, a, view);

        if (_puffs.Count > 0) DrawClouds(dc, a, view);
    }

    // ------------------------------------------------------------- camera basis

    private readonly struct View
    {
        public readonly Vec3 Forward, Right, Up;
        public readonly double TanHalf, Aspect, W, H;
        public View(Vec3 f, Vec3 r, Vec3 u, double tanHalf, double aspect, double w, double h)
        { Forward = f; Right = r; Up = u; TanHalf = tanHalf; Aspect = aspect; W = w; H = h; }

        public bool Project(Vec3 dir, out Point point, out double depth)
        {
            depth = dir.X * Forward.X + dir.Y * Forward.Y + dir.Z * Forward.Z;
            if (depth <= 0.02) { point = default; return false; }
            double x = dir.X * Right.X + dir.Y * Right.Y + dir.Z * Right.Z;
            double y = dir.X * Up.X + dir.Y * Up.Y + dir.Z * Up.Z;
            double nx = x / depth / (TanHalf * Aspect);
            double ny = y / depth / TanHalf;
            point = new Point(W * 0.5 + nx * W * 0.5, H * 0.5 - ny * H * 0.5);
            return true;
        }

        public bool ProjectDirection(Vec3 dir, out Point point)
        {
            if (Project(dir, out point, out _)) return true;
            point = default;
            return false;
        }
    }

    private View BuildView(double w, double h, AmbienceSettings a)
    {
        Vec3 forward = new(0, 0, -1);
        Vec3 upHint = new(0, 1, 0);
        double fov = 55;
        if (_camera != null)
        {
            var look = _camera.LookDirection;
            if (look.LengthSquared > 1e-9)
            {
                var l = new Vec3((float)look.X, (float)look.Y, (float)look.Z);
                forward = l.Normalized;
            }
            upHint = new Vec3((float)_camera.UpDirection.X, (float)_camera.UpDirection.Y, (float)_camera.UpDirection.Z);
            fov = Math.Clamp(_camera.FieldOfView, 1, 170);
        }
        var right = Vec3Cross(forward, upHint);
        if (right.Length < 1e-5) right = new Vec3(1, 0, 0);
        right = right.Normalized;
        var up = Vec3Cross(right, forward).Normalized;
        double tan = Math.Tan(fov * Deg * 0.5);
        return new View(forward, right, up, tan, w / h, w, h);
    }

    // ------------------------------------------------------------- gradient

    private void DrawGradient(DrawingContext dc, AmbienceSettings a, View view, double w, double h)
    {
        var horizon = ToColor(a.SkyHorizonColor);
        var zenith = ToColor(a.SkyZenithColor);
        var below = Color.FromRgb(
            (byte)Math.Clamp(horizon.R * 0.34, 0, 255),
            (byte)Math.Clamp(horizon.G * 0.37, 0, 255),
            (byte)Math.Clamp(horizon.B * 0.44, 0, 255));

        // 72 bands is plenty for a smooth gradient and keeps the cost tiny.
        int bands = 72;
        double maxElev = Math.Asin(Math.Clamp(view.Forward.Y, -1, 1)) + Math.Atan(view.TanHalf);
        double minElev = Math.Asin(Math.Clamp(view.Forward.Y, -1, 1)) - Math.Atan(view.TanHalf);
        for (int i = 0; i < bands; i++)
        {
            double y0 = i * h / bands, y1 = (i + 1) * h / bands;
            double sy = 1 - 2 * ((i + 0.5) / bands);
            double elevation = Math.Asin(Math.Clamp(view.Forward.Y + (view.Up.Y) * (sy * view.TanHalf), -1, 1));
            // Use the actual up vector to stay consistent with the camera basis.
            var dir = new Vec3(
                (float)(view.Forward.X + view.Up.X * (sy * view.TanHalf)),
                (float)(view.Forward.Y + view.Up.Y * (sy * view.TanHalf)),
                (float)(view.Forward.Z + view.Up.Z * (sy * view.TanHalf))).Normalized;
            elevation = Math.Asin(Math.Clamp(dir.Y, -1, 1));

            Color c;
            if (elevation >= 0)
            {
                double t = Math.Pow(Math.Clamp(elevation / (Math.PI * 0.5), 0, 1), 0.55);
                c = Lerp(horizon, zenith, t);
            }
            else
            {
                c = Lerp(horizon, below, Math.Clamp(-elevation * 3.2, 0, 1));
            }
            double fog = a.FogDensity * Math.Exp(-Math.Abs(elevation) * 9.0) * 0.85;
            c = Lerp(c, horizon, Math.Clamp(fog, 0, 1));

            var brush = new SolidColorBrush(c);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(0, y0 - 0.6, w, y1 - y0 + 1.2));
        }

        // Warm the sky around the sun the way GRAB's atmosphere does at low sun angles.
        if (a.SunAltitude > -12)
        {
            var sunDir = SunDirection(a);
            if (view.ProjectDirection(sunDir, out var sp))
            {
                double radius = h * (0.55 + Math.Clamp(a.SunSize, 0, 4) * 0.18);
                var glow = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.5),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                var warm = Scale(ToColor(a.SunColor), 0.5 + Math.Clamp(a.SunBrightness, 0, 3) * 0.5);
                double low = Math.Clamp(1 - a.SunAltitude / 30.0, 0, 1);
                double strength = (0.16 + low * 0.30) * Math.Clamp(a.SunBrightness, 0, 3);
                glow.GradientStops.Add(new GradientStop(WithAlpha(Scale(warm, 0.9), Math.Clamp(strength, 0, 0.6)), 0));
                glow.GradientStops.Add(new GradientStop(WithAlpha(warm, Math.Clamp(strength * 0.5, 0, 0.4)), 0.35));
                glow.GradientStops.Add(new GradientStop(WithAlpha(warm, 0), 1));
                glow.Freeze();
                dc.DrawRectangle(glow, null, new Rect(sp.X - radius, sp.Y - radius, radius * 2, radius * 2));
            }
        }
    }

    // ------------------------------------------------------------- sun

    private static Vec3 SunDirection(AmbienceSettings a)
    {
        double alt = a.SunAltitude * Deg, az = a.SunAzimuth * Deg;
        return new Vec3(
            (float)(Math.Sin(az) * Math.Cos(alt)),
            (float)Math.Sin(alt),
            (float)(Math.Cos(az) * Math.Cos(alt))).Normalized;
    }

    private void DrawSun(DrawingContext dc, AmbienceSettings a, View view)
    {
        if (a.SunAltitude <= -3) return;
        var dir = SunDirection(a);
        if (!view.ProjectDirection(dir, out var p)) return;

        var core = Scale(ToColor(a.SunColor), 0.6 + Math.Clamp(a.SunBrightness, 0, 3) * 0.6);
        double angular = Math.Clamp(0.9 * a.SunSize, 0.1, 14) * Deg;
        double disc = Math.Tan(angular) / view.TanHalf * view.H * 0.5;
        disc = Math.Clamp(disc, 1.2, view.H * 0.35);

        var halo = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        halo.GradientStops.Add(new GradientStop(WithAlpha(core, 0.95), 0));
        halo.GradientStops.Add(new GradientStop(WithAlpha(core, 0.55), 1));
        halo.GradientStops.Add(new GradientStop(WithAlpha(core, 0), 1));
        halo.Freeze();

        double r = disc * 9;
        dc.DrawEllipse(halo, null, p, r, r);

        var body = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.42, 0.38),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        body.GradientStops.Add(new GradientStop(WithAlpha(Mix(core, Colors.White, 0.55), 1), 0));
        body.GradientStops.Add(new GradientStop(WithAlpha(core, 1), 0.72));
        body.GradientStops.Add(new GradientStop(WithAlpha(Scale(core, 0.92), 1), 1));
        body.Freeze();
        dc.DrawEllipse(body, null, p, disc, disc);
    }

    // ------------------------------------------------------------- stars

    private void EnsureStars(AmbienceSettings a)
    {
        int seed = HashSeed(a.StarDensity, a.StarClustering, a.StarSize, a.StarVisibility);
        foreach (var v in new[] { a.StarRotationEuler.X, a.StarRotationEuler.Y, a.StarRotationEuler.Z })
            seed = seed * 31 + (int)(v * 10);
        if (seed == _starSeed) return;
        _starSeed = seed;
        _stars.Clear();

        double density = Math.Clamp(a.StarDensity, 0, 2);
        double visibility = Math.Clamp(a.StarVisibility, 0, 2);
        if (density <= 0.01 || visibility <= 0.01) return;

        int count = (int)Math.Clamp(520 * Math.Min(density, 1.5) * Math.Min(visibility, 1.5), 0, 1400);
        var rotation = Quat.FromEulerDegrees(a.StarRotationEuler);
        var rnd = new Random(918273);
        for (int i = 0; i < count; i++)
        {
            double y = Math.Sqrt(1 - rnd.NextDouble() * 0.99);
            double theta = rnd.NextDouble() * Math.PI * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            var dir = rotation.Rotate(new Vec3((float)(r * Math.Cos(theta)), (float)y, (float)(r * Math.Sin(theta))));
            if (dir.Y < 0.01f) continue;
            double size = (1.4 + rnd.NextDouble() * 2.6) * Math.Clamp(a.StarSize, 0.1, 8);
            double brightness = 0.35 + rnd.NextDouble() * 0.65;
            _stars.Add(new Star(dir, size, brightness));

            // StarClustering pulls a couple of faint companions around the bright star.
            if (a.StarClustering > 0.01 && rnd.NextDouble() < Math.Clamp(a.StarClustering, 0, 1) * 0.6)
            {
                int extra = 2 + rnd.Next(4);
                for (int k = 0; k < extra; k++)
                {
                    var off = new Vec3(
                        (float)((rnd.NextDouble() - 0.5) * 0.05),
                        (float)((rnd.NextDouble() - 0.5) * 0.05),
                        (float)((rnd.NextDouble() - 0.5) * 0.05));
                    var d2 = (dir + off).Normalized;
                    if (d2.Y < 0.01f) continue;
                    _stars.Add(new Star(d2, (0.7 + rnd.NextDouble() * 1.2) * Math.Clamp(a.StarSize, 0.1, 8), 0.15 + rnd.NextDouble() * 0.35));
                }
            }
        }
    }

    private void DrawStars(DrawingContext dc, AmbienceSettings a, View view)
    {
        var sprite = _starSprite ??= StarSprite();
        double fog = Math.Clamp(a.FogDensity, 0, 1);
        foreach (var s in _stars)
        {
            if (!view.Project(s.Direction, out var p, out _)) continue;
            if (p.X < -20 || p.Y < -20 || p.X > view.W + 20 || p.Y > view.H + 20) continue;
            // Stars wash out near the horizon, exactly where GRAB's sky fog is thickest.
            double horizon = Math.Clamp(s.Direction.Y * 6.0, 0, 1);
            double fade = (1 - fog * 0.85) * horizon;
            if (fade <= 0.01) continue;
            double size = s.Size;
            var rect = new Rect(p.X - size, p.Y - size, size * 2, size * 2);
            dc.PushOpacity(Math.Clamp(s.Brightness * fade, 0, 1));
            dc.DrawImage(sprite, rect);
            dc.Pop();
        }
    }

    private static BitmapSource StarSprite()
    {
        const int S = 32;
        var px = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            double nx = (x + 0.5) / S * 2 - 1, ny = (y + 0.5) / S * 2 - 1;
            double d = Math.Sqrt(nx * nx + ny * ny);
            double a = Math.Clamp(1 - d, 0, 1);
            a = Math.Pow(a, 1.7);
            byte aa = (byte)(a * 255);
            int i = (y * S + x) * 4;
            px[i] = aa;
            px[i + 1] = aa;
            px[i + 2] = aa;
            px[i + 3] = aa;
        }
        var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Pbgra32, null, px, S * 4);
        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------- clouds

    private void EnsureClouds(AmbienceSettings a)
    {
        double density = Math.Clamp(a.CloudDensity, 0, 1);
        string key = $"{density:0.##}|{a.CloudHeight:0.#}|{a.CloudShading:0.##}|{a.CloudColor.Hex}";
        if (key == _seedKey) return;
        _seedKey = key;
        _puffs.Clear();
        if (density <= 0.01) return;

        // A cloud deck: clusters of puffs at slightly different bearings and
        // heights, like the billboarded cumulus GRAB places around the skybox.
        int clusters = (int)Math.Round(22 + 128 * density);
        var rnd = new Random(20240607);
        double heightBias = Math.Clamp(a.CloudHeight / 400.0, 0, 1);
        for (int c = 0; c < clusters; c++)
        {
            double azimuth = rnd.NextDouble() * Math.PI * 2;
            double elevation = Lerp(3, 36, Math.Pow(rnd.NextDouble(), 0.78)) + heightBias * 12;
            elevation = Math.Min(elevation, 70);
            int puffs = 3 + rnd.Next(4);
            double clusterSize = 1.6 + rnd.NextDouble() * 2.9;
            for (int p = 0; p < puffs; p++)
            {
                double az = azimuth + (rnd.NextDouble() - 0.5) * clusterSize * 2.6 * Deg;
                double el = elevation + (rnd.NextDouble() - 0.5) * clusterSize * 1.5 * Deg;
                el = Math.Clamp(el, 1.5, 84);
                double half = (clusterSize * (0.55 + rnd.NextDouble() * 0.75)) * Deg;
                double opacity = 0.55 + rnd.NextDouble() * 0.45;
                _puffs.Add(new Puff(az, el, half, p % 4, opacity));
            }
        }
        // Draw the higher/farther puffs first so the deck overlaps naturally.
        _puffs.Sort((x, y) => y.Elevation.CompareTo(x.Elevation));
    }

    private void DrawClouds(DrawingContext dc, AmbienceSettings a, View view)
    {
        double speedX = a.CloudSpeedX * 0.55 * Deg;
        double speedY = a.CloudSpeedZ * 0.10 * Deg;
        var sprites = CloudSprites(a);
        foreach (var puff in _puffs)
        {
            double az = puff.Azimuth + _time * speedX;
            double el = Math.Clamp(puff.Elevation + _time * speedY, 0.5, 88) * Deg;
            var dir = new Vec3(
                (float)(Math.Sin(az) * Math.Cos(el)),
                (float)Math.Sin(el),
                (float)(Math.Cos(az) * Math.Cos(el)));
            if (!view.ProjectDirection(dir, out var p)) continue;

            double size = Math.Tan(puff.HalfAngle) / view.TanHalf * view.H;
            if (size < 2) continue;
            if (p.X + size < 0 || p.Y + size < 0 || p.X - size > view.W || p.Y - size > view.H) continue;

            // Fade the deck into the horizon haze.
            double haze = Math.Clamp((puff.Elevation - 1) / 14.0, 0.18, 1);
            double opacity = puff.Opacity * haze * (1 - Math.Clamp(a.FogDensity, 0, 1) * 0.35);
            if (opacity <= 0.02) continue;

            // Puffs are drawn slightly wider than tall, which reads as a cumulus
            // deck instead of a row of cotton balls.
            double pw = size * 1.18, ph = size * 0.92;
            var sprite = sprites[puff.Variant];
            dc.PushOpacity(Math.Clamp(opacity, 0, 1));
            dc.DrawImage(sprite, new Rect(p.X - pw * 0.5, p.Y - ph * 0.5, pw, ph));
            dc.Pop();
        }
    }

    private BitmapSource[] CloudSprites(AmbienceSettings a)
    {
        var result = new BitmapSource[4];
        for (int i = 0; i < result.Length; i++) result[i] = CloudSprite(a, i);
        return result;
    }

    private BitmapSource CloudSprite(AmbienceSettings a, int variant)
    {
        string key = $"{a.CloudColor.Hex}|{a.CloudShading:0.##}|{variant}";
        if (_cloudSprites.TryGetValue(key, out var cached)) return cached;
        if (_cloudSprites.Count > 32) _cloudSprites.Clear();

        const int S = 192;
        var pixels = new byte[S * S * 4];
        var tint = ToColor(a.CloudColor);
        double shading = Math.Clamp(a.CloudShading, 0, 2);
        double seed = variant * 21.7 + 5.3;
        for (int y = 0; y < S; y++)
        {
            double ny = (y + 0.5) / S * 2 - 1;
            for (int x = 0; x < S; x++)
            {
                double nx = (x + 0.5) / S * 2 - 1;
                double d = Math.Sqrt(nx * nx * 0.94 + ny * ny * 1.16);
                double n = ProceduralNoise.Fbm2(nx * 2.6 + seed, ny * 2.6 - seed, 5);
                double edge = ProceduralNoise.Fbm2(nx * 5.4 + 3.7, ny * 5.4 + 8.4, 4);
                d += (edge - 0.5) * 0.42;
                if (d >= 1.05) continue;
                double shape = Math.Clamp((1.02 - d) / 0.22, 0, 1);
                shape = shape * shape * (3 - 2 * shape);
                double body = 0.55 + 0.55 * n;
                double alpha = shape * body;
                alpha = Math.Clamp(alpha, 0, 1);
                if (alpha <= 0.004) continue;

                // Lit top, shaded underside, plus a soft internal gradient.
                double v = (ny + 1) * 0.5;
                double light = 1.06 - v * 0.30 * shading + (n - 0.5) * 0.18;
                light = Math.Clamp(light, 0.35, 1.35);
                byte r = (byte)Math.Clamp(tint.R * light, 0, 255);
                byte g = (byte)Math.Clamp(tint.G * light, 0, 255);
                byte b = (byte)Math.Clamp(tint.B * light, 0, 255);
                byte aa = (byte)(alpha * 255);
                int i = (y * S + x) * 4;
                pixels[i] = (byte)(b * aa / 255);
                pixels[i + 1] = (byte)(g * aa / 255);
                pixels[i + 2] = (byte)(r * aa / 255);
                pixels[i + 3] = aa;
            }
        }
        var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Pbgra32, null, pixels, S * 4);
        bmp.Freeze();
        _cloudSprites[key] = bmp;
        return bmp;
    }

    // ------------------------------------------------------------- helpers

    private static int HashSeed(params float[] values)
    {
        int h = 17;
        foreach (var v in values) h = unchecked(h * 31 + (int)(v * 100));
        return h;
    }

    private static Vec3 Vec3Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Color ToColor(Col c) => Color.FromRgb(
        (byte)Math.Clamp(c.Ri, 0, 255),
        (byte)Math.Clamp(c.Gi, 0, 255),
        (byte)Math.Clamp(c.Bi, 0, 255));

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb(
        (byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    private static Color Scale(Color c, double k) => Color.FromRgb(
        (byte)Math.Clamp(c.R * k, 0, 255),
        (byte)Math.Clamp(c.G * k, 0, 255),
        (byte)Math.Clamp(c.B * k, 0, 255));

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
