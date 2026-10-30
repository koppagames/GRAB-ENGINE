using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GrabEngine.Core;

/// <summary>How the extruded glyph is filled in.</summary>
public enum TextFillMode
{
    Solid,
    Hollow,
    Outline
}

/// <summary>How the text stands in the world.</summary>
public enum TextPlane
{
    Standing,
    Lying
}

/// <summary>How hard the edge smoother works to replace stair-steps with angled blocks.</summary>
public enum TextSmoothness
{
    Tight = 0,
    Balanced = 1,
    VerySmooth = 2
}

/// <summary>Everything the Text → Blocks tool needs to turn a string into blocks.</summary>
public sealed class TextBlockSettings
{
    public string Text { get; set; } = "GRAB";
    public string FontFamily { get; set; } = "Segoe UI";
    public string FontFile { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>Vertical resolution: how many blocks tall the glyphs are.</summary>
    public int Height { get; set; } = 24;
    /// <summary>Supersampling per block. 1 = quick, 4 = crispest edges.</summary>
    public int Detail { get; set; } = 2;
    /// <summary>How many blocks deep the extrusion is.</summary>
    public int Depth { get; set; } = 2;
    /// <summary>Extra gap between letters, in blocks (may be negative).</summary>
    public int LetterSpacing { get; set; }
    /// <summary>Extra gap between lines, in blocks.</summary>
    public int LineSpacing { get; set; }

    public TextFillMode Fill { get; set; } = TextFillMode.Solid;
    public TextPlane Plane { get; set; } = TextPlane.Standing;

    public float BlockSize { get; set; } = 0.25f;
    public int Material { get; set; } = 8;
    public string Color { get; set; } = "#E8E8E8";
    public int Shape { get; set; } = ShapeInfo.Cube;

    public bool MergeRuns { get; set; } = true;
    /// <summary>Refines the block grid (2× or 3×) for finer edges.</summary>
    public bool SmoothEdges { get; set; } = true;
    public TextSmoothness Smoothing { get; set; } = TextSmoothness.Balanced;
    /// <summary>
    /// Experimental: lays rotated bars along diagonals. These smooth the silhouette but their
    /// square ends can notch or spike at tight corners, so they are off by default.
    /// </summary>
    public bool AngledEdges { get; set; }
    /// <summary>Edge bar thickness in base blocks.</summary>
    public float EdgeThickness { get; set; } = 1.5f;
    public bool Center { get; set; } = true;
    public int YawDegrees { get; set; }
    public Vec3 Origin { get; set; } = Vec3.Zero;
}

/// <summary>A front-view block footprint, in block cells, used by the live preview.</summary>
public readonly record struct TextPreviewShape(float X, float Y, float W, float H, float Angle);

/// <summary>The flat pixel mask the font was rasterized into (row 0 is the top).</summary>
public sealed class TextMask
{
    public bool[,] Cells { get; init; } = new bool[0, 0];
    public int Width => Cells.GetLength(1);
    public int Height => Cells.GetLength(0);
    public string FontName { get; init; } = "";
    public int DetailUsed { get; init; } = 1;
    /// <summary>Optional supersampled coverage used to trace sub-block-smooth outlines.</summary>
    public bool[]? Fine { get; init; }
    public int FineWidth { get; init; }
    public int FineHeight { get; init; }
    public int FineScale { get; init; } = 1;

    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Width && y < Height && Cells[y, x];
}

/// <summary>The blocks produced by the Text → Blocks tool, ready to add to a level.</summary>
public sealed class TextBlockPlan
{
    public List<LevelNode> Nodes { get; } = new();
    public string Summary { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0;
    public int Columns { get; set; }
    public int Rows { get; set; }
    public int Depth { get; set; }
    public int Voxels { get; set; }
    public int EdgeBars { get; set; }
    /// <summary>Block supersampling used for smoothing (1 = base resolution).</summary>
    public int SmoothScale { get; set; } = 1;
    public string FontName { get; set; } = "";
    public TextMask? Mask { get; set; }
    /// <summary>Exact front-view footprints, including the angled smoothing blocks.</summary>
    public List<TextPreviewShape> Shapes { get; } = new();
}

/// <summary>
/// Turns text into GRAB blocks. The glyph outlines are rasterized into a block grid
/// (supersampled for clean edges), then extruded into 3D and merged into long runs so a
/// headline is a few hundred scaled blocks instead of thousands of single cubes.
/// </summary>
public static class TextBlockBuilder
{
    /// <summary>Font families offered in the tool's dropdown.</summary>
    public static readonly string[] BuiltinFonts =
    {
        "Segoe UI", "Arial", "Arial Black", "Bahnschrift", "Calibri", "Cambria",
        "Candara", "Comic Sans MS", "Consolas", "Constantia", "Corbel", "Courier New",
        "Franklin Gothic Medium", "Georgia", "Impact", "Lucida Console",
        "Palatino Linotype", "Rockwell", "Segoe Print", "Segoe Script", "Tahoma",
        "Times New Roman", "Trebuchet MS", "Verdana"
    };

