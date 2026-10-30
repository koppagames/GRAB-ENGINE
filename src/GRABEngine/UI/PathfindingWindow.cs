using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// Pathfinding Studio: pick a finder and a target, tune a real 3D A* agent, preview
/// the route live, and drive it automatically in Play mode. The preview recomputes
/// whenever the target moves or a setting changes, so the route tracks moving objects.
/// </summary>
public sealed class PathfindingWindow : Window
{
    private readonly GrabLevel _level;
    private readonly Func<LevelNode?> _selectedNode;
    private readonly Action<PathfindingResult?, bool> _showPath;
    private readonly Action _changed;
    private readonly Action _startPlay;
    private readonly Action<LevelNode>? _baked;
    private readonly DispatcherTimer _timer;
    private readonly ComboBox _finderCombo = Theme.Combo(Array.Empty<string>());
    private readonly ComboBox _targetCombo = Theme.Combo(Array.Empty<string>());
    private readonly ComboBox _traversalCombo = Theme.Combo(new[] { "Ground — walk, step, drop", "Fly — free 3D space" });
    private readonly TextBlock _finderInfo = Theme.Hint("No finder selected.");
    private readonly TextBlock _targetInfo = Theme.Hint("No target selected.");
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = Theme.Text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 5)
    };
    private readonly TextBlock _distance = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
    private readonly TextBlock _waypoints = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
    private readonly TextBlock _visited = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
    private readonly TextBlock _time = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
    private readonly TextBlock _gridInfo = Theme.Hint("—");
    private readonly CheckBox _showEditor = Theme.Check("Live route in the editor", true);
    private readonly CheckBox _showPlay = Theme.Check("Show route while playing", true);
    private readonly CheckBox _showDots = Theme.Check("Show waypoint markers", true);
    private readonly CheckBox _showNav = Theme.Check("Show nav mesh and watched walls", false);
    private readonly CheckBox _follow = Theme.Check("Auto-follow target in Play mode", true);
    private readonly CheckBox _bakeLoop = Theme.Check("Loop the baked route", false);
    private readonly CheckBox _liveBake = Theme.Check("Live re-planning at runtime (reads moved walls and the current target)", true);
    private readonly List<ObjectItem> _items = new();
    private readonly List<Action> _refreshSliders = new();
    private bool _loading;
    private bool _closed;
    private bool _settingsDirty;
    private bool _firstObjectLoad = true;
    private string _lastQuery = "";
    private DateTime _lastPreview = DateTime.MinValue;

    public PathfindingWindow(Window owner, GrabLevel level, Func<LevelNode?> selectedNode,
        Action<PathfindingResult?, bool> showPath, Action changed, Action startPlay,
        Action<LevelNode>? baked = null)
    {
        _level = level;
        _selectedNode = selectedNode;
        _showPath = showPath;
        _changed = changed;
        _startPlay = startPlay;
        _baked = baked;

        Title = "Pathfinding Studio";
        Width = 1080;
        Height = 740;
        MinWidth = 850;
        MinHeight = 560;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;

        Content = BuildUi();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(240), DispatcherPriority.Background,
            (_, _) => PreviewTick(), Dispatcher);
        Loaded += (_, _) =>
        {
            LoadObjectLists();
            LoadSettings();
            RefreshSummary();
            Preview(force: true);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            SaveSettings();
            _showPath(null, false);
            if (_settingsDirty) _changed();
        };
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel();
        var title = new StackPanel();
        title.Children.Add(Theme.Heading("PATHFINDING STUDIO"));
        title.Children.Add(Theme.Hint("A real 3D A* navmesh for GRAB ENGINE. Pick any two objects, press Find Path, then play to chase the target in real time — or bake it to a Code Block: Bake Live Nav keeps re-planning in the exported .level, Bake Route freezes the current path."));
        header.Children.Add(title);
        var close = Theme.Button("Close", small: true);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        root.Children.Add(Theme.AccentBar(header));

        var body = new Grid { Margin = new Thickness(0, 12, 0, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(332) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 350 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var left = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 12, 0) };
        left.Content = BuildObjectsPanel();
        body.Children.Add(left);

        var center = BuildRoutePanel();
        Grid.SetColumn(center, 1);
        body.Children.Add(center);

        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0) };
        right.Content = BuildSettingsPanel();
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var find = Theme.Button("⌕  Find Path", accent: true);
        find.Click += (_, _) => Preview(force: true);
        actions.Children.Add(find);
        var clear = Theme.Button("Clear Route");
        clear.Margin = new Thickness(6, 0, 0, 0);
        clear.Click += (_, _) => { _showPath(null, false); _status.Text = "Route cleared."; _status.Foreground = Theme.Dim; };
        actions.Children.Add(clear);
        var bake = Theme.Button("⬢  Bake Live Nav");
        bake.Margin = new Thickness(12, 0, 0, 0);
        bake.ToolTip = "Compiles a live runtime planner to a real GASM Code Block. The exported .level re-plans around moved walls and a moving target.";
        bake.Click += (_, _) => BakePath();
        actions.Children.Add(bake);
        var play = Theme.Button("▶  Test in Play Mode");
        play.Margin = new Thickness(18, 0, 0, 0);
        play.Tag = "green";
        play.Click += (_, _) =>
        {
            SaveSettings();
            _showPath(null, false);
            _startPlay();
        };
        actions.Children.Add(play);
        footer.Children.Add(actions);
        var hint = new TextBlock
        {
            Text = "The finder follows the target's current position, not its starting position. Repathing is automatic.",
            Foreground = Theme.Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 360
        };
        DockPanel.SetDock(hint, Dock.Right);
        footer.Children.Add(hint);
        root.Children.Add(footer);
        Grid.SetRow(footer, 2);
        return root;
    }

    private UIElement BuildObjectsPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.SectionHeader("1 · CHOOSE OBJECTS"));
        panel.Children.Add(Theme.Hint("Finder is the object that moves. Target is the object it should reach. Both can be any scene object, including blocks, signs, lights and code blocks."));

        panel.Children.Add(Theme.FieldLabel("FINDER (MOVES)"));
        _finderCombo.Margin = new Thickness(0, 2, 0, 3);
        _finderCombo.SelectionChanged += (_, _) => OnObjectChanged();
        panel.Children.Add(_finderCombo);
        var useFinder = Theme.Button("Use current selection as Finder", small: true);
        useFinder.Margin = new Thickness(0, 0, 0, 2);
        useFinder.Click += (_, _) => UseSelection(isFinder: true);
        panel.Children.Add(useFinder);
        panel.Children.Add(_finderInfo);

        panel.Children.Add(Theme.FieldLabel("TARGET (DESTINATION)"));
        _targetCombo.Margin = new Thickness(0, 2, 0, 3);
        _targetCombo.SelectionChanged += (_, _) => OnObjectChanged();
        panel.Children.Add(_targetCombo);
        var useTarget = Theme.Button("Use current selection as Target", small: true);
        useTarget.Margin = new Thickness(0, 0, 0, 2);
        useTarget.Click += (_, _) => UseSelection(isFinder: false);
        panel.Children.Add(useTarget);
        panel.Children.Add(_targetInfo);

        var refresh = Theme.Button("Refresh object list", small: true);
        refresh.Margin = new Thickness(0, 5, 0, 0);
        refresh.Click += (_, _) => LoadObjectLists();
        panel.Children.Add(refresh);

        panel.Children.Add(Theme.SectionHeader("ROUTE PREVIEW"));
        _showEditor.Checked += (_, _) => { if (_loading) return; Settings.ShowPathInEditor = true; SettingsChanged(); };
        _showEditor.Unchecked += (_, _) => { if (_loading) return; Settings.ShowPathInEditor = false; _showPath(null, false); SettingsChanged(); };
        panel.Children.Add(_showEditor);
        _showDots.Checked += (_, _) => { if (_loading) return; Settings.ShowWaypoints = true; SettingsChanged(); };
        _showDots.Unchecked += (_, _) => { if (_loading) return; Settings.ShowWaypoints = false; SettingsChanged(); };
        panel.Children.Add(_showDots);
        panel.Children.Add(Theme.Hint("The route is drawn in the viewport with a green start marker and an orange goal ring. Waypoints are actual standable cells."));
        _showNav.Checked += (_, _) => { if (_loading) return; Settings.ShowNavMesh = true; SettingsChanged(); };
        _showNav.Unchecked += (_, _) => { if (_loading) return; Settings.ShowNavMesh = false; SettingsChanged(); };
        panel.Children.Add(_showNav);
        panel.Children.Add(Theme.Hint("Nav mesh draws every walkable cell around the route as a translucent blue tile, and outlines the blocking objects a baked live planner would watch in orange."));

        panel.Children.Add(Theme.SectionHeader("EXPORT TO GRAB"));
        panel.Children.Add(Theme.Hint("Bake Live Nav creates a real Code Block that reads the current target and wall positions every frame. When the straight line is blocked it picks the end of that wall nearest the target, walks the face to that end and rounds it, then heads for the target again, so a wall that moves or a target that runs is re-planned inside retail GRAB. Turn live re-planning off to bake a fixed route instead."));
        panel.Children.Add(SliderRow("Blocking Objects", 1, 96, 1, 0, () => Settings.BlockingObjects, v => Settings.BlockingObjects = (int)v, ""));
        panel.Children.Add(Theme.Hint("Blocking Objects is how many walls the live planner watches. GRAB does not cap the connections, so raise it for dense levels; each object costs about 160 GASM instructions that the game runs every frame. Only objects inside the agent's height band count, nearest to the straight line first."));
        panel.Children.Add(_liveBake);
        panel.Children.Add(_bakeLoop);
        panel.Children.Add(Theme.Hint("Loop only applies to a fixed baked route. Live Nav keeps following the target indefinitely."));

        panel.Children.Add(Theme.SectionHeader("PLAY MODE"));
        _follow.Checked += (_, _) => { if (!_loading) { Settings.AutoFollowInPlay = true; _settingsDirty = true; } };
        _follow.Unchecked += (_, _) => { if (!_loading) { Settings.AutoFollowInPlay = false; _settingsDirty = true; } };
        panel.Children.Add(_follow);
        _showPlay.Checked += (_, _) => { if (!_loading) { Settings.ShowPathInPlay = true; _settingsDirty = true; } };
        _showPlay.Unchecked += (_, _) => { if (!_loading) { Settings.ShowPathInPlay = false; _settingsDirty = true; } };
        panel.Children.Add(_showPlay);
        panel.Children.Add(Theme.Hint("In Play mode the finder is moved by the agent controller. The scene is restored when you stop."));
        return panel;
    }

    private UIElement BuildRoutePanel()
    {
        var panel = new StackPanel();
        var card = Theme.Card(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "ROUTE", FontSize = 10, Foreground = Theme.Dim, FontWeight = FontWeights.SemiBold },
                _status
            }
        });
        panel.Children.Add(card);

        var stats = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        AddStat(stats, 0, 0, "DISTANCE", _distance);
        AddStat(stats, 0, 1, "WAYPOINTS", _waypoints);
        AddStat(stats, 1, 0, "CELLS SEARCHED", _visited);
        AddStat(stats, 1, 1, "SOLVE TIME", _time);
        panel.Children.Add(stats);

        panel.Children.Add(Theme.SectionHeader("HOW TO USE IT"));
        panel.Children.Add(Step("1", "Pick the moving object as Finder and the destination as Target. You can also select an object in the viewport and press the Use current selection button."));
        panel.Children.Add(Step("2", "Press Find Path. The orange route appears through walls; the green cube is the start and the orange ring is the goal."));
        panel.Children.Add(Step("3", "Press Test in Play Mode. The finder steers along the route and replans every few frames, so a moving target can be followed continuously."));
        panel.Children.Add(Step("4", "Tune Agent Radius, Step Height and Max Drop to match the object's real size. A wider agent automatically keeps a wider corridor."));
        panel.Children.Add(Theme.SectionHeader("WHAT THE PATHFINDER DOES"));
        panel.Children.Add(Bullet("A* searches real 3D voxels, so bridges, stairs and multiple floors work without a separate navmesh."));
        panel.Children.Add(Bullet("Diagonal movement is octile-costed and corner-cutting is blocked, producing the shortest legal route instead of a staircase."));
        panel.Children.Add(Bullet("String-pulling smoothing removes redundant grid turns while line-of-sight stays collision-free."));
        panel.Children.Add(Bullet("Live replanning uses the target's current position with optional velocity prediction, so chasing a moving object does not lock onto its spawn point."));
        panel.Children.Add(Bullet("The navigation grid automatically scales up if the level is too large for the configured cell budget, instead of hanging."));
        panel.Children.Add(_gridInfo);
        return panel;
    }

    private UIElement BuildSettingsPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.SectionHeader("AGENT"));
        panel.Children.Add(Theme.Row("Traversal", _traversalCombo, 92));
        _traversalCombo.SelectionChanged += (_, _) => { if (_loading) return; Settings.Traversal = _traversalCombo.SelectedIndex == 1 ? PathfindingTraversal.Fly : PathfindingTraversal.Ground; SettingsChanged(); };
        panel.Children.Add(SliderRow("Cell Size", 0.1, 2, 0.05, 2, () => Settings.CellSize, v => Settings.CellSize = (float)v, " m"));
        panel.Children.Add(SliderRow("Radius", 0.05, 3, 0.05, 2, () => Settings.AgentRadius, v => Settings.AgentRadius = (float)v, " m"));
        panel.Children.Add(SliderRow("Height", 0.3, 4, 0.1, 1, () => Settings.AgentHeight, v => Settings.AgentHeight = (float)v, " m"));
        panel.Children.Add(SliderRow("Step Up", 0, 3, 0.05, 2, () => Settings.MaxStepHeight, v => Settings.MaxStepHeight = (float)v, " m"));
        panel.Children.Add(SliderRow("Drop Down", 0, 12, 0.1, 1, () => Settings.MaxDropHeight, v => Settings.MaxDropHeight = (float)v, " m"));

        panel.Children.Add(Theme.SectionHeader("SEARCH"));
        panel.Children.Add(Check("Allow diagonal paths", true, v => Settings.AllowDiagonal = v));
        panel.Children.Add(Check("Block corner cutting", true, v => Settings.AvoidCornerCutting = v));
        panel.Children.Add(Check("Smooth with line of sight", true, v => Settings.SmoothPath = v));
        panel.Children.Add(SliderRow("Wall Hug Penalty", 0, 0.5, 0.01, 2, () => Settings.WallHugPenalty, v => Settings.WallHugPenalty = (float)v, ""));
        panel.Children.Add(SliderRow("Max Iterations", 1000, 600000, 1000, 0, () => Settings.MaxIterations, v => Settings.MaxIterations = (int)v, ""));
        panel.Children.Add(Theme.Hint("Wall Hug Penalty nudges routes toward open space without changing their length much."));

        panel.Children.Add(Theme.SectionHeader("LIVE FOLLOW"));
        panel.Children.Add(SliderRow("Repath", 0.03, 1, 0.01, 2, () => Settings.RepathInterval, v => Settings.RepathInterval = (float)v, " s"));
        panel.Children.Add(SliderRow("Prediction", 0, 1.5, 0.01, 2, () => Settings.TargetPrediction, v => Settings.TargetPrediction = (float)v, " s"));
        panel.Children.Add(SliderRow("Stop Distance", 0.05, 3, 0.05, 2, () => Settings.StopDistance, v => Settings.StopDistance = (float)v, " m"));
        panel.Children.Add(SliderRow("Speed", 0.2, 30, 0.1, 1, () => Settings.Speed, v => Settings.Speed = (float)v, " m/s"));
        panel.Children.Add(SliderRow("Acceleration", 1, 100, 1, 0, () => Settings.Acceleration, v => Settings.Acceleration = (float)v, " m/s²"));
        panel.Children.Add(SliderRow("Turn Speed", 30, 1440, 10, 0, () => Settings.TurnSpeed, v => Settings.TurnSpeed = (float)v, " °/s"));
        panel.Children.Add(Check("Rotate to face movement", true, v => Settings.FaceMovement = v));
        panel.Children.Add(SliderRow("Waypoint Radius", 0.05, 1.5, 0.01, 2, () => Settings.WaypointTolerance, v => Settings.WaypointTolerance = (float)v, " m"));
        panel.Children.Add(SliderRow("Rebuild Grid", 0.15, 5, 0.05, 2, () => Settings.NavRebuildInterval, v => Settings.NavRebuildInterval = (float)v, " s"));
        panel.Children.Add(Theme.Hint("Prediction aims where the target will be in that many seconds. Use 0 for exact current position chasing. Rebuild Grid controls how often moving blocks are re-scanned; static levels can use a larger value."));
        return panel;
    }

    private PathfindingSettings Settings => _level.Pathfinding ??= new PathfindingSettings();

    private void AddStat(Grid grid, int row, int column, string label, TextBlock value)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 59, 64)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8, 5, 8, 5),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label, FontSize = 9, Foreground = Theme.Dim },
                    value
                }
            }
        };
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        if (row >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(card);
    }

    private static Border Step(string number, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var index = new TextBlock
        {
            Text = number, FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(index);
        var body = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Text,
            FontSize = 11.5, Margin = new Thickness(3, 0, 0, 0)
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return Theme.Card(grid, new Thickness(6));
    }

    private static TextBlock Bullet(string text) => new()
    {
        Text = "•  " + text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 2)
    };

    private CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = Theme.Check(text, value);
        box.Checked += (_, _) => { if (!_loading) changed(true); };
        box.Unchecked += (_, _) => { if (!_loading) changed(false); };
        return box;
    }

    private FrameworkElement SliderRow(string label, double min, double max, double step,
        int decimals, Func<double> read, Action<double> write, string suffix)
    {
        var value = new TextBlock
        {
            Text = read().ToString("0." + new string('#', Math.Max(1, decimals))) + suffix,
            Foreground = Theme.Text,
            FontSize = 11,
            Width = 56,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(read(), min, max),
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0 && (max - min) / step < 400,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, e) =>
        {
            if (_loading) return;
            write(e.NewValue);
            value.Text = e.NewValue.ToString("0." + new string('#', Math.Max(1, decimals))) + suffix;
            SettingsChanged();
        };
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1), MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.Children.Add(new TextBlock
        {
            Text = label, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        _refreshSliders.Add(() =>
        {
            double current = Math.Clamp(read(), min, max);
            slider.Value = current;
            value.Text = current.ToString("0." + new string('#', Math.Max(1, decimals))) + suffix;
        });
        return grid;
    }

    private void LoadObjectLists()
    {
        _loading = true;
        try
        {
            _items.Clear();
            foreach (var node in _level.AllNodes()
                         .Where(n => !n.Generated && !n.ScriptCreated)
                         .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(new ObjectItem(node.EditorId, node.DisplayName, NodeKindInfo.Display(node.Kind)));
            }
            _items.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
            _finderCombo.ItemsSource = _items;
            _finderCombo.DisplayMemberPath = "Display";
            _targetCombo.ItemsSource = _items;
            _targetCombo.DisplayMemberPath = "Display";

            var settings = Settings;
            _finderCombo.SelectedItem = _items.FirstOrDefault(i => i.Id == settings.FinderId);
            _targetCombo.SelectedItem = _items.FirstOrDefault(i => i.Id == settings.TargetId);
            if (_finderCombo.SelectedItem == null && _items.Count > 0)
            {
                var sel = _selectedNode();
                _finderCombo.SelectedItem = _items.FirstOrDefault(i => i.Id == sel?.EditorId) ?? _items[0];
            }
            if (_targetCombo.SelectedItem == null && _items.Count > 1)
                _targetCombo.SelectedItem = _items.FirstOrDefault(i => i.Id != (_finderCombo.SelectedItem as ObjectItem)?.Id);
        }
        finally { _loading = false; }
        OnObjectChanged();
        if (_firstObjectLoad)
        {
            _firstObjectLoad = false;
            // Opening the studio must not mark an untouched project dirty.
            _settingsDirty = false;
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = Settings;
            s.Clamp();
            _traversalCombo.SelectedIndex = s.Traversal == PathfindingTraversal.Fly ? 1 : 0;
            _showEditor.IsChecked = s.ShowPathInEditor;
            _showPlay.IsChecked = s.ShowPathInPlay;
            _showDots.IsChecked = s.ShowWaypoints;
            _showNav.IsChecked = s.ShowNavMesh;
            _follow.IsChecked = s.AutoFollowInPlay;
            RefreshAllSliderValues();
        }
        finally { _loading = false; }
    }

    private void RefreshAllSliderValues()
    {
        foreach (var refresh in _refreshSliders) refresh();
    }

    private void OnObjectChanged()
    {
        if (_loading) return;
        var finder = _finderCombo.SelectedItem as ObjectItem;
        var target = _targetCombo.SelectedItem as ObjectItem;
        Settings.FinderId = finder?.Id ?? "";
        Settings.TargetId = target?.Id ?? "";
        _finderInfo.Text = finder == null ? "No finder selected." : "Selected: " + finder.Display;
        _targetInfo.Text = target == null ? "No target selected." : "Selected: " + target.Display;
        SettingsChanged();
    }

    private void UseSelection(bool isFinder)
    {
        var node = _selectedNode();
        if (node == null)
        {
            _status.Text = "Nothing is selected. Select an object in the viewport or Hierarchy first.";
            _status.Foreground = Theme.Warning;
            return;
        }
        var item = _items.FirstOrDefault(i => i.Id == node.EditorId);
        if (item == null)
        {
            LoadObjectLists();
            item = _items.FirstOrDefault(i => i.Id == node.EditorId);
        }
        if (item == null)
        {
            _status.Text = "That object is generated and cannot be used as a finder or target.";
            _status.Foreground = Theme.Warning;
            return;
        }
        if (isFinder) _finderCombo.SelectedItem = item;
        else _targetCombo.SelectedItem = item;
    }

    private void SettingsChanged()
    {
        Settings.Clamp();
        _settingsDirty = true;
        _lastQuery = "";
        RefreshSummary();
        Preview(force: true);
    }

    private void PreviewTick()
    {
        if (_closed) return;
        if (_showEditor.IsChecked != true)
        {
            _showPath(null, false);
            return;
        }
        Preview(force: false);
    }

    private void Preview(bool force)
    {
        bool show = _showEditor.IsChecked == true;
        var finder = CurrentFinder();
        var target = CurrentTarget();
        if (finder == null || target == null)
        {
            _showPath(null, false);
            SetStatus("Choose a finder and a target to calculate a route.", Theme.Dim);
            return;
        }
        var s = Settings;
        string query = $"{finder.EditorId}|{target.EditorId}|{finder.WorldPosition}|{target.WorldPosition}|{s.CellSize}|{s.AgentRadius}|{s.AgentHeight}|{s.MaxStepHeight}|{s.MaxDropHeight}|{s.Traversal}|{s.AllowDiagonal}|{s.AvoidCornerCutting}|{s.SmoothPath}|{s.WallHugPenalty}";
        if (!force && query == _lastQuery && (DateTime.UtcNow - _lastPreview).TotalMilliseconds < 220) return;
        _lastQuery = query;
        _lastPreview = DateTime.UtcNow;
        try
        {
            var result = Pathfinder.Find(_level, s, finder, target);
            if (show) _showPath(result.Success ? result : null, _showDots.IsChecked == true);
            else _showPath(null, false);
            _distance.Text = result.Success ? result.Length.ToString("0.00") + " m" : "—";
            _waypoints.Text = result.Success ? result.Points.Count.ToString("N0") : "—";
            _visited.Text = result.Visited.ToString("N0");
            _time.Text = result.Milliseconds < 1 ? "<1 ms" : result.Milliseconds.ToString("0.0") + " ms";
            _gridInfo.Text = result.CellSize > 0
                ? $"Grid resolution {result.CellSize:0.##} m · {result.Visited:N0} cells searched · {(result.Success ? result.Message : "no route")}"
                : "Grid not built.";
            SetStatus(result.Message, result.Success ? Theme.Ok : Theme.Error);
            _status.ToolTip = result.Debug;
        }
        catch (Exception ex)
        {
            _showPath(null, false);
            SetStatus("Pathfinding failed: " + ex.Message, Theme.Error);
        }
    }

    private void BakePath()
    {
        var finder = CurrentFinder();
        var target = CurrentTarget();
        if (finder == null || target == null)
        {
            SetStatus("Choose a finder and a target before baking a route.", Theme.Error);
            return;
        }
        if (ReferenceEquals(finder, target))
        {
            SetStatus("Finder and target must be different objects.", Theme.Error);
            return;
        }
        try
        {
            SaveSettings();
            var path = Pathfinder.Find(_level, Settings, finder, target);
            if (!path.Success)
            {
                SetStatus("Cannot bake: " + path.Message, Theme.Error);
                return;
            }
            var result = _liveBake.IsChecked == true
                ? PathfindingExporter.BakeRuntime(_level, Settings, finder, target)
                : PathfindingExporter.Bake(_level, Settings, finder, path, loop: _bakeLoop.IsChecked == true);
            SetStatus(result.Message, result.Success ? Theme.Ok : Theme.Error);
            if (result.Block != null)
            {
                _level.IsDirty = true;
                _baked?.Invoke(result.Block);
            }
            if (result.Success)
            {
                // The baked block replaces the live preview as the thing to test.
                _showPath(null, false);
                Settings.AutoFollowInPlay = false;
                _follow.IsChecked = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Bake failed: " + ex.Message, Theme.Error);
        }
    }

    private void SetStatus(string text, Brush color)
    {
        _status.Text = text;
        _status.Foreground = color;
    }

    private LevelNode? CurrentFinder() => _level.FindByEditorId(Settings.FinderId);
    private LevelNode? CurrentTarget() => _level.FindByEditorId(Settings.TargetId);

    private void RefreshSummary()
    {
        var finder = CurrentFinder();
        var target = CurrentTarget();
        if (finder != null) _finderInfo.Text = "Selected: " + finder.DisplayName;
        if (target != null) _targetInfo.Text = "Selected: " + target.DisplayName;
        _follow.IsChecked = Settings.AutoFollowInPlay;
        _showPlay.IsChecked = Settings.ShowPathInPlay;
        _showDots.IsChecked = Settings.ShowWaypoints;
        _showEditor.IsChecked = Settings.ShowPathInEditor;
        _showNav.IsChecked = Settings.ShowNavMesh;
    }

    private void SaveSettings()
    {
        Settings.Clamp();
        Settings.ShowPathInEditor = _showEditor.IsChecked == true;
        Settings.ShowPathInPlay = _showPlay.IsChecked == true;
        Settings.ShowWaypoints = _showDots.IsChecked == true;
        Settings.AutoFollowInPlay = _follow.IsChecked == true;
        Settings.ShowNavMesh = _showNav.IsChecked == true;
        if (_settingsDirty) _level.IsDirty = true;
    }

    private sealed class ObjectItem
    {
        public ObjectItem(string id, string name, string kind)
        {
            Id = id;
            Name = name;
            Kind = kind;
        }

        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }
        public string Display => $"{Name}  ·  {Kind}";
        public override string ToString() => Display;
    }
}
