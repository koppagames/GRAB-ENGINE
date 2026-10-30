using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrabEngine.Core;
using GrabEngine.Rendering;

namespace GrabEngine.Controls;

public enum EditorTool { Select, Move, Rotate, Scale, Brush, Terrain, Measure, Prefab, Build }

/// <summary>Shapes the in-scene Draw tool turns into real blocks.</summary>
public enum BuildShape { Box, Sphere, Cylinder, Cone, Stairs, Ramp, Polygon }

/// <summary>Snapping modes for placement and prefab stamping.</summary>
public enum SmartSnapMode { Grid, Face, Edge, Center }

public partial class SceneView : UserControl
{
    private GrabLevel? _level;
    private LevelNode? _selected;
    private readonly List<LevelNode> _selection = new();
    private readonly List<DragStart> _dragStarts = new();
    private Vec3 _dragPivot;
    private Vec3 _dragLocalAxis = Vec3.Right;
    private EditorTool _tool = EditorTool.Move;
    private bool _localSpace;
    private SceneBuildResult? _build;
    private readonly Dictionary<GeometryModel3D, int> _gizmoModels = new();
    private readonly ParticleSystem _particles = new();
    private readonly DispatcherTimer _animTimer;
    private readonly Stopwatch _animClock = new();
    private double _lastAnimTime;
    private AmbienceSettings _ambience = new();
    private Model3DGroup _lighting = new();
    private Model3DGroup? _playerGroup;
    private double _editorYaw, _editorPitch, _editorDistance;
    private Vec3 _editorTarget;

    private double _yaw = 42, _pitch = 15, _distance = 19;
    private Vec3 _target = new(0, 1, 0);
    private Point _lastMouse;
    private bool _orbiting, _panning;
    private Vec3 _dragStartPos, _dragStartScale;
    private Quat _dragStartRot;
    private LevelNode? _dragNode;
    private int _dragAxis = -1;
    private bool _draggingGizmo;
    private double _dragStartT;
    private Point3D _dragOrigin;
    private Vector3D _dragDir;
    private double _dragStartAngle;
    private double _gizmoScale = 1.2;
    private LevelNode? _previewNode;
    private Vec3 _previewPosition;
    private Quat _previewRotation = Quat.Identity;

    // Block brush state. The brush reuses the editor camera and hit testing but does
    // not touch the normal selection/gizmo path while it is active.
    private BlockBrushMode _brushMode = BlockBrushMode.None;
    private int _brushShape = ShapeInfo.Cube;
    private int _brushMaterial = 8;
    private double _brushCellSize = 1.0;
    private bool _brushHollow;
    private int _brushLayers = 1;
    private Col _brushColor = new(0.72f, 0.72f, 0.72f, 1f);
    private bool _brushStroke;
    private bool _brushHoverValid;
    private VoxelCell _brushAnchor;
    private VoxelCell _brushHover;
    private Point3D _brushPlanePoint;
    private Vector3D _brushPlaneNormal = new(0, 1, 0);
    private LevelNode? _brushStartBlock;
    private LevelNode? _brushHoverNode;
    private Point _brushLastPoint;
    private readonly HashSet<LevelNode> _brushReplaceNodes = new();
    private readonly HashSet<LevelNode> _brushEraseNodes = new();
    private BrushPreviewState? _brushPreviewState;
    private LevelNode? _brushFillPreviewNode;
    private readonly List<LevelNode> _brushFillPreview = new();
    private BlockSymmetry _symmetry;
    private Vec3 _symmetryPivot = Vec3.Zero;

    // Draw/build tool state. Footprints are picked from the scene, so the tool never
    // asks for X/Y/Z coordinates.
    private BuildShape _buildShape = BuildShape.Box;
    private int _buildHeight = 3;
    private bool _buildHollow;
    private int _buildMaterial = 8;
    private Col _buildColor = new(0.72f, 0.72f, 0.72f, 1f);
    private bool _buildDragging;
    private bool _buildHoverValid;
    private VoxelCell _buildAnchor;
    private VoxelCell _buildHover;
    private TerrainFoundationMode _buildFoundation = TerrainFoundationMode.None;
    private float _buildFoundationDepth = 4f;

    // 3D terrain shaper state. This is deliberately separate from the block brush:
    // it edits terrain columns as volumes, so subtract and smooth keep a usable surface.
    private TerrainSculptMode _terrainMode = TerrainSculptMode.Add;
    private TerrainBrushVolume _terrainVolume = TerrainBrushVolume.Sphere;
    private int _terrainRadius = 4;
    private double _terrainCellSize = 1.0;
    private float _terrainStrength = 1f;
    private int _terrainMaterial = 8;
    private Col _terrainColor = new(0.33f, 0.55f, 0.28f, 1f);
    private bool _terrainStroke;
    private bool _terrainHoverValid;
    private Vec3 _terrainPoint;
    private (int X, int Y, int Z) _terrainLastCell;
    private string _terrainPreviewKey = "";
    private readonly List<Point3D> _buildPolygon = new();
    private Point3D? _buildPolygonHover;
    private double _buildPolygonY;
    private string _buildPreviewKey = "";
    private DateTime _buildClickSuppressUntil;

    // Measurement and prefab-stamp state.
    private Point3D? _measureStart;
    private Point3D? _measureEnd;
    private string _measureText = "Click two points to measure.";
    private GrabLevel? _prefabTemplate;
    private string _prefabName = "";
    private Model3DGroup? _prefabPreviewGroup;
    private Vec3 _prefabPreviewPosition;
    private Quat _prefabPreviewRotation = Quat.Identity;
    private Vec3 _prefabBoundsMin;
    private Vec3 _prefabBoundsMax;
    private bool _prefabPlacementValid;
    private double _prefabYaw;
    private readonly List<BuildIssue> _validationIssues = new();
    private PathfindingResult? _pathPreview;
    private bool _pathPreviewWaypoints = true;
    private string _pathPreviewSignature = "";

    private readonly record struct BrushPick(LevelNode? Node, Point3D Point, Vector3D Normal, VoxelCell Cell);
    private readonly record struct BrushPreviewState(
        BlockBrushMode Mode, VoxelCell Anchor, VoxelCell Hover, int Shape, int Material,
        double CellSize, bool Hollow, int Layers, string Color, LevelNode? HoverNode, int ReplaceCount,
        BlockSymmetry Symmetry, VoxelCell SymmetryCell);

    private sealed class DragStart
    {
        public LevelNode Node = null!;
        public Vec3 WorldPosition;
        public Quat WorldRotation;
        public Vec3 LocalScale;
    }

    public event Action<LevelNode?>? SelectionChanged;
    public event Action<IReadOnlyList<LevelNode>>? SelectionSetChanged;
    public event Action? SceneModified;
    public event Action<LevelNode>? NodePlaced;
    public event Action? DeleteRequested;
    public event Action<BlockBrushResult>? BrushApplied;
    public event Action<TerrainSculptResult>? TerrainSculpted;
    public event Action<string>? MeasurementChanged;
    public event Action<Vec3, Quat>? PrefabStampRequested;
    public event Action<List<LevelNode>, string>? StructureRequested;
    public event Action<int>? BuildHeightChanged;

    public bool SnapEnabled { get; set; } = true;
    public double SnapSize { get; set; } = 0.5;
    public BuildShape BuildShape
    {
        get => _buildShape;
        set
        {
            if (_buildShape == value) return;
            CancelBuild();
            _buildShape = value;
            RefreshBuildPreview();
            UpdateModeLabel();
        }
    }

    public int BuildHeight
    {
        get => _buildHeight;
        set
        {
            _buildHeight = Math.Clamp(value, 1, 64);
            BuildHeightChanged?.Invoke(_buildHeight);
            RefreshBuildPreview();
        }
    }

    public bool BuildHollow
    {
        get => _buildHollow;
        set { _buildHollow = value; RefreshBuildPreview(); }
    }

    public int BuildMaterial
    {
        get => _buildMaterial;
        set { _buildMaterial = value; RefreshBuildPreview(); }
    }

    public Col BuildColor
    {
        get => _buildColor;
        set { _buildColor = value; RefreshBuildPreview(); }
    }

    public TerrainFoundationMode BuildFoundation
    {
        get => _buildFoundation;
        set { _buildFoundation = value; RefreshBuildPreview(); }
    }

    public float BuildFoundationDepth
    {
        get => _buildFoundationDepth;
        set => _buildFoundationDepth = Math.Clamp(value, 0.5f, 40f);
    }

    public double RotationSnap { get; set; } = 15;
    /// <summary>Grid keeps the classic behaviour; Face/Edge/Center snap to the surface
    /// under the cursor so blocks line up with existing geometry.</summary>
    private SmartSnapMode _snapMode = SmartSnapMode.Face;
    public SmartSnapMode SnapMode
    {
        get => _snapMode;
        set
        {
            _snapMode = value;
            UpdateModeLabel();
            RefreshBuildPreview();
            RefreshBrushPreview();
        }
    }
    public bool PlacementEnabled { get; set; }
    public int PlacementShape { get; set; } = ShapeInfo.Cube;
    public bool PlayMode { get; set; }
    public bool FlyNavigating => _orbiting;
    public bool LocalSpace
    {
        get => _localSpace;
        set { _localSpace = value; RefreshGizmo(); }
    }

    public bool MeasureActive => _tool == EditorTool.Measure;
    public bool PrefabActive => _tool == EditorTool.Prefab;
    public bool BuildActive => !PlayMode && _tool == EditorTool.Build;
    public bool BuildPolygonActive => BuildActive && _buildShape == BuildShape.Polygon;
    public string MeasureText => _measureText;

    public BlockSymmetry BrushSymmetry
    {
        get => _symmetry;
        set { _symmetry = value; RefreshBrushPreview(); }
    }

    public Vec3 SymmetryPivot
    {
        get => _symmetryPivot;
        set { _symmetryPivot = value; RefreshBrushPreview(); }
    }

    public BlockBrushMode BrushMode
    {
        get => _brushMode;
        set
        {
            if (_brushMode == value) return;
            CancelBrushStroke(clearPreview: true);
            _brushMode = value;
            UpdateModeLabel();
            RefreshGizmo();
            RefreshBrushPreview();
        }
    }

    public int BrushShape
    {
        get => _brushShape;
        set { _brushShape = value; RefreshBrushPreview(); }
    }

    public int BrushMaterial
    {
        get => _brushMaterial;
        set { _brushMaterial = value; RefreshBrushPreview(); }
    }

    public double BrushCellSize
    {
        get => _brushCellSize;
        set
        {
            _brushCellSize = Math.Clamp(value, 0.1, 8);
            RefreshBrushPreview();
        }
    }

    public bool BrushHollow
    {
        get => _brushHollow;
        set { _brushHollow = value; RefreshBrushPreview(); }
    }

    public int BrushLayers
    {
        get => _brushLayers;
        set { _brushLayers = Math.Clamp(value, 1, 64); RefreshBrushPreview(); }
    }

    public Col BrushColor
    {
        get => _brushColor;
        set { _brushColor = value; RefreshBrushPreview(); }
    }

    public bool BrushActive => !PlayMode && _tool == EditorTool.Brush && _brushMode != BlockBrushMode.None;
    public bool TerrainActive => !PlayMode && _tool == EditorTool.Terrain;
    public TerrainSculptMode TerrainMode
    {
        get => _terrainMode;
        set { _terrainMode = value; UpdateModeLabel(); RefreshTerrainPreview(); }
    }

    public TerrainBrushVolume TerrainVolume
    {
        get => _terrainVolume;
        set { _terrainVolume = value; UpdateModeLabel(); RefreshTerrainPreview(); }
    }

    public int TerrainRadius
    {
        get => _terrainRadius;
        set
        {
            _terrainRadius = Math.Clamp(value, 1, 16);
            RefreshTerrainPreview();
            UpdateModeLabel();
        }
    }

    public double TerrainCellSize
    {
        get => _terrainCellSize;
        set
        {
            _terrainCellSize = Math.Clamp(value, 0.25, 4.0);
            _terrainLastCell = (int.MaxValue, int.MaxValue, int.MaxValue);
            RefreshTerrainPreview();
            UpdateModeLabel();
        }
    }

    public float TerrainStrength
    {
        get => _terrainStrength;
        set { _terrainStrength = Math.Clamp(value, 0.05f, 1f); RefreshTerrainPreview(); }
    }

    public int TerrainMaterial
    {
        get => _terrainMaterial;
        set { _terrainMaterial = value; RefreshTerrainPreview(); }
    }

    public Col TerrainColor
    {
        get => _terrainColor;
        set { _terrainColor = value; RefreshTerrainPreview(); }
    }

    /// <summary>Camera-relative axes used by the play-mode character controller.</summary>
    public Vec3 PlayForward
    {
        get
        {
            var look = Camera.LookDirection;
            var f = new Vec3((float)look.X, 0, (float)look.Z).Normalized;
            return Math.Abs(f.X) + Math.Abs(f.Z) < 1e-4f ? new Vec3(0, 0, 1) : f;
        }
    }

    public Vec3 PlayRight
    {
        get
        {
            var f = PlayForward;
            return new Vec3(-f.Z, 0, f.X);
        }
    }

