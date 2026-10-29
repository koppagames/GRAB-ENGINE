using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// Structure and shape authoring: stairs, ramps, bridges, arches, towers, roofs,
/// voxelised primitives and silhouette extrusion. Everything is previewed as a
/// footprint before it is written as real blocks.
/// </summary>
public sealed class StructureStudioWindow : Window
{
    private readonly GrabLevel _level;
    private readonly ComboBox _kind = Theme.Combo(Enum.GetNames<StructureKind>());
    private readonly TextBox _width = Theme.TextBox("4");
    private readonly TextBox _length = Theme.TextBox("6");
    private readonly TextBox _height = Theme.TextBox("3");
    private readonly TextBox _steps = Theme.TextBox("8");
    private readonly TextBox _sides = Theme.TextBox("12");
    private readonly TextBox _radius = Theme.TextBox("2.5");
    private readonly TextBox _cell = Theme.TextBox("1");
    private readonly TextBox _thickness = Theme.TextBox("0.35");
    private readonly ComboBox _material;
    private readonly TextBox _color = Theme.TextBox("#B8B8B8");
    private readonly CheckBox _railings = Theme.Check("Railings / trim", true);
    private readonly CheckBox _hollow = Theme.Check("Hollow shell", true);
    private readonly CheckBox _mirrorX = Theme.Check("Mirror X", false);
    private readonly CheckBox _mirrorZ = Theme.Check("Mirror Z", false);
    private readonly CheckBox _atSelection = Theme.Check("Build at view target", true);
    private readonly TextBlock _status = Theme.Hint("Adjust the settings and press Build.");
    private readonly TextBlock _summary = new() { Foreground = Theme.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StructurePreview _preview = new();
    private readonly Vec3 _origin;
    private TabControl? _tabs;

    // Shape tab
    private readonly ComboBox _shapeKind;
    private readonly TextBox _shapeSize = Theme.TextBox("4, 4, 4");
    private readonly TextBox _shapeCell = Theme.TextBox("0.5");
    private readonly ComboBox _shapeMaterial;
    private readonly TextBox _outline = Theme.TextBox("0,0; 4,0; 4,3; 0,3");
    private readonly TextBox _outlineHeight = Theme.TextBox("3");
    private readonly TextBox _outlineCell = Theme.TextBox("0.5");

    // Easy draw tab. The user clicks a footprint instead of typing coordinates.
    private readonly FootprintCanvas _footprint = new();
    private readonly TextBox _drawHeight = Theme.TextBox("3");
    private readonly TextBox _drawColor = Theme.TextBox("#B8B8B8");
    private readonly ComboBox _drawMaterial;
    private readonly TextBlock _drawStatus = Theme.Hint("Click inside the grid to draw an outline.");

    public event Action<List<LevelNode>, string>? Built;
    /// <summary>Raised when the user asks for the in-scene Draw tool instead.</summary>
    public event Action? DrawInSceneRequested;

    public StructureStudioWindow(Window owner, GrabLevel level, Vec3? origin = null)
    {
        _level = level;
        _origin = origin ?? Vec3.Zero;
        Owner = owner;
        Title = "Structure & Shape Studio";
        Width = 1060;
        Height = 760;
        MinWidth = 860;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _material = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8)));
        _shapeKind = Theme.Combo(ShapeInfo.All.Select(s => s.Name).ToArray(), 0);
        _shapeMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8)));
        _drawMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8)));

        var root = new DockPanel();
        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("STRUCTURE & SHAPE STUDIO"),
                Theme.Hint("Draw a footprint, or use presets for stairs, ramps, bridges, arches, towers, roofs and voxel shapes — all ordinary GRAB blocks.")
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
        var build = Theme.Button("Build", accent: true);
        build.Margin = new Thickness(6, 0, 0, 0);
        build.Click += (_, _) => BuildActiveTab();
        buttons.Children.Add(build);
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Insert(0, buttons);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _tabs = new TabControl { Background = Theme.Panel, BorderThickness = new Thickness(0) };
        _tabs.Items.Add(new TabItem { Header = "Draw Footprint", Content = BuildDrawTab() });
        _tabs.Items.Add(new TabItem { Header = "Presets", Content = BuildStructureTab(origin) });
        _tabs.Items.Add(new TabItem { Header = "Voxel Shape", Content = BuildShapeTab(origin) });
        root.Children.Add(_tabs);
        Content = root;

        foreach (var control in new Control[] { _kind, _width, _length, _height, _steps, _sides, _radius, _cell, _thickness, _material, _color })
        {
            if (control is TextBox box) box.LostFocus += (_, _) => RefreshPreview();
            if (control is ComboBox combo) combo.SelectionChanged += (_, _) => RefreshPreview();
        }
        _railings.Click += (_, _) => RefreshPreview();
        _hollow.Click += (_, _) => RefreshPreview();
        _mirrorX.Click += (_, _) => RefreshPreview();
        _mirrorZ.Click += (_, _) => RefreshPreview();
        Loaded += (_, _) => RefreshPreview();
    }

    private UIElement BuildDrawTab()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 0, 8, 12) };
        var panel = new StackPanel();
        panel.Children.Add(Theme.SectionHeader("1 · DRAW THE OUTLINE"));
        panel.Children.Add(Theme.Hint("Click points on the grid. Click the first point again (or double-click) to close the shape. This is the whole footprint step, no X/Z entry."));
        panel.Children.Add(Theme.SectionHeader("2 · SET THE HEIGHT"));
        panel.Children.Add(Theme.Row("Height", _drawHeight));
        panel.Children.Add(Theme.Row("Material", _drawMaterial));
        panel.Children.Add(Theme.Row("Colour", _drawColor));
        panel.Children.Add(Theme.Hint("The outline is filled and extruded upward into normal GRAB blocks. The default is 1 m per cell."));
        var build = Theme.Button("Build Drawn Shape", accent: true);
        build.Margin = new Thickness(0, 8, 0, 0);
        build.Click += (_, _) => BuildFootprint();
        panel.Children.Add(build);
        var clear = Theme.Button("Clear Outline", small: true);
        clear.Margin = new Thickness(0, 5, 0, 0);
        clear.Click += (_, _) => { _footprint.Clear(); _drawStatus.Text = "Outline cleared. Click points to start again."; };
        panel.Children.Add(clear);
        panel.Children.Add(Theme.SectionHeader("OR DRAW IN 3D"));
        panel.Children.Add(Theme.Hint("The in-scene Draw tool lets you drag boxes, spheres, cylinders, cones, stairs and ramps directly against block faces."));
        var inScene = Theme.Button("Use In-Scene Draw Tool", small: true);
        inScene.Click += (_, _) => { DrawInSceneRequested?.Invoke(); Close(); };
        panel.Children.Add(inScene);
        panel.Children.Add(_drawStatus);
        scroll.Content = panel;
        grid.Children.Add(scroll);

        _footprint.Changed += () =>
        {
            var count = _footprint.Points.Count;
            _drawStatus.Text = count < 3
                ? $"{count} point(s) — add at least 3 to make a shape."
                : $"{count} points — outline closed. Press Build Drawn Shape.";
            _drawStatus.Foreground = count < 3 ? Theme.Dim : Theme.Ok;
        };
        _footprint.DoubleClickCompleted += () =>
        {
            if (_footprint.Points.Count >= 3)
                _drawStatus.Text = $"{_footprint.Points.Count} points — outline ready. Press Build Drawn Shape.";
        };

        var host = new Border
        {
            Margin = new Thickness(4, 6, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 26)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _footprint
        };
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);
        return grid;
    }

    private void BuildFootprint()
    {
        if (_footprint.Points.Count < 3)
        {
            _drawStatus.Text = "Draw at least three points first.";
            _drawStatus.Foreground = Theme.Warning;
            return;
        }
        float height = ParseFloat(_drawHeight.Text, 3, 0.5f, 80);
        var material = MaterialInfo.All[Math.Clamp(_drawMaterial.SelectedIndex, 0, MaterialInfo.All.Length - 1)].Id;
        var settings = ReadSettings();
        settings.Origin = Origin();
        settings.Cell = 1f;
        settings.Depth = Math.Clamp((int)MathF.Round(height), 1, 64);
        settings.Material = material;
        settings.Color = Col.FromHex(_drawColor.Text, new Col(0.72f, 0.72f, 0.72f, 1));
        var nodes = StructureBuilder.ExtrudePolygon(_footprint.Points, settings);
        if (nodes.Count == 0)
        {
            _drawStatus.Text = "That outline produced no blocks. Draw a larger shape.";
            _drawStatus.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(nodes, "Drawn Shape");
        _drawStatus.Text = $"Drawn shape written: {nodes.Count} block(s).";
        _drawStatus.Foreground = Theme.Ok;
    }

    private UIElement BuildStructureTab(Vec3? origin)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 0, 8, 12) };
        var settings = new StackPanel();
        settings.Children.Add(Theme.SectionHeader("TYPE"));
        settings.Children.Add(Theme.Row("Structure", _kind));
        settings.Children.Add(Theme.SectionHeader("DIMENSIONS"));
        settings.Children.Add(Theme.Row("Width", _width));
        settings.Children.Add(Theme.Row("Length", _length));
        settings.Children.Add(Theme.Row("Height", _height));
        settings.Children.Add(Theme.Row("Steps", _steps));
        settings.Children.Add(Theme.Row("Sides", _sides));
        settings.Children.Add(Theme.Row("Radius", _radius));
        settings.Children.Add(Theme.Row("Cell", _cell));
        settings.Children.Add(Theme.Row("Thickness", _thickness));
        settings.Children.Add(Theme.SectionHeader("STYLE"));
        settings.Children.Add(Theme.Row("Material", _material));
        settings.Children.Add(Theme.Row("Colour", _color));
        settings.Children.Add(_railings);
        settings.Children.Add(_hollow);
        settings.Children.Add(Theme.SectionHeader("SYMMETRY"));
        settings.Children.Add(_mirrorX);
        settings.Children.Add(_mirrorZ);
        settings.Children.Add(Theme.Hint("Mirror creates a second copy across the symmetry pivot, so a half-built structure becomes a full one."));
        settings.Children.Add(Theme.SectionHeader("QUICK BUILD"));
        var quick = new StackPanel { Orientation = Orientation.Horizontal };
        var quickStairs = Theme.Button("Stairs", small: true);
        quickStairs.ToolTip = "Build the current dimensions as stairs at the selection";
        quickStairs.Click += (_, _) => BuildQuick(StructureKind.Stairs);
        var quickRamp = Theme.Button("Ramp", small: true);
        quickRamp.Margin = new Thickness(5, 0, 0, 0);
        quickRamp.ToolTip = "Build a clean ramp at the selection";
        quickRamp.Click += (_, _) => BuildQuick(StructureKind.Ramp);
        var quickPlatform = Theme.Button("Platform", small: true);
        quickPlatform.Margin = new Thickness(5, 0, 0, 0);
        quickPlatform.Click += (_, _) => BuildQuick(StructureKind.Platform);
        quick.Children.Add(quickStairs);
        quick.Children.Add(quickRamp);
        quick.Children.Add(quickPlatform);
        settings.Children.Add(quick);
        settings.Children.Add(_atSelection);
        settings.Children.Add(_summary);
        scroll.Content = settings;
        grid.Children.Add(scroll);

        var previewHost = new Border
        {
            Margin = new Thickness(4, 6, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 26)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _preview
        };
        Grid.SetColumn(previewHost, 1);
        grid.Children.Add(previewHost);
        return grid;
    }

    private UIElement BuildShapeTab(Vec3? origin)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 8, 14, 12) };
        panel.Children.Add(Theme.Hint("Voxelises a primitive into blocks. This is useful for building spheres, domes, cylinders and custom block silhouettes at a chosen resolution."));
        panel.Children.Add(Theme.Row("Shape", _shapeKind));
        panel.Children.Add(Theme.Row("Size X,Y,Z", _shapeSize));
        panel.Children.Add(Theme.Row("Cell", _shapeCell));
        panel.Children.Add(Theme.Row("Material", _shapeMaterial));
        var build = Theme.Button("Build Voxel Shape", accent: true);
        build.Margin = new Thickness(0, 8, 0, 0);
        build.Click += (_, _) => BuildVoxelShape();
        panel.Children.Add(build);
        panel.Children.Add(Theme.Hint("Lower cell size means more blocks and a smoother surface. Above roughly 4000 blocks, prefer a sculpted shape or scatter."));

        panel.Children.Add(Theme.SectionHeader("SILHOUETTE / EXTRUDE"));
        panel.Children.Add(Theme.Hint("Type a closed outline as world X,Z pairs separated by semicolons. The shape is filled and extruded upward into blocks."));
        panel.Children.Add(Theme.Row("Outline X,Z", _outline));
        panel.Children.Add(Theme.Row("Height", _outlineHeight));
        panel.Children.Add(Theme.Row("Cell", _outlineCell));
        var extrude = Theme.Button("Extrude Outline", accent: true);
        extrude.Margin = new Thickness(0, 8, 0, 0);
        extrude.Click += (_, _) => BuildExtrusion();
        panel.Children.Add(extrude);
        panel.Children.Add(Theme.Hint("Great for block letters, logos, custom platforms and wall silhouettes. Keep the outline convex or simple concave for predictable fills."));
        return panel;
    }

    private Vec3 Origin()
    {
        if (_atSelection.IsChecked == true) return _origin;
        return Vec3.Zero;
    }

    private StructureSettings ReadSettings()
    {
        var materialIndex = Math.Clamp(_material.SelectedIndex, 0, MaterialInfo.All.Length - 1);
        return new StructureSettings
        {
            Origin = Origin(),
            Width = ParseFloat(_width.Text, 4, 0.2f, 200),
            Length = ParseFloat(_length.Text, 6, 0.2f, 200),
            Height = ParseFloat(_height.Text, 3, 0.1f, 120),
            Steps = ParseInt(_steps.Text, 8, 1, 128),
            Sides = ParseInt(_sides.Text, 12, 3, 96),
            Radius = ParseFloat(_radius.Text, 2.5f, 0.2f, 80),
            Cell = ParseFloat(_cell.Text, 1, 0.1f, 8),
            Thickness = ParseFloat(_thickness.Text, 0.35f, 0.05f, 20),
            Material = materialIndex >= 0 ? MaterialInfo.All[materialIndex].Id : 8,
            Color = Col.FromHex(_color.Text, new Col(0.72f, 0.72f, 0.72f, 1)),
            Railings = _railings.IsChecked == true,
            Hollow = _hollow.IsChecked == true,
            MirrorX = _mirrorX.IsChecked == true,
            MirrorZ = _mirrorZ.IsChecked == true,
            MirrorPivot = Origin()
        };
    }

    private StructurePlan CurrentPlan()
    {
        var kind = Enum.TryParse<StructureKind>(_kind.SelectedItem?.ToString(), out var k) ? k : StructureKind.Stairs;
        return StructureBuilder.Build(kind, ReadSettings());
    }

    private void RefreshPreview()
    {
        if (!IsLoaded) return;
        try
        {
            var plan = CurrentPlan();
            _preview.SetPlan(plan.Nodes);
            _summary.Text = plan.Summary + $"  ·  approx. {EstimateBytes(plan.Nodes)} KB in the .level";
            _status.Text = "Press Build to write the blocks. Undo restores the previous state.";
            _status.Foreground = Theme.Dim;
        }
        catch (Exception ex)
        {
            _status.Text = "Preview failed: " + ex.Message;
            _status.Foreground = Theme.Error;
        }
    }

    private static string EstimateBytes(IReadOnlyList<LevelNode> nodes)
        => ((nodes.Count * 220) / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);

    private void BuildActiveTab()
    {
        switch (_tabs?.SelectedIndex)
        {
            case 0: BuildFootprint(); break;
            case 2: BuildVoxelShape(); break;
            default: Build(); break;
        }
    }

    private void Build()
    {
        var plan = CurrentPlan();
        if (plan.Nodes.Count == 0)
        {
            _status.Text = "Nothing to build with these settings.";
            _status.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(plan.Nodes, plan.Name);
        _status.Text = plan.Summary + " written.";
        _status.Foreground = Theme.Ok;
    }

    private void BuildQuick(StructureKind kind)
    {
        var settings = ReadSettings();
        var plan = StructureBuilder.Build(kind, settings);
        if (plan.Nodes.Count == 0)
        {
            _status.Text = "Nothing to build with these settings.";
            _status.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(plan.Nodes, plan.Name);
        _status.Text = plan.Summary + " written.";
        _status.Foreground = Theme.Ok;
    }

    private void BuildExtrusion()
    {
        var points = new List<(float X, float Z)>();
        foreach (var pair in _outline.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = pair.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bits.Length < 2) continue;
            if (float.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                points.Add((x, z));
        }
        if (points.Count < 3)
        {
            _status.Text = "An outline needs at least three X,Z pairs, e.g. 0,0; 4,0; 4,3.";
            _status.Foreground = Theme.Warning;
            return;
        }
        float cell = ParseFloat(_outlineCell.Text, 0.5f, 0.1f, 4);
        float height = ParseFloat(_outlineHeight.Text, 3, 0.1f, 80);
        var settings = ReadSettings();
        settings.Cell = cell;
        settings.Depth = Math.Clamp((int)MathF.Ceiling(height / cell), 1, 64);
        var nodes = StructureBuilder.ExtrudePolygon(points, settings);
        if (nodes.Count == 0)
        {
            _status.Text = "The outline produced no blocks.";
            _status.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(nodes, "Extrusion");
        _status.Text = $"Extruded {nodes.Count} block(s) from {points.Count} outline point(s).";
        _status.Foreground = Theme.Ok;
    }

    private void BuildVoxelShape()
    {
        var shape = ShapeInfo.All[Math.Clamp(_shapeKind.SelectedIndex, 0, ShapeInfo.All.Length - 1)].Id;
        var parts = _shapeSize.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        float x = parts.Length > 0 ? ParseFloat(parts[0], 4, 0.1f, 500) : 4;
        float y = parts.Length > 1 ? ParseFloat(parts[1], 4, 0.1f, 500) : 4;
        float z = parts.Length > 2 ? ParseFloat(parts[2], 4, 0.1f, 500) : 4;
        var material = MaterialInfo.All[Math.Clamp(_shapeMaterial.SelectedIndex, 0, MaterialInfo.All.Length - 1)].Id;
        var nodes = StructureBuilder.VoxelShape(shape, Origin() + new Vec3(0, y * 0.5f, 0), new Vec3(x, y, z),
            ParseFloat(_shapeCell.Text, 0.5f, 0.1f, 4), material, new Col(0.72f, 0.72f, 0.72f, 1));
        if (nodes.Count == 0)
        {
            _status.Text = "Shape produced no blocks.";
            _status.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(nodes, ShapeInfo.Display(shape));
        _status.Text = $"{ShapeInfo.Display(shape)}: {nodes.Count} block(s) written.";
        _status.Foreground = Theme.Ok;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out int v) ? Math.Clamp(v, min, max) : fallback;

    private static float ParseFloat(string text, float fallback, float min, float max)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Click-to-draw footprint grid used by the easy Structure tab.</summary>
public sealed class FootprintCanvas : FrameworkElement
{
    private Point _mouse;
    private bool _closed;

    public List<(float X, float Z)> Points { get; } = new();
    public event Action? Changed;
    public event Action? DoubleClickCompleted;

    public FootprintCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public void Clear()
    {
        Points.Clear();
        _closed = false;
        Changed?.Invoke();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 29)), null, new Rect(0, 0, w, h));
        if (w < 20 || h < 20) return;
        double scale = Math.Min(w, h) / 18.0;
        double cx = w * 0.5, cy = h * 0.5;
        var minor = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), 1.4);
        int half = 9;
        for (int i = -half; i <= half; i++)
        {
            var pen = i == 0 ? major : minor;
            double x = cx + i * scale, y = cy + i * scale;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }

        if (Points.Count > 0)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(ToScreen(Points[0]), true, true);
                for (int i = 1; i < Points.Count; i++) ctx.LineTo(ToScreen(Points[i]), true, false);
            }
            geo.Freeze();
            if (Points.Count >= 3)
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 92, 175, 255)), new Pen(new SolidColorBrush(Color.FromRgb(105, 190, 255)), 2), geo);
            else
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(105, 190, 255)), 2), geo);

            var vertex = new SolidColorBrush(Color.FromRgb(240, 220, 130));
            foreach (var p in Points)
                dc.DrawEllipse(vertex, null, ToScreen(p), 4, 4);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 208, 90)), 1.5), ToScreen(Points[0]), 7, 7);

            if (!_closed)
            {
                var dash = new Pen(new SolidColorBrush(Color.FromArgb(180, 140, 200, 255)), 1.2)
                { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) };
                var local = ToWorld(_mouse);
                dc.DrawLine(dash, ToScreen(Points[^1]), ToScreen((local.X, local.Z)));
                if (Points.Count >= 2) dc.DrawLine(dash, ToScreen((local.X, local.Z)), ToScreen(Points[0]));
            }
        }

        var label = new FormattedText(_closed
                ? "Outline closed — press Build Drawn Shape"
                : "1 grid cell = 1 m  ·  click to add points  ·  click the amber start point to close",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10.5, new SolidColorBrush(Color.FromRgb(150, 156, 160)), 1);
        dc.DrawText(label, new Point(12, h - 24));
    }

    private Point ToScreen((float X, float Z) p)
    {
        double scale = Math.Min(ActualWidth, ActualHeight) / 18.0;
        return new Point(ActualWidth * 0.5 + p.X * scale, ActualHeight * 0.5 + p.Z * scale);
    }

    private (float X, float Z) ToWorld(Point p)
    {
        double scale = Math.Min(ActualWidth, ActualHeight) / 18.0;
        return ((float)((p.X - ActualWidth * 0.5) / scale), (float)((p.Y - ActualHeight * 0.5) / scale));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = new Point(-100, -100);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right)
        {
            if (Points.Count > 0) Points.RemoveAt(Points.Count - 1);
            _closed = false;
            Changed?.Invoke();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_closed)
        {
            e.Handled = true;
            return;
        }
        if (Points.Count >= 3)
        {
            var first = ToScreen(Points[0]);
            if ((p - first).Length <= 12)
            {
                _closed = true;
                Changed?.Invoke();
                DoubleClickCompleted?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
        var world = ToWorld(p);
        Points.Add(world);
        if (e.ClickCount >= 2 && Points.Count >= 3)
        {
            _closed = true;
            DoubleClickCompleted?.Invoke();
        }
        Changed?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }
}

