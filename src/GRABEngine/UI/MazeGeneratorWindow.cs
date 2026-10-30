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

public enum MazeEndpoint
{
    Start,
    Finish
}

/// <summary>
/// Maze Generator: builds corridor mazes out of ordinary GRAB blocks with a live
/// top-down preview. The start and finish markers can be dragged straight onto a cell,
/// and the maze (plus its solution) redraws as they move.
/// </summary>
public sealed class MazeGeneratorWindow : Window
{
    private readonly Vec3 _viewTarget;
    private readonly TextBox _columns = Theme.TextBox("14");
    private readonly TextBox _rows = Theme.TextBox("14");
    private readonly TextBox _rings = Theme.TextBox("5");
    private readonly TextBox _sectors = Theme.TextBox("20");
    private readonly TextBox _cell = Theme.TextBox("3");
    private readonly TextBox _height = Theme.TextBox("3");
    private readonly TextBox _thickness = Theme.TextBox("0.4");
    private readonly TextBox _bias = Theme.TextBox("35");
    private readonly ComboBox _shape = Theme.Combo(new[] { "Rectangle", "Circle (rings)" });
    private readonly ComboBox _difficulty = Theme.Combo(new[] { "Custom", "Easy", "Normal", "Hard", "Brutal" });
    private readonly ComboBox _style = Theme.Combo(new[] { "Perfect", "Braided", "Rooms", "Open", "Spiral" });
    private readonly ComboBox _finish = Theme.Combo(new[] { "Opposite side", "Centre (inside start)" });
    private readonly TextBox _loops = Theme.TextBox("15");
    private readonly TextBox _rooms = Theme.TextBox("3");
    private readonly TextBox _roomSize = Theme.TextBox("3");
    private readonly TextBox _seed = Theme.TextBox("1");
    private readonly ComboBox _wallMaterial;
    private readonly TextBox _wallColor = Theme.TextBox("#B8B8B8");
    private readonly CheckBox _floor = Theme.Check("Floor slab", true);
    private readonly ComboBox _floorMaterial;
    private readonly TextBox _floorColor = Theme.TextBox("#946B47");
    private readonly TextBox _floorThickness = Theme.TextBox("0.4");
    private readonly CheckBox _ceiling = Theme.Check("Ceiling slab", false);
    private readonly CheckBox _lights = Theme.Check("Ceiling lights", false);
    private readonly TextBox _lightSpacing = Theme.TextBox("3");
    private readonly CheckBox _openings = Theme.Check("Entrance + exit openings", true);
    private readonly CheckBox _startFinish = Theme.Check("Place Start + Finish", true);
    private readonly CheckBox _solution = Theme.Check("Show solution", true);
    private readonly CheckBox _atView = Theme.Check("Build at view target", true);
    private readonly ComboBox _rotation = Theme.Combo(new[] { "0°", "90°", "180°", "270°" });
    private readonly TextBlock _status = Theme.Hint("Drag the green S or red F marker in the preview to move the endpoints.");
    private readonly TextBlock _summary = new() { Foreground = Theme.Text, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
    private readonly MazePreviewCanvas _preview = new();
    private readonly Button _resetEndpoints = Theme.Button("Reset endpoints", small: true);

    private int _startCell = -1;
    private int _finishCell = -1;
    private MazePlan? _lastPlan;

    public event Action<MazePlan>? Built;

    public MazeGeneratorWindow(Window owner, Vec3? viewTarget = null)
    {
        _viewTarget = viewTarget ?? Vec3.Zero;
        Owner = owner;
        Title = "Maze Generator";
        Width = 1120;
        Height = 760;
        MinWidth = 900;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var wallIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8));
        _wallMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), wallIndex);
        _floorMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 4)));

        var root = new DockPanel();

        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("MAZE GENERATOR"),
                Theme.Hint("Choose a shape, style and seed. Rectangle mazes merge their walls into long runs; circle mazes build concentric rings out of blocks. Drag the S and F markers in the preview to place the endpoints — the route redraws as you drag.")
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
        var reroll = Theme.Button("New Layout");
        reroll.Margin = new Thickness(6, 0, 0, 0);
        reroll.ToolTip = "Pick a new random seed and regenerate";
        reroll.Click += (_, _) =>
        {
            _seed.Text = new Random().Next(1, 999999).ToString(CultureInfo.InvariantCulture);
            RefreshPreview();
        };
        buttons.Children.Add(reroll);
        var build = Theme.Button("Build Maze", accent: true);
        build.Margin = new Thickness(6, 0, 0, 0);
        build.Click += (_, _) => Build();
        buttons.Children.Add(build);
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Insert(0, buttons);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(BuildSettings());
        var previewHost = new Border
        {
            Margin = new Thickness(4, 8, 12, 12),
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 26)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _preview
        };
        Grid.SetColumn(previewHost, 1);
        grid.Children.Add(previewHost);
        root.Children.Add(grid);
        Content = root;

        foreach (var box in new[] { _columns, _rows, _rings, _sectors, _cell, _height, _thickness, _bias, _loops, _rooms, _roomSize, _seed, _floorThickness, _lightSpacing })
            box.LostFocus += (_, _) => RefreshPreview();
        foreach (var combo in new[] { _style, _wallMaterial, _floorMaterial, _rotation, _finish })
            combo.SelectionChanged += (_, _) => RefreshPreview();
        foreach (var check in new[] { _floor, _ceiling, _lights, _openings, _startFinish, _solution, _atView })
            check.Click += (_, _) => RefreshPreview();

        _shape.SelectionChanged += (_, _) =>
        {
            // Cell ids mean different things in a grid and in a ring, so a dragged
            // endpoint cannot survive a shape change.
            _startCell = -1;
            _finishCell = -1;
            RefreshPreview();
        };
        _difficulty.SelectionChanged += (_, _) => ApplyDifficulty();
        _resetEndpoints.Click += (_, _) =>
        {
            _startCell = -1;
            _finishCell = -1;
            RefreshPreview();
        };
        _preview.EndpointMoved += (endpoint, cell) =>
        {
            if (endpoint == MazeEndpoint.Start) _startCell = cell;
            else _finishCell = cell;
            RefreshPreview();
            if (_lastPlan != null)
            {
                string label = endpoint == MazeEndpoint.Start ? "Start" : "Finish";
                _status.Text = $"{label} moved to cell {cell} — route is {_lastPlan.Solution.Count} cells long.";
                _status.Foreground = Theme.Dim;
            }
        };

        Loaded += (_, _) => RefreshPreview();
    }

    private UIElement BuildSettings()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 2, 8, 12)
        };
        var panel = new StackPanel();

        panel.Children.Add(Theme.SectionHeader("SHAPE"));
        panel.Children.Add(Theme.Row("Layout", _shape, 104));
        panel.Children.Add(Theme.Row("Difficulty", _difficulty, 104));
        panel.Children.Add(Theme.Hint("Difficulty is a shortcut: it sets the style and corridor width. Brutal is a single spiral corridor with no branches. Pick Custom to keep your own numbers."));
        panel.Children.Add(Theme.Row("Columns", _columns, 104));
        panel.Children.Add(Theme.Row("Rows", _rows, 104));
        panel.Children.Add(Theme.Row("Rings", _rings, 104));
        panel.Children.Add(Theme.Row("Sectors", _sectors, 104));
        panel.Children.Add(Theme.Hint("Columns and Rows are the grid of a rectangle maze; Rings and Sectors are the concentric circles of a circle maze. Only the active layout is used."));

        panel.Children.Add(Theme.SectionHeader("SIZE"));
        panel.Children.Add(Theme.Row("Cell (m)", _cell, 104));
        panel.Children.Add(Theme.Hint("One cell is one corridor wide. A 14×14 maze with 3 m cells is 42×42 m; a 5-ring circle with 3 m cells is about 33 m across."));

        panel.Children.Add(Theme.SectionHeader("STYLE"));
        panel.Children.Add(Theme.Row("Layout style", _style, 104));
        panel.Children.Add(Theme.Row("Straight %", _bias, 104));
        panel.Children.Add(Theme.Row("Loops %", _loops, 104));
        panel.Children.Add(Theme.Row("Rooms", _rooms, 104));
        panel.Children.Add(Theme.Row("Room size", _roomSize, 104));
        panel.Children.Add(Theme.Hint("Straight % keeps corridors running instead of turning every cell. Loops % applies to Braided (opens dead ends) and Open (opens many walls). Rooms carves chambers in both layouts. Spiral ignores all of these and carves one unbroken route."));

        panel.Children.Add(Theme.SectionHeader("START & FINISH"));
        panel.Children.Add(Theme.Row("Finish", _finish, 104));
        panel.Children.Add(_solution);
        panel.Children.Add(_resetEndpoints);
        panel.Children.Add(Theme.Hint("In the preview, drag the green S or red F marker onto any cell — the maze and the highlighted route update while you drag. 'Centre (inside start)' boxes the finish in the middle of the maze."));

        panel.Children.Add(Theme.SectionHeader("WALLS"));
        panel.Children.Add(Theme.Row("Height", _height, 104));
        panel.Children.Add(Theme.Row("Thickness", _thickness, 104));
        panel.Children.Add(Theme.Row("Material", _wallMaterial, 104));
        panel.Children.Add(Theme.Row("Colour", ColorInputs.Field(_wallColor, _ => RefreshPreview()), 104));
        panel.Children.Add(Theme.Hint("Only Default Colored uses the wall colour; GRAB's ice, lava and wood textures ignore it."));

        panel.Children.Add(Theme.SectionHeader("GROUND & LIGHT"));
        panel.Children.Add(_floor);
        panel.Children.Add(Theme.Row("Floor mat.", _floorMaterial, 104));
        panel.Children.Add(Theme.Row("Floor colour", ColorInputs.Field(_floorColor, _ => RefreshPreview()), 104));
        panel.Children.Add(Theme.Row("Floor thick.", _floorThickness, 104));
        panel.Children.Add(_ceiling);
        panel.Children.Add(_lights);
        panel.Children.Add(Theme.Row("Light every", _lightSpacing, 104));
        panel.Children.Add(Theme.Hint("One slab spans the whole maze (a round slab for circle mazes). Lights are placed on a grid of cells; they only matter indoors or at night, and cost one object each."));

        panel.Children.Add(Theme.SectionHeader("PLACEMENT"));
        panel.Children.Add(_openings);
        panel.Children.Add(_startFinish);
        panel.Children.Add(_atView);
        panel.Children.Add(Theme.Row("Rotation", _rotation, 104));
        panel.Children.Add(Theme.Row("Seed", _seed, 104));
        panel.Children.Add(Theme.Hint("The seed is the layout; the same seed always rebuilds the same maze. Openings are cut in the edge next to the start and finish. Start + Finish move the level's existing points instead of adding duplicates."));

        panel.Children.Add(Theme.SectionHeader("PREVIEW"));
        panel.Children.Add(_summary);
        scroll.Content = panel;
        return scroll;
    }

    /// <summary>Development aid for the --shot-window maze-circle screenshot.</summary>
    internal void ShowCircleForScreenshot()
    {
        _shape.SelectedIndex = 1;
        _style.SelectedIndex = 0;
        RefreshPreview();
    }

    private void ApplyDifficulty()
    {
        switch (_difficulty.SelectedIndex)
        {
            case 1:
                _style.SelectedIndex = 0;
                _bias.Text = "55";
                _loops.Text = "15";
                break;
            case 2:
                _style.SelectedIndex = 0;
                _bias.Text = "35";
                _loops.Text = "0";
                break;
            case 3:
                _style.SelectedIndex = 0;
                _bias.Text = "12";
                _loops.Text = "0";
                break;
            case 4:
                _style.SelectedIndex = 4;
                _bias.Text = "0";
                _loops.Text = "0";
                break;
        }
        RefreshPreview();
    }

    private MazeSettings ReadSettings()
    {
        int materialIndex = Math.Clamp(_wallMaterial.SelectedIndex, 0, MaterialInfo.All.Length - 1);
        int floorIndex = Math.Clamp(_floorMaterial.SelectedIndex, 0, MaterialInfo.All.Length - 1);
        return new MazeSettings
        {
            Shape = _shape.SelectedIndex == 1 ? MazeShape.Circle : MazeShape.Rectangle,
            Columns = ParseInt(_columns.Text, 14, 2, MazeBuilder.MaxCells),
            Rows = ParseInt(_rows.Text, 14, 2, MazeBuilder.MaxCells),
            Rings = ParseInt(_rings.Text, 5, 2, 40),
            Sectors = ParseInt(_sectors.Text, 20, 6, 72),
            Cell = ParseFloat(_cell.Text, 3, 1, 24),
            WallHeight = ParseFloat(_height.Text, 3, 0.5f, 40),
            WallThickness = ParseFloat(_thickness.Text, 0.4f, 0.1f, 3),
            StraightBias = ParseFloat(_bias.Text, 35, 0, 95) / 100f,
            Style = _style.SelectedIndex switch
            {
                1 => MazeStyle.Braided,
                2 => MazeStyle.Rooms,
                3 => MazeStyle.Open,
                4 => MazeStyle.Spiral,
                _ => MazeStyle.Perfect
            },
            FinishPlacement = _finish.SelectedIndex == 1 ? MazeFinishPlacement.Centre : MazeFinishPlacement.Opposite,
            StartCell = _startCell,
            FinishCell = _finishCell,
            LoopPercent = ParseInt(_loops.Text, 15, 0, 100),
            Rooms = ParseInt(_rooms.Text, 3, 0, 40),
            RoomSize = ParseInt(_roomSize.Text, 3, 2, 12),
            Seed = ParseInt(_seed.Text, 1, 1, 999999),
            WallMaterial = MaterialInfo.All[materialIndex].Id,
            WallColor = Col.FromHex(_wallColor.Text, new Col(0.72f, 0.72f, 0.72f, 1f)),
            Floor = _floor.IsChecked == true,
            FloorThickness = ParseFloat(_floorThickness.Text, 0.4f, 0.1f, 4),
            FloorMaterial = MaterialInfo.All[floorIndex].Id,
            FloorColor = Col.FromHex(_floorColor.Text, new Col(0.58f, 0.42f, 0.28f, 1f)),
            Ceiling = _ceiling.IsChecked == true,
            CeilingMaterial = MaterialInfo.All[materialIndex].Id,
            CeilingColor = Col.FromHex(_wallColor.Text, new Col(0.72f, 0.72f, 0.72f, 1f)),
            Lights = _lights.IsChecked == true,
            LightSpacing = ParseInt(_lightSpacing.Text, 3, 1, 24),
            Openings = _openings.IsChecked == true,
            PlaceStartFinish = _startFinish.IsChecked == true,
            Origin = _atView.IsChecked == true ? _viewTarget : Vec3.Zero,
            Rotation = Quat.FromEulerDegrees(new Vec3(0, _rotation.SelectedIndex * 90f, 0))
        };
    }

    private void RefreshPreview()
    {
        if (!IsLoaded) return;
        try
        {
            _preview.ShowSolution = _solution.IsChecked == true;
            var plan = MazeBuilder.Build(ReadSettings());
            _lastPlan = plan;
            _preview.SetPlan(plan);
            _summary.Text = plan.Summary;
            _status.Text = plan.ObjectCount > 4000
                ? $"Large maze: {plan.ObjectCount} objects. Consider bigger cells or fewer loops before building."
                : "Drag the green S or red F marker in the preview to move the endpoints.";
            _status.Foreground = plan.ObjectCount > 4000 ? Theme.Warning : Theme.Dim;
        }
        catch (Exception ex)
        {
            _status.Text = "Preview failed: " + ex.Message;
            _status.Foreground = Theme.Error;
        }
    }

    private void Build()
    {
        MazePlan plan;
        try
        {
            plan = MazeBuilder.Build(ReadSettings());
        }
        catch (Exception ex)
        {
            _status.Text = "Generation failed: " + ex.Message;
            _status.Foreground = Theme.Error;
            return;
        }
        if (plan.Blocks.Count == 0)
        {
            _status.Text = "Nothing to build with these settings.";
            _status.Foreground = Theme.Warning;
            return;
        }
        Built?.Invoke(plan);
        _summary.Text = plan.Summary;
        _status.Text = plan.Summary + " written.";
        _status.Foreground = Theme.Ok;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out int v) ? Math.Clamp(v, min, max) : fallback;

    private static float ParseFloat(string text, float fallback, float min, float max)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>