    // ------------------------------------------------------------------ font

    /// <summary>Resolves the selected font (system family or a .ttf/.otf file).</summary>
    public static bool TryGetGlyphTypeface(TextBlockSettings settings, out GlyphTypeface face,
        out string label, out string error)
    {
        face = null!;
        label = "";
        error = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.FontFile))
            {
                string path = Path.GetFullPath(settings.FontFile);
                if (!File.Exists(path))
                {
                    error = "Font file not found: " + path;
                    return false;
                }
                face = new GlyphTypeface(new Uri(path, UriKind.Absolute));
                label = face.FamilyNames.TryGetValue(CultureInfo.CurrentCulture, out string? name)
                    ? name
                    : face.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
                if (face.Style == FontStyles.Italic || face.Style == FontStyles.Oblique)
                    label += " Italic";
                else if (face.Weight == FontWeights.Bold || face.Weight == FontWeights.Black)
                    label += " Bold";
                return true;
            }

            var typeface = new Typeface(new FontFamily(settings.FontFamily),
                settings.Italic ? FontStyles.Italic : FontStyles.Normal,
                settings.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out face))
            {
                error = $"Font '{settings.FontFamily}' is not installed.";
                return false;
            }
            label = settings.FontFamily;
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not load the font: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ mask

    /// <summary>Rasterizes the text into a solid/outline block mask.</summary>
    public static bool TryBuildMask(TextBlockSettings settings, out TextMask mask, out string error)
    {
        mask = new TextMask();
        error = "";

        string text = (settings.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Type some text first.";
            return false;
        }

        if (!TryGetGlyphTypeface(settings, out GlyphTypeface face, out string fontName, out error))
            return false;

        try
        {
            int rows = Math.Clamp(settings.Height, 4, 288);
            int letterSpacing = Math.Clamp(settings.LetterSpacing, -8, 24);
            int lineSpacing = Math.Clamp(settings.LineSpacing, -8, 24);

            // First pass measures the text so letter/line spacing can be expressed in
            // blocks: one block is bounds.Height / rows of the unscaled layout.
            var probe = Layout(face, text, 128.0, 0, 0);
            if (probe.Group.Bounds.IsEmpty || probe.Group.Bounds.Height < 0.001 || probe.Group.Bounds.Width < 0.001)
            {
                error = "That text has no drawable glyphs in this font.";
                return false;
            }
            double emPerBlock = probe.Group.Bounds.Height / rows;
            double letterEm = letterSpacing * emPerBlock;
            double lineGapEm = lineSpacing * emPerBlock;

            var layout = letterSpacing == 0 && lineSpacing == 0
                ? probe
                : Layout(face, text, 128.0, letterEm, lineGapEm);
            Rect bounds = layout.Group.Bounds;
            if (bounds.IsEmpty || bounds.Height < 0.001 || bounds.Width < 0.001)
            {
                error = "That text has no drawable glyphs in this font.";
                return false;
            }

            int cols = (int)Math.Ceiling(bounds.Width / bounds.Height * rows);
            cols = Math.Clamp(cols, 1, 2048);
            if (cols > 1600 && rows > 160)
            {
                error = $"That is a lot of blocks ({cols} × {rows}). Shorten the text or lower the height.";
                return false;
            }

            int detail = Math.Clamp(settings.Detail, 1, 4);
            int pxW = cols * detail;
            int pxH = rows * detail;
            const long maxPixels = 7_000_000;
            while ((long)pxW * pxH > maxPixels && detail > 1)
            {
                detail--;
                pxW = cols * detail;
                pxH = rows * detail;
            }

            double scale = (double)pxH / bounds.Height;
            pxW = Math.Max(pxW, (int)Math.Ceiling(bounds.Width * scale));
            pxH = Math.Max(pxH, (int)Math.Ceiling(bounds.Height * scale));

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                dc.DrawGeometry(Brushes.White, null, layout.Group);
                dc.Pop();
                dc.Pop();
            }

            var bitmap = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[pxW * pxH * 4];
            bitmap.CopyPixels(pixels, pxW * 4, 0);

            var cells = new bool[rows, cols];
            int filled = 0;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    long sum = 0;
                    int count = 0;
                    for (int sy = 0; sy < detail; sy++)
                    {
                        int py = y * detail + sy;
                        if (py >= pxH) break;
                        for (int sx = 0; sx < detail; sx++)
                        {
                            int px = x * detail + sx;
                            if (px >= pxW) break;
                            sum += pixels[(py * pxW + px) * 4 + 3];
                            count++;
                        }
                    }
                    // 40% coverage keeps thin diagonal strokes without fattening letters.
                    bool on = count > 0 && sum >= count * 255.0 * 0.40;
                    cells[y, x] = on;
                    if (on) filled++;
                }
            }

            if (filled == 0)
            {
                error = "The text rasterized to nothing — try a bigger height or another font.";
                return false;
            }

            if (settings.Fill == TextFillMode.Outline)
            {
                var edge = new bool[rows, cols];
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        if (!cells[y, x]) continue;
                        bool border = x == 0 || y == 0 || x == cols - 1 || y == rows - 1 ||
                                      !cells[y, x - 1] || !cells[y, x + 1] ||
                                      !cells[y - 1, x] || !cells[y + 1, x];
                        edge[y, x] = border;
                    }
                }
                cells = edge;
            }

            // Keep the supersampled coverage when it is small enough to trace: it lets the
            // smoother follow true glyph curves instead of block-cell staircases.
            bool[]? fine = null;
            if (settings.Fill != TextFillMode.Outline && (long)pxW * pxH <= 200_000)
            {
                fine = new bool[pxW * pxH];
                for (int i = 0; i < fine.Length; i++)
                    fine[i] = pixels[i * 4 + 3] >= 108;   // ~42% coverage, same rule as the cells
            }

            mask = new TextMask
            {
                Cells = cells,
                FontName = fontName,
                DetailUsed = detail,
                Fine = fine,
                FineWidth = fine == null ? 0 : pxW,
                FineHeight = fine == null ? 0 : pxH,
                FineScale = detail
            };
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not build the text: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ blocks

    /// <summary>Rasterizes and extrudes the text into ready-to-add blocks.</summary>
    public static TextBlockPlan BuildPlan(TextBlockSettings settings)
    {
        var plan = new TextBlockPlan();

        // Smoothing raises the block resolution first (2× or 3×), which makes the outline
        // genuinely finer instead of only covering coarse steps with angled bars. The world
        // size stays identical because each block is that much smaller.
        int baseHeight = Math.Clamp(settings.Height, 4, 96);
        int wanted = settings.SmoothEdges ? Math.Clamp((int)settings.Smoothing + 1, 1, 3) : 1;
        wanted = Math.Min(wanted, Math.Max(1, 288 / Math.Max(1, baseHeight)));

        TextMask? mask = null;
        string error = "";
        int usedScale = 1;
        for (int attempt = wanted; attempt >= 1; attempt--)
        {
            if (!TryBuildMask(attempt == 1 ? settings : ScaledSettings(settings, attempt),
                    out TextMask built, out error))
            {
                // Only the block-count guard is worth retrying coarser.
                if (!error.Contains("lot of blocks", StringComparison.Ordinal)) break;
                continue;
            }
            mask = built;
            usedScale = attempt;
            break;
        }
        if (mask == null)
        {
            plan.Error = error;
            return plan;
        }

        plan.Mask = mask;
        plan.FontName = mask.FontName;
        plan.Columns = mask.Width;
        plan.Rows = mask.Height;
        plan.SmoothScale = usedScale;

        float size = Math.Clamp(settings.BlockSize, 0.02f, 4f) / usedScale;
        int depth = Math.Clamp(settings.Depth * usedScale, 1, 96);
        plan.Depth = depth;
        bool[,] filled = mask.Cells;
        int cols = mask.Width, rows = mask.Height;

        bool Border(int x, int y) => x == 0 || y == 0 || x == cols - 1 || y == rows - 1 ||
                                     !filled[y, x - 1] || !filled[y, x + 1] ||
                                     !filled[y - 1, x] || !filled[y + 1, x];

        // 3D occupancy: [row, column, depth slice].
        var solid = new bool[rows, cols, depth];
        int voxels = 0;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (!filled[y, x]) continue;
                for (int z = 0; z < depth; z++)
                {
                    bool on = settings.Fill switch
                    {
                        TextFillMode.Outline => true,
                        TextFillMode.Hollow => z == 0 || z == depth - 1 || Border(x, y),
                        _ => true
                    };
                    if (!on) continue;
                    solid[y, x, z] = true;
                    voxels++;
                }
            }
        }

        if (voxels == 0)
        {
            plan.Error = "The outline mode erased every block — the text is too thin at this height.";
            return plan;
        }

        float width = cols * size;
        float height = rows * size;
        float deep = depth * size;
        float offX = settings.Center ? width * 0.5f : 0f;
        float offZ = settings.Center ? deep * 0.5f : 0f;
        // Text always sits on the build point: standing letters rise from it and lying
        // letters lie on it, so the origin is the ground contact instead of the middle.
        float offFlat = settings.Center ? height * 0.5f : 0f;
        bool lying = settings.Plane == TextPlane.Lying;
        var yaw = Quat.FromAxisAngle(new Vec3(0, 1, 0), settings.YawDegrees);
        var blockRotation = Quat.FromEulerDegrees(new Vec3(0, settings.YawDegrees, 0));
        var color = MaterialInfo.IsColorable(settings.Material)
            ? Col.FromHex(settings.Color, new Col(0.91f, 0.91f, 0.91f, 1f))
            : Col.White;
        int shape = ShapeInfo.All.Any(s => s.Id == settings.Shape) ? settings.Shape : ShapeInfo.Cube;
        var origin = settings.Origin;

        void Add(Vec3 local, Quat rotation, Vec3 scale)
        {
            plan.Nodes.Add(new LevelNode
            {
                Kind = NodeKind.Static,
                Shape = shape,
                Position = yaw.Rotate(local) + origin,
                Rotation = rotation,
                Scale = new Vec3(MathF.Max(0.01f, scale.X), MathF.Max(0.01f, scale.Y), MathF.Max(0.01f, scale.Z)),
                Material = settings.Material,
                Color1 = color,
                EditorLabel = "text"
            });
        }

        void EmitFill(int x, int y, int w, int h, int z0, int zRun)
        {
            float lx = (x + w * 0.5f) * size - offX;
            float vy = (rows - y - h * 0.5f) * size;
            float zCentre = (z0 + zRun * 0.5f) * size;
            // Merged boxes meet exactly, and WPF's anti-aliasing then leaves a hairline of
            // background along every shared face. A tiny bleed closes those cracks.
            float bleed = size * 0.04f;
            // Sub-millimetre depth stagger keeps the overlapping front faces from z-fighting.
            int hash = (x * 73856093) ^ (y * 19349663) ^ (z0 * 83492791);
            float stagger = (hash & 0x3F) * size * 0.00004f;
            var local = lying
                ? new Vec3(lx, zCentre + stagger, vy - offFlat)
                : new Vec3(lx, vy, zCentre - offZ - stagger);
            var scale = lying
                ? new Vec3(w * size + bleed, zRun * size + bleed, h * size + bleed)
                : new Vec3(w * size + bleed, h * size + bleed, zRun * size + bleed);
            Add(local, blockRotation, scale);
            plan.Shapes.Add(new TextPreviewShape(x + w * 0.5f, y + h * 0.5f, w, h, 0));
        }

        if (settings.Fill == TextFillMode.Solid)
        {
            // Every depth slice is identical, so decompose once and extrude the full depth.
            // This keeps max-height/max-smoothing text fast instead of scanning every slice.
            var rects = Decompose(filled, cols, rows);
            if (!settings.MergeRuns)
            {
                foreach (var r in rects)
                    for (int yy = 0; yy < r.H; yy++)
                        for (int xx = 0; xx < r.W; xx++)
                            for (int z = 0; z < depth; z++)
                                EmitFill(r.X + xx, r.Y + yy, 1, 1, z, 1);
            }
            else
            {
                foreach (var r in rects) EmitFill(r.X, r.Y, r.W, r.H, 0, depth);
            }
        }
        else
        {
            EmitLayeredFill();
        }

        // Hollow and outline fills differ per depth slice.
        void EmitLayeredFill()
        {
        // Fill each depth slice with the largest boxes that fit, so both horizontal and
        // vertical stems become one long block instead of a row of cubes.
        var sliceRects = new List<List<(int X, int Y, int W, int H)>>(depth);
        for (int z = 0; z < depth; z++)
        {
            var layer = new bool[rows, cols];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    layer[y, x] = solid[y, x, z];
            sliceRects.Add(Decompose(layer, cols, rows));
        }

        if (!settings.MergeRuns)
        {
            for (int z = 0; z < depth; z++)
                foreach (var r in sliceRects[z])
                    for (int yy = 0; yy < r.H; yy++)
                        for (int xx = 0; xx < r.W; xx++)
                            EmitFill(r.X + xx, r.Y + yy, 1, 1, z, 1);
        }
        else
        {
            // Identical rectangles are merged through the depth, so solid glyphs become one
            // deep box rather than one box per slice.
            var byRect = new Dictionary<(int X, int Y, int W, int H), List<int>>();
            for (int z = 0; z < depth; z++)
            {
                foreach (var r in sliceRects[z])
                {
                    var key = (r.X, r.Y, r.W, r.H);
                    if (!byRect.TryGetValue(key, out var zs))
                    {
                        zs = new List<int>();
                        byRect[key] = zs;
                    }
                    zs.Add(z);
                }
            }

            foreach (var pair in byRect)
            {
                var zs = pair.Value;
                zs.Sort();
                int startZ = zs[0];
                int prevZ = zs[0];
                for (int i = 1; i <= zs.Count; i++)
                {
                    bool stop = i == zs.Count || zs[i] != prevZ + 1;
                    if (!stop) { prevZ = zs[i]; continue; }
                    EmitFill(pair.Key.X, pair.Key.Y, pair.Key.W, pair.Key.H, startZ, prevZ - startZ + 1);
                    if (i < zs.Count) { startZ = zs[i]; prevZ = zs[i]; }
                }
            }
        }
        }

        // Optional angled edge bars: trace the glyph outline, simplify the stair-step walk
        // into long straight chords, and lay a rotated bar along each one.
        if (settings.SmoothEdges && settings.AngledEdges)
        {
            double epsilon = settings.Smoothing switch
            {
                TextSmoothness.Tight => 0.7,
                TextSmoothness.VerySmooth => 1.55,
                _ => 1.05
            };
            float thickness = Math.Clamp(settings.EdgeThickness, 0.6f, 3f) * usedScale;
            float depthCentre = deep * 0.5f;

            // Trace the supersampled coverage when available; that puts the chords on the real
            // glyph curve rather than on block-cell corners.
            bool useFine = mask.Fine != null && mask.FineWidth > 1 && mask.FineHeight > 1;
            bool[] smoothGrid = useFine ? mask.Fine! : Flatten(filled, cols, rows);
            int smoothW = useFine ? mask.FineWidth : cols;
            int smoothH = useFine ? mask.FineHeight : rows;
            double fineScale = useFine ? Math.Max(1, mask.FineScale) : 1;

            foreach (var loop in TraceContours(smoothGrid, smoothW, smoothH))
            {
                var kept = SimplifyIndices(loop, epsilon * fineScale);
                for (int i = 0; i + 1 < kept.Count; i++)
                {
                    int ia = kept[i], ib = kept[i + 1];
                    double fx0 = loop[ia].X, fy0 = loop[ia].Y;
                    double fx1 = loop[ib].X, fy1 = loop[ib].Y;
                    double dx = fx1 - fx0, dy = fy1 - fy0;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1.0 * fineScale) continue;

                    // Centre the bar on the chord. At 2×/3× the block staircase is small enough
                    // that this width spans it without fattening the letter around corners.
                    float barThickness = MathF.Min(thickness, 4.5f);
                    double cx = (fx0 + fx1) * 0.5 / fineScale;
                    double cy = (fy0 + fy1) * 0.5 / fineScale;
                    double lenCells = len / fineScale;

                    float angleUp = (float)(Math.Atan2(-dy, dx) * 180.0 / Math.PI);
                    float angleFlat = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                    var planeRotation = lying
                        ? Quat.FromAxisAngle(new Vec3(0, 1, 0), angleFlat)
                        : Quat.FromAxisAngle(new Vec3(0, 0, 1), angleUp);

                    float lx = (float)cx * size - offX;
                    float vy = (float)(rows - cy) * size;
                    // A 2% depth offset keeps overlapping bar/fill faces from z-fighting.
                    var local = lying
                        ? new Vec3(lx, depthCentre + size * 0.02f, vy - offFlat)
                        : new Vec3(lx, vy, depthCentre - offZ - size * 0.02f);
                    // Overlap onto the flat edge at each end so the corner where an angled bar
                    // meets a skipped axis-aligned run is filled, not notched.
                    float length = (float)lenCells + barThickness * 0.34f;
                    var scale = lying
                        ? new Vec3(length * size, deep, barThickness * size)
                        : new Vec3(length * size, barThickness * size, deep);

                    Add(local, blockRotation * planeRotation, scale);
                    plan.Shapes.Add(new TextPreviewShape((float)cx, (float)cy, length, (float)barThickness,
                        (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI)));
                    plan.EdgeBars++;
                }
            }
        }

        // Angled bars near the baseline can dip a fraction of a block below it. Lift the whole
        // word by exactly that amount so standing text still rests on its build point.
        if (!lying)
        {
            float lowest = float.MaxValue;
            foreach (var node in plan.Nodes)
            {
                Vec3 ax = node.Rotation.Rotate(new Vec3(1, 0, 0));
                Vec3 ay = node.Rotation.Rotate(new Vec3(0, 1, 0));
                Vec3 az = node.Rotation.Rotate(new Vec3(0, 0, 1));
                float half = (MathF.Abs(ax.Y) * node.Scale.X + MathF.Abs(ay.Y) * node.Scale.Y +
                              MathF.Abs(az.Y) * node.Scale.Z) * 0.5f;
                lowest = MathF.Min(lowest, node.Position.Y - half);
            }
            if (lowest < 0f && lowest > -size * 4f)
            {
                var lift = new Vec3(0, -lowest, 0);
                foreach (var node in plan.Nodes) node.Position += lift;
            }
        }

        plan.Voxels = voxels;
        string lines = settings.Text.Split('\n').Length.ToString(CultureInfo.InvariantCulture);
        string smooth = settings.SmoothEdges
            ? $" · {usedScale}× blocks" + (plan.EdgeBars > 0 ? $" · {plan.EdgeBars} angled edge(s)" : "")
            : "";
        plan.Summary = $"Text: {plan.Nodes.Count} block(s) · {cols} × {rows} cells · {depth} deep · " +
                       $"{plan.FontName} · {lines} line(s){smooth}";
        return plan;
    }

    /// <summary>Copy of the settings with the block grid refined by <paramref name="scale"/>.</summary>
    private static TextBlockSettings ScaledSettings(TextBlockSettings s, int scale) => new()
    {
        Text = s.Text,
        FontFamily = s.FontFamily,
        FontFile = s.FontFile,
        Bold = s.Bold,
        Italic = s.Italic,
        Height = s.Height * scale,
        Detail = s.Detail,
        Depth = s.Depth * scale,
        LetterSpacing = s.LetterSpacing * scale,
        LineSpacing = s.LineSpacing * scale,
        Fill = s.Fill,
        Plane = s.Plane,
        BlockSize = s.BlockSize,
        Material = s.Material,
        Color = s.Color,
        Shape = s.Shape,
        MergeRuns = s.MergeRuns,
        SmoothEdges = s.SmoothEdges,
        Smoothing = s.Smoothing,
        AngledEdges = s.AngledEdges,
        EdgeThickness = s.EdgeThickness,
        Center = s.Center,
        YawDegrees = s.YawDegrees,
        Origin = s.Origin
    };

    // ------------------------------------------------------------------ smoothing

    /// <summary>Greedy decomposition of a filled grid into the largest axis-aligned boxes.</summary>
    private static List<(int X, int Y, int W, int H)> Decompose(bool[,] grid, int cols, int rows)
    {
        var rects = new List<(int X, int Y, int W, int H)>();
        var used = new bool[rows, cols];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (!grid[y, x] || used[y, x]) continue;
                int w = 0;
                while (x + w < cols && grid[y, x + w] && !used[y, x + w]) w++;
                int h = 1;
                while (y + h < rows)
                {
                    bool fits = true;
                    for (int i = 0; i < w; i++)
                    {
                        if (!grid[y + h, x + i] || used[y + h, x + i]) { fits = false; break; }
                    }
                    if (!fits) break;
                    h++;
                }
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++)
                        used[y + yy, x + xx] = true;
                rects.Add((x, y, w, h));
            }
        }
        return rects;
    }

    /// <summary>Flattens a 2D mask for the row-major contour tracer.</summary>
    private static bool[] Flatten(bool[,] grid, int cols, int rows)
    {
        var flat = new bool[cols * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                flat[y * cols + x] = grid[y, x];
        return flat;
    }

    /// <summary>
    /// Walks the cell borders of the mask into boundary chains. Each directed unit edge keeps
    /// the filled side on its right, so chords can be offset toward the glyph interior.
    /// </summary>
    private static List<List<(double X, double Y)>> TraceContours(bool[] grid, int cols, int rows)
    {
        bool At(int x, int y) => x >= 0 && y >= 0 && x < cols && y < rows && grid[y * cols + x];
        var edges = new List<(int X0, int Y0, int X1, int Y1)>();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (!At(x, y)) continue;
                bool up = At(x, y - 1);
                bool down = At(x, y + 1);
                bool left = At(x - 1, y);
                bool right = At(x + 1, y);
                if (!up) edges.Add((x, y, x + 1, y));
                if (!right) edges.Add((x + 1, y, x + 1, y + 1));
                if (!down) edges.Add((x + 1, y + 1, x, y + 1));
                if (!left) edges.Add((x, y + 1, x, y));
            }
        }

        var byStart = new Dictionary<long, List<int>>();
        long Key(int x, int y) => (long)y * (cols + 2) + x;
        for (int i = 0; i < edges.Count; i++)
        {
            long key = Key(edges[i].X0, edges[i].Y0);
            if (!byStart.TryGetValue(key, out var list))
            {
                list = new List<int>();
                byStart[key] = list;
            }
            list.Add(i);
        }

        var used = new bool[edges.Count];
        var chains = new List<List<(double X, double Y)>>();
        for (int seed = 0; seed < edges.Count; seed++)
        {
            if (used[seed]) continue;
            var chain = new List<(double X, double Y)>();
            int current = seed;
            while (current >= 0 && !used[current])
            {
                used[current] = true;
                var e = edges[current];
                if (chain.Count == 0) chain.Add((e.X0, e.Y0));
                chain.Add((e.X1, e.Y1));

                if (chain.Count > 2 && chain[0] == chain[^1]) break;
                if (!byStart.TryGetValue(Key(e.X1, e.Y1), out var candidates)) break;
                int next = -1;
                foreach (int c in candidates)
                {
                    if (used[c]) continue;
                    if (next >= 0) { next = -1; break; }
                    next = c;
                }
                if (next < 0) break;
                current = next;
            }
            if (chain.Count >= 3) chains.Add(chain);
        }
        return chains;
    }

    /// <summary>
    /// Douglas–Peucker simplification: keeps only the indices needed to follow the outline.
    /// The discarded points are still used to measure how far the staircase bulges.
    /// </summary>
    private static List<int> SimplifyIndices(List<(double X, double Y)> points, double epsilon)
    {
        int n = points.Count;
        var kept = new List<int>();
        if (n == 0) return kept;
        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end <= start + 1) continue;
            double best = -1;
            int index = -1;
            for (int i = start + 1; i < end; i++)
            {
                double d = DistanceToSegment(points[i].X, points[i].Y, points[start], points[end]);
                if (d > best) { best = d; index = i; }
            }
            if (best > epsilon && index > 0)
            {
                keep[index] = true;
                stack.Push((start, index));
                stack.Push((index, end));
            }
        }
        for (int i = 0; i < n; i++)
            if (keep[i]) kept.Add(i);
        return kept;
    }

    private static double DistanceToSegment(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-12) return Math.Sqrt((px - a.X) * (px - a.X) + (py - a.Y) * (py - a.Y));
        double t = ((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        double qx = a.X + t * dx - px, qy = a.Y + t * dy - py;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // ------------------------------------------------------------------ layout

    private sealed record GlyphLayout(GeometryGroup Group, double LineAdvance);

    /// <summary>Lays the glyph outlines out by hand so any glyph typeface works.</summary>
    private static GlyphLayout Layout(GlyphTypeface face, string text, double em,
        double letterSpacing, double lineSpacing)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        double lineAdvance = Math.Max(em * 0.6, face.Height * em) + lineSpacing;
        double baseline = face.Baseline * em;
        double penX = 0;
        int line = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n')
            {
                line++;
                penX = 0;
                continue;
            }

            int codepoint = ch;
            if (!face.CharacterToGlyphMap.TryGetValue(codepoint, out ushort glyph))
            {
                // Fall back to the font's own "missing glyph" box when it has one.
                if (!face.CharacterToGlyphMap.TryGetValue(0, out glyph))
                    continue;
            }

            double advance = face.AdvanceWidths.TryGetValue(glyph, out double aw) ? aw * em : em * 0.3;
            if (ch == ' ')
            {
                penX += advance + letterSpacing;
                continue;
            }
            if (ch == '\t')
            {
                penX += advance * 4 + letterSpacing;
                continue;
            }

            try
            {
                Geometry outline = face.GetGlyphOutline(glyph, em, em);
                outline.Transform = new TranslateTransform(penX, baseline + line * lineAdvance);
                group.Children.Add(outline);
            }
            catch
            {
                // A glyph with a broken outline is skipped rather than killing the build.
            }
            penX += advance + letterSpacing;
        }

        return new GlyphLayout(group, lineAdvance);
    }
}
