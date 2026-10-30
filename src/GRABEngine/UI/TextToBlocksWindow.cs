using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrabEngine.Core;
using Microsoft.Win32;

namespace GrabEngine.UI;

/// <summary>
/// Text → Blocks: type a string, pick a built-in or custom font, and stamp the glyphs as
/// real GRAB blocks. A live mask preview shows exactly what will be placed.
/// </summary>
public sealed class TextToBlocksWindow : Window
{
    private readonly Vec3 _viewTarget;
    private readonly TextBox _text = Theme.TextBox("GRAB", multiline: true, height: 74);
    private readonly ComboBox _font = new() { Height = 24, MinWidth = 210 };
    private readonly List<string> _fontItems = new(TextBlockBuilder.BuiltinFonts);
    private readonly TextBlock _customFontLabel = Theme.Hint("");
    private readonly CheckBox _bold = Theme.Check("Bold");
    private readonly CheckBox _italic = Theme.Check("Italic");
    private readonly TextBox _height = Theme.TextBox("24");
    private readonly Slider _detail = new() { Minimum = 1, Maximum = 4, Value = 2, TickFrequency = 1, IsSnapToTickEnabled = true, Height = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detailValue = new() { Width = 24, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _depth = Theme.TextBox("2");
    private readonly TextBox _letter = Theme.TextBox("0");
    private readonly TextBox _line = Theme.TextBox("0");
    private readonly TextBox _size = Theme.TextBox("0.25");
    private readonly ComboBox _fill = Theme.Combo(new[] { "Solid", "Hollow shell", "Outline only" });
    private readonly ComboBox _plane = Theme.Combo(new[] { "Standing (wall text)", "Lying flat (floor text)" });
    private readonly ComboBox _shape;
    private readonly ComboBox _material;
    private readonly TextBox _color = Theme.TextBox("#E8E8E8");
    private readonly CheckBox _smooth = Theme.Check("Smooth grid (finer blocks)", true);
    private readonly CheckBox _angled = Theme.Check("Angled edge blocks (experimental)", false);
    private readonly ComboBox _smoothQuality = Theme.Combo(new[] { "Tight (1× blocks)", "Balanced (2× blocks)", "Very smooth (3× blocks)" }, 1);
    private readonly TextBox _edgeWidth = Theme.TextBox("1.5");
    private readonly CheckBox _merge = Theme.Check("Merge blocks into larger boxes", true);
    private readonly CheckBox _center = Theme.Check("Centre on the build point", true);
    private readonly CheckBox _atView = Theme.Check("Build at view target", true);
    private readonly TextBox _yaw = Theme.TextBox("0");
    private readonly TextBlock _status = Theme.Hint("Type text and press Generate.");
    private readonly TextBlock _stats = new() { Foreground = Theme.Text, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextMaskPreview _preview = new();
    private readonly DispatcherTimer _debounce;
    private string _customFile = "";
    private string _customItem = "";
    private TextBlockPlan? _lastPlan;
    private bool _updating;

    public event Action<TextBlockPlan>? Built;

    public TextToBlocksWindow(Window owner, Vec3? viewTarget = null)
    {
        _viewTarget = viewTarget ?? Vec3.Zero;
        Owner = owner;
        Title = "Text to Blocks";
        Width = 1180;
        Height = 790;
        MinWidth = 940;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _shape = Theme.Combo(ShapeInfo.All.Select(s => s.Name).ToArray());
        _material = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(),
            Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8)));

        _font.ItemsSource = _fontItems;
        _font.SelectedItem = TextBlockBuilder.BuiltinFonts[0];

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshPreview();
        };