    public SceneView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateCamera();
            Focus();
        };
        // Use preview events on the control itself. A Viewport3D does not hit-test over
        // empty sky, and a 3D model can swallow bubbling mouse moves; either made the
        // camera feel randomly stuck. Preview events plus capture on the whole control
        // make orbit/pan/zoom work over models, sky, grid and empty space alike.
        PreviewMouseDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseUp += OnMouseUp;
        PreviewMouseWheel += OnWheel;
        LostMouseCapture += OnLostCapture;
        PreviewKeyDown += OnKeyDown;
        SizeChanged += (_, _) => UpdateCamera();

        ParticleVisual.Content = _particles.Root;
        SkyGradient.Update(_ambience, Camera);
        BuildLighting(_ambience);

        // Clouds drift and particle emitters run all the time in the editor, exactly
        // like Unity's scene view. 30 Hz keeps the UI responsive and the CPU cool.
        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnAnimate, Dispatcher);
        _animClock.Start();
        Unloaded += (_, _) => _animTimer.Stop();
        Loaded += (_, _) => { if (!_animTimer.IsEnabled) _animTimer.Start(); };
    }

    private void OnAnimate(object? sender, EventArgs e)
    {
        if (!IsVisible || _animClock == null) return;
        double now = _animClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastAnimTime, 0, 0.1);
        _lastAnimTime = now;
        if (dt <= 0) return;
        SkyGradient.Advance(dt);
        _particles.Advance(dt, Camera);
        if (!PlayMode && _orbiting) UpdateFly(dt);
    }

    /// <summary>Unity-style fly navigation: hold the right mouse button and use WASD,
    /// Space/Ctrl for up/down and Shift to boost. Orbit still works with the mouse.</summary>
    private void UpdateFly(double dt)
    {
        if (IsTextInputFocused()) return;
        double px = 0, py = 0, pz = 0;
        if (Keyboard.IsKeyDown(Key.W)) pz += 1;
        if (Keyboard.IsKeyDown(Key.S)) pz -= 1;
        if (Keyboard.IsKeyDown(Key.D)) px += 1;
        if (Keyboard.IsKeyDown(Key.A)) px -= 1;
        if (Keyboard.IsKeyDown(Key.Space)) py += 1;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) py -= 1;
        if (!(px == 0 && py == 0 && pz == 0))
        {
            double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
            var forward = new Vec3((float)(-Math.Sin(yaw) * Math.Cos(pitch)), (float)-Math.Sin(pitch), (float)(-Math.Cos(yaw) * Math.Cos(pitch)));
            var right = Vec3Cross(forward, new Vec3(0, 1, 0)).Normalized;
            var up = new Vec3(0, 1, 0);
            double speed = Math.Clamp(_distance * 0.9, 2.0, 45.0);
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) speed *= 2.5;
            var delta = (forward * (float)pz + right * (float)px + up * (float)py) * (float)(speed * dt);
            _target = _target + delta;
            UpdateCamera();
            RefreshGizmo();
        }
    }

    private static bool IsTextInputFocused()
    {
        var f = Keyboard.FocusedElement;
        if (f is System.Windows.Controls.Primitives.TextBoxBase) return true;
        var tn = f?.GetType().FullName;
        return tn != null && tn.StartsWith("ICSharpCode.AvalonEdit", StringComparison.Ordinal);
    }

    /// <summary>Shows a transient animation pose without modifying the level.</summary>
    public void PreviewPose(LevelNode node, Vec3 position, Quat rotation)
    {
        _previewNode = node;
        _previewPosition = position;
        _previewRotation = rotation;
        ApplyPreviewPose();
    }

    public void ClearPreviewPose()
    {
        if (_previewNode != null && _build != null && _build.Visuals.TryGetValue(_previewNode, out var visual))
            visual.Transform = Transform3D.Identity;
        _previewNode = null;
    }

    private void ApplyPreviewPose()
    {
        if (_previewNode == null || _build == null) return;
        if (!_build.Visuals.TryGetValue(_previewNode, out var visual)) return;
        var parent = _previewNode.Parent;
        var newWorldPosition = parent == null
            ? _previewPosition
            : parent.WorldPosition + parent.WorldRotation.Rotate(_previewPosition * parent.WorldScale);
        var newWorldRotation = parent == null ? _previewRotation : parent.WorldRotation * _previewRotation;
        var oldPosition = _previewNode.WorldPosition;
        var oldRotation = _previewNode.WorldRotation;
        var group = new Transform3DGroup();
        group.Children.Add(new TranslateTransform3D(-oldPosition.X, -oldPosition.Y, -oldPosition.Z));
        group.Children.Add(new RotateTransform3D(new QuaternionRotation3D(QuatToQuaternion(oldRotation.Inverse() * newWorldRotation))));
        group.Children.Add(new TranslateTransform3D(newWorldPosition.X, newWorldPosition.Y, newWorldPosition.Z));
        visual.Transform = group;
    }

    private static Quaternion QuatToQuaternion(Quat q) => new(q.X, q.Y, q.Z, q.W);

    public GrabLevel? Level => _level;
    public LevelNode? Selected => _selected;
    public IReadOnlyList<LevelNode> Selection => _selection;
    public Vec3 ViewTarget => _target;

    public void SetLevel(GrabLevel level, bool frame = true)
    {
        CancelBrushStroke(clearPreview: true);
        ClearPrefab();
        _level = level;
        _selected = null;
        _selection.Clear();
        _particles.Reset(level);
        if (frame) FrameAll();
        Refresh();
    }

    public void SetTool(EditorTool tool)
    {
        _tool = tool;
        if (tool == EditorTool.Brush && _brushMode == BlockBrushMode.None)
            _brushMode = BlockBrushMode.Fill;
        if (tool != EditorTool.Build) CancelBuild();
        if (tool != EditorTool.Terrain) CancelTerrainStroke();
        Cursor = tool is EditorTool.Brush or EditorTool.Terrain or EditorTool.Measure or EditorTool.Prefab or EditorTool.Build
            ? Cursors.Cross : null;
        if (tool != EditorTool.Prefab) HidePrefabPreview();
        if (tool == EditorTool.Measure)
        {
            _measureText = "Click two points to measure.";
            DrawMeasure();
        }
        else if (_measureStart != null || _measureEnd != null)
        {
            ClearMeasure();
        }
        UpdateModeLabel();
        RefreshGizmo();
        RefreshBrushPreview();
        RefreshBuildPreview();
        RefreshTerrainPreview();
    }

    private void UpdateModeLabel()
    {
        string mode = _tool == EditorTool.Brush
            ? "Brush • " + BlockBrushModeInfo.Display(_brushMode)
            : _tool == EditorTool.Terrain
                ? "Terrain • " + _terrainMode + " • " + _terrainVolume
            : _tool == EditorTool.Build
                ? "Draw • " + _buildShape + (SnapMode == SmartSnapMode.Face ? " • face snap" : "")
                : _tool + (PlacementEnabled ? " • Place" : "");
        ModeLabel.Text = mode;
        ModeHint.Text = _tool switch
        {
            EditorTool.Brush => "  •  LMB paint   RMB orbit   MMB pan   Wheel zoom   Esc cancel",
            EditorTool.Terrain => "  •  LMB sculpt   RMB orbit   MMB pan   Wheel zoom   [ / ] brush size   Esc cancel",
            EditorTool.Measure => "  •  LMB set points   Esc clear   RMB orbit   Wheel zoom",
            EditorTool.Prefab => "  •  LMB stamp   [ / ] rotate 90°   RMB orbit   Wheel zoom",
            EditorTool.Build => _buildShape == BuildShape.Polygon
                ? "  •  LMB add point   double-click/Enter build   Esc cancel   [ / ] height"
                : "  •  LMB drag footprint   [ / ] height   Esc cancel   Wheel zoom",
            _ => "  •  RMB orbit   RMB + WASD fly   MMB pan   Wheel zoom   F focus"
        };
        UpdateMeasureOverlay();
    }

    /// <summary>Replaces the whole selection with one object.</summary>
    public void SetSelection(LevelNode? node)
    {
        if (node != null && _level != null && _level.IsLayerLocked(node)) node = null;
        bool same = _selection.Count == (node == null ? 0 : 1) && (node == null || ReferenceEquals(_selection[0], node));
        if (same && ReferenceEquals(_selected, node)) return;
        _selection.Clear();
        if (node != null) _selection.Add(node);
        _selected = node;
        NotifySelection();
    }

    /// <summary>Replaces the selection with a set of objects; the last one is active.</summary>
    public void SetSelectionRange(IEnumerable<LevelNode> nodes)
    {
        _selection.Clear();
        foreach (var n in nodes.Distinct())
            if (n.Kind != NodeKind.Group || n.Parent == null || !_selection.Contains(n.Parent))
                _selection.Add(n);
        _selected = _selection.Count > 0 ? _selection[^1] : null;
        NotifySelection();
    }

    /// <summary>Ctrl-click adds/removes one object without losing the rest.</summary>
    public void ToggleSelection(LevelNode node)
    {
        if (_selection.Remove(node))
            _selected = _selection.Count > 0 ? _selection[^1] : null;
        else
        {
            _selection.Add(node);
            _selected = node;
        }
        NotifySelection();
    }

    public void AddToSelection(LevelNode node)
    {
        if (!_selection.Contains(node)) _selection.Add(node);
        _selected = node;
        NotifySelection();
    }

    /// <summary>Selected objects whose parent is not also selected. Transforming a parent
    /// already moves its children, so only roots take part in a gizmo drag.</summary>
    public List<LevelNode> TopLevelSelection()
    {
        var selected = new HashSet<LevelNode>(_selection);
        return _selection.Where(n => n.Parent == null || !selected.Contains(n.Parent)).ToList();
    }

    private void NotifySelection()
    {
        Refresh();
        SelectionChanged?.Invoke(_selected);
        SelectionSetChanged?.Invoke(_selection.ToArray());
    }

    public void Refresh()
    {
        if (_level == null)
        {
            LevelVisual.Content = null;
            GizmoVisual.Content = null;
            StatsLabel.Text = "No level";
            return;
        }
        _build = SceneBuilder.Build(_level, _selection, _selected, showGrid: !PlayMode);
        var root = new Model3DGroup();
        root.Children.Add(_build.Group);
        root.Children.Add(_lighting);
        LevelVisual.Content = root;
        StatsLabel.Text = $"{_level.AllNodes().Count()} objects";
        ApplyPreviewPose();
        RefreshGizmo();
        // The scene changed, so any cached fill island / preview geometry is stale.
        _brushPreviewState = null;
        _brushFillPreviewNode = null;
        RefreshBrushPreview();
        _terrainPreviewKey = "";
        RefreshTerrainPreview();
    }

    // ================= play mode view =================

    public void BeginPlay(Vec3 playerCenter, float yaw)
    {
        _editorYaw = _yaw;
        _editorPitch = _pitch;
        _editorDistance = _distance;
        _editorTarget = _target;
        ShowPlayer(true);
        _target = new Vec3(playerCenter.X, playerCenter.Y + 0.2f, playerCenter.Z);
        _yaw = yaw + 180.0;
        _pitch = 12;
        _distance = 4.6;
        UpdateCamera();
        SetPlayerPose(playerCenter, yaw, 0, true);
    }

    public void EndPlay()
    {
        ShowPlayer(false);
        _yaw = _editorYaw;
        _pitch = _editorPitch;
        _distance = _editorDistance;
        _target = _editorTarget;
        UpdateCamera();
    }

    public void ShowPlayer(bool visible)
    {
        if (!visible)
        {
            PlayerVisual.Content = null;
            return;
        }
        _playerGroup ??= PlayerModel.Create();
        PlayerVisual.Content = _playerGroup;
    }

    public int PlayerModelPartCount => _playerGroup?.Children.Count ?? 0;
    public string CameraPositionInfo => $"{Camera.Position} look {Camera.LookDirection}";

    /// <summary>Places the real player avatar at the simulated capsule centre.</summary>
    public void SetPlayerPose(Vec3 center, float yaw, float bob, bool grounded)
    {
        if (_playerGroup == null) return;
        var group = new Transform3DGroup();
        // GRAB's avatar is authored facing -Z (the visor sits on the -Z side), while
        // the controller's yaw uses +Z as forward. Flip the mesh so the character
        // faces the direction it actually walks.
        group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), yaw + 180)));
        group.Children.Add(new TranslateTransform3D(center.X, center.Y - 0.85f + bob, center.Z));
        group.Freeze();
        _playerGroup.Transform = group;
    }

    public void UpdatePlayView(Vec3 playerCenter, float yaw, float bob, bool grounded, float dt)
    {
        _target = new Vec3(playerCenter.X, playerCenter.Y + 0.2f, playerCenter.Z);
        SetPlayerPose(playerCenter, yaw, bob, grounded);
        UpdateCamera();
    }

    private void RefreshGizmo()
    {
        _gizmoModels.Clear();
        if (_level == null || _selection.Count == 0 || _tool is EditorTool.Select or EditorTool.Measure or EditorTool.Prefab || BrushActive || BuildActive || TerrainActive)
        {
            GizmoVisual.Content = null;
            return;
        }
        Vec3 center = SelectionCenter();
        double gs = _gizmoScale = Math.Clamp(_distance * 0.085, 0.5, 4.5);
        var group = new Model3DGroup();
        // The gizmo viewport is composited separately from the scene and has no scene
        // lights. Give it its own ambient fill so diffuse handles never render black.
        group.Children.Add(new AmbientLight(Color.FromRgb(230, 230, 230)));
        var axes = GizmoAxes();
        if (_tool == EditorTool.Move || _tool == EditorTool.Scale)
        {
            var colors = new[] { new Col(0.9f, 0.18f, 0.16f), new Col(0.25f, 0.85f, 0.28f), new Col(0.2f, 0.45f, 1f) };
            for (int i = 0; i < 3; i++)
            {
                var mesh = MeshFactory.Arrow(new Vector3D(axes[i].X, axes[i].Y, axes[i].Z), gs * 1.45, gs * 0.035);
                var geo = new GeometryModel3D(mesh, SceneMaterial(colors[i])) { BackMaterial = SceneMaterial(colors[i]) };
                geo.Transform = new TranslateTransform3D(center.X, center.Y, center.Z);
                group.Children.Add(geo);
                _gizmoModels[geo] = i;
                if (_tool == EditorTool.Scale)
                {
                    var box = new MeshBuilder();
                    box.Box(new Point3D(axes[i].X * gs * 1.45, axes[i].Y * gs * 1.45, axes[i].Z * gs * 1.45), new Vector3D(gs * 0.12, gs * 0.12, gs * 0.12));
                    var bgeo = new GeometryModel3D(box.ToMesh(), SceneMaterial(colors[i])) { BackMaterial = SceneMaterial(colors[i]) };
                    bgeo.Transform = new TranslateTransform3D(center.X, center.Y, center.Z);
                    group.Children.Add(bgeo);
                    _gizmoModels[bgeo] = i;
                }
            }
        }
        else if (_tool == EditorTool.Rotate)
        {
            var colors = new[] { new Col(0.95f, 0.18f, 0.16f), new Col(0.25f, 0.9f, 0.3f), new Col(0.18f, 0.45f, 1f) };
            for (int i = 0; i < 3; i++)
            {
                var normal = new Vector3D(axes[i].X, axes[i].Y, axes[i].Z);
                var geo = new GeometryModel3D(MeshFactory.Ring(normal, gs * 1.3, gs * 0.065), SceneMaterial(colors[i]))
                { BackMaterial = SceneMaterial(colors[i]) };
                geo.Transform = new TranslateTransform3D(center.X, center.Y, center.Z);
                group.Children.Add(geo);
                _gizmoModels[geo] = i;
            }
        }
        GizmoVisual.Content = group;
    }

    private Vec3[] GizmoAxes()
    {
        if (!_localSpace || _selection.Count == 0)
            return new[] { Vec3.Right, Vec3.Up, Vec3.Forward };
        var rotation = (_selected ?? _selection[^1]).WorldRotation;
        return new[] { rotation.Rotate(Vec3.Right), rotation.Rotate(Vec3.Up), rotation.Rotate(Vec3.Forward) };
    }

    private static Material SceneMaterial(Col c)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(c.Ai, 0, 255), (byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
        brush.Freeze();
        var g = new MaterialGroup();
        g.Children.Add(new EmissiveMaterial(brush));
        g.Children.Add(new DiffuseMaterial(brush));
        g.Freeze();
        return g;
    }

    public Vec3 SelectionCenter()
    {
        var nodes = TopLevelSelection();
        if (nodes.Count == 0) return _selected?.WorldPosition ?? Vec3.Zero;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var n in nodes)
        {
            var (c, s) = SceneBuilder.BoundsOf(n);
            var half = new Vec3(Math.Abs(s.X) * 0.5f, Math.Abs(s.Y) * 0.5f, Math.Abs(s.Z) * 0.5f);
            minX = MathF.Min(minX, c.X - half.X); minY = MathF.Min(minY, c.Y - half.Y); minZ = MathF.Min(minZ, c.Z - half.Z);
            maxX = MathF.Max(maxX, c.X + half.X); maxY = MathF.Max(maxY, c.Y + half.Y); maxZ = MathF.Max(maxZ, c.Z + half.Z);
        }
        return new Vec3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
    }

    private (Vec3 Center, Vec3 Size) SelectionBounds()
    {
        var nodes = TopLevelSelection();
        if (nodes.Count == 0) return (_selected?.WorldPosition ?? Vec3.Zero, Vec3.One);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var n in nodes)
        {
            var (c, s) = SceneBuilder.BoundsOf(n);
            var half = new Vec3(Math.Abs(s.X) * 0.5f, Math.Abs(s.Y) * 0.5f, Math.Abs(s.Z) * 0.5f);
            minX = MathF.Min(minX, c.X - half.X); minY = MathF.Min(minY, c.Y - half.Y); minZ = MathF.Min(minZ, c.Z - half.Z);
            maxX = MathF.Max(maxX, c.X + half.X); maxY = MathF.Max(maxY, c.Y + half.Y); maxZ = MathF.Max(maxZ, c.Z + half.Z);
        }
        return (new Vec3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
            new Vec3(maxX - minX, maxY - minY, maxZ - minZ));
    }

    /// <summary>Rounds every selected root's world position to the active grid step.</summary>
    /// <summary>Computes where a 1×1×1 block (or a prefab footprint) should land for the
    /// current smart-snap mode. Face/Edge/Center use the hit surface instead of the ground
    /// plane, which is what makes building against walls and existing blocks predictable.</summary>
    private Vec3 SnapPlacement(Point3D point, Vector3D normal, LevelNode? hitNode, Vec3 size)
    {
        var n = new Vec3((float)normal.X, (float)normal.Y, (float)normal.Z);
        if (MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z) < 1e-3f) n = Vec3.Up;
        // Side faces do not have a meaningful Y offset. Keep the new object on the same
        // block layer instead of letting the hit point put it half a block off-grid.
        double sideY = Math.Floor(point.Y) + size.Y * 0.5;
        switch (SnapMode)
        {
            case SmartSnapMode.Face:
                return new Vec3((float)(point.X + n.X * size.X * 0.5),
                    (float)(Math.Abs(n.Y) > 0.5 ? point.Y + n.Y * size.Y * 0.5 : sideY),
                    (float)(point.Z + n.Z * size.Z * 0.5));
            case SmartSnapMode.Edge:
            {
                var q = new Vec3((float)Math.Round(point.X / 0.25) * 0.25f,
                    (float)(Math.Abs(n.Y) > 0.5 ? point.Y : sideY), (float)Math.Round(point.Z / 0.25) * 0.25f);
                return new Vec3(q.X + n.X * size.X * 0.5f,
                    (float)(q.Y + (Math.Abs(n.Y) > 0.5 ? n.Y * size.Y * 0.5 : 0)),
                    q.Z + n.Z * size.Z * 0.5f);
            }
            case SmartSnapMode.Center:
                if (hitNode == null) return new Vec3((float)Snap(point.X), (float)(size.Y * 0.5), (float)Snap(point.Z));
                return hitNode.WorldPosition + new Vec3(
                    (float)(n.X * (Math.Abs(hitNode.WorldScale.X) * 0.5 + size.X * 0.5)),
                    (float)(n.Y * (Math.Abs(hitNode.WorldScale.Y) * 0.5 + size.Y * 0.5)),
                    (float)(n.Z * (Math.Abs(hitNode.WorldScale.Z) * 0.5 + size.Z * 0.5)));
            default:
                return new Vec3((float)Snap(point.X), (float)(size.Y * 0.5), (float)Snap(point.Z));
        }
    }

    private bool TrySurfacePick(Point pt, out Point3D point, out Vector3D normal, out LevelNode? node)
    {
        var hit = HitTestLevel(pt);
        if (hit is RayMeshGeometry3DHitTestResult rm)
        {
            point = rm.PointHit;
            normal = MeshHitNormal(rm, pt);
            node = ResolveNode(rm);
            return true;
        }
        var ground = GroundPoint(pt);
        if (ground is Point3D g)
        {
            point = g;
            normal = new Vector3D(0, 1, 0);
            node = null;
            return true;
        }
        point = default;
        normal = default;
        node = null;
        return false;
    }

    /// <summary>Adds a generated structure/shape to the level as one undoable group.</summary>
    public List<LevelNode> AddStructure(IReadOnlyList<LevelNode> nodes, string name)
    {
        var added = new List<LevelNode>();
        if (_level == null || nodes.Count == 0) return added;
        if (nodes.Count == 1)
        {
            var single = nodes[0];
            _level.AddNode(single);
            added.Add(single);
        }
        else
        {
            var group = new LevelNode
            {
                Kind = NodeKind.Group,
                Name = string.IsNullOrWhiteSpace(name) ? "Structure" : name,
                Position = Vec3.Zero,
                Scale = Vec3.One,
                EditorLabel = "structure"
            };
            _level.Nodes.Add(group);
            group.RebuildParents();
            foreach (var node in nodes)
            {
                group.Children.Add(node);
                node.RebuildParents(group);
                added.Add(node);
            }
        }
        _level.EnsureNames();
        _level.IsDirty = true;
        Refresh();
        if (added.Count > 0) SetSelectionRange(added);
        SceneModified?.Invoke();
        return added;
    }

    // ================= build validation overlay =================

    public void ShowValidation(IReadOnlyList<BuildIssue> issues)
    {
        _validationIssues.Clear();
        _validationIssues.AddRange(issues);
        RefreshValidationVisual();
    }

    public void ClearValidation()
    {
        _validationIssues.Clear();
        if (ValidationVisual != null) ValidationVisual.Content = null;
    }

    // ================= pathfinding overlay =================

    /// <summary>Shows a live A* route in the overlay viewport. Pass null to clear it.</summary>
    public void ShowPath(PathfindingResult? path, bool showWaypoints = true)
    {
        if (path == null || !path.Success || path.Points.Count == 0)
        {
            ClearPath();
            return;
        }
        _pathPreviewWaypoints = showWaypoints;
        var signature = new System.Text.StringBuilder();
        signature.Append(path.Points.Count).Append(':').Append(showWaypoints)
            .Append(':').Append(path.NavCells.Count).Append(':').Append(path.WatchWalls.Count)
            .Append(':').Append(path.NavCellSize.ToString("0.###"));
        foreach (var p in path.Points)
            signature.Append(':').Append(p.X.ToString("0.###")).Append(',').Append(p.Y.ToString("0.###")).Append(',').Append(p.Z.ToString("0.###"));
        string sig = signature.ToString();
        if (sig == _pathPreviewSignature) return;
        _pathPreview = path;
        _pathPreviewSignature = sig;
        RefreshPathVisual();
    }

    public void ClearPath()
    {
        _pathPreview = null;
        _pathPreviewSignature = "";
        if (PathVisual != null) PathVisual.Content = null;
        if (NavVisual != null) NavVisual.Content = null;
    }

    /// <summary>
    /// Walkable navmesh tiles plus the objects a baked live planner would watch. Drawn in
    /// the overlay viewport so the grid reads through geometry, and built as one merged
    /// mesh per colour so thousands of cells stay cheap.
    /// </summary>
    private void RefreshNavVisual()
    {
        if (NavVisual == null) return;
        var nav = _pathPreview;
        if (nav == null || nav.NavCells.Count == 0)
        {
            NavVisual.Content = null;
            return;
        }
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(235, 235, 235)));
        float cell = MathF.Max(0.1f, nav.NavCellSize) * 0.9f;
        var tiles = new MeshBuilder();
        foreach (var c in nav.NavCells)
        {
            double x0 = c.X - cell * 0.5, x1 = c.X + cell * 0.5;
            double z0 = c.Z - cell * 0.5, z1 = c.Z + cell * 0.5;
            double y = c.Y + 0.04;
            tiles.Quad(new Point3D(x0, y, z0), new Point3D(x0, y, z1),
                new Point3D(x1, y, z1), new Point3D(x1, y, z0), new Vector3D(0, 1, 0));
        }
        var tileMaterial = SceneMaterial(new Col(0.22f, 0.66f, 1f, 0.28f));
        group.Children.Add(new GeometryModel3D(tiles.ToMesh(), tileMaterial) { BackMaterial = tileMaterial });

        if (nav.WatchWalls.Count > 0)
        {
            var boxes = new MeshBuilder();
            foreach (var wall in nav.WatchWalls)
            {
                var (c, s) = SceneBuilder.BoundsOf(wall);
                boxes.WireBox(new Point3D(c.X, c.Y, c.Z),
                    new Vector3D(Math.Max(0.06, s.X + 0.06), Math.Max(0.06, s.Y + 0.06), Math.Max(0.06, s.Z + 0.06)), 0.035);
            }
            var watchMaterial = SceneMaterial(new Col(1f, 0.55f, 0.15f, 0.85f));
            group.Children.Add(new GeometryModel3D(boxes.ToMesh(), watchMaterial) { BackMaterial = watchMaterial });
        }
        NavVisual.Content = group;
    }

    private void RefreshPathVisual()
    {
        if (PathVisual == null) return;
        if (_pathPreview == null || !_pathPreview.Success || _pathPreview.Points.Count == 0)
        {
            PathVisual.Content = null;
            return;
        }
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(230, 230, 230)));
        var cyan = new Col(0.12f, 0.86f, 1f);
        var lime = new Col(0.3f, 1f, 0.48f);
        var amber = new Col(1f, 0.62f, 0.12f);
        var line = new MeshBuilder();
        var points = _pathPreview.Points;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1] + new Vec3(0, 0.12f, 0);
            var b = points[i] + new Vec3(0, 0.12f, 0);
            line.Line(new Point3D(a.X, a.Y, a.Z), new Point3D(b.X, b.Y, b.Z), 0.075);
        }
        var lineMat = SceneMaterial(cyan);
        group.Children.Add(new GeometryModel3D(line.ToMesh(), lineMat) { BackMaterial = lineMat });

        if (_pathPreviewWaypoints)
        {
            foreach (var p in points.Skip(1).Take(Math.Max(0, points.Count - 2)))
            {
                var dot = new MeshBuilder();
                dot.Box(new Point3D(p.X, p.Y + 0.14, p.Z), new Vector3D(0.16, 0.16, 0.16));
                var mat = SceneMaterial(cyan);
                group.Children.Add(new GeometryModel3D(dot.ToMesh(), mat) { BackMaterial = mat });
            }
        }
        var start = new MeshBuilder();
        start.Box(new Point3D(points[0].X, points[0].Y + 0.16, points[0].Z), new Vector3D(0.28, 0.28, 0.28));
        var startMat = SceneMaterial(lime);
        group.Children.Add(new GeometryModel3D(start.ToMesh(), startMat) { BackMaterial = startMat });

        var goal = new MeshBuilder();
        goal.Circle(new Point3D(points[^1].X, points[^1].Y + 0.14, points[^1].Z), new Vector3D(0, 1, 0), 0.38, 0.075, 28);
        goal.Line(new Point3D(points[^1].X, points[^1].Y + 0.05, points[^1].Z),
            new Point3D(points[^1].X, points[^1].Y + 0.9, points[^1].Z), 0.07);
        var goalMat = SceneMaterial(amber);
        group.Children.Add(new GeometryModel3D(goal.ToMesh(), goalMat) { BackMaterial = goalMat });
        PathVisual.Content = group;
        RefreshNavVisual();
    }

    private void RefreshValidationVisual()
    {
        if (ValidationVisual == null) return;
        if (_validationIssues.Count == 0) { ValidationVisual.Content = null; return; }
        var group = new Model3DGroup();
        foreach (var issue in _validationIssues.Take(120))
        {
            var color = issue.Kind == BuildIssueKind.Error ? new Col(1f, 0.22f, 0.18f)
                : issue.Kind == BuildIssueKind.Warning ? new Col(1f, 0.72f, 0.15f) : new Col(0.35f, 0.75f, 1f);
            var scale = new Vec3(MathF.Max(0.25f, issue.Size.X + 0.08f),
                MathF.Max(0.25f, issue.Size.Y + 0.08f), MathF.Max(0.25f, issue.Size.Z + 0.08f));
            var mesh = new MeshBuilder();
            mesh.Box(new Point3D(issue.Position.X, issue.Position.Y, issue.Position.Z),
                new Vector3D(scale.X, scale.Y, scale.Z));
            var material = SceneMaterial(color);
            group.Children.Add(new GeometryModel3D(mesh.ToMesh(), material) { BackMaterial = material });
        }
        ValidationVisual.Content = group;
    }

    public void SnapSelectionToGrid()
    {
        if (_level == null || _selection.Count == 0) return;
        foreach (var node in TopLevelSelection())
        {
            var p = node.WorldPosition;
            var snapped = new Vec3((float)Snap(p.X), (float)Snap(p.Y), (float)Snap(p.Z));
            SetWorldPosition(node, snapped);
        }
        _level.IsDirty = true;
        Refresh();
        SceneModified?.Invoke();
    }

    /// <summary>Aligns the selected roots to the selection's min/center/max on one axis.</summary>
    public void AlignSelection(int axis, int mode)
    {
        var nodes = TopLevelSelection();
        if (nodes.Count < 1) return;
        var (allCenter, allSize) = SelectionBounds();
        double target = mode switch
        {
            0 => allCenter[axis] - allSize[axis] * 0.5,
            2 => allCenter[axis] + allSize[axis] * 0.5,
            _ => allCenter[axis]
        };
        foreach (var node in nodes)
        {
            var (center, size) = SceneBuilder.BoundsOf(node);
            double current = mode switch
            {
                0 => center[axis] - Math.Abs(size[axis]) * 0.5,
                2 => center[axis] + Math.Abs(size[axis]) * 0.5,
                _ => center[axis]
            };
            var world = node.WorldPosition;
            var delta = new Vec3(0, 0, 0);
            delta[axis] = (float)(target - current);
            SetWorldPosition(node, world + delta);
        }
        _level!.IsDirty = true;
        Refresh();
        SceneModified?.Invoke();
    }

    /// <summary>Spreads the selected roots evenly between the first and last object.</summary>
    public void DistributeSelection(int axis)
    {
        var nodes = TopLevelSelection();
        if (nodes.Count < 3) return;
        var ordered = nodes.OrderBy(n => n.WorldPosition[axis]).ToList();
        float first = ordered[0].WorldPosition[axis];
        float last = ordered[^1].WorldPosition[axis];
        double step = (last - first) / (ordered.Count - 1);
        for (int i = 1; i < ordered.Count - 1; i++)
        {
            var world = ordered[i].WorldPosition;
            world[axis] = (float)(first + step * i);
            SetWorldPosition(ordered[i], world);
        }
        _level!.IsDirty = true;
        Refresh();
        SceneModified?.Invoke();
    }

    public void OffsetNodeWorld(LevelNode node, Vec3 delta) => SetWorldPosition(node, node.WorldPosition + delta);

    /// <summary>Mirrors the selection across the brush symmetry pivot. Optionally
    /// creates a duplicate, which is the useful mode for building an asymmetric map
    /// from one half.</summary>
    public List<LevelNode> MirrorSelection(int axis, bool duplicate)
    {
        var nodes = TopLevelSelection();
        var result = new List<LevelNode>();
        foreach (var source in nodes)
        {
            var target = duplicate ? source.Clone() : source;
            if (duplicate)
            {
                target.Name = "";
                _level!.AddNode(target, source.Parent);
            }
            var world = target.WorldPosition;
            world[axis] = _symmetryPivot[axis] * 2f - world[axis];
            SetWorldPosition(target, world);
            var q = target.WorldRotation;
            SetWorldRotation(target, axis == 0
                ? new Quat(q.X, -q.Y, -q.Z, q.W)
                : new Quat(-q.X, -q.Y, q.Z, q.W));
            result.Add(target);
        }
        _level?.EnsureNames();
        Refresh();
        SceneModified?.Invoke();
        return result;
    }

    public void FrameAll()
    {
        if (_level == null) return;
        // Ignore hidden helper objects (GASM buses live at y = -2000 and generated
        // animation triggers at y = -9999). Framing them would zoom away from the
        // actual level, which is what happened to every split neural script.
        var hiddenLayers = _level.EditorLayers.Where(l => !l.Visible).Select(l => l.Id).ToHashSet();
        var visible = _level.AllNodes()
            .Where(n => n.Kind != NodeKind.Group && !n.HideModel && !n.IsHidden &&
                        !hiddenLayers.Contains(n.LayerId) && !(n.Generated && n.ScriptCreated))
            .ToList();
        var nodes = visible.Count > 0 ? visible : _level.AllNodes().Where(n => n.Kind != NodeKind.Group).ToList();
        // Last line of defence for imported split levels, where a helper's hidden flag
        // is not stored in the format: ignore anything parked far below the world.
        var grounded = nodes.Where(n => n.WorldPosition.Y > -500f).ToList();
        if (grounded.Count > 0) nodes = grounded;
        if (nodes.Count == 0) { _target = new Vec3(0, 0.5f, 0); _distance = 12; UpdateCamera(); return; }
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var n in nodes)
        {
            var p = n.WorldPosition;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
        }
        _target = new Vec3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _distance = Math.Clamp(Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) * 1.25 + 5, 5, 400);
        UpdateCamera();
    }

    public void FocusSelected()
    {
        if (_selection.Count == 0) { FrameAll(); return; }
        var (c, s) = SelectionBounds();
        _target = c;
        _distance = Math.Clamp(Math.Max(s.X, Math.Max(s.Y, s.Z)) * 2.6 + 2.5, 1.5, 300);
        UpdateCamera();
        RefreshGizmo();
    }

    // ================= measure =================

    private Point3D SnapMeasurePoint(Point3D p)
    {
        if (!IsSnapping) return p;
        return new Point3D(Snap(p.X), Snap(p.Y), Snap(p.Z));
    }

    private void HandleMeasureClick(Point pt)
    {
        if (!TrySurfacePoint(pt, out var point, out _)) return;
        point = SnapMeasurePoint(point);
        if (_measureStart == null || _measureEnd != null)
        {
            _measureStart = point;
            _measureEnd = null;
            _measureText = $"Start  {point.X:0.##}, {point.Y:0.##}, {point.Z:0.##}  —  click the second point";
        }
        else
        {
            _measureEnd = point;
            var a = _measureStart.Value;
            var d = point - a;
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            var measurement = StructureBuilder.Measure(new Vec3((float)a.X, (float)a.Y, (float)a.Z), new Vec3((float)point.X, (float)point.Y, (float)point.Z));
            string clearance = "";
            if (_level != null)
            {
                var fit = StructureBuilder.Clearance(_level, measurement.Start, measurement.End);
                clearance = fit.Fits ? "  •  player clearance OK" : "  •  BLOCKED at midpoint";
            }
            _measureText = $"{measurement.Distance:0.###} m   ·   ΔX {d.X:0.##}  ΔY {d.Y:0.##}  ΔZ {d.Z:0.##}   ·   {measurement.AngleDegrees:0}°{clearance}";
        }
        DrawMeasure();
        MeasurementChanged?.Invoke(_measureText);
    }

    public void ClearMeasure()
    {
        _measureStart = null;
        _measureEnd = null;
        _measureText = "Click two points to measure.";
        if (MeasureVisual != null) MeasureVisual.Content = null;
        UpdateMeasureOverlay();
        MeasurementChanged?.Invoke(_measureText);
    }

    private void DrawMeasure()
    {
        if (MeasureVisual == null) return;
        if (_measureStart == null)
        {
            MeasureVisual.Content = null;
            UpdateMeasureOverlay();
            return;
        }
        var group = new Model3DGroup();
        var a = _measureStart.Value;
        var b = _measureEnd ?? _measureStart.Value;
        var marker = new MeshBuilder();
        double size = Math.Clamp(_distance * 0.018, 0.035, 0.16);
        marker.Box(a, new Vector3D(size, size, size));
        if (_measureEnd != null) marker.Box(b, new Vector3D(size, size, size));
        if (_measureEnd != null)
        {
            var line = new MeshBuilder();
            line.Line(a, b, Math.Max(0.015, size * 0.28));
            var lineMaterial = SceneMaterial(new Col(1f, 0.82f, 0.2f));
            group.Children.Add(new GeometryModel3D(line.ToMesh(), lineMaterial) { BackMaterial = lineMaterial });
        }
        var markerMaterial = SceneMaterial(new Col(0.2f, 1f, 0.55f));
        group.Children.Add(new GeometryModel3D(marker.ToMesh(), markerMaterial) { BackMaterial = markerMaterial });
        MeasureVisual.Content = group;
        UpdateMeasureOverlay();
    }

    private void UpdateMeasureOverlay()
    {
        if (MeasureBox == null || MeasureLabel == null) return;
        MeasureBox.Visibility = MeasureActive && (_measureStart != null || _measureText.StartsWith("Click", StringComparison.Ordinal)) ? Visibility.Visible : Visibility.Collapsed;
        MeasureLabel.Text = _measureText;
    }

    // ================= prefab stamp =================

    public void SetPrefabTemplate(GrabLevel? template, string name)
    {
        _prefabTemplate = template;
        _prefabName = name;
        _prefabYaw = 0;
        _prefabPlacementValid = false;
        _prefabPreviewGroup = null;
        if (PrefabPreviewVisual != null) PrefabPreviewVisual.Content = null;
        if (template == null) return;
        var nodes = template.Nodes.Where(n => n.Kind != NodeKind.Group).ToList();
        if (nodes.Count == 0) nodes = template.Nodes.ToList();
        if (nodes.Count == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var n in nodes)
        {
            var (c, s) = SceneBuilder.BoundsOf(n);
            var h = new Vec3(Math.Abs(s.X) * 0.5f, Math.Abs(s.Y) * 0.5f, Math.Abs(s.Z) * 0.5f);
            minX = MathF.Min(minX, c.X - h.X); minY = MathF.Min(minY, c.Y - h.Y); minZ = MathF.Min(minZ, c.Z - h.Z);
            maxX = MathF.Max(maxX, c.X + h.X); maxY = MathF.Max(maxY, c.Y + h.Y); maxZ = MathF.Max(maxZ, c.Z + h.Z);
        }
        _prefabBoundsMin = new Vec3(minX, minY, minZ);
        _prefabBoundsMax = new Vec3(maxX, maxY, maxZ);
        var build = SceneBuilder.Build(template, (IReadOnlyCollection<LevelNode>?)null, null, showGrid: false);
        _prefabPreviewGroup = build.Group;
        _prefabPreviewPosition = new Vec3((float)((minX + maxX) * 0.5f), (float)-minY, (float)((minZ + maxZ) * 0.5f));
        _prefabPreviewRotation = Quat.FromAxisAngle(Vec3.Up, (float)_prefabYaw);
        ApplyPrefabTransform();
        PrefabPreviewVisual.Content = _prefabPreviewGroup;
    }

    public void RotatePrefab(double degrees)
    {
        _prefabYaw = (_prefabYaw + degrees) % 360;
        _prefabPreviewRotation = Quat.FromAxisAngle(Vec3.Up, (float)_prefabYaw);
        ApplyPrefabTransform();
    }

    private void ApplyPrefabTransform()
    {
        if (_prefabPreviewGroup == null) return;
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new QuaternionRotation3D(QuatToQuaternion(_prefabPreviewRotation))));
        transform.Children.Add(new TranslateTransform3D(_prefabPreviewPosition.X, _prefabPreviewPosition.Y, _prefabPreviewPosition.Z));
        transform.Freeze();
        _prefabPreviewGroup.Transform = transform;
    }

    private void UpdatePrefabPreview(Point pt)
    {
        if (_prefabTemplate == null || _prefabPreviewGroup == null) return;
        if (!TrySurfacePoint(pt, out var point, out var normal))
        {
            _prefabPlacementValid = false;
            PrefabPreviewVisual.Content = null;
            return;
        }
        var size = new Vec3(Math.Max(0.1f, _prefabBoundsMax.X - _prefabBoundsMin.X),
            Math.Max(0.1f, _prefabBoundsMax.Y - _prefabBoundsMin.Y),
            Math.Max(0.1f, _prefabBoundsMax.Z - _prefabBoundsMin.Z));
        var anchor = SnapPlacement(point, normal, null, size);
        _prefabPreviewPosition = new Vec3(anchor.X,
            (float)(anchor.Y - size.Y * 0.5 - _prefabBoundsMin.Y), anchor.Z);
        _prefabPlacementValid = true;
        ApplyPrefabTransform();
        PrefabPreviewVisual.Content = _prefabPreviewGroup;
    }

    private void HidePrefabPreview()
    {
        _prefabPlacementValid = false;
        if (PrefabPreviewVisual != null) PrefabPreviewVisual.Content = null;
    }

    private bool TrySurfacePoint(Point pt, out Point3D point, out Vector3D normal)
    {
        var hit = HitTestLevel(pt);
        if (hit is RayMeshGeometry3DHitTestResult rm)
        {
            point = rm.PointHit;
            normal = MeshHitNormal(rm, pt);
            return true;
        }
        var ground = GroundPoint(pt);
        if (ground is Point3D g)
        {
            point = g;
            normal = new Vector3D(0, 1, 0);
            return true;
        }
        point = default;
        normal = default;
        return false;
    }

    public void ClearPrefab()
    {
        _prefabTemplate = null;
        _prefabName = "";
        _prefabPreviewGroup = null;
        _prefabPlacementValid = false;
        if (PrefabPreviewVisual != null) PrefabPreviewVisual.Content = null;
    }

    public void SetAmbience(AmbienceSettings a)
    {
        _ambience = a.Clone();
        SkyGradient.Update(_ambience, Camera);
        BuildLighting(_ambience);
        Refresh();
    }

    private void BuildLighting(AmbienceSettings a)
    {
        _lighting = new Model3DGroup();
        // GRAB levels are lit by a broad sky ambient plus a warm sun. The budget is
        // kept just under 1.0 so texture colour stays saturated instead of clipping
        // to white; a soft sky fill from the shadow side keeps dark faces readable.
        double ambient = Math.Clamp(a.AmbientBrightness, 0, 4) * 0.42;
        _lighting.Children.Add(new AmbientLight(ScaleColor(a.AmbientColor, ambient)));

        _lighting.Children.Add(new DirectionalLight(ScaleColor(a.SkyHorizonColor, 0.10),
            new Vector3D(-0.35, -1.0, -0.45)));

        if (a.SunAltitude > -3)
        {
            double alt = a.SunAltitude * Math.PI / 180, az = a.SunAzimuth * Math.PI / 180;
            var toSun = new Vector3D(Math.Sin(az) * Math.Cos(alt), Math.Sin(alt), Math.Cos(az) * Math.Cos(alt));
            double sun = Math.Clamp(a.SunBrightness, 0, 3) * (0.46 + 0.26 * Math.Clamp(a.SunAltitude / 45.0, 0, 1));
            _lighting.Children.Add(new DirectionalLight(ScaleColor(a.SunColor, sun), -toSun));

            // Bounce light from the ground on the side away from the sun.
            _lighting.Children.Add(new DirectionalLight(ScaleColor(a.SkyHorizonColor, 0.12),
                new Vector3D(-toSun.X * 0.6, -0.55, -toSun.Z * 0.6)));
        }
    }

    private static Color ScaleColor(Col c, double scale) => Color.FromRgb(
        (byte)Math.Clamp(c.Ri * scale, 0, 255),
        (byte)Math.Clamp(c.Gi * scale, 0, 255),
        (byte)Math.Clamp(c.Bi * scale, 0, 255));

    private void UpdateCamera()
    {
        double p = _pitch * Math.PI / 180, y = _yaw * Math.PI / 180;
        var pos = new Point3D(
            _target.X + _distance * Math.Cos(p) * Math.Sin(y),
            _target.Y + _distance * Math.Sin(p),
            _target.Z + _distance * Math.Cos(p) * Math.Cos(y));
        Camera.Position = pos;
        Camera.LookDirection = new Vector3D(_target.X - pos.X, _target.Y - pos.Y, _target.Z - pos.Z);
        Camera.UpDirection = new Vector3D(0, 1, 0);
        SkyGradient.Update(_ambience, Camera);
        SyncGizmoCamera();
    }

    /// <summary>The overlay Viewport3D has its own depth buffer, so it must use the exact
    /// same projection as the scene camera to keep handles aligned with the selection.</summary>
    private void SyncGizmoCamera()
    {
        if (GizmoCamera == null) return;
        GizmoCamera.Position = Camera.Position;
        GizmoCamera.LookDirection = Camera.LookDirection;
        GizmoCamera.UpDirection = Camera.UpDirection;
        GizmoCamera.FieldOfView = Camera.FieldOfView;
        GizmoCamera.NearPlaneDistance = Camera.NearPlaneDistance;
        GizmoCamera.FarPlaneDistance = Camera.FarPlaneDistance;
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        _orbiting = false;
        _panning = false;
        _draggingGizmo = false;
        _dragNode = null;
        _dragStarts.Clear();
        // Losing focus in the middle of a brush gesture should cancel it instead of
        // leaving the stroke armed on the next mouse move.
        _brushStroke = false;
        _brushReplaceNodes.Clear();
        _brushStartBlock = null;
        if (_buildDragging) CancelBuildGestureOnly();
        CancelTerrainStroke();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Right)
        {
            _orbiting = true;
            _panning = false;
            _lastMouse = e.GetPosition(Viewport);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _orbiting = false;
            _lastMouse = e.GetPosition(Viewport);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (PlayMode) return;
        var pt = e.GetPosition(Viewport);
        if (BuildActive)
        {
            HandleBuildClick(pt, e.ClickCount);
            e.Handled = true;
            return;
        }
        if (TerrainActive)
        {
            BeginTerrainStroke(pt);
            e.Handled = true;
            return;
        }
        if (MeasureActive)
        {
            HandleMeasureClick(pt);
            e.Handled = true;
            return;
        }
        if (PrefabActive)
        {
            UpdatePrefabPreview(pt);
            if (_prefabPlacementValid)
                PrefabStampRequested?.Invoke(_prefabPreviewPosition, _prefabPreviewRotation);
            e.Handled = true;
            return;
        }
        if (BrushActive)
        {
            BeginBrushStroke(pt);
            e.Handled = true;
            return;
        }
        // Gizmo handles live in the overlay viewport. Test them first so a handle
        // behind a wall can still be dragged; if the ray misses the gizmo, fall
        // through to the normal scene hit test used for block selection.
        var gizmoHit = VisualTreeHelper.HitTest(GizmoViewport, pt);
        if (gizmoHit is RayMeshGeometry3DHitTestResult grm && grm.ModelHit is GeometryModel3D ggm && _gizmoModels.TryGetValue(ggm, out int axis))
        {
            StartGizmoDrag(axis, pt);
            e.Handled = true;
            return;
        }
        var hit = VisualTreeHelper.HitTest(Viewport, pt);
        var node = ResolveNode(hit);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (node != null)
        {
            if (ctrl) ToggleSelection(node);
            else if (shift) AddToSelection(node);
            else if (_selection.Count > 1 && _selection.Contains(node))
            {
                // Keep a multi-selection when the user clicks one of its objects; the
                // clicked object becomes active so its details fill the inspector.
                _selected = node;
                NotifySelection();
            }
            else SetSelection(node);
            return;
        }
        if (PlacementEnabled)
        {
            if (TrySurfacePick(pt, out var point, out var normal, out var hitNode))
            {
                var placedPosition = SnapPlacement(point, normal, hitNode, Vec3.One);
                var n = new LevelNode
                {
                    Kind = NodeKind.Static, Shape = PlacementShape, Material = 8,
                    Position = placedPosition,
                    Scale = Vec3.One, Color1 = new Col(0.72f, 0.72f, 0.72f, 1)
                };
                _level?.AddNode(n);
                _level?.EnsureNames();
                Refresh();
                SetSelection(n);
                NodePlaced?.Invoke(n);
                SceneModified?.Invoke();
            }
        }
        else if (!ctrl && !shift) SetSelection(null);
    }

    private bool IsSnapping => SnapEnabled && (Keyboard.Modifiers & ModifierKeys.Control) == 0;
    private double Snap(double v) => IsSnapping ? Math.Round(v / Math.Max(0.001, SnapSize)) * SnapSize : v;

    private LevelNode? ResolveNode(HitTestResult? hit)
    {
        if (hit is not RayMeshGeometry3DHitTestResult rm) return null;
        if (rm.ModelHit is GeometryModel3D gm && _build != null && _build.NodeOfModel.TryGetValue(gm, out var n))
        {
            if (_level != null && (_level.IsLayerLocked(n) || !_level.IsLayerVisible(n))) return null;
            return n;
        }
        return null;
    }

    private void StartGizmoDrag(int axis, Point pt)
    {
        if (_selection.Count == 0) return;
        if (_level != null && _selection.Any(_level.IsLayerLocked))
        {
            MeasurementChanged?.Invoke("This object is on a locked layer. Unlock the layer to transform it.");
            return;
        }
        _dragNode = _selected ?? _selection[^1];
        _dragAxis = axis;
        _draggingGizmo = true;
        _dragStarts.Clear();
        foreach (var node in TopLevelSelection())
            _dragStarts.Add(new DragStart
            {
                Node = node,
                WorldPosition = node.WorldPosition,
                WorldRotation = node.WorldRotation,
                LocalScale = node.Scale
            });
        _dragPivot = SelectionCenter();
        _dragOrigin = new Point3D(_dragPivot.X, _dragPivot.Y, _dragPivot.Z);
        _dragLocalAxis = GizmoAxes()[Math.Clamp(axis, 0, 2)];
        _dragDir = new Vector3D(_dragLocalAxis.X, _dragLocalAxis.Y, _dragLocalAxis.Z);
        var (o, d) = RayFrom(pt);
        _dragStartT = ClosestOnAxis(o, d, _dragOrigin, _dragDir);
        if (_tool == EditorTool.Rotate)
        {
            var p = IntersectPlane(o, d, _dragOrigin, _dragDir);
            if (p is Point3D q)
            {
                var v = q - _dragOrigin;
                _dragStartAngle = Math.Atan2(Vector3D.DotProduct(v, Perp1(_dragDir)), Vector3D.DotProduct(v, Perp2(_dragDir))) * 180 / Math.PI;
            }
        }
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(Viewport);
        if (_orbiting && e.RightButton == MouseButtonState.Pressed)
        {
            var d = pt - _lastMouse;
            _yaw -= d.X * 0.35;
            // Dragging down now looks down, matching Unity and the rest of the editor.
            _pitch = Math.Clamp(_pitch + d.Y * 0.35, -89, 89);
            _lastMouse = pt;
            UpdateCamera();
            RefreshGizmo();
            return;
        }
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            var d = pt - _lastMouse;
            var look = new Vec3((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z).Normalized;
            var right = Vec3Cross(new Vec3(0, 1, 0), look).Normalized;
            var up = Vec3Cross(look, right).Normalized;
            float k = (float)(_distance * 0.0022);
            _target = _target - right * (float)(d.X * k) + up * (float)(d.Y * k);
            _lastMouse = pt;
            UpdateCamera();
            RefreshGizmo();
            return;
        }
        if (PrefabActive)
        {
            UpdatePrefabPreview(pt);
            return;
        }
        if (TerrainActive)
        {
            UpdateTerrainHover(pt);
            if (_terrainStroke && e.LeftButton == MouseButtonState.Pressed)
                ApplyTerrainAt(_terrainPoint);
            return;
        }
        if (BuildActive)
        {
            UpdateBuildHover(pt);
            return;
        }
        if (MeasureActive) return;
        if (BrushActive)
        {
            if (_brushStroke && e.LeftButton == MouseButtonState.Pressed)
                UpdateBrushStroke(pt);
            else if (!_brushStroke)
                UpdateBrushHover(pt);
            return;
        }
        if (_draggingGizmo && _dragNode != null && e.LeftButton == MouseButtonState.Pressed)
        {
            DragGizmo(pt);
            return;
        }
        _lastMouse = pt;
    }

    private void DragGizmo(Point pt)
    {
        if (_dragStarts.Count == 0) return;
        var (o, d) = RayFrom(pt);
        if (_tool == EditorTool.Move)
        {
            double t = ClosestOnAxis(o, d, _dragOrigin, _dragDir);
            double delta = t - _dragStartT;
            var move = _dragDir * delta;
            foreach (var start in _dragStarts)
            {
                var world = start.WorldPosition + new Vec3((float)move.X, (float)move.Y, (float)move.Z);
                var local = WorldToLocal(start.Node, world);
                if (IsSnapping) local = new Vec3((float)Snap(local.X), (float)Snap(local.Y), (float)Snap(local.Z));
                start.Node.Position = local;
            }
            _level!.IsDirty = true;
            Refresh();
            SceneModified?.Invoke();
        }
        else if (_tool == EditorTool.Scale)
        {
            double t = ClosestOnAxis(o, d, _dragOrigin, _dragDir);
            double factor = Math.Clamp(1 + (t - _dragStartT) / Math.Max(_gizmoScale, 0.2), 0.05, 100);
            foreach (var start in _dragStarts)
            {
                var world = start.WorldPosition;
                var offset = world - _dragPivot;
                // Scale around the pivot along the handle's actual world direction,
                // which matters when the gizmo is in Local mode.
                double along = Vec3Dot(offset, _dragLocalAxis);
                offset = offset + _dragLocalAxis * (float)(along * (factor - 1));
                SetWorldPosition(start.Node, _dragPivot + offset);
                var s = start.LocalScale;
                if (_dragAxis == 0) s.X = (float)(s.X * factor);
                else if (_dragAxis == 1) s.Y = (float)(s.Y * factor);
                else s.Z = (float)(s.Z * factor);
                start.Node.Scale = new Vec3(Math.Max(0.01f, s.X), Math.Max(0.01f, s.Y), Math.Max(0.01f, s.Z));
            }
            _level!.IsDirty = true;
            Refresh();
            SceneModified?.Invoke();
        }
        else if (_tool == EditorTool.Rotate)
        {
            var p = IntersectPlane(o, d, _dragOrigin, _dragDir);
            if (p is not Point3D q) return;
            var v = q - _dragOrigin;
            double angle = Math.Atan2(Vector3D.DotProduct(v, Perp1(_dragDir)), Vector3D.DotProduct(v, Perp2(_dragDir))) * 180 / Math.PI;
            double deltaAngle = angle - _dragStartAngle;
            if (IsSnapping) deltaAngle = Math.Round(deltaAngle / Math.Max(1, RotationSnap)) * RotationSnap;
            Quat worldDelta = Quat.FromAxisAngle(_dragLocalAxis, (float)deltaAngle);
            foreach (var start in _dragStarts)
            {
                var offset = start.WorldPosition - _dragPivot;
                SetWorldPosition(start.Node, _dragPivot + worldDelta.Rotate(offset));
                SetWorldRotation(start.Node, worldDelta * start.WorldRotation);
            }
            _level!.IsDirty = true;
            Refresh();
            SceneModified?.Invoke();
        }
    }

    private static void SetWorldPosition(LevelNode node, Vec3 world)
    {
        if (node.Parent == null) { node.Position = world; return; }
        var parent = node.Parent;
        var rel = world - parent.WorldPosition;
        var local = parent.WorldRotation.Inverse().Rotate(rel);
        var ps = parent.WorldScale;
        node.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f), local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
    }

    private static void SetWorldRotation(LevelNode node, Quat world)
    {
        node.Rotation = node.Parent != null ? node.Parent.WorldRotation.Inverse() * world : world;
    }

    private static Vec3 WorldToLocal(LevelNode node, Vec3 world)
    {
        if (node.Parent == null) return world;
        var p = node.Parent;
        var rel = world - p.WorldPosition;
        var inv = Inverse(p.WorldRotation);
        var local = inv.Rotate(rel);
        var s = p.WorldScale;
        return new Vec3(local.X / Math.Max(s.X, 1e-5f), local.Y / Math.Max(s.Y, 1e-5f), local.Z / Math.Max(s.Z, 1e-5f));
    }

    private static Quat Inverse(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (BuildActive)
            {
                if (_buildDragging) FinishBuildDrag();
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            if (BrushActive)
            {
                if (_brushStroke) FinishBrushStroke();
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            if (TerrainActive)
            {
                _terrainStroke = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            _draggingGizmo = false;
            _dragNode = null;
            _dragStarts.Clear();
            if (IsMouseCaptured) ReleaseMouseCapture();
            RefreshGizmo();
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            _orbiting = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
    }

    // ================= draw / build tool =================

    private static string BuildShapeLabel(BuildShape shape) => shape switch
    {
        BuildShape.Box => "Box",
        BuildShape.Sphere => "Sphere",
        BuildShape.Cylinder => "Cylinder",
        BuildShape.Cone => "Cone",
        BuildShape.Stairs => "Stairs",
        BuildShape.Ramp => "Ramp",
        _ => "Polygon"
    };

    private void HandleBuildClick(Point pt, int clickCount)
    {
        if (_level == null) return;
        if (_buildShape == BuildShape.Polygon)
        {
            // Closing by clicking the first point produces a second MouseDown for the
            // double-click; ignore that stray click instead of starting a new polygon.
            if (DateTime.UtcNow < _buildClickSuppressUntil) return;
            if (!TryBuildSurfacePoint(pt, out var point)) return;
            if (clickCount >= 2 && _buildPolygon.Count >= 3)
            {
                CommitPolygon();
                return;
            }
            if (_buildPolygon.Count >= 3 && (point - _buildPolygon[0]).Length < 0.8)
            {
                CommitPolygon();
                return;
            }
            _buildPolygon.Add(point);
            if (_buildPolygon.Count == 1) _buildPolygonY = point.Y;
            _buildPolygonHover = point;
            RefreshBuildPreview();
            return;
        }

        if (!TryBuildCell(pt, out var cell)) return;
        _buildAnchor = cell;
        _buildHover = cell;
        _buildHoverValid = true;
        _buildDragging = true;
        CaptureMouse();
        RefreshBuildPreview();
    }

    private void UpdateBuildHover(Point pt)
    {
        if (_buildShape == BuildShape.Polygon)
        {
            if (TryBuildSurfacePoint(pt, out var point)) _buildPolygonHover = point;
            RefreshBuildPreview();
            return;
        }
        if (TryBuildCell(pt, out var cell))
        {
            _buildHover = cell;
            _buildHoverValid = true;
        }
        else _buildHoverValid = false;
        RefreshBuildPreview();
    }

    private void FinishBuildDrag()
    {
        if (_buildShape == BuildShape.Polygon) return;
        _buildDragging = false;
        if (!_buildHoverValid) { CancelBuildGestureOnly(); return; }
        var plan = PlanBuild(_buildAnchor, _buildHover, _buildShape, _buildHeight, _buildHollow);
        var label = BuildShapeLabel(_buildShape);
        var result = PlaceBuildPlan(plan, label);
        BrushApplied?.Invoke(result);
        CancelBuildGestureOnly();
    }

    /// <summary>Finishes the active custom-shape drawing from the Draw palette or Enter.</summary>
    public void FinishBuildPolygon() => CommitPolygon();

    private void CommitPolygon()
    {
        _buildClickSuppressUntil = DateTime.UtcNow.AddMilliseconds(350);
        if (_buildPolygon.Count < 3)
        {
            BrushApplied?.Invoke(new BlockBrushResult { Description = "Draw Polygon (add at least 3 points)", Count = 0 });
            return;
        }
        var points = _buildPolygon.ToList();
        if ((points[0] - points[^1]).Length < 0.01f) points.RemoveAt(points.Count - 1);
        var plan = PolygonPlan(points, null);
        var result = PlaceBuildPlan(plan, "Polygon");
        BrushApplied?.Invoke(result);
        _buildPolygon.Clear();
        _buildPolygonHover = null;
        _buildPreviewKey = "";
        RefreshBuildPreview();
    }

    private void CancelBuild()
    {
        CancelBuildGestureOnly();
        _buildPolygon.Clear();
        _buildPolygonHover = null;
        _buildPreviewKey = "";
        if (BuildVisual != null) BuildVisual.Content = null;
    }

    private void CancelBuildGestureOnly()
    {
        _buildDragging = false;
        _buildHoverValid = false;
        _buildPreviewKey = "";
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (BuildVisual != null) BuildVisual.Content = null;
    }

    // ================= 3D terrain shaper =================

    private void BeginTerrainStroke(Point pt)
    {
        if (!TryTerrainPick(pt, out var point))
        {
            RefreshTerrainPreview();
            return;
        }
        _terrainStroke = true;
        _terrainPoint = point;
        _terrainHoverValid = true;
        _terrainLastCell = (int.MaxValue, int.MaxValue, int.MaxValue);
        CaptureMouse();
        ApplyTerrainAt(point);
    }

    private void UpdateTerrainHover(Point pt)
    {
        if (TryTerrainPick(pt, out var point))
        {
            _terrainPoint = point;
            _terrainHoverValid = true;
        }
        else _terrainHoverValid = false;
        RefreshTerrainPreview();
    }

    private bool TryTerrainPick(Point pt, out Vec3 point)
    {
        if (TryBrushPick(pt, out var pick))
        {
            point = new Vec3((float)pick.Point.X, (float)pick.Point.Y, (float)pick.Point.Z);
            return true;
        }
        point = Vec3.Zero;
        return false;
    }

    private void ApplyTerrainAt(Vec3 point)
    {
        if (_level == null) return;
        var cell = BlockBrushMath.CellAt(point, _terrainCellSize);
        var key = (cell.X, cell.Y, cell.Z);
        if (key == _terrainLastCell) return;
        _terrainLastCell = key;
        var result = TerrainSculptor.Apply(_level, point, new TerrainSculptSettings
        {
            Mode = _terrainMode,
            Volume = _terrainVolume,
            Radius = _terrainRadius,
            Strength = _terrainStrength,
            Material = _terrainMaterial,
            Color = _terrainColor,
            CellSize = _terrainCellSize
        });
        if (result.Count == 0)
        {
            TerrainSculpted?.Invoke(result);
            return;
        }
        _level.IsDirty = true;
        Refresh();
        SceneModified?.Invoke();
        TerrainSculpted?.Invoke(result);
    }

    private void CancelTerrainStroke()
    {
        _terrainStroke = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void RefreshTerrainPreview()
    {
        if (TerrainVisual == null) return;
        if (!TerrainActive || _level == null || !_terrainHoverValid)
        {
            TerrainVisual.Content = null;
            _terrainPreviewKey = "";
            return;
        }
        var centre = BlockBrushMath.CellAt(_terrainPoint, _terrainCellSize);
        string key = _terrainMode + "|" + _terrainVolume + "|" + _terrainRadius + "|" + _terrainCellSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + centre.X + "," + centre.Y + "," + centre.Z;
        if (key == _terrainPreviewKey) return;
        _terrainPreviewKey = key;
        var centreWorld = BlockBrushMath.Center(centre, _terrainCellSize);
        var p = new Point3D(centreWorld.X, centreWorld.Y, centreWorld.Z);
        double r = _terrainRadius * _terrainCellSize;
        var mesh = new MeshBuilder();
        double thickness = Math.Max(0.025, r * 0.018);
        if (_terrainVolume == TerrainBrushVolume.Box)
        {
            mesh.WireBox(p, new Vector3D(r * 2, r * 2, r * 2), thickness);
            mesh.WireBox(p, new Vector3D(r * 1.5, r * 1.5, r * 1.5), thickness * 0.65);
        }
        else
        {
            mesh.Circle(p, new Vector3D(0, 1, 0), r, thickness);
            mesh.Circle(p, new Vector3D(1, 0, 0), r, thickness);
            mesh.Circle(p, new Vector3D(0, 0, 1), r, thickness);
            mesh.WireBox(p, new Vector3D(r * 2, r * 2, r * 2), thickness * 0.65);
        }
        var color = _terrainMode switch
        {
            TerrainSculptMode.Remove => new Col(1f, 0.35f, 0.25f, 1f),
            TerrainSculptMode.Paint => _terrainColor,
            TerrainSculptMode.Smooth => new Col(0.35f, 0.75f, 1f, 1f),
            TerrainSculptMode.Flatten => new Col(1f, 0.85f, 0.3f, 1f),
            _ => new Col(0.35f, 1f, 0.45f, 1f)
        };
        var material = BrushPreviewMaterial(color, 230);
        TerrainVisual.Content = new GeometryModel3D(mesh.ToMesh(), material) { BackMaterial = material };
    }

    private bool TryBuildSurfacePoint(Point pt, out Point3D point)
    {
        if (!TrySurfacePick(pt, out var hit, out var normal, out _))
        {
            point = default;
            return false;
        }
        point = hit;
        if (IsSnapping && SnapMode == SmartSnapMode.Grid)
            point = new Point3D(Snap(point.X), 0, Snap(point.Z));
        return true;
    }

    private bool TryBuildCell(Point pt, out VoxelCell cell)
    {
        if (!TrySurfacePick(pt, out var point, out var normal, out var hitNode))
        {
            cell = default;
            return false;
        }
        var n = SnapNormalToAxis(normal);
        var placement = SnapPlacement(point, n, hitNode, Vec3.One);
        cell = BlockBrushMath.CellAt(placement, 1.0);
        return true;
    }

    private void RefreshBuildPreview()
    {
        if (BuildVisual == null) return;
        if (!BuildActive || _level == null || !_buildHoverValid && _buildPolygon.Count == 0)
        {
            BuildVisual.Content = null;
            _buildPreviewKey = "";
            return;
        }

        var pointsKey = _buildPolygon.Count == 0 ? "-" :
            string.Join(";", _buildPolygon.Select(p => $"{p.X:0.##},{p.Y:0.##},{p.Z:0.##}"));
        var hoverKey = _buildPolygonHover is Point3D hp ? $"{hp.X:0.##},{hp.Y:0.##},{hp.Z:0.##}" : "-";
        string key = $"{_buildShape}|{_buildAnchor}|{_buildHover}|{_buildHeight}|{_buildHollow}|{pointsKey}|{hoverKey}|{SnapMode}";
        if (key == _buildPreviewKey) return;
        _buildPreviewKey = key;

        BrushPlan plan;
        if (_buildShape == BuildShape.Polygon)
        {
            var previewPoints = _buildPolygon.ToList();
            if (_buildPolygonHover is Point3D hover && previewPoints.Count > 0) previewPoints.Add(hover);
            plan = previewPoints.Count >= 3 ? PolygonPlan(previewPoints, null) : new BrushPlan();
        }
        else
        {
            var start = _buildDragging ? _buildAnchor : _buildHover;
            plan = PlanBuild(start, _buildHover, _buildShape, _buildHeight, _buildHollow);
        }

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(225, 225, 225)));
        if (plan.Cells.Count > 0)
            AddBrushCellPreview(group, plan, new Col(0.25f, 0.78f, 1f, 1f));

        // Polygon vertices and the closing preview line make the draw workflow legible.
        if (_buildShape == BuildShape.Polygon && _buildPolygon.Count > 0)
        {
            var outline = new MeshBuilder();
            var previewPoints = _buildPolygon.ToList();
            if (_buildPolygonHover is Point3D hover) previewPoints.Add(hover);
            for (int i = 0; i < previewPoints.Count - 1; i++)
                outline.Line(previewPoints[i], previewPoints[i + 1], 0.045);
            if (previewPoints.Count >= 3)
                outline.Line(previewPoints[^1], previewPoints[0], 0.045);
            foreach (var p in _buildPolygon)
                outline.Box(p, new Vector3D(0.12, 0.12, 0.12));
            var lineMaterial = BrushPreviewMaterial(new Col(1f, 0.82f, 0.2f, 1f), 240);
            group.Children.Add(new GeometryModel3D(outline.ToMesh(), lineMaterial) { BackMaterial = lineMaterial });
        }
        BuildVisual.Content = group;
    }

    private BrushPlan PolygonPlan(IReadOnlyList<Point3D> points, double? baseY)
    {
        var plan = new BrushPlan();
        if (points.Count < 3) return plan;
        double y = baseY ?? points[0].Y;
        int baseCell = BlockBrushMath.CellAt(new Vec3(0, (float)(y + 0.5), 0), 1.0).Y;
        float minX = points.Min(p => (float)p.X), maxX = points.Max(p => (float)p.X);
        float minZ = points.Min(p => (float)p.Z), maxZ = points.Max(p => (float)p.Z);
        int x0 = (int)Math.Floor(minX) - 1, x1 = (int)Math.Ceiling(maxX) + 1;
        int z0 = (int)Math.Floor(minZ) - 1, z1 = (int)Math.Ceiling(maxZ) + 1;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (!PointInPolygon(x, z, points)) continue;
                for (int layer = 0; layer < Math.Max(1, _buildHeight); layer++)
                {
                    if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                    plan.Cells.Add(new VoxelCell(x, baseCell + layer, z));
                }
            }
        }
        return BlockBrushMath.Mirror(plan, _symmetry, _symmetryPivot, 1.0);
    }

    private static bool PointInPolygon(float x, float z, IReadOnlyList<Point3D> points)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var pi = points[i];
            var pj = points[j];
            if ((pi.Z > z) != (pj.Z > z) &&
                x < (pj.X - pi.X) * (z - pi.Z) / (pj.Z - pi.Z) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private BrushPlan PlanBuild(VoxelCell anchor, VoxelCell end, BuildShape shape, int height, bool hollow)
    {
        var plan = new BrushPlan();
        height = Math.Clamp(height, 1, 64);
        int dx = end.X - anchor.X, dz = end.Z - anchor.Z;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dz));
        switch (shape)
        {
            case BuildShape.Box:
            {
                int minX = Math.Min(anchor.X, end.X), maxX = Math.Max(anchor.X, end.X);
                int minZ = Math.Min(anchor.Z, end.Z), maxZ = Math.Max(anchor.Z, end.Z);
                int minY = anchor.Y, maxY = anchor.Y + height - 1;
                for (int y = minY; y <= maxY; y++)
                    for (int z = minZ; z <= maxZ; z++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            if (hollow && x > minX && x < maxX && y > minY && y < maxY && z > minZ && z < maxZ) continue;
                            if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                            plan.Cells.Add(new VoxelCell(x, y, z));
                        }
                break;
            }
            case BuildShape.Sphere:
            {
                int r = Math.Clamp(Math.Max(1, steps), 1, 32);
                int cy = anchor.Y + r;
                for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                        for (int x = -r; x <= r; x++)
                        {
                            int d2 = x * x + y * y + z * z;
                            if (d2 > r * r + r) continue;
                            if (hollow && d2 < (r - 1) * (r - 1)) continue;
                            if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                            plan.Cells.Add(new VoxelCell(anchor.X + x, cy + y, anchor.Z + z));
                        }
                break;
            }
            case BuildShape.Cylinder:
            {
                int r = Math.Clamp(Math.Max(1, steps), 1, 32);
                for (int layer = 0; layer < height; layer++)
                    for (int z = -r; z <= r; z++)
                        for (int x = -r; x <= r; x++)
                        {
                            int d2 = x * x + z * z;
                            if (d2 > r * r + r) continue;
                            if (hollow && d2 < (r - 1) * (r - 1)) continue;
                            if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                            plan.Cells.Add(new VoxelCell(anchor.X + x, anchor.Y + layer, anchor.Z + z));
                        }
                break;
            }
            case BuildShape.Cone:
            {
                int r = Math.Clamp(Math.Max(1, steps), 1, 32);
                for (int layer = 0; layer < height; layer++)
                {
                    double radius = r * (1.0 - (double)layer / Math.Max(1, height));
                    int rr = (int)Math.Round(radius);
                    for (int z = -rr; z <= rr; z++)
                        for (int x = -rr; x <= rr; x++)
                        {
                            if (x * x + z * z > rr * rr + rr) continue;
                            if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                            plan.Cells.Add(new VoxelCell(anchor.X + x, anchor.Y + layer, anchor.Z + z));
                        }
                }
                break;
            }
            case BuildShape.Stairs:
            case BuildShape.Ramp:
            {
                bool alongX = Math.Abs(dx) >= Math.Abs(dz);
                int length = Math.Max(1, steps);
                int sign = alongX ? Math.Sign(dx) : Math.Sign(dz);
                if (sign == 0) sign = 1;
                for (int i = 0; i < length; i++)
                {
                    int top = shape == BuildShape.Ramp
                        ? 1 + (int)Math.Round(i * (height - 1.0) / Math.Max(1, length - 1))
                        : Math.Max(1, (int)Math.Round((i + 1) * (double)height / length));
                    int ax = anchor.X + (alongX ? sign * i : 0);
                    int az = anchor.Z + (alongX ? 0 : sign * i);
                    for (int layer = 0; layer < top; layer++)
                        for (int w = -1; w <= 1; w++)
                        {
                            if (plan.Cells.Count >= BlockBrushMath.MaxCells) { plan.Truncated = true; return plan; }
                            plan.Cells.Add(alongX
                                ? new VoxelCell(ax, anchor.Y + layer, az + w)
                                : new VoxelCell(ax + w, anchor.Y + layer, az));
                        }
                }
                break;
            }
        }
        return BlockBrushMath.Mirror(plan, _symmetry, _symmetryPivot, 1.0);
    }

    private BlockBrushResult PlaceBuildPlan(BrushPlan plan, string label)
    {
        int created = 0;
        var placed = new List<LevelNode>();
        if (_level != null && plan.Cells.Count > 0)
        {
            var group = new LevelNode
            {
                Kind = NodeKind.Group,
                Name = "Draw " + label,
                Position = Vec3.Zero,
                Scale = Vec3.One
            };
            _level.Nodes.Add(group);
            group.RebuildParents();
            var occupancy = BlockBrushMath.Occupancy(_level, 1.0);
            foreach (var cell in plan.Cells)
            {
                if (occupancy.ContainsKey(cell)) continue;
                var node = new LevelNode
                {
                    Kind = NodeKind.Static,
                    Shape = ShapeInfo.Cube,
                    Material = _buildMaterial,
                    Color1 = _buildColor,
                    Position = BlockBrushMath.Center(cell, 1.0),
                    Scale = Vec3.One
                };
                group.Children.Add(node);
                node.RebuildParents(group);
                occupancy[cell] = node;
                placed.Add(node);
                created++;
            }
            if (created == 0) _level.Nodes.Remove(group);
            else
            {
                if (_buildFoundation != TerrainFoundationMode.None)
                    TerrainSculptor.PrepareFoundation(_level, placed, _buildFoundation, _buildFoundationDepth);
                _level.EnsureNames();
                _level.IsDirty = true;
            }
        }
        Refresh();
        return new BlockBrushResult
        {
            Description = "Draw " + label,
            Count = created,
            Created = created > 0,
            Truncated = plan.Truncated
        };
    }

    // ================= block brush =================

    private void ResetBrushGesture()
    {
        _brushStroke = false;
        _brushReplaceNodes.Clear();
        _brushEraseNodes.Clear();
        _brushStartBlock = null;
        _brushHoverNode = null;
    }

    private void CancelBrushStroke(bool clearPreview)
    {
        ResetBrushGesture();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!clearPreview) return;
        _brushHoverValid = false;
        _brushPreviewState = null;
        _brushFillPreviewNode = null;
        _brushFillPreview.Clear();
        if (BrushVisual != null) BrushVisual.Content = null;
    }

    private void BeginBrushStroke(Point pt)
    {
        ResetBrushGesture();
        if (!TryBrushPick(pt, out var pick))
        {
            _brushHoverValid = false;
            RefreshBrushPreview();
            return;
        }
        _brushHoverValid = true;
        _brushHoverNode = pick.Node;
        _brushAnchor = pick.Cell;
        _brushHover = pick.Cell;
        _brushLastPoint = pt;
        var centre = BlockBrushMath.Center(_brushAnchor, _brushCellSize);
        _brushPlanePoint = new Point3D(centre.X, centre.Y, centre.Z);
        _brushPlaneNormal = SnapNormalToAxis(pick.Normal);

        if (_brushMode is BlockBrushMode.Fill or BlockBrushMode.Replace or BlockBrushMode.Erase)
        {
            if (pick.Node is null || pick.Node.Kind is not (NodeKind.Static or NodeKind.Crumbling))
            {
                // Invalid target: still give the user feedback instead of silently doing nothing.
                BrushApplied?.Invoke(new BlockBrushResult
                {
                    Description = BlockBrushModeInfo.Display(_brushMode) + " (click a block)",
                    Count = 0
                });
                return;
            }
            _brushStartBlock = pick.Node;
            if (_brushMode == BlockBrushMode.Erase) _brushEraseNodes.Add(pick.Node);
            else _brushReplaceNodes.Add(pick.Node);
            _brushStroke = true;
            CaptureMouse();
            RefreshBrushPreview();
            return;
        }

        _brushStroke = true;
        CaptureMouse();
        RefreshBrushPreview();
    }

    private void UpdateBrushHover(Point pt)
    {
        if (!TryBrushPick(pt, out var pick))
        {
            _brushHoverValid = false;
            _brushHoverNode = null;
            RefreshBrushPreview();
            return;
        }
        _brushHoverValid = true;
        _brushHoverNode = pick.Node;
        _brushHover = pick.Cell;
        RefreshBrushPreview();
    }

    private void UpdateBrushStroke(Point pt)
    {
        if (_brushMode is BlockBrushMode.Replace or BlockBrushMode.Erase)
        {
            // Sample every few pixels between mouse messages. A fast drag used to
            // skip over blocks and leave unpainted/erased holes in the stroke.
            double dx = pt.X - _brushLastPoint.X, dy = pt.Y - _brushLastPoint.Y;
            int steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 4.0), 1, 512);
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                var sample = new Point(_brushLastPoint.X + dx * t, _brushLastPoint.Y + dy * t);
                if (!TryBrushPick(sample, out var samplePick) || samplePick.Node is null ||
                    samplePick.Node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) continue;
                _brushHoverValid = true;
                _brushHoverNode = samplePick.Node;
                if (_brushMode == BlockBrushMode.Erase) _brushEraseNodes.Add(samplePick.Node);
                else _brushReplaceNodes.Add(samplePick.Node);
            }
            _brushLastPoint = pt;
            RefreshBrushPreview();
            return;
        }
        if (_brushMode == BlockBrushMode.Fill)
        {
            if (TryBrushPick(pt, out var pick) && pick.Node is not null &&
                pick.Node.Kind is NodeKind.Static or NodeKind.Crumbling)
            {
                _brushHoverValid = true;
                _brushHoverNode = pick.Node;
            }
            _brushLastPoint = pt;
            RefreshBrushPreview();
            return;
        }

        _brushLastPoint = pt;
        var (origin, direction) = RayFrom(pt);
        if (IntersectPlane(origin, direction, _brushPlanePoint, _brushPlaneNormal) is not Point3D point)
            return;
        _brushHover = BlockBrushMath.CellAt(new Vec3((float)point.X, (float)point.Y, (float)point.Z), _brushCellSize);
        _brushHoverValid = true;
        RefreshBrushPreview();
    }

    private void FinishBrushStroke()
    {
        var mode = _brushMode;
        var startBlock = _brushStartBlock;
        var touched = _brushReplaceNodes.ToList();
        var erased = _brushEraseNodes.ToList();
        var anchor = _brushAnchor;
        var end = _brushHover;
        ResetBrushGesture();

        BlockBrushResult result;
        if (mode == BlockBrushMode.Erase)
        {
            result = EraseBrushNodes(erased);
        }
        else if (mode is BlockBrushMode.Fill or BlockBrushMode.Replace)
        {
            result = PaintBrushTargets(mode, startBlock, touched);
        }
        else
        {
            var plan = PlanBrush(anchor, end, mode);
            result = PlaceBrushPlan(plan, BlockBrushModeInfo.Display(mode));
        }

        if (result.Count > 0 && _level != null) _level.IsDirty = true;
        _brushHoverValid = false;
        RefreshBrushPreview();
        BrushApplied?.Invoke(result);
    }

    /// <summary>Deletes a brush stroke and any empty brush/GLB group left behind.</summary>
    private BlockBrushResult EraseBrushNodes(IEnumerable<LevelNode> nodes)
    {
        var targets = WithMirroredTargets(nodes.Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling));
        var parents = new HashSet<LevelNode>();
        int count = 0;
        foreach (var node in targets.Distinct())
        {
            if (node.Parent != null) parents.Add(node.Parent);
            _level?.RemoveNode(node);
            count++;
        }
        if (_level != null)
        {
            foreach (var parent in parents)
            {
                if (parent.Children.Count == 0 &&
                    (parent.Name.StartsWith("Brush ", StringComparison.Ordinal) ||
                     parent.Name.StartsWith("GLB ", StringComparison.Ordinal)))
                    _level.RemoveNode(parent);
            }
            _level.EnsureNames();
        }
        return new BlockBrushResult { Description = "Erase", Count = count };
    }

    /// <summary>Adds mirrored block nodes at the other side of the symmetry pivot.</summary>
    private List<LevelNode> WithMirroredTargets(IEnumerable<LevelNode> nodes)
    {
        var result = nodes.Distinct().ToList();
        if (_level == null || _symmetry == BlockSymmetry.None) return result;
        var occupancy = BlockBrushMath.Occupancy(_level, _brushCellSize);
        var extra = new List<LevelNode>();
        foreach (var node in result.ToList())
        {
            var one = new BrushPlan();
            one.Cells.Add(BlockBrushMath.CellAt(node.WorldPosition, _brushCellSize));
            var mirrored = BlockBrushMath.Mirror(one, _symmetry, _symmetryPivot, _brushCellSize);
            foreach (var cell in mirrored.Cells)
                if (occupancy.TryGetValue(cell, out var other) && other.Kind is NodeKind.Static or NodeKind.Crumbling)
                    extra.Add(other);
        }
        result.AddRange(extra);
        return result.Distinct().ToList();
    }

    /// <summary>Applies Fill or Replace to real block nodes. Fill walks the connected
    /// same-material island; Replace paints exactly the blocks the drag touched.</summary>
    private BlockBrushResult PaintBrushTargets(BlockBrushMode mode, LevelNode? startBlock, IEnumerable<LevelNode> touched)
    {
        bool truncated = false;
        List<LevelNode> targets;
        if (mode == BlockBrushMode.Fill)
        {
            targets = _level != null && startBlock != null
                ? BlockBrushMath.ConnectedIsland(_level, startBlock, _brushCellSize, BlockBrushMath.MaxFillBlocks, out truncated)
                : new List<LevelNode>();
        }
        else
        {
            targets = touched
                .Where(n => n.Kind is NodeKind.Static or NodeKind.Crumbling)
                .Distinct()
                .ToList();
        }
        targets = WithMirroredTargets(targets);

        int count = 0;
        foreach (var node in targets)
        {
            if (ApplyBrushMaterial(node)) count++;
        }
        return new BlockBrushResult
        {
            Description = BlockBrushModeInfo.Display(mode),
            Count = count,
            Truncated = truncated
        };
    }

    private BrushPlan PlanBrush(VoxelCell anchor, VoxelCell end, BlockBrushMode mode)
    {
        var plan = mode switch
        {
            BlockBrushMode.Line => BlockBrushMath.Line(anchor, end),
            BlockBrushMode.Box => BlockBrushMath.Box(anchor, end, _brushLayers, _brushHollow),
            BlockBrushMode.Sphere => BlockBrushMath.Sphere(anchor, Math.Max(1, BlockBrushMath.Distance(anchor, end)), _brushHollow),
            _ => new BrushPlan()
        };
        return BlockBrushMath.Mirror(plan, _symmetry, _symmetryPivot, _brushCellSize);
    }

    private BlockBrushResult PlaceBrushPlan(BrushPlan plan, string label)
    {
        var created = 0;
        if (_level != null)
        {
            // One group per stroke keeps a voxel build from flooding the hierarchy
            // with thousands of root blocks. The group's pivot stays at the origin,
            // so the children keep their world-space grid positions until the user
            // moves the group deliberately.
            var group = new LevelNode
            {
                Kind = NodeKind.Group,
                Name = "Brush " + label,
                Position = Vec3.Zero,
                Scale = Vec3.One
            };
            _level.Nodes.Add(group);
            group.RebuildParents();

            var occupancy = BlockBrushMath.Occupancy(_level, _brushCellSize);
            double size = _brushCellSize;
            foreach (var cell in plan.Cells)
            {
                if (occupancy.ContainsKey(cell)) continue;
                var node = new LevelNode
                {
                    Kind = NodeKind.Static,
                    Shape = _brushShape,
                    Material = _brushMaterial,
                    Color1 = _brushColor,
                    Position = BlockBrushMath.Center(cell, size),
                    Rotation = Quat.Identity,
                    Scale = new Vec3((float)size, (float)size, (float)size)
                };
                group.Children.Add(node);
                node.RebuildParents(group);
                occupancy[cell] = node;
                created++;
            }
            if (created == 0) _level.Nodes.Remove(group);
            else _level.EnsureNames();
        }
        return new BlockBrushResult
        {
            Description = label,
            Count = created,
            Created = created > 0,
            Truncated = plan.Truncated
        };
    }

    /// <summary>Paints one node and reports whether anything actually changed, so a
    /// no-op fill says "nothing changed" instead of dirtying the level.</summary>
    private bool ApplyBrushMaterial(LevelNode node)
    {
        bool changed = node.Material != _brushMaterial;
        node.Material = _brushMaterial;
        if (!MaterialInfo.IsColorable(_brushMaterial)) return changed;
        if (node.Color1.Ri != _brushColor.Ri || node.Color1.Gi != _brushColor.Gi || node.Color1.Bi != _brushColor.Bi)
            changed = true;
        node.Color1 = _brushColor;
        return changed;
    }

    /// <summary>Automation entry point used by the screenshot and self-test runner.
    /// Applies a brush stroke without a mouse gesture; editing commands still go through
    /// the same PlanBrush and PlaceBrushPlan paths as real input.</summary>
    /// <summary>Automation entry point for the screenshot runner. Uses the same
    /// PlanBuild/PlaceBuildPlan path as a real draw gesture.</summary>
    internal BlockBrushResult DrawShapeForAutomation(VoxelCell anchor, VoxelCell end, BuildShape shape, int height)
    {
        var plan = PlanBuild(anchor, end, shape, height, false);
        return PlaceBuildPlan(plan, BuildShapeLabel(shape));
    }

    internal BlockBrushResult ApplyBrushForAutomation(VoxelCell anchor, VoxelCell end)
    {
        if (_level == null || _brushMode == BlockBrushMode.None)
            return new BlockBrushResult { Description = "Brush", Count = 0 };
        var plan = PlanBrush(anchor, end, _brushMode);
        var result = PlaceBrushPlan(plan, BlockBrushModeInfo.Display(_brushMode));
        if (result.Count > 0) _level.IsDirty = true;
        return result;
    }

    /// <summary>Automation entry point for the material brushes. Uses the same
    /// PaintBrushTargets path as a real click/drag on the scene.</summary>
    internal BlockBrushResult ApplyMaterialBrushForAutomation(VoxelCell cell)
    {
        if (_level == null || _brushMode is not (BlockBrushMode.Fill or BlockBrushMode.Replace))
            return new BlockBrushResult { Description = "Brush", Count = 0 };
        var occupancy = BlockBrushMath.Occupancy(_level, _brushCellSize);
        occupancy.TryGetValue(cell, out var start);
        var touched = start != null ? new[] { start } : Array.Empty<LevelNode>();
        var result = PaintBrushTargets(_brushMode, start, touched);
        if (result.Count > 0) _level.IsDirty = true;
        return result;
    }

    /// <summary>Automation entry point for Erase. The real gesture builds the
    /// same target list in UpdateBrushStroke and FinishBrushStroke.</summary>
    internal BlockBrushResult EraseForAutomation(IEnumerable<VoxelCell> cells)
    {
        if (_level == null) return new BlockBrushResult { Description = "Erase", Count = 0 };
        var occupancy = BlockBrushMath.Occupancy(_level, _brushCellSize);
        var nodes = cells
            .Select(c => occupancy.TryGetValue(c, out var n) ? n : null)
            .Where(n => n is not null && n.Kind is NodeKind.Static or NodeKind.Crumbling)
            .Select(n => n!)
            .ToList();
        var result = EraseBrushNodes(nodes);
        if (result.Count > 0) _level.IsDirty = true;
        return result;
    }

    internal void ClearBrushPreviewForAutomation()
    {
        _brushHoverValid = false;
        _brushPreviewState = null;
        _brushFillPreviewNode = null;
        _brushFillPreview.Clear();
        if (BrushVisual != null) BrushVisual.Content = null;
    }

    private bool TryBrushPick(Point pt, out BrushPick pick)
    {
        var hit = HitTestLevel(pt);
        if (hit is RayMeshGeometry3DHitTestResult rm)
        {
            var node = ResolveNode(rm);
            if (node != null)
            {
                var normal = MeshHitNormal(rm, pt);
                var point = rm.PointHit;
                double offset = _brushCellSize * 0.5;
                var centre = new Vec3(
                    (float)(point.X + normal.X * offset),
                    (float)(point.Y + normal.Y * offset),
                    (float)(point.Z + normal.Z * offset));
                pick = new BrushPick(node, point, normal, BlockBrushMath.CellAt(centre, _brushCellSize));
                return true;
            }
        }
        var ground = GroundPoint(pt);
        if (ground is Point3D g)
        {
            var centre = new Vec3((float)g.X, (float)(g.Y + _brushCellSize * 0.5), (float)g.Z);
            pick = new BrushPick(null, g, new Vector3D(0, 1, 0), BlockBrushMath.CellAt(centre, _brushCellSize));
            return true;
        }
        pick = default;
        return false;
    }

    /// <summary>Hit-tests only the editable scene. The prefab ghost, player and particles
    /// must not intercept placement rays, or face snapping snaps to its own preview.</summary>
    private HitTestResult? HitTestLevel(Point pt)
    {
        Model3D? prefab = PrefabPreviewVisual?.Content;
        Model3D? player = PlayerVisual?.Content;
        Model3D? particles = ParticleVisual?.Content;
        try
        {
            if (PrefabPreviewVisual != null) PrefabPreviewVisual.Content = null;
            if (PlayerVisual != null) PlayerVisual.Content = null;
            if (ParticleVisual != null) ParticleVisual.Content = null;
            return VisualTreeHelper.HitTest(Viewport, pt);
        }
        finally
        {
            if (PrefabPreviewVisual != null) PrefabPreviewVisual.Content = prefab;
            if (PlayerVisual != null) PlayerVisual.Content = player;
            if (ParticleVisual != null) ParticleVisual.Content = particles;
        }
    }

    private Vector3D MeshHitNormal(RayMeshGeometry3DHitTestResult hit, Point pt)
    {
        var mesh = hit.MeshHit;
        int i0 = hit.VertexIndex1, i1 = hit.VertexIndex2, i2 = hit.VertexIndex3;
        if (mesh?.Positions != null && i0 >= 0 && i1 >= 0 && i2 >= 0 &&
            i0 < mesh.Positions.Count && i1 < mesh.Positions.Count && i2 < mesh.Positions.Count)
        {
            var a = mesh.Positions[i0];
            var b = mesh.Positions[i1];
            var c = mesh.Positions[i2];
            var normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.LengthSquared > 1e-10)
            {
                normal.Normalize();
                // Mesh positions are local to the block. Rotate/scale the normal into
                // world space before using it for face snapping, otherwise a rotated
                // block reports its unrotated face direction and placement misses.
                var transform = hit.ModelHit?.Transform;
                if (transform != null)
                {
                    var m = transform.Value;
                    if (m.HasInverse)
                    {
                        m.Invert();
                        normal = new Vector3D(
                            normal.X * m.M11 + normal.Y * m.M12 + normal.Z * m.M13,
                            normal.X * m.M21 + normal.Y * m.M22 + normal.Z * m.M23,
                            normal.X * m.M31 + normal.Y * m.M32 + normal.Z * m.M33);
                    }
                }
                if (normal.LengthSquared < 1e-10) normal = new Vector3D(0, 1, 0);
                normal.Normalize();
                // The mesh can be viewed from either side, so make the normal point
                // back toward the camera. Otherwise the previous block face would
                // place the new cell inside the block instead of beside it.
                var (_, dir) = RayFrom(pt);
                if (Vector3D.DotProduct(normal, dir) > 0) normal = -normal;
                return normal;
            }
        }
        return new Vector3D(0, 1, 0);
    }

    private static Vector3D SnapNormalToAxis(Vector3D n)
    {
        if (n.LengthSquared < 1e-8) return new Vector3D(0, 1, 0);
        double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
        if (ax >= ay && ax >= az) return new Vector3D(Math.Sign(n.X), 0, 0);
        if (ay >= az) return new Vector3D(0, Math.Sign(n.Y), 0);
        return new Vector3D(0, 0, Math.Sign(n.Z));
    }

    private void RefreshBrushPreview()
    {
        if (BrushVisual == null) return;
        if (!BrushActive || _level == null || !_brushHoverValid)
        {
            BrushVisual.Content = null;
            _brushPreviewState = null;
            _brushFillPreviewNode = null;
            return;
        }

        // Rebuilding a few thousand preview boxes on every mouse-move message
        // made large Box/Sphere brushes feel sticky. The preview only changes when
        // one of these settings or the target cell changes.
        var state = new BrushPreviewState(_brushMode, _brushAnchor, _brushHover, _brushShape,
            _brushMaterial, _brushCellSize, _brushHollow, _brushLayers, _brushColor.Hex,
            _brushMode == BlockBrushMode.Fill ? _brushHoverNode : null,
            _brushMode == BlockBrushMode.Replace ? _brushReplaceNodes.Count :
            _brushMode == BlockBrushMode.Erase ? _brushEraseNodes.Count : 0,
            _symmetry, BlockBrushMath.CellAt(_symmetryPivot, _brushCellSize));
        if (_brushPreviewState == state) return;
        _brushPreviewState = state;

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(220, 220, 220)));

        if (_brushMode is BlockBrushMode.Fill or BlockBrushMode.Replace or BlockBrushMode.Erase)
        {
            List<LevelNode> targets;
            if (_brushMode == BlockBrushMode.Fill && _brushHoverNode != null)
            {
                if (!ReferenceEquals(_brushFillPreviewNode, _brushHoverNode))
                {
                    _brushFillPreview.Clear();
                    _brushFillPreview.AddRange(BlockBrushMath.ConnectedIsland(
                        _level, _brushHoverNode, _brushCellSize, 700, out _));
                    _brushFillPreviewNode = _brushHoverNode;
                }
                targets = _brushFillPreview;
            }
            else if (_brushMode == BlockBrushMode.Replace && _brushReplaceNodes.Count > 0)
            {
                targets = WithMirroredTargets(_brushReplaceNodes);
            }
            else if (_brushMode == BlockBrushMode.Erase && _brushEraseNodes.Count > 0)
            {
                targets = WithMirroredTargets(_brushEraseNodes);
            }
            else
            {
                targets = _brushHoverNode == null ? new List<LevelNode>() : new List<LevelNode> { _brushHoverNode };
            }
            var color = _brushMode switch
            {
                BlockBrushMode.Fill => new Col(0.2f, 0.9f, 0.4f, 1f),
                BlockBrushMode.Erase => new Col(1f, 0.22f, 0.18f, 1f),
                _ => new Col(1f, 0.6f, 0.12f, 1f)
            };
            var wire = new MeshBuilder();
            foreach (var node in targets)
            {
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) continue;
                var size = node.WorldScale;
                wire.WireBox(new Point3D(node.WorldPosition.X, node.WorldPosition.Y, node.WorldPosition.Z),
                    new Vector3D(Math.Max(0.05f, size.X * 1.025f), Math.Max(0.05f, size.Y * 1.025f), Math.Max(0.05f, size.Z * 1.025f)),
                    Math.Max(0.012, Math.Min(size.X, Math.Min(size.Y, size.Z)) * 0.035));
            }
            var material = BrushPreviewMaterial(color, 235);
            group.Children.Add(new GeometryModel3D(wire.ToMesh(), material) { BackMaterial = material });
        }
        else
        {
            // While merely hovering, preview the shape of a single click at the
            // hovered cell. Using the previous stroke's anchor here made a huge
            // rectangle follow the mouse after the first drag.
            var start = _brushStroke ? _brushAnchor : _brushHover;
            var plan = PlanBrush(start, _brushHover, _brushMode);
            AddBrushCellPreview(group, plan, new Col(0.15f, 0.82f, 1f, 1f));
        }
        BrushVisual.Content = group;
    }

    private void AddBrushCellPreview(Model3DGroup group, BrushPlan plan, Col color)
    {
        if (plan.Cells.Count == 0) return;
        double size = _brushCellSize;
        var wire = new MeshBuilder();
        var fill = new MeshBuilder();
        if (plan.Cells.Count <= 350)
        {
            foreach (var cell in plan.Cells)
            {
                var centre = BlockBrushMath.Center(cell, size);
                var p = new Point3D(centre.X, centre.Y, centre.Z);
                wire.WireBox(p, new Vector3D(size * 0.985, size * 0.985, size * 0.985), Math.Max(0.012, size * 0.015));
                fill.Box(p, new Vector3D(size * 0.9, size * 0.9, size * 0.9));
            }
        }
        else
        {
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var cell in plan.Cells)
            {
                var c = BlockBrushMath.Center(cell, size);
                minX = MathF.Min(minX, c.X); minY = MathF.Min(minY, c.Y); minZ = MathF.Min(minZ, c.Z);
                maxX = MathF.Max(maxX, c.X); maxY = MathF.Max(maxY, c.Y); maxZ = MathF.Max(maxZ, c.Z);
            }
            wire.WireBox(new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2),
                new Vector3D(maxX - minX + size, maxY - minY + size, maxZ - minZ + size), Math.Max(0.025, size * 0.035));
            int stride = Math.Max(1, plan.Cells.Count / 1200);
            for (int i = 0; i < plan.Cells.Count; i += stride)
            {
                var c = BlockBrushMath.Center(plan.Cells[i], size);
                fill.Box(new Point3D(c.X, c.Y, c.Z), new Vector3D(size * 0.18, size * 0.18, size * 0.18));
            }
        }
        group.Children.Add(new GeometryModel3D(wire.ToMesh(), BrushPreviewMaterial(color, 240))
        {
            BackMaterial = BrushPreviewMaterial(color, 240)
        });
        group.Children.Add(new GeometryModel3D(fill.ToMesh(), BrushPreviewMaterial(color, 38))
        {
            BackMaterial = BrushPreviewMaterial(color, 38)
        });
    }

    private static Material BrushPreviewMaterial(Col color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)color.Ri, (byte)color.Gi, (byte)color.Bi));
        brush.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new EmissiveMaterial(brush));
        if (alpha < 200) material.Children.Add(new DiffuseMaterial(brush));
        material.Freeze();
        return material;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.88 : 1.12), 0.4, 800);
        UpdateCamera();
        RefreshGizmo();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (PlayMode) return;
        // While flying with RMB the movement keys belong to the camera, not the tools.
        if (_orbiting && e.Key is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
        {
            e.Handled = true;
            return;
        }
        if (BrushActive && e.Key == Key.Escape)
        {
            CancelBrushStroke(clearPreview: true);
            e.Handled = true;
            return;
        }
        if (MeasureActive && e.Key == Key.Escape)
        {
            ClearMeasure();
            e.Handled = true;
            return;
        }
        if (PrefabActive && (e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets))
        {
            RotatePrefab(e.Key == Key.OemOpenBrackets ? -90 : 90);
            e.Handled = true;
            return;
        }
        if (BuildActive)
        {
            if (e.Key == Key.Escape)
            {
                CancelBuild();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && _buildShape == BuildShape.Polygon)
            {
                CommitPolygon();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Back && _buildShape == BuildShape.Polygon && _buildPolygon.Count > 0)
            {
                _buildPolygon.RemoveAt(_buildPolygon.Count - 1);
                _buildPreviewKey = "";
                RefreshBuildPreview();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets)
            {
                BuildHeight += e.Key == Key.OemCloseBrackets ? 1 : -1;
                RefreshBuildPreview();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.F) { FocusSelected(); e.Handled = true; }
        else if (TerrainActive && (e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets))
        {
            TerrainRadius += e.Key == Key.OemCloseBrackets ? 1 : -1;
            e.Handled = true;
        }
        else if (e.Key == Key.W) { SetTool(EditorTool.Move); e.Handled = true; }
        else if (e.Key == Key.E) { SetTool(EditorTool.Rotate); e.Handled = true; }
        else if (e.Key == Key.R) { SetTool(EditorTool.Scale); e.Handled = true; }
        else if (e.Key == Key.Q) { SetTool(EditorTool.Select); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
    }

    private (Point3D Origin, Vector3D Dir) RayFrom(Point p)
    {
        double w = Math.Max(1, Viewport.ActualWidth), h = Math.Max(1, Viewport.ActualHeight);
        double aspect = w / h;
        double tan = Math.Tan(Camera.FieldOfView * Math.PI / 360);
        var look = Camera.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, Camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, look); up.Normalize();
        double nx = (2 * p.X / w - 1) * tan * aspect;
        double ny = (1 - 2 * p.Y / h) * tan;
        var dir = look + right * nx + up * ny;
        dir.Normalize();
        return (Camera.Position, dir);
    }

    private Point3D? GroundPoint(Point p)
    {
        var (o, d) = RayFrom(p);
        return IntersectPlane(o, d, new Point3D(0, 0, 0), new Vector3D(0, 1, 0));
    }

    private static Point3D? IntersectPlane(Point3D origin, Vector3D dir, Point3D planePoint, Vector3D normal)
    {
        double denom = Vector3D.DotProduct(dir, normal);
        if (Math.Abs(denom) < 1e-7) return null;
        double t = Vector3D.DotProduct(planePoint - origin, normal) / denom;
        if (t <= 0) return null;
        return origin + dir * t;
    }

    private static double ClosestOnAxis(Point3D rayOrigin, Vector3D rayDir, Point3D axisOrigin, Vector3D axisDir)
    {
        var w0 = axisOrigin - rayOrigin;
        double b = Vector3D.DotProduct(rayDir, axisDir);
        double d = Vector3D.DotProduct(rayDir, w0);
        double e = Vector3D.DotProduct(axisDir, w0);
        double denom = 1 - b * b;
        if (Math.Abs(denom) < 1e-7) return 0;
        return (b * d - e) / denom;
    }

    private static Vector3D Perp1(Vector3D axis)
    {
        var up = Math.Abs(axis.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var p = Vector3D.CrossProduct(axis, up); p.Normalize(); return p;
    }
    private static Vector3D Perp2(Vector3D axis)
    {
        var p = Vector3D.CrossProduct(axis, Perp1(axis)); p.Normalize(); return p;
    }

    private static float Vec3Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Vec3Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