/// Top-down preview of a generated maze with draggable start/finish markers and the
/// shortest route drawn through the corridors. Works for grid and ring mazes.
/// </summary>
public sealed class MazePreviewCanvas : FrameworkElement
{
    private MazePlan? _plan;
    private MazeEndpoint? _dragging;
    private double _scale = 1;
    private double _ox;
    private double _oy;

    public bool ShowSolution { get; set; } = true;

    public event Action<MazeEndpoint, int>? EndpointMoved;

    public MazePreviewCanvas()
    {
        Cursor = Cursors.Arrow;
    }

    public void SetPlan(MazePlan plan)
    {
        _plan = plan;
        InvalidateVisual();
    }

    private void UpdateTransform()
    {
        if (_plan == null) return;
        double w = ActualWidth, h = ActualHeight;
        const double pad = 26;
        double spanX = Math.Max(0.001, _plan.MaxX - _plan.MinX);
        double spanZ = Math.Max(0.001, _plan.MaxZ - _plan.MinZ);
        _scale = Math.Min((w - pad * 2) / spanX, (h - pad * 2 - 26) / spanZ);
        if (_scale <= 0) _scale = 0.1;
        _ox = (w - spanX * _scale) * 0.5 - _plan.MinX * _scale;
        _oy = (h - spanZ * _scale) * 0.5 - 10 - _plan.MinZ * _scale;
    }