        var root = new DockPanel();

        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("TEXT TO BLOCKS"),
                Theme.Hint("Type text, choose a font (installed or a .ttf/.otf file) and it is rasterized into block letters. " +
                           "Height is the number of base blocks tall, Detail sharpens the edges, Depth extrudes the glyphs. " +
                           "Smoothing refines the grid to 2× or 3× smaller blocks; angled edge bars are an experimental extra.")
            }
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8, 14, 8)
        };
        var footerRow = new DockPanel();
        footerRow.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var close = Theme.Button("Close");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        var build = Theme.Button("Generate Blocks", accent: true);
        build.Margin = new Thickness(6, 0, 0, 0);
        build.ToolTip = "Place the previewed text into the level as a group of blocks";
        build.Click += (_, _) => Generate();
        buttons.Children.Add(build);
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Insert(0, buttons);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(410) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var settingsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var settings = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        settingsScroll.Content = settings;

        settings.Children.Add(Theme.SectionHeader("TEXT"));
        settings.Children.Add(_text);
        settings.Children.Add(Theme.Hint("Press Enter for a new line. Each line is laid out by the font's own metrics."));

        settings.Children.Add(Theme.SectionHeader("FONT"));
        settings.Children.Add(Theme.Row("Font", _font));
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal };
        styleRow.Children.Add(_bold);
        _italic.Margin = new Thickness(14, 3, 0, 3);
        styleRow.Children.Add(_italic);
        var loadFont = Theme.Button("Font file…", small: true);
        loadFont.Margin = new Thickness(14, 0, 0, 0);
        loadFont.ToolTip = "Load a .ttf or .otf font from disk";
        loadFont.Click += (_, _) => PickFontFile();
        styleRow.Children.Add(loadFont);
        settings.Children.Add(Theme.Row("Style", styleRow));
        settings.Children.Add(_customFontLabel);

        settings.Children.Add(Theme.SectionHeader("RESOLUTION"));
        settings.Children.Add(Theme.Row("Height", WithUnits(_height, "blocks")));
        var detailRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_detailValue, Dock.Right);
        detailRow.Children.Add(_detailValue);
        detailRow.Children.Add(_detail);
        settings.Children.Add(Theme.Row("Detail", detailRow));
        settings.Children.Add(Theme.Row("Depth", WithUnits(_depth, "blocks")));
        settings.Children.Add(Theme.Row("Letter gap", WithUnits(_letter, "blocks")));
        settings.Children.Add(Theme.Row("Line gap", WithUnits(_line, "blocks")));
        settings.Children.Add(Theme.Row("Base block", WithUnits(_size, "m")));

        settings.Children.Add(Theme.SectionHeader("SMOOTHING"));
        _smooth.ToolTip = "Uses 2× or 3× smaller blocks so the letter edges are finer; the text keeps its world size";
        settings.Children.Add(_smooth);
        _angled.ToolTip = "Lays rotated bars along diagonals and curves. Off by default: their square ends can notch tight corners";
        settings.Children.Add(_angled);
        _smoothQuality.ToolTip = "2× and 3× use smaller blocks for a finer outline; the text keeps the same world size";
        settings.Children.Add(Theme.Row("Grid", _smoothQuality));
        settings.Children.Add(Theme.Row("Edge width", WithUnits(_edgeWidth, "base blocks")));
        settings.Children.Add(Theme.Hint("The fine grid is the clean way to smooth text. Angled edge blocks are experimental; leave them off for crisp, gap-free edges."));

        settings.Children.Add(Theme.SectionHeader("STYLE"));
        settings.Children.Add(Theme.Row("Fill", _fill));
        settings.Children.Add(Theme.Row("Orientation", _plane));
        settings.Children.Add(Theme.Row("Block shape", _shape));
        settings.Children.Add(Theme.Row("Material", _material));
        _color.ToolTip = "Tint used when the material is Default Colored";
        settings.Children.Add(Theme.Row("Colour", ColorInputs.Field(_color)));

        settings.Children.Add(Theme.SectionHeader("PLACEMENT"));
        settings.Children.Add(_merge);
        settings.Children.Add(_center);
        settings.Children.Add(_atView);
        settings.Children.Add(Theme.Row("Yaw", WithUnits(_yaw, "°")));

        Grid.SetColumn(settingsScroll, 0);
        body.Children.Add(settingsScroll);

        var previewHead = new StackPanel
        {
            Children =
            {
                Theme.Heading("FRONT VIEW"),
                Theme.Hint("Every block footprint is drawn here, so you see the final edges before placing."),
                _stats
            }
        };
        DockPanel.SetDock(previewHead, Dock.Top);
        var previewStack = new DockPanel { LastChildFill = true };
        _preview.Margin = new Thickness(0, 8, 0, 0);
        previewStack.Children.Add(previewHead);
        previewStack.Children.Add(_preview);
        var previewCard = Theme.Card(previewStack, new Thickness(10));
        Grid.SetColumn(previewCard, 2);
        body.Children.Add(previewCard);

        root.Children.Add(body);

        Content = root;

        _detail.ValueChanged += (_, _) =>
        {
            _detailValue.Text = ((int)_detail.Value).ToString(CultureInfo.InvariantCulture);
            Schedule();
        };
        _detailValue.Text = "2";

        _text.TextChanged += (_, _) => Schedule();
        _font.SelectionChanged += (_, _) =>
        {
            _customFontLabel.Text = CustomSelected ? "Custom font loaded from " + System.IO.Path.GetFileName(_customFile) : "";
            Schedule();
        };
        _bold.Click += (_, _) => Schedule();
        _italic.Click += (_, _) => Schedule();
        _fill.SelectionChanged += (_, _) => Schedule();
        _plane.SelectionChanged += (_, _) =>
        {
            _preview.Lying = _plane.SelectedIndex == 1;
            Schedule();
        };
        _material.SelectionChanged += (_, _) =>
        {
            _updating = true;
            _color.IsEnabled = MaterialInfo.IsColorable(SelectedMaterial);
            _updating = false;
            Schedule();
        };
        _color.TextChanged += (_, _) => Schedule();
        _merge.Click += (_, _) => Schedule();
        _smooth.Click += (_, _) =>
        {
            _smoothQuality.IsEnabled = _smooth.IsChecked == true;
            _edgeWidth.IsEnabled = _smooth.IsChecked == true && _angled.IsChecked == true;
            _angled.IsEnabled = _smooth.IsChecked == true;
            Schedule();
        };
        _angled.Click += (_, _) =>
        {
            _edgeWidth.IsEnabled = _smooth.IsChecked == true && _angled.IsChecked == true;
            Schedule();
        };
        _smoothQuality.SelectionChanged += (_, _) => Schedule();
        _atView.Click += (_, _) => Schedule();
        _yaw.TextChanged += (_, _) => Schedule();

        foreach (var box in new[] { _height, _depth, _letter, _line, _size, _edgeWidth, _yaw })
        {
            box.TextChanged += (_, _) => Schedule();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; RefreshPreview(); }
            };
        }

        _color.IsEnabled = true;
        _edgeWidth.IsEnabled = false;
        Loaded += (_, _) => RefreshPreview();
    }

    private bool CustomSelected => _customItem.Length > 0 && (_font.SelectedItem as string) == _customItem;

    private int SelectedMaterial
    {
        get
        {
            int index = Math.Clamp(_material.SelectedIndex, 0, MaterialInfo.All.Length - 1);
            return MaterialInfo.All[index].Id;
        }
    }

    private static DockPanel WithUnits(TextBox box, string units)
    {
        var panel = new DockPanel { LastChildFill = true };
        var label = new TextBlock
        {
            Text = units,
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(label, Dock.Right);
        panel.Children.Add(label);
        panel.Children.Add(box);
        return panel;
    }

    private void PickFontFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load a font for block text",
            Filter = "Font files (*.ttf;*.otf)|*.ttf;*.otf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var probe = new TextBlockSettings { FontFile = dialog.FileName };
        if (!TextBlockBuilder.TryGetGlyphTypeface(probe, out _, out string label, out string error))
        {
            MessageBox.Show(this, error, "Font could not be loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _customFile = dialog.FileName;
        _customItem = $"{label}  (file)";
        if (_fontItems.Count > 0 && _fontItems[0].EndsWith("(file)", StringComparison.Ordinal))
            _fontItems[0] = _customItem;
        else
            _fontItems.Insert(0, _customItem);
        _font.ItemsSource = null;
        _font.ItemsSource = _fontItems;
        _font.SelectedIndex = 0;
        _customFontLabel.Text = "Custom font loaded from " + System.IO.Path.GetFileName(_customFile);
        Schedule();
    }

    private void Schedule()
    {
        if (_updating || !IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private TextBlockSettings ReadSettings() => new()
    {
        Text = _text.Text,
        FontFamily = _font.SelectedItem as string ?? TextBlockBuilder.BuiltinFonts[0],
        FontFile = CustomSelected ? _customFile : "",
        Bold = _bold.IsChecked == true,
        Italic = _italic.IsChecked == true,
        Height = ParseInt(_height.Text, 24, 4, 96),
        Detail = (int)Math.Round(_detail.Value),
        Depth = ParseInt(_depth.Text, 2, 1, 32),
        LetterSpacing = ParseInt(_letter.Text, 0, -8, 24),
        LineSpacing = ParseInt(_line.Text, 0, -8, 24),
        Fill = (TextFillMode)Math.Clamp(_fill.SelectedIndex, 0, 2),
        Plane = (TextPlane)Math.Clamp(_plane.SelectedIndex, 0, 1),
        BlockSize = ParseFloat(_size.Text, 0.25f, 0.02f, 4f),
        Material = SelectedMaterial,
        Color = _color.Text,
        Shape = ShapeInfo.All[Math.Clamp(_shape.SelectedIndex, 0, ShapeInfo.All.Length - 1)].Id,
        MergeRuns = _merge.IsChecked == true,
        SmoothEdges = _smooth.IsChecked == true,
        Smoothing = (TextSmoothness)Math.Clamp(_smoothQuality.SelectedIndex, 0, 2),
        AngledEdges = _angled.IsChecked == true,
        EdgeThickness = ParseFloat(_edgeWidth.Text, 1.5f, 0.6f, 3f),
        Center = _center.IsChecked == true,
        YawDegrees = ParseInt(_yaw.Text, 0, -360, 360),
        Origin = _atView.IsChecked == true ? _viewTarget : Vec3.Zero
    };

    private void RefreshPreview()
    {
        try
        {
            var settings = ReadSettings();
            var plan = TextBlockBuilder.BuildPlan(settings);
            _lastPlan = plan;

            if (!plan.Ok || plan.Mask == null)
            {
                _preview.SetPlan(null, Col.White);
                _stats.Text = "";
                _status.Text = plan.Ok ? "Nothing to place." : plan.Error;
                _status.Foreground = Theme.Error;
                return;
            }

            var color = MaterialInfo.IsColorable(settings.Material)
                ? Col.FromHex(settings.Color, new Col(0.91f, 0.91f, 0.91f, 1f))
                : new Col(0.86f, 0.86f, 0.86f, 1f);
            _preview.SetPlan(plan, color);
            _stats.Text = $"{plan.Columns} × {plan.Rows} cells  ·  {plan.Depth} deep  ·  " +
                          $"detail {plan.Mask.DetailUsed}  ·  {plan.Nodes.Count} block(s)" +
                          (plan.SmoothScale > 1 ? $"  ·  {plan.SmoothScale}× grid" : "") +
                          (plan.EdgeBars > 0 ? $"  ·  {plan.EdgeBars} smooth edge(s)" : "");
            _status.Text = plan.Nodes.Count > 3500
                ? $"Large text: {plan.Nodes.Count} blocks. Lower the height, depth or detail for a lighter build."
                : $"{plan.FontName} · ready to place {plan.Nodes.Count} block(s).";
            _status.Foreground = plan.Nodes.Count > 3500 ? Theme.Warning : Theme.Dim;
        }
        catch (Exception ex)
        {
            _status.Text = "Preview failed: " + ex.Message;
            _status.Foreground = Theme.Error;
        }
    }

    private void Generate()
    {
        TextBlockPlan plan;
        try
        {
            plan = TextBlockBuilder.BuildPlan(ReadSettings());
        }
        catch (Exception ex)
        {
            _status.Text = "Generation failed: " + ex.Message;
            _status.Foreground = Theme.Error;
            return;
        }

        if (!plan.Ok || plan.Nodes.Count == 0)
        {
            _status.Text = plan.Ok ? "Nothing to place." : plan.Error;
            _status.Foreground = Theme.Error;
            return;
        }

        Built?.Invoke(plan);
        _lastPlan = plan;
        _status.Text = plan.Summary + " placed.";
        _status.Foreground = Theme.Ok;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static float ParseFloat(string text, float fallback, float min, float max)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

/// <summary>Draws the block footprints — including the angled smoothing bars — as placed.</summary>
public sealed class TextMaskPreview : FrameworkElement
{
    private TextBlockPlan? _plan;
    private Col _color = Col.White;
    public bool Lying { get; set; }

    public TextMaskPreview()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void SetPlan(TextBlockPlan? plan, Col color)
    {
        _plan = plan;
        _color = color;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 27, 29)), null, new Rect(0, 0, w, h));

        var plan = _plan;
        if (plan == null || plan.Columns == 0 || plan.Rows == 0 || plan.Shapes.Count == 0 || w < 40 || h < 40)
        {
            var hint = new FormattedText(
                "Type text to see it here",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(110, 114, 118)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point((w - hint.Width) * 0.5, (h - hint.Height) * 0.5));
            return;
        }

        // Include the angled edge bars' rotated extents so nothing is clipped.
        double minX = 0, maxX = plan.Columns, minY = 0, maxY = plan.Rows;
        foreach (var s in plan.Shapes)
        {
            double rad = s.Angle * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
            double ex = (cos * s.W + sin * s.H) * 0.5;
            double ey = (sin * s.W + cos * s.H) * 0.5;
            minX = Math.Min(minX, s.X - ex);
            maxX = Math.Max(maxX, s.X + ex);
            minY = Math.Min(minY, s.Y - ey);
            maxY = Math.Max(maxY, s.Y + ey);
        }

        const double pad = 24;
        double scale = Math.Min((w - pad * 2) / (maxX - minX), (h - pad * 2 - 18) / (maxY - minY));
        if (scale <= 0) return;
        double ox = (w - (maxX - minX) * scale) * 0.5 - minX * scale;
        double oy = (h - (maxY - minY) * scale) * 0.5 - minY * scale;

        // Block grid, so the block resolution is obvious at a glance.
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
        if (scale >= 5 && plan.Columns <= 600)
        {
            for (int x = 0; x <= plan.Columns; x++)
                dc.DrawLine(gridPen, new Point(ox + x * scale, oy), new Point(ox + x * scale, oy + plan.Rows * scale));
            for (int y = 0; y <= plan.Rows; y++)
                dc.DrawLine(gridPen, new Point(ox, oy + y * scale), new Point(ox + plan.Columns * scale, oy + y * scale));
        }

        var brush = new SolidColorBrush(Color.FromRgb((byte)_color.Ri, (byte)_color.Gi, (byte)_color.Bi));
        brush.Freeze();
        foreach (var s in plan.Shapes)
        {
            double cx = ox + s.X * scale;
            double cy = oy + s.Y * scale;
            // Grow each footprint by a hair so anti-aliased neighbours never show a seam.
            double hw = s.W * scale * 0.5 + 0.7;
            double hh = s.H * scale * 0.5 + 0.7;
            dc.PushTransform(new RotateTransform(s.Angle, cx, cy));
            dc.DrawRectangle(brush, null, new Rect(cx - hw, cy - hh, hw * 2, hh * 2));
            dc.Pop();
        }

        var caption = new FormattedText(
            (Lying ? "floor view · " : "front view · ") + plan.Columns + " × " + plan.Rows +
            (plan.EdgeBars > 0 ? " · " + plan.EdgeBars + " angled edge(s)" : ""),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.FromRgb(140, 145, 150)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(caption, new Point(ox, Math.Min(oy + plan.Rows * scale + 8, h - caption.Height - 2)));
    }
}