/// <summary>Top-down footprint preview for generated structures.</summary>
public sealed class StructurePreview : FrameworkElement
{
    private IReadOnlyList<LevelNode> _nodes = Array.Empty<LevelNode>();
    private bool _hasPlan;
    private readonly Dictionary<LevelNode, Rect> _rects = new();

    public void SetPlan(IReadOnlyList<LevelNode> nodes)
    {
        _nodes = nodes;
        _hasPlan = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 29)), null, new Rect(0, 0, w, h));
        if (!_hasPlan || _nodes.Count == 0) return;
        var (min, max) = StructureBuilder.Bounds(_nodes);
        float spanX = MathF.Max(0.5f, max.X - min.X), spanZ = MathF.Max(0.5f, max.Z - min.Z);
        double scale = Math.Min((w - 46) / spanX, (h - 46) / spanZ);
        double ox = (w - spanX * scale) * 0.5 - min.X * scale;
        double oy = (h - spanZ * scale) * 0.5 - min.Z * scale;

        int shown = 0;
        foreach (var n in _nodes)
        {
            var (c, s) = GrabEngine.Rendering.SceneBuilder.BoundsOf(n);
            double rw = Math.Max(1.5, Math.Abs(s.X) * scale);
            double rh = Math.Max(1.5, Math.Abs(s.Z) * scale);
            var rect = new Rect(ox + (c.X - Math.Abs(s.X) * 0.5f) * scale, oy + (c.Z - Math.Abs(s.Z) * 0.5f) * scale, rw, rh);
            var height = (c.Y - min.Y) / MathF.Max(0.1f, max.Y - min.Y);
            var color = Color.FromRgb((byte)Math.Clamp(110 + height * 110, 0, 255),
                (byte)Math.Clamp(130 + height * 70, 0, 255), (byte)Math.Clamp(150 + height * 40, 0, 255));
            dc.DrawRectangle(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(90, 20, 20, 20)), 0.6), rect);
            shown++;
            if (shown > 6000) break;
        }
        var label = new FormattedText($"{_nodes.Count} block(s)   ·   {spanX:0.#} × {spanZ:0.#} m footprint",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5,
            new SolidColorBrush(Color.FromRgb(150, 156, 160)), 1);
        dc.DrawText(label, new Point(12, 10));
        var hint = new FormattedText("top-down preview  ·  darker = lower blocks",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
            new SolidColorBrush(Color.FromRgb(110, 116, 120)), 1);
        dc.DrawText(hint, new Point(12, h - 20));
    }
}

