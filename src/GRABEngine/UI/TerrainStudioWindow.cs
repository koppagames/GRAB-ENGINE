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
/// Heightmap studio for terrain. The preview is an actual editable heightmap; noise,
/// smoothing, raising/lowering and flattening happen before blocks are created. The
/// generated terrain is ordinary GRAB geometry and remains editable with the block brush.
/// </summary>
public sealed class TerrainStudioWindow : Window
{
    private readonly GrabLevel _level;
    private readonly TerrainPreview _preview = new();
    private readonly TextBox _width = Theme.TextBox("32");
    private readonly TextBox _depth = Theme.TextBox("32");
    private readonly TextBox _cell = Theme.TextBox("1");
    private readonly TextBox _heightScale = Theme.TextBox("7");
    private readonly TextBox _noiseScale = Theme.TextBox("0.075");
    private readonly TextBox _octaves = Theme.TextBox("4");
    private readonly TextBox _seed = Theme.TextBox("2026");
    private readonly TextBox _roughness = Theme.TextBox("1.15");
    private readonly TextBox _caveThreshold = Theme.TextBox("0.63");
    private readonly CheckBox _caves = Theme.Check("Carve caves (3D noise)");
    private readonly ComboBox _topMaterial;
    private readonly ComboBox _bodyMaterial;
    private readonly ComboBox _cliffMaterial;
    private readonly ComboBox _snowMaterial;
    private readonly TextBox _topColor = Theme.TextBox("#547A47");
    private readonly TextBox _bodyColor = Theme.TextBox("#615B54");
    private readonly TextBox _cliffColor = Theme.TextBox("#4C4945");
    private readonly TextBox _snowColor = Theme.TextBox("#E0EBF2");
    private readonly CheckBox _replace = Theme.Check("Replace previous terrain", true);
    private readonly TextBox _chunk = Theme.TextBox("8");
    private readonly TextBlock _status = Theme.Hint("Edit the heightmap, then build the terrain.");
    private readonly ComboBox _brushMode;
    private readonly Slider _brushSize = new() { Minimum = 1, Maximum = 18, Value = 4, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider _brushStrength = new() { Minimum = 0.01, Maximum = 0.25, Value = 0.06, TickFrequency = 0.01 };
    private readonly TextBlock _brushInfo = new() { Foreground = Theme.Dim, FontSize = 10.5 };

    // Biome painting / auto materials
    private readonly CheckBox _autoMaterials = Theme.Check("Auto-assign materials by height + slope");
    private readonly List<TerrainBiome> _biomes = TerrainBiome.Defaults();
    private readonly ComboBox _biomeSelect = new() { Height = 24, MinWidth = 130 };
    private readonly ComboBox _biomeTopMaterial;
    private readonly ComboBox _biomeBodyMaterial;
    private readonly ComboBox _biomeCliffMaterial;
    private readonly TextBox _biomeTopColor = Theme.TextBox("#547A47");
    private readonly TextBox _biomeBodyColor = Theme.TextBox("#615B54");
    private readonly TextBox _biomeMinHeight = Theme.TextBox("0");
    private readonly TextBox _biomeMaxHeight = Theme.TextBox("1");
    private readonly TextBox _biomeMaxSlope = Theme.TextBox("99");

    // Water
    private readonly CheckBox _water = Theme.Check("Add water + shoreline");
    private readonly TextBox _waterLevel = Theme.TextBox("0.8");
    private readonly TextBox _waterColor = Theme.TextBox("#2E7AB3");

    // Path / road. Roads are painted onto the heightmap instead of typed as X/Z pairs.
    private readonly ComboBox _pathMaterial;
    private readonly TextBox _pathColor = Theme.TextBox("#857866");

    // Scatter
    private readonly ComboBox _scatterPrefab = new() { Height = 24, MinWidth = 130 };
    private readonly TextBox _scatterCount = Theme.TextBox("12");
    private readonly TextBox _scatterSlope = Theme.TextBox("0.9");
    private readonly TextBox _scatterMinHeight = Theme.TextBox("0.05");

    public event Action<TerrainResult>? Applied;
    public event Action? LevelChanged;

    public TerrainStudioWindow(Window owner, GrabLevel level)
    {
        _level = level;
        Owner = owner;
        Title = "Terrain & World Builder";
        Width = 1180;
        Height = 800;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _topMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _bodyMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _cliffMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _snowMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(10));
        _biomeTopMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _biomeBodyMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _biomeCliffMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _pathMaterial = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(), MaterialIndex(8));
        _brushMode = Theme.Combo(new[] { "Raise", "Lower", "Smooth", "Flatten", "Paint biome", "Erode", "Paint path" });
        _brushMode.Width = 130;
        _brushMode.SelectionChanged += (_, _) =>
        {
            _preview.BrushMode = (TerrainBrushMode)_brushMode.SelectedIndex;
            _preview.BrushBiome = Math.Max(0, _biomeSelect.SelectedIndex);
        };

