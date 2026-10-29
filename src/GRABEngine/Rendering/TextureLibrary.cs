using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrabEngine.Core;

namespace GrabEngine.Rendering;

public static class TextureLibrary
{
    private static readonly Dictionary<string, BitmapSource> Raw = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageBrush> Tinted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapSource> TextCache = new();

    public static string AssetRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string TexturePath(string name) => Path.Combine(AssetRoot, "Textures", name + ".png");
    public static string ModelPath(string name) => Path.Combine(AssetRoot, "Models", name + ".sgm");

    public static BitmapSource RawTexture(string name)
    {
        if (Raw.TryGetValue(name, out var b)) return b;
        var path = TexturePath(name);
        if (!File.Exists(path)) path = TexturePath("default");
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path, UriKind.Absolute);
        img.EndInit();
        img.Freeze();
        Raw[name] = img;
        return img;
    }

    public static ImageBrush GetBrush(string texture, Col tint, bool opaque = false, double tile = 1)
    {
        string key = texture + "|" + tint.Hex + "|" + tint.Ai + "|" + (opaque ? "opaque" : "alpha") + "|" + tile.ToString("0.##", CultureInfo.InvariantCulture);
        if (Tinted.TryGetValue(key, out var cached)) return cached;
        var src = RawTexture(texture);
        // GRAB's block textures carry alpha masks meant for other shaders. Solid blocks
        // must ignore that alpha or the wood/lava blocks turn into see-through lattices.
        BitmapSource target = src;
        if (opaque || tint.Ri != 255 || tint.Gi != 255 || tint.Bi != 255)
            target = Tint(src, tint, opaque);
        var brush = new ImageBrush(target)
        {
            Stretch = Stretch.Fill,
            Opacity = Math.Clamp(tint.A, 0, 1),
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Math.Max(1, tile), Math.Max(1, tile)),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        brush.Freeze();
        Tinted[key] = brush;
        return brush;
    }

    private static BitmapSource Tint(BitmapSource src, Col tint, bool opaque)
    {
        // Bgra32 keeps the texture in straight alpha. Reading Pbgra32 and then forcing
        // alpha to 255 divides the colour by the source alpha twice: default_colored
        // (alpha 178) rendered at 70% brightness, which is why the floor and other
        // masked GRAB textures looked darker than the real game.
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);
        byte r = (byte)Math.Clamp(tint.Ri, 0, 255);
        byte g = (byte)Math.Clamp(tint.Gi, 0, 255);
        byte b = (byte)Math.Clamp(tint.Bi, 0, 255);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * b / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * g / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * r / 255);
            if (opaque) pixels[i + 3] = 255;
        }
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }

    public static BitmapSource SignText(string text, Col color, int weight)
    {
        text ??= "";
        string key = text + "|" + color.Hex + "|" + weight;
        if (TextCache.TryGetValue(key, out var c)) return c;
        int w = 640, h = 256;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                weight >= 3 ? FontWeights.Bold : weight == 1 ? FontWeights.Light : weight == 2 ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal);
            var brush = new SolidColorBrush(Color.FromRgb((byte)color.Ri, (byte)color.Gi, (byte)color.Bi));
            double size = 64;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, size, brush, 1.0);
            ft.MaxTextWidth = w - 40;
            ft.TextAlignment = TextAlignment.Center;
            if (ft.Height > h - 30)
            {
                size = Math.Max(10, size * (h - 30) / ft.Height);
                ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, brush, 1.0);
                ft.MaxTextWidth = w - 40;
                ft.TextAlignment = TextAlignment.Center;
            }
            dc.DrawText(ft, new Point(20, Math.Max(10, (h - ft.Height) / 2)));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        TextCache[key] = rtb;
        return rtb;
    }

    public static ImageBrush SignBrush(string text, Col color, int weight)
    {
        var brush = new ImageBrush(SignText(text, color, weight)) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    public static Brush Solid(Col c)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(c.Ai, 0, 255),
            (byte)Math.Clamp(c.Ri, 0, 255), (byte)Math.Clamp(c.Gi, 0, 255), (byte)Math.Clamp(c.Bi, 0, 255)));
        b.Freeze();
        return b;
    }
}