/// <summary>Lists build-validation problems and jumps to the offending object.</summary>
public sealed class ValidationWindow : Window
{
    private readonly GrabLevel _level;
    private readonly ListView _list = new();
    private readonly TextBlock _summary = Theme.Hint("");
    private readonly CheckBox _overlay = Theme.Check("Highlight in scene", true);
    private readonly Action<LevelNode> _select;
    private readonly Action<BuildReport> _report;
    private BuildReport _current = new();

    public ValidationWindow(Window owner, GrabLevel level, Action<LevelNode> select, Action<BuildReport> report)
    {
        _level = level;
        _select = select;
        _report = report;
        Owner = owner;
        Title = "Build Validation";
        Width = 860;
        Height = 560;
        MinWidth = 680;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel();
        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("BUILD VALIDATION"),
                Theme.Hint("Checks floating blocks, overlaps, dead triggers, GRAB limits, script errors and whether the finish is reachable.")
            }
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new Border { Background = Theme.PanelAlt, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        var footerRow = new DockPanel();
        footerRow.Children.Add(_summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var revalidate = Theme.Button("Revalidate", accent: true, small: true);
        revalidate.Click += (_, _) => Run();
        buttons.Children.Add(revalidate);
        var close = Theme.Button("Close", small: true);
        close.Margin = new Thickness(6, 0, 0, 0);
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Insert(0, buttons);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is BuildIssueRow row && row.Issue.Node != null) _select(row.Issue.Node);
        };
        _overlay.Click += (_, _) => _report(_current);
        var overlayBar = new Border { Background = Theme.PanelAlt, Padding = new Thickness(12, 5, 12, 5), BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
        overlayBar.Child = _overlay;
        DockPanel.SetDock(overlayBar, Dock.Top);
        root.Children.Add(overlayBar);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "", Width = 30, DisplayMemberBinding = new System.Windows.Data.Binding("Icon") });
        grid.Columns.Add(new GridViewColumn { Header = "Category", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Category") });
        grid.Columns.Add(new GridViewColumn { Header = "Problem", Width = 620, DisplayMemberBinding = new System.Windows.Data.Binding("Message") });
        _list.View = grid;
        root.Children.Add(_list);
        Content = root;
        Loaded += (_, _) => Run();
    }

    private sealed class BuildIssueRow
    {
        public BuildIssue Issue { get; init; } = null!;
        public string Icon => Issue.Kind == BuildIssueKind.Error ? "✕" : Issue.Kind == BuildIssueKind.Warning ? "⚠" : "ℹ";
        public string Category => Issue.Category;
        public string Message => Issue.Message;
    }

    public void Run()
    {
        _current = BuildValidator.Validate(_level);
        _list.ItemsSource = _current.Issues.Select(i => new BuildIssueRow { Issue = i }).ToList();
        _summary.Text = _current.Summary;
        _summary.Foreground = _current.Errors > 0 ? Theme.Error : _current.Warnings > 0 ? Theme.Warning : Theme.Ok;
        _report(_overlay.IsChecked == true ? _current : new BuildReport());
    }
}