    private Point ToScreen(float x, float z) => new(_ox + x * _scale, _oy + z * _scale);

    private Point MarkerPoint(int cell)
    {
        if (_plan == null) return new Point(0, 0);
        var (x, z) = _plan.Centre(cell);
        return ToScreen(x, z);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 29)), null, new Rect(0, 0, w, h));
        var plan = _plan;
        if (plan == null || plan.CellCount <= 0 || w < 60 || h < 60) return;
        UpdateTransform();

        var floor = new SolidColorBrush(Color.FromRgb(44, 47, 50));
        if (plan.Shape == MazeShape.Circle)
        {
            var c = new Point(_ox, _oy);
            double radius = (plan.HubRadius + plan.Columns * plan.Cell) * _scale;
            dc.DrawEllipse(floor, null, c, radius, radius);
            DrawRing(dc, c, radius, plan.HubRadius * _scale, plan.Columns);
        }
        else
        {
            var rect = new Rect(_ox, _oy, (plan.MaxX - plan.MinX) * _scale, (plan.MaxZ - plan.MinZ) * _scale);
            dc.DrawRectangle(floor, null, rect);
            var cellLine = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
            if (_scale > 8)
            {
                for (int x = 1; x < plan.Columns; x++)
                    dc.DrawLine(cellLine,
                        new Point(rect.Left + x * plan.Cell * _scale, rect.Top),
                        new Point(rect.Left + x * plan.Cell * _scale, rect.Bottom));
                for (int z = 1; z < plan.Rows; z++)
                    dc.DrawLine(cellLine,
                        new Point(rect.Left, rect.Top + z * plan.Cell * _scale),
                        new Point(rect.Right, rect.Top + z * plan.Cell * _scale));
            }
        }

        // Solution under the walls so wall blocks still read as solid.
        if (ShowSolution && plan.Solution.Count > 1)
        {
            var pathPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 90, 190, 245)), Math.Max(2.2, _scale * 0.16))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool first = true;
                foreach (int cell in plan.Solution)
                {
                    var (x, z) = plan.Centre(cell);
                    var p = ToScreen(x, z);
                    if (first) { ctx.BeginFigure(p, false, false); first = false; }
                    else ctx.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pathPen, geometry);
        }

        var wallPen = new Pen(new SolidColorBrush(Color.FromRgb(206, 208, 210)), Math.Max(2.0, _scale * 0.14))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        foreach (var (x0, z0, x1, z1) in plan.PreviewWalls)
            dc.DrawLine(wallPen, ToScreen(x0, z0), ToScreen(x1, z1));

        DrawMarker(dc, plan.StartCell, MazeEndpoint.Start);
        DrawMarker(dc, plan.FinishCell, MazeEndpoint.Finish);

        string footer = plan.Shape == MazeShape.Circle
            ? $"{plan.Columns} ring(s) × {plan.Rows} sector(s)  ·  {plan.Cell:0.#} m cells  ·  route {Math.Max(0, plan.Solution.Count - 1)} steps"
            : $"{plan.Columns} × {plan.Rows} cells  ·  {plan.Cell:0.#} m per cell  ·  route {Math.Max(0, plan.Solution.Count - 1)} steps";
        var label = new FormattedText(footer, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10.5, new SolidColorBrush(Color.FromRgb(150, 156, 160)), 1);
        dc.DrawText(label, new Point(12, h - 22));
    }

    private static void DrawRing(DrawingContext dc, Point centre, double outer, double hub, int rings)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
        for (int r = 1; r <= rings; r++)
        {
            double radius = hub + (outer - hub) * r / rings;
            dc.DrawEllipse(null, pen, centre, radius, radius);
        }
        for (int s = 0; s < 12; s++)
        {
            double a = s * Math.PI * 2 / 12;
            var p0 = new Point(centre.X + Math.Cos(a) * hub, centre.Y + Math.Sin(a) * hub);
            var p1 = new Point(centre.X + Math.Cos(a) * outer, centre.Y + Math.Sin(a) * outer);
            dc.DrawLine(pen, p0, p1);
        }
    }

    private void DrawMarker(DrawingContext dc, int cell, MazeEndpoint endpoint)
    {
        if (_plan == null) return;
        var p = MarkerPoint(cell);
        bool start = endpoint == MazeEndpoint.Start;
        var fill = new SolidColorBrush(start ? Color.FromRgb(110, 220, 130) : Color.FromRgb(240, 130, 130));
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(20, 22, 24)), 2);
        double radius = Math.Max(7, _plan.Cell * _scale * 0.3);
        dc.DrawEllipse(fill, ring, p, radius, radius);
        var text = new FormattedText(start ? "S" : "F", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            radius * 1.1, Brushes.Black, 1);
        dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_plan == null) return;
        var p = e.GetPosition(this);
        var start = MarkerPoint(_plan.StartCell);
        var finish = MarkerPoint(_plan.FinishCell);
        if ((p - start).Length <= 15) _dragging = MazeEndpoint.Start;
        else if ((p - finish).Length <= 15) _dragging = MazeEndpoint.Finish;
        else return;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plan = _plan;
        if (plan == null) return;
        var p = e.GetPosition(this);

        if (_dragging == null)
        {
            bool near = (p - MarkerPoint(plan.StartCell)).Length <= 15 || (p - MarkerPoint(plan.FinishCell)).Length <= 15;
            Cursor = near ? Cursors.Hand : Cursors.Arrow;
            return;
        }

        var local = new Point((p.X - _ox) / _scale, (p.Y - _oy) / _scale);
        int cell = plan.CellAt((float)local.X, (float)local.Y);
        if (cell < 0) return;
        int current = _dragging == MazeEndpoint.Start ? plan.StartCell : plan.FinishCell;
        if (cell == current) return;
        EndpointMoved?.Invoke(_dragging.Value, cell);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging != null)
        {
            _dragging = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