        var root = new DockPanel();
        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("TERRAIN & WORLD BUILDER"),
                Theme.Hint("Generate, sculpt and carve terrain made from real GRAB blocks. Use the block brush afterwards for detail.")
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
        var footerGrid = new DockPanel();
        _status.VerticalAlignment = VerticalAlignment.Center;
        footerGrid.Children.Add(_status);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var close = Theme.Button("Close");
        close.Click += (_, _) => Close();
        right.Children.Add(close);
        var build = Theme.Button("Build Terrain", accent: true);
        build.Margin = new Thickness(6, 0, 0, 0);
        build.Click += (_, _) => BuildTerrain();
        right.Children.Add(build);
        DockPanel.SetDock(right, Dock.Right);
        footerGrid.Children.Insert(0, right);
        footer.Child = footerGrid;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var settingsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(13, 0, 9, 13)
        };
        var settings = new StackPanel();
        settings.Children.Add(Theme.SectionHeader("LAYOUT"));
        settings.Children.Add(Theme.Row("Width", _width));
        settings.Children.Add(Theme.Row("Depth", _depth));
        settings.Children.Add(Theme.Row("Cell size", _cell));
        settings.Children.Add(Theme.Row("Height", _heightScale));
        settings.Children.Add(Theme.Hint("Drag on the heightmap to sculpt. The terrain is built at ground level and stays centred, so there are no X/Y coordinates to type. Tall columns are single scaled blocks, so the level stays efficient."));

        settings.Children.Add(Theme.SectionHeader("NOISE"));
        settings.Children.Add(Theme.Row("Scale", _noiseScale));
        settings.Children.Add(Theme.Row("Octaves", _octaves));
        settings.Children.Add(Theme.Row("Seed", _seed));
        settings.Children.Add(Theme.Row("Roughness", _roughness));

        var quickRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var generate = Theme.Button("Random Hills", small: true);
        generate.Click += (_, _) => RegenerateNoise();
        quickRow.Children.Add(generate);
        var flatten = Theme.Button("Flatten", small: true);
        flatten.Margin = new Thickness(5, 0, 0, 0);
        flatten.ToolTip = "Set the whole heightmap to one level so you can draw a fresh landscape";
        flatten.Click += (_, _) => FlattenHeightmap();
        quickRow.Children.Add(flatten);
        var smoothAll = Theme.Button("Smooth", small: true);
        smoothAll.Margin = new Thickness(5, 0, 0, 0);
        smoothAll.Click += (_, _) =>
        {
            if (_preview.Heights == null) return;
            TerrainBuilder.Smooth(_preview.Heights);
            _preview.Refresh();
            _status.Text = "Heightmap smoothed.";
        };
        quickRow.Children.Add(smoothAll);
        settings.Children.Add(quickRow);

        settings.Children.Add(Theme.SectionHeader("PATH / ROAD"));
        settings.Children.Add(Theme.Hint("Pick the Paint path brush above, then drag on the heightmap. Right-drag erases path cells. Brush size is the road width; there are no start/end coordinates."));
        settings.Children.Add(Theme.Row("Material", _pathMaterial));
        settings.Children.Add(Theme.Row("Colour", ColorInputs.Field(_pathColor, c => _preview.SetPathColor(c))));
        var clearPath = Theme.Button("Clear Path", small: true);
        clearPath.Click += (_, _) =>
        {
            _preview.ClearPath();
            _status.Text = "Path cleared. Build Terrain to write the change.";
        };
        settings.Children.Add(clearPath);

        settings.Children.Add(Theme.SectionHeader("CAVES"));
        settings.Children.Add(_caves);
        settings.Children.Add(Theme.Row("Threshold", _caveThreshold));

        settings.Children.Add(Theme.SectionHeader("MATERIAL LAYERS"));
        settings.Children.Add(Theme.Row("Top", _topMaterial));
        settings.Children.Add(Theme.Row("Body", _bodyMaterial));
        settings.Children.Add(Theme.Row("Cliff", _cliffMaterial));
        settings.Children.Add(Theme.Row("Snow", _snowMaterial));
        settings.Children.Add(Theme.Row("Top colour", ColorInputs.Field(_topColor)));
        settings.Children.Add(Theme.Row("Body colour", ColorInputs.Field(_bodyColor)));
        settings.Children.Add(Theme.Row("Cliff colour", ColorInputs.Field(_cliffColor)));
        settings.Children.Add(Theme.Row("Snow colour", ColorInputs.Field(_snowColor)));
        settings.Children.Add(Theme.Hint("Colours apply to Default Colored. Materials with fixed textures ignore tint in GRAB, just like the Inspector."));

        settings.Children.Add(Theme.SectionHeader("BIOMES  ·  AUTO MATERIALS"));
        settings.Children.Add(_autoMaterials);
        settings.Children.Add(Theme.Hint("When enabled, the top material follows height and slope. Paint biome cells on the heightmap to override a region."));
        _biomeSelect.ItemsSource = _biomes.Select(b => b.Name).ToList();
        _biomeSelect.SelectedIndex = 1;
        settings.Children.Add(Theme.Row("Edit biome", _biomeSelect));
        settings.Children.Add(Theme.Row("Top material", _biomeTopMaterial));
        settings.Children.Add(Theme.Row("Body material", _biomeBodyMaterial));
        settings.Children.Add(Theme.Row("Cliff material", _biomeCliffMaterial));
        settings.Children.Add(Theme.Row("Top colour", ColorInputs.Field(_biomeTopColor)));
        settings.Children.Add(Theme.Row("Body colour", ColorInputs.Field(_biomeBodyColor)));
        settings.Children.Add(Theme.Row("Min height", _biomeMinHeight));
        settings.Children.Add(Theme.Row("Max height", _biomeMaxHeight));
        settings.Children.Add(Theme.Row("Max slope", _biomeMaxSlope));

        settings.Children.Add(Theme.SectionHeader("WATER & COASTLINE"));
        settings.Children.Add(_water);
        settings.Children.Add(Theme.Row("Water level Y", _waterLevel));
        settings.Children.Add(Theme.Row("Water colour", ColorInputs.Field(_waterColor)));
        settings.Children.Add(Theme.Hint("Water is one passable transparent slab. The top block turns sandy within 0.75 m of the water line."));

        settings.Children.Add(Theme.SectionHeader("SCULPT HELPERS"));
        var erode = Theme.Button("Erode (thermal)", small: true);
        erode.Margin = new Thickness(0, 2, 0, 3);
        erode.Click += (_, _) =>
        {
            if (_preview.Heights == null) return;
            TerrainBuilder.Erode(_preview.Heights);
            _preview.Refresh();
            _status.Text = "Erosion applied to the heightmap. Build Terrain to write the blocks.";
        };
        settings.Children.Add(erode);
        var smooth = Theme.Button("Smooth heightmap", small: true);
        smooth.Margin = new Thickness(0, 0, 0, 8);
        smooth.Click += (_, _) =>
        {
            if (_preview.Heights == null) return;
            TerrainBuilder.Smooth(_preview.Heights);
            _preview.Refresh();
        };
        settings.Children.Add(smooth);

        settings.Children.Add(Theme.SectionHeader("SLOPE-AWARE SCATTER"));
        _scatterPrefab.ItemsSource = PrefabLibrary.List().Select(p => p.Name).ToList();
        if (_scatterPrefab.Items.Count > 0) _scatterPrefab.SelectedIndex = 0;
        settings.Children.Add(Theme.Row("Prefab", _scatterPrefab));
        settings.Children.Add(Theme.Row("Count", _scatterCount));
        settings.Children.Add(Theme.Row("Max slope", _scatterSlope));
        settings.Children.Add(Theme.Row("Min height", _scatterMinHeight));
        var scatter = Theme.Button("Scatter on Terrain", small: true);
        scatter.Click += (_, _) => Scatter();
        settings.Children.Add(scatter);
        settings.Children.Add(Theme.Hint("Save a tree/rock prefab first. Scatter skips steep slopes and low ground."));

        settings.Children.Add(Theme.SectionHeader("CHUNKS"));
        settings.Children.Add(Theme.Row("Cells/chunk", _chunk));
        settings.Children.Add(_replace);
        settingsScroll.Content = settings;
        body.Children.Add(settingsScroll);

        var previewPanel = new Grid { Margin = new Thickness(0, 6, 13, 10) };
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var title = new TextBlock
        {
            Text = "HEIGHTMAP",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            VerticalAlignment = VerticalAlignment.Center
        };
        previewHeader.Children.Add(title);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(new TextBlock
        {
            Text = "Brush",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 6, 0)
        });
        tools.Children.Add(_brushMode);
        tools.Children.Add(new TextBlock
        {
            Text = "Size",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 5, 0)
        });
        _brushSize.Width = 78;
        tools.Children.Add(_brushSize);
        tools.Children.Add(new TextBlock
        {
            Text = "Strength",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 5, 0)
        });
        _brushStrength.Width = 78;
        tools.Children.Add(_brushStrength);
        DockPanel.SetDock(tools, Dock.Right);
        previewHeader.Children.Add(tools);
        previewPanel.Children.Add(previewHeader);
        Grid.SetRow(previewHeader, 0);

        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 26)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _preview
        };
        previewPanel.Children.Add(frame);
        Grid.SetRow(frame, 1);

        var hintBar = new TextBlock
        {
            Text = "LMB sculpt  ·  RMB orbit the scene  ·  leave the heightmap to get a live preview  ·  Build Terrain writes real blocks",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0)
        };
        previewPanel.Children.Add(hintBar);
        Grid.SetRow(hintBar, 2);
        Grid.SetColumn(previewPanel, 1);
        body.Children.Add(previewPanel);
        root.Children.Add(body);
        Content = root;

        _brushMode.SelectedIndex = 0;
        _preview.BrushMode = TerrainBrushMode.Raise;
        _preview.BrushSize = 4;
        _preview.BrushStrength = 0.06f;
        _brushSize.ValueChanged += (_, _) => _preview.BrushSize = (int)_brushSize.Value;
        _brushStrength.ValueChanged += (_, _) => _preview.BrushStrength = (float)_brushStrength.Value;
        _preview.HeightsChanged += () =>
        {
            _brushInfo.Text = PreviewStats();
            _status.Text = "Heightmap edited. Build Terrain to write the blocks.";
        };

        _width.LostFocus += (_, _) => ResizeHeightmap();
        _depth.LostFocus += (_, _) => ResizeHeightmap();
        _seed.LostFocus += (_, _) => { };
        _width.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ResizeHeightmap(); Keyboard.ClearFocus(); e.Handled = true; } };
        _depth.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ResizeHeightmap(); Keyboard.ClearFocus(); e.Handled = true; } };
        _biomeSelect.SelectionChanged += (_, _) => LoadBiomeIntoUi();
        _pathColor.LostFocus += (_, _) => _preview.SetPathColor(ParseColor(_pathColor.Text, new Col(0.52f, 0.47f, 0.4f, 1)));
        foreach (var control in new Control[] { _biomeTopMaterial, _biomeBodyMaterial, _biomeCliffMaterial })
            if (control is ComboBox cb) cb.SelectionChanged += (_, _) => SaveBiomeFromUi();
        foreach (var box in new[] { _biomeTopColor, _biomeBodyColor, _biomeMinHeight, _biomeMaxHeight, _biomeMaxSlope })
            box.LostFocus += (_, _) => SaveBiomeFromUi();
        Loaded += (_, _) =>
        {
            var settings = ReadSettings();
            _preview.SetHeightmap(TerrainBuilder.GenerateHeights(settings), settings);
            _preview.Biomes = new int[settings.Width, settings.Depth];
            for (int x = 0; x < settings.Width; x++)
                for (int z = 0; z < settings.Depth; z++)
                    _preview.Biomes[x, z] = -1;
            LoadBiomeIntoUi();
            _status.Text = PreviewStats();
        };
    }

    private void LoadBiomeIntoUi()
    {
        int index = Math.Clamp(_biomeSelect.SelectedIndex, 0, _biomes.Count - 1);
        if (index < 0 || index >= _biomes.Count) return;
        var b = _biomes[index];
        _biomeTopMaterial.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == b.TopMaterial));
        _biomeBodyMaterial.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == b.BodyMaterial));
        _biomeCliffMaterial.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == b.CliffMaterial));
        _biomeTopColor.Text = b.TopColor.Hex;
        _biomeBodyColor.Text = b.BodyColor.Hex;
        _biomeMinHeight.Text = b.MinHeight.ToString("0.##", CultureInfo.InvariantCulture);
        _biomeMaxHeight.Text = b.MaxHeight.ToString("0.##", CultureInfo.InvariantCulture);
        _biomeMaxSlope.Text = b.MaxSlope.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void SaveBiomeFromUi()
    {
        int index = Math.Clamp(_biomeSelect.SelectedIndex, 0, _biomes.Count - 1);
        if (index < 0 || index >= _biomes.Count) return;
        var b = _biomes[index];
        b.TopMaterial = SelectedMaterial(_biomeTopMaterial, 8);
        b.BodyMaterial = SelectedMaterial(_biomeBodyMaterial, 8);
        b.CliffMaterial = SelectedMaterial(_biomeCliffMaterial, 8);
        b.TopColor = ParseColor(_biomeTopColor.Text, b.TopColor);
        b.BodyColor = ParseColor(_biomeBodyColor.Text, b.BodyColor);
        b.MinHeight = ParseFloat(_biomeMinHeight.Text, b.MinHeight, 0, 1);
        b.MaxHeight = ParseFloat(_biomeMaxHeight.Text, b.MaxHeight, 0, 1);
        b.MaxSlope = ParseFloat(_biomeMaxSlope.Text, b.MaxSlope, 0, 99);
    }

    private void Scatter()
    {
        var prefs = PrefabLibrary.List();
        if (prefs.Count == 0)
        {
            _status.Text = "Save a prefab first (Prefabs → Save Selection as Prefab).";
            _status.Foreground = Theme.Warning;
            return;
        }
        int index = Math.Clamp(_scatterPrefab.SelectedIndex, 0, prefs.Count - 1);
        if (_preview.Heights == null) return;
        SaveBiomeFromUi();
        var settings = ReadSettings();
        int count = TerrainBuilder.ScatterPrefabs(_level, settings, _preview.Heights,
            new[] { prefs[index].FilePath }, ParseInt(_scatterCount.Text, 12, 0, 500),
            0, ParseFloat(_scatterSlope.Text, 0.9f, 0.01f, 99),
            ParseFloat(_scatterMinHeight.Text, 0.05f, 0, 1), 0.98f);
        _status.Text = count == 0 ? "No valid scatter points (slope/height limits)." : $"Scattered {count} prefab(s) on the terrain.";
        _status.Foreground = count == 0 ? Theme.Warning : Theme.Ok;
        LevelChanged?.Invoke();
    }

    private static int MaterialIndex(int id) => Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == id));

    private TerrainSettings ReadSettings()
    {
        var s = new TerrainSettings
        {
            Width = ParseInt(_width.Text, 32, 2, 128),
            Depth = ParseInt(_depth.Text, 32, 2, 128),
            CellSize = ParseFloat(_cell.Text, 1, 0.25f, 4),
            BaseHeight = 0,
            HeightScale = ParseFloat(_heightScale.Text, 7, 0.5f, 40),
            NoiseScale = ParseFloat(_noiseScale.Text, 0.075f, 0.005f, 0.5f),
            Octaves = ParseInt(_octaves.Text, 4, 1, 8),
            Seed = ParseInt(_seed.Text, 2026, -999999, 999999),
            Roughness = ParseFloat(_roughness.Text, 1.15f, 0.5f, 2.5f),
            Caves = _caves.IsChecked == true,
            CaveThreshold = ParseFloat(_caveThreshold.Text, 0.63f, 0.35f, 0.95f),
            ChunkSize = ParseInt(_chunk.Text, 8, 2, 32),
            ReplaceExisting = _replace.IsChecked == true,
            TopMaterial = SelectedMaterial(_topMaterial, 8),
            BodyMaterial = SelectedMaterial(_bodyMaterial, 8),
            CliffMaterial = SelectedMaterial(_cliffMaterial, 8),
            SnowMaterial = SelectedMaterial(_snowMaterial, 10),
            TopColor = ParseColor(_topColor.Text, new Col(0.33f, 0.55f, 0.28f, 1)),
            BodyColor = ParseColor(_bodyColor.Text, new Col(0.38f, 0.36f, 0.33f, 1)),
            CliffColor = ParseColor(_cliffColor.Text, new Col(0.30f, 0.29f, 0.28f, 1)),
            SnowColor = ParseColor(_snowColor.Text, new Col(0.88f, 0.92f, 0.96f, 1)),
            AutoMaterials = _autoMaterials.IsChecked == true,
            Biomes = _biomes.Select(b => b.Clone()).ToList(),
            Water = _water.IsChecked == true,
            WaterLevel = ParseFloat(_waterLevel.Text, 0.8f, -20, 60),
            WaterColor = ParseColor(_waterColor.Text, new Col(0.18f, 0.48f, 0.7f, 0.55f)).WithAlpha(0.55f),
            PathWidth = 1.5f,
            PathMaterial = SelectedMaterial(_pathMaterial, 8),
            PathColor = ParseColor(_pathColor.Text, new Col(0.52f, 0.47f, 0.4f, 1)),
            PathPoints = new List<Vec3>()
        };
        return s;
    }

    private void ResizeHeightmap()
    {
        var s = ReadSettings();
        if (_preview.Heights == null || _preview.Heights.GetLength(0) != s.Width || _preview.Heights.GetLength(1) != s.Depth)
        {
            _preview.SetHeightmap(TerrainBuilder.GenerateHeights(s), s);
            _status.Text = PreviewStats();
        }
    }

    private void RegenerateNoise()
    {
        var s = ReadSettings();
        _preview.SetHeightmap(TerrainBuilder.GenerateHeights(s), s);
        _preview.ClearPath();
        _status.Text = PreviewStats();
    }

    private void FlattenHeightmap()
    {
        var s = ReadSettings();
        var heights = new float[s.Width, s.Depth];
        for (int x = 0; x < s.Width; x++)
            for (int z = 0; z < s.Depth; z++)
                heights[x, z] = 0.22f;
        _preview.SetHeightmap(heights, s);
        _preview.ClearPath();
        _status.Text = "Flat heightmap ready. Drag to raise hills, then Build Terrain.";
    }

    private void BuildTerrain()
    {
        var settings = ReadSettings();
        var heights = _preview.Heights;
        if (heights == null || heights.GetLength(0) != settings.Width || heights.GetLength(1) != settings.Depth)
        {
            heights = TerrainBuilder.GenerateHeights(settings);
            _preview.SetHeightmap(heights, settings);
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var result = TerrainBuilder.Build(_level, settings, heights, _preview.Biomes, _preview.PathMask);
            _status.Text = result.Summary;
            _status.Foreground = Theme.Ok;
            Applied?.Invoke(result);
        }
        catch (Exception ex)
        {
            _status.Text = "Terrain build failed: " + ex.Message;
            _status.Foreground = Theme.Error;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private string PreviewStats()
    {
        var h = _preview.Heights;
        if (h == null) return "No heightmap.";
        double max = 0, sum = 0;
        foreach (var value in h) { max = Math.Max(max, value); sum += value; }
        return $"{h.GetLength(0)} × {h.GetLength(1)} heightmap  ·  peak {max * 100:0}%  ·  average {sum / h.Length * 100:0}%";
    }

    private static int SelectedMaterial(ComboBox combo, int fallback)
    {
        int index = Math.Clamp(combo.SelectedIndex, 0, MaterialInfo.All.Length - 1);
        return index >= 0 ? MaterialInfo.All[index].Id : fallback;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out int v) ? Math.Clamp(v, min, max) : fallback;

    private static float ParseFloat(string text, float fallback, float min, float max)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? Math.Clamp(v, min, max) : fallback;

    private static Col ParseColor(string text, Col fallback)
    {
        try
        {
            var value = text.Trim().TrimStart('#');
            if (value.Length != 6 && value.Length != 8) return fallback;
            uint.Parse(value[..6], NumberStyles.HexNumber);
            var color = (Color)ColorConverter.ConvertFromString("#" + value);
            return new Col(color.R / 255f, color.G / 255f, color.B / 255f,
                value.Length == 8 ? color.A / 255f : 1f);
        }
        catch
        {
            return fallback;
        }
    }
}

public enum TerrainBrushMode { Raise, Lower, Smooth, Flatten, Biome, Erode, Path }

/// <summary>Top-down editable heightmap with a soft circular brush.</summary>
public sealed class TerrainPreview : FrameworkElement
{
    private float[,]? _heights;
    private TerrainSettings _settings = new();
    private bool _painting;
    private int _paintButton;
    private Point _mouse;

    public float[,]? Heights => _heights;
    public int[,]? Biomes { get; set; }
    /// <summary>Cells painted with the road brush. The terrain builder carves and
    /// surfaces these cells without any typed path coordinates.</summary>
    public bool[,]? PathMask { get; private set; }
    public int BrushBiome { get; set; } = 1;
    public int BrushSize { get; set; } = 4;
    public float BrushStrength { get; set; } = 0.06f;
    public TerrainBrushMode BrushMode { get; set; }
    public void Refresh() => InvalidateVisual();
    public event Action? HeightsChanged;

    public TerrainPreview()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public void SetHeightmap(float[,] heights, TerrainSettings settings)
    {
        _heights = heights;
        _settings = settings;
        if (Biomes == null || Biomes.GetLength(0) != heights.GetLength(0) || Biomes.GetLength(1) != heights.GetLength(1))
        {
            Biomes = new int[heights.GetLength(0), heights.GetLength(1)];
            for (int x = 0; x < Biomes.GetLength(0); x++)
                for (int z = 0; z < Biomes.GetLength(1); z++)
                    Biomes[x, z] = -1;
        }
        if (PathMask == null || PathMask.GetLength(0) != heights.GetLength(0) || PathMask.GetLength(1) != heights.GetLength(1))
            PathMask = new bool[heights.GetLength(0), heights.GetLength(1)];
        InvalidateVisual();
    }

    public void ClearPath()
    {
        if (PathMask == null) return;
        Array.Clear(PathMask, 0, PathMask.Length);
        HeightmapChangedForPath();
        InvalidateVisual();
    }

    public void SetPathColor(Col color)
    {
        _settings.PathColor = color;
        InvalidateVisual();
    }

    private void HeightmapChangedForPath()
    {
        HeightsChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 29)), null, new Rect(0, 0, w, h));
        if (_heights == null || w <= 10 || h <= 10) return;
        int cols = _heights.GetLength(0), rows = _heights.GetLength(1);
        double cell = Math.Min((w - 24) / cols, (h - 24) / rows);
        double ox = (w - cell * cols) * 0.5, oy = (h - cell * rows) * 0.5;

        for (int x = 0; x < cols; x++)
        {
            for (int z = 0; z < rows; z++)
            {
                float v = _heights[x, z];
                double left = x > 0 ? Math.Abs(v - _heights[x - 1, z]) : 0;
                double back = z > 0 ? Math.Abs(v - _heights[x, z - 1]) : 0;
                bool cliff = Math.Max(left, back) > 0.09;
                Color c = cliff ? Color.FromRgb(110, 100, 88)
                    : v > 0.75 ? Color.FromRgb(222, 232, 238)
                    : v > 0.25 ? Color.FromRgb(92, 126, 76)
                    : Color.FromRgb(140, 128, 92);
                int biome = Biomes != null && x < Biomes.GetLength(0) && z < Biomes.GetLength(1) ? Biomes[x, z] : -1;
                if (biome >= 0 && _settings.Biomes.Count > biome)
                {
                    var bc = _settings.Biomes[biome].TopColor;
                    c = Color.FromRgb((byte)Math.Clamp(bc.Ri, 0, 255), (byte)Math.Clamp(bc.Gi, 0, 255), (byte)Math.Clamp(bc.Bi, 0, 255));
                    if (cliff) c = Color.FromRgb((byte)(c.R * 0.8f), (byte)(c.G * 0.8f), (byte)(c.B * 0.8f));
                }
                if (PathMask != null && x < PathMask.GetLength(0) && z < PathMask.GetLength(1) && PathMask[x, z])
                {
                    var pc = _settings.PathColor;
                    c = Color.FromRgb((byte)Math.Clamp(pc.Ri, 0, 255), (byte)Math.Clamp(pc.Gi, 0, 255), (byte)Math.Clamp(pc.Bi, 0, 255));
                }
                double shade = 0.55 + v * 0.65;
                c = Color.FromRgb((byte)Math.Clamp(c.R * shade, 0, 255),
                    (byte)Math.Clamp(c.G * shade, 0, 255), (byte)Math.Clamp(c.B * shade, 0, 255));
                dc.DrawRectangle(new SolidColorBrush(c), null,
                    new Rect(ox + x * cell, oy + z * cell, Math.Ceiling(cell) + 0.2, Math.Ceiling(cell) + 0.2));
            }
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
        int step = Math.Max(1, Math.Min(cols, rows) / 16);
        for (int x = 0; x <= cols; x += step)
            dc.DrawLine(gridPen, new Point(ox + x * cell, oy), new Point(ox + x * cell, oy + rows * cell));
        for (int z = 0; z <= rows; z += step)
            dc.DrawLine(gridPen, new Point(ox, oy + z * cell), new Point(ox + cols * cell, oy + z * cell));

        var outer = new Pen(new SolidColorBrush(Color.FromRgb(80, 82, 86)), 1);
        dc.DrawRectangle(null, outer, new Rect(ox, oy, cols * cell, rows * cell));

        // Brush cursor: a ring with a small crosshair. Using an overlay instead of a
        // second visual keeps the control lightweight.
        var local = ToCell(_mouse);
        if (local.X >= 0 && local.X < cols && local.Z >= 0 && local.Z < rows)
        {
            double cx = ox + (local.X + 0.5) * cell, cz = oy + (local.Z + 0.5) * cell;
            double radius = Math.Max(2, BrushSize * cell);
            var brushPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 214, 92)), 1.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, 255, 214, 92)), brushPen,
                new Point(cx, cz), radius, radius);
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(cx - 4, cz), new Point(cx + 4, cz));
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(cx, cz - 4), new Point(cx, cz + 4));
        }

        if (_settings.PathPoints.Count >= 2)
        {
            var pathPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 210, 100)), Math.Max(1, _settings.PathWidth * cell * 0.35));
            for (int i = 0; i < _settings.PathPoints.Count - 1; i++)
            {
                var pa = _settings.PathPoints[i];
                var pb = _settings.PathPoints[i + 1];
                var p0 = new Point(ox + (pa.X / MathF.Max(0.001f, _settings.CellSize) + (cols - 1) * 0.5f + 0.5f) * cell,
                    oy + (pa.Z / MathF.Max(0.001f, _settings.CellSize) + (rows - 1) * 0.5f + 0.5f) * cell);
                var p1 = new Point(ox + (pb.X / MathF.Max(0.001f, _settings.CellSize) + (cols - 1) * 0.5f + 0.5f) * cell,
                    oy + (pb.Z / MathF.Max(0.001f, _settings.CellSize) + (rows - 1) * 0.5f + 0.5f) * cell);
                dc.DrawLine(pathPen, p0, p1);
            }
        }

        var label = new FormattedText($"{cols} × {rows} cells  ·  click and drag to sculpt" +
            (BrushMode == TerrainBrushMode.Biome ? "  ·  painting biome" :
             BrushMode == TerrainBrushMode.Path ? "  ·  drawing road  ·  right-drag erases" : ""),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
            new SolidColorBrush(Color.FromRgb(140, 146, 150)), 1.0);
        dc.DrawText(label, new Point(ox, Math.Max(3, oy - 16)));
    }

    private (int X, int Z) ToCell(Point p)
    {
        if (_heights == null) return (-1, -1);
        int cols = _heights.GetLength(0), rows = _heights.GetLength(1);
        double cell = Math.Min((ActualWidth - 24) / cols, (ActualHeight - 24) / rows);
        double ox = (ActualWidth - cell * cols) * 0.5, oy = (ActualHeight - cell * rows) * 0.5;
        int x = (int)Math.Floor((p.X - ox) / cell), z = (int)Math.Floor((p.Y - oy) / cell);
        return x >= 0 && x < cols && z >= 0 && z < rows ? (x, z) : (-1, -1);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        _painting = true;
        _paintButton = e.ChangedButton == MouseButton.Left ? 1 : -1;
        _mouse = e.GetPosition(this);
        CaptureMouse();
        PaintAt(_mouse);
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_painting) PaintAt(_mouse);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (!_painting) return;
        _painting = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!_painting) { _mouse = new Point(-100, -100); InvalidateVisual(); }
    }

    private void PaintAt(Point point)
    {
        if (_heights == null) return;
        var (cx, cz) = ToCell(point);
        if (cx < 0) return;
        int radius = Math.Max(1, BrushSize);
        int cols = _heights.GetLength(0), rows = _heights.GetLength(1);
        float strength = BrushStrength * (_paintButton > 0 ? 1 : -1);
        if (BrushMode == TerrainBrushMode.Biome)
        {
            if (Biomes == null) Biomes = new int[cols, rows];
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(cols - 1, cx + radius); x++)
                for (int z = Math.Max(0, cz - radius); z <= Math.Min(rows - 1, cz + radius); z++)
                {
                    double dx = x - cx, dz = z - cz;
                    if (Math.Sqrt(dx * dx + dz * dz) > radius) continue;
                    Biomes[x, z] = BrushBiome;
                }
            HeightsChanged?.Invoke();
            InvalidateVisual();
            return;
        }
        if (BrushMode == TerrainBrushMode.Path)
        {
            if (PathMask == null) PathMask = new bool[cols, rows];
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(cols - 1, cx + radius); x++)
                for (int z = Math.Max(0, cz - radius); z <= Math.Min(rows - 1, cz + radius); z++)
                {
                    double dx = x - cx, dz = z - cz;
                    if (Math.Sqrt(dx * dx + dz * dz) > radius) continue;
                    PathMask[x, z] = _paintButton > 0;
                }
            HeightsChanged?.Invoke();
            InvalidateVisual();
            return;
        }
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(cols - 1, cx + radius); x++)
        {
            for (int z = Math.Max(0, cz - radius); z <= Math.Min(rows - 1, cz + radius); z++)
            {
                double dx = x - cx, dz = z - cz;
                double dist = Math.Sqrt(dx * dx + dz * dz) / Math.Max(1, radius);
                if (dist > 1) continue;
                float falloff = (float)(1 - dist * dist);
                switch (BrushMode)
                {
                    case TerrainBrushMode.Raise:
                        _heights[x, z] = Math.Clamp(_heights[x, z] + strength * falloff, 0, 1);
                        break;
                    case TerrainBrushMode.Lower:
                        _heights[x, z] = Math.Clamp(_heights[x, z] - Math.Abs(strength) * falloff, 0, 1);
                        break;
                    case TerrainBrushMode.Smooth:
                    {
                        float sum = 0;
                        int count = 0;
                        for (int ax = Math.Max(0, x - 1); ax <= Math.Min(cols - 1, x + 1); ax++)
                            for (int az = Math.Max(0, z - 1); az <= Math.Min(rows - 1, z + 1); az++)
                            { sum += _heights[ax, az]; count++; }
                        _heights[x, z] += (sum / Math.Max(1, count) - _heights[x, z]) * falloff;
                        break;
                    }
                    case TerrainBrushMode.Flatten:
                    {
                        float target = _heights[cx, cz];
                        _heights[x, z] += (target - _heights[x, z]) * falloff * 0.35f;
                        break;
                    }
                    case TerrainBrushMode.Erode:
                    {
                        // Thermal erosion: material above the talus angle slides to the
                        // lowest neighbour. Right-drag deposits material instead.
                        float h = _heights[x, z];
                        int lowX = x, lowZ = z;
                        float low = h;
                        for (int ax = Math.Max(0, x - 1); ax <= Math.Min(cols - 1, x + 1); ax++)
                            for (int az = Math.Max(0, z - 1); az <= Math.Min(rows - 1, z + 1); az++)
                                if (_heights[ax, az] < low) { low = _heights[ax, az]; lowX = ax; lowZ = az; }
                        float diff = h - low;
                        if (diff <= 0.002f) break;
                        float move = Math.Min(diff * 0.45f, BrushStrength * 2f) * falloff;
                        if (_paintButton > 0)
                        {
                            _heights[x, z] = Math.Clamp(h - move, 0, 1);
                            _heights[lowX, lowZ] = Math.Clamp(_heights[lowX, lowZ] + move * 0.6f, 0, 1);
                        }
                        else
                        {
                            float average = (h + low) * 0.5f;
                            _heights[x, z] = h + (average - h) * falloff * 0.5f;
                        }
                        break;
                    }
                }
            }
        }
        HeightsChanged?.Invoke();
        InvalidateVisual();
    }
}
