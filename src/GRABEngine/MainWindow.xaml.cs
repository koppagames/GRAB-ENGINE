using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrabEngine.AI;
using GrabEngine.Audio;
using GrabEngine.Code;
using GrabEngine.Controls;
using GrabEngine.Core;
using GrabEngine.Formats;
using GrabEngine.Rendering;
using GrabEngine.UI;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace GRABEngine;

public partial class MainWindow : Window
{
    private GrabLevel _level = GrabLevel.CreateDefault();
    private LevelNode? _selected;
    private List<LevelNode> _selection = new();
    private string? _projectPath;
    private bool _syncSelection;
    private bool _suppressScript;
    private bool _suppressAnimationSync;
    private bool _suppressHistory;
    private bool _compiling;
    private bool _applyingHistory;
    private readonly EditHistory _history = new();
    private readonly List<ProblemItem> _problems = new();
    private readonly Dictionary<string, HashSet<int>> _breakpointsByNode = new();

    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private bool _startupRecoveryChecked;
    private readonly Stopwatch _playClock = new();
    private PlaySession? _play;
    private GameAudio? _playAudio;
    private string? _playBackup;
    private string? _playSelectedId;
    private List<string> _playSelectedIds = new();
    private bool _playing;
    private bool _paused;
    private bool _consoleAutoOpened;
    private double _lastTick;
    private readonly HashSet<Key> _keys = new();
    private string _lastExportPath = "";
    private BlockBrushMode _lastBrushMode = BlockBrushMode.Fill;
    private bool _syncingBrushUi;
    private bool _syncingTerrainUi;
    private readonly BlockPaletteState _blockPalette = BlockPalette.Load();
    private DocsWindow? _docsWindow;
    private AnimationPanel? _animationPanel;
    private PrefabInfo? _stampPrefab;
    private string _lastGlbPath = "";
    private string _hierarchyQuery = "";
    private string? _layerFilterId;
    private bool _syncingLayerFilter;
    private GridLength _bottomHeightBeforeAnimation = new(292);
    private bool _animationDockExpanded;

    public MainWindow()
    {
        InitializeComponent();
        Scene.SelectionSetChanged += Scene_SelectionSetChanged;
        Scene.SceneModified += Scene_SceneModified;
        Scene.NodePlaced += Scene_NodePlaced;
        Scene.DeleteRequested += DeleteSelected;
        Scene.BrushApplied += Scene_BrushApplied;
        Scene.TerrainSculpted += Scene_TerrainSculpted;
        Scene.PrefabStampRequested += Scene_PrefabStampRequested;
        Scene.BuildHeightChanged += height =>
        {
            if (BuildHeightSlider == null || BuildHeightValue == null) return;
            _syncingBuildUi = true;
            try
            {
                BuildHeightSlider.Value = height;
                BuildHeightValue.Text = height.ToString();
            }
            finally { _syncingBuildUi = false; }
        };
        Scene.MeasurementChanged += text => StatusText.Text = text;
        CodeEditor.ScriptChanged += CodeEditor_ScriptChanged;
        CodeEditor.CompileRequested += () => CompileAll();
        CodeEditor.ProblemsRequested += () => BottomTabs.SelectedIndex = 3;
        CodeEditor.BreakpointsChanged += CodeEditor_BreakpointsChanged;
        Debugger.ContinueRequested += DebugContinue;
        Debugger.StepLineRequested += DebugStepLine;
        Debugger.StepInstructionRequested += DebugStepInstruction;
        Debugger.PauseRequested += () => { if (_playing && !_paused) PausePlay(); };
        Debugger.StopRequested += StopPlay;
        Debugger.SourceRequested += () => BottomTabs.SelectedIndex = 0;
        Debugger.ClearBreakpointsRequested += ClearAllBreakpoints;
        _playTimer.Tick += PlayTick;
        _autosaveTimer.Tick += (_, _) => AutoSave();
        _autosaveTimer.Start();
        bool automation = Environment.GetCommandLineArgs().Any(a =>
            a is "--shot" or "--shot-animation" or "--shot-window" or "--shot-project" or "--selftest" or "--shot-play" or "--shot-brush" or "--shot-build");
        if (!automation) Loaded += (_, _) => CheckAutosaveRecovery();
        _animationPanel = new AnimationPanel(new AnimationWindowContext
        {
            Level = _level,
            PreviewPose = (node, position, rotation) => Scene.PreviewPose(node, position, rotation),
            ClearPreview = () => Scene.ClearPreviewPose(),
            Changed = continuous =>
            {
                MarkDirty(continuous ? "animation" : null, "Animation");
                Scene.Refresh();
                HierarchyInfo.Text = $"{_level.AllNodes().Count()} objects";
            },
            CreateCodeBlock = CreateCodeBlockFromSnippet
        });
        AnimationTab.Content = _animationPanel;
        BottomTabs.SelectionChanged += BottomTabs_SelectionChanged;
        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewKeyUp += Window_PreviewKeyUp;
        Closing += Window_Closing;

        LoadLevel(GrabLevel.CreateDefault(), null);
        InitializeBrushPalette();
        InitializeBuildPalette();
        InitializeTerrainPalette();
        Log("GRAB ENGINE ready. Real GRAB assets loaded from the game install.");
        Log("F5 play  •  F7 compile  •  Ctrl+S save project  •  Ctrl+E export .level");
        Log("Block brush: B, then Fill / Replace / Erase / Line / Box / Sphere");
        Log("Draw structures: G, then drag a footprint or draw a custom shape");
        Log("Measure: M  •  Prefab stamp: choose a prefab under Prefabs → Stamp Prefab, then P");
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) OpenFile(args[1]);
        RefreshPrefabMenu();
    }

    private void Log(string message)
    {
        ConsoleList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (ConsoleList.Items.Count > 400) ConsoleList.Items.RemoveAt(0);
        ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    private void LoadLevel(GrabLevel level, string? projectPath)
    {
        bool previousSuppress = _suppressHistory;
        _suppressHistory = true;
        try
        {
            _level = level;
            _projectPath = projectPath;
            _selected = null;
            _selection.Clear();
            _level.RebuildParents();
            _level.EnsureLayers();
            _level.EnsureNames();
            _animationPanel?.SetLevel(_level);
            Scene.PlayMode = false;
            Scene.ShowPlayer(false);
            Scene.SetLevel(_level);
            Scene.SnapEnabled = SnapCheck.IsChecked == true;
            Scene.SetAmbience(_level.Ambience);
            BuildHierarchy();
            SelectNode(null);
            UpdateTitle();
            StatusText.Text = $"{_level.AllNodes().Count()} objects  •  {_level.Title}";
            BottomTabs.SelectedIndex = 0;
            _history.Reset(ProjectIO.Serialize(_level), null);
            _lastSnapshotUtc = DateTime.UtcNow;
            UpdateHistoryUi();
        }
        finally
        {
            _suppressHistory = previousSuppress;
        }
    }

    private void UpdateTitle()
    {
        string name = _projectPath != null ? Path.GetFileName(_projectPath) : _level.Title;
        Title = $"GRAB ENGINE  —  {name}{(_level.IsDirty ? " *" : "")}";
        ToolbarFile.Text = _projectPath != null ? Path.GetFileName(_projectPath) : (_level.IsDirty ? "Untitled *" : "Untitled");
    }

    private void MarkDirty(string? coalesceKey = null, string? historyLabel = null)
    {
        _level.IsDirty = true;
        UpdateTitle();
        if (_suppressHistory || _compiling || _applyingHistory || _playing || _history.Current == null) return;
        string json = ProjectIO.Serialize(_level);
        var label = historyLabel ?? coalesceKey switch
        {
            "transform" => "Transform",
            "inspector" => "Inspector",
            "animation" => "Animation",
            "code" => "C# script",
            _ => "Edit"
        };
        if (_history.Record(json, _selected?.EditorId, label, coalesceKey)) UpdateHistoryUi();
    }

    private void UpdateHistoryUi()
    {
        bool enabled = !_playing;
        UndoButton.IsEnabled = enabled && _history.CanUndo;
        RedoButton.IsEnabled = enabled && _history.CanRedo;
        UndoMenuItem.IsEnabled = enabled && _history.CanUndo;
        RedoMenuItem.IsEnabled = enabled && _history.CanRedo;
        UndoButton.ToolTip = _history.CanUndo ? $"Undo {_history.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        RedoButton.ToolTip = _history.CanRedo ? $"Redo {_history.RedoLabel} (Ctrl+Y)" : "Nothing to redo";
        UndoMenuItem.Header = _history.CanUndo ? $"Undo {_history.UndoLabel}" : "Undo";
        RedoMenuItem.Header = _history.CanRedo ? $"Redo {_history.RedoLabel}" : "Redo";
    }

    private void Undo()
    {
        if (_playing)
        {
            StatusText.Text = "Undo is unavailable while playing. Press Esc to stop first.";
            return;
        }
        if (_compiling || _history.Current == null) return;
        var target = _history.Undo(ProjectIO.Serialize(_level), _selected?.EditorId, out var label);
        if (target == null)
        {
            StatusText.Text = "Nothing to undo";
            UpdateHistoryUi();
            return;
        }
        ApplyHistorySnapshot(target, $"Undid {label}");
    }

    private void Redo()
    {
        if (_playing)
        {
            StatusText.Text = "Redo is unavailable while playing. Press Esc to stop first.";
            return;
        }
        if (_compiling || _history.Current == null) return;
        var target = _history.Redo(ProjectIO.Serialize(_level), _selected?.EditorId, out var label);
        if (target == null)
        {
            StatusText.Text = "Nothing to redo";
            UpdateHistoryUi();
            return;
        }
        ApplyHistorySnapshot(target, $"Redid {label}");
    }

    private void ApplyHistorySnapshot(EditSnapshot snapshot, string message)
    {
        _applyingHistory = true;
        _suppressHistory = true;
        try
        {
            _animationPanel?.Deactivate();
            var restored = ProjectIO.Deserialize(snapshot.Json);
            restored.FilePath = _projectPath;
            restored.IsDirty = true;
            _level = restored;
            _animationPanel?.SetLevel(_level);
            Scene.PlayMode = false;
            Scene.ShowPlayer(false);
            Scene.SetLevel(_level, frame: false);
            Scene.SetAmbience(_level.Ambience);
            BuildHierarchy();
            var node = snapshot.SelectedId != null ? _level.FindByEditorId(snapshot.SelectedId) : null;
            SelectNode(node);
            CompileAll();
            _history.SyncCurrent(ProjectIO.Serialize(_level), _selected?.EditorId);
            Scene.Refresh();
            _level.IsDirty = true;
            UpdateTitle();
            StatusText.Text = message;
        }
        finally
        {
            _applyingHistory = false;
            _suppressHistory = false;
            UpdateHistoryUi();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    // ================= hierarchy =================

    private void BuildHierarchy()
    {
        _level.EnsureLayers();
        RefreshLayerFilter();
        _syncSelection = true;
        try
        {
            HierarchyTree.Items.Clear();
            bool any = false;
            foreach (var n in _level.Nodes)
                any |= AddTreeItem(HierarchyTree.Items, n);
            if (!any && (_hierarchyQuery.Length > 0 || _layerFilterId != null))
            {
                HierarchyTree.Items.Add(new TreeViewItem
                {
                    Header = new TextBlock
                    {
                        Text = "No objects match this filter",
                        Foreground = new SolidColorBrush(Color.FromRgb(145, 145, 145)),
                        FontStyle = FontStyles.Italic
                    },
                    IsEnabled = false
                });
            }
            int count = _level.AllNodes().Count();
            HierarchyInfo.Text = _hierarchyQuery.Length > 0 || _layerFilterId != null
                ? $"{count} object{(count == 1 ? "" : "s")}  ·  filtered"
                : $"{count} object{(count == 1 ? "" : "s")}";
            RefreshTreeSelection();
        }
        finally
        {
            _syncSelection = false;
        }
    }

    private void RefreshLayerFilter()
    {
        if (LayerFilterCombo == null) return;
        _syncingLayerFilter = true;
        try
        {
            string? keep = _layerFilterId;
            LayerFilterCombo.Items.Clear();
            LayerFilterCombo.Items.Add(new ComboBoxItem { Content = "All Layers", Tag = null });
            foreach (var layer in _level.EditorLayers)
                LayerFilterCombo.Items.Add(new ComboBoxItem { Content = layer.Name, Tag = layer.Id });
            int index = 0;
            if (keep != null)
            {
                for (int i = 1; i < LayerFilterCombo.Items.Count; i++)
                    if ((LayerFilterCombo.Items[i] as ComboBoxItem)?.Tag as string == keep) { index = i; break; }
            }
            LayerFilterCombo.SelectedIndex = index;
            _layerFilterId = index == 0 ? null : (LayerFilterCombo.Items[index] as ComboBoxItem)?.Tag as string;
        }
        finally
        {
            _syncingLayerFilter = false;
        }
    }

    private void LayerFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLayerFilter) return;
        _layerFilterId = (LayerFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        BuildHierarchy();
    }

    private void HierarchySearch_Changed(object sender, TextChangedEventArgs e)
    {
        string query = HierarchySearch.Text.Trim();
        if (query == _hierarchyQuery) return;
        _hierarchyQuery = query;
        BuildHierarchy();
    }

    private void HierarchyClearSearch_Click(object sender, RoutedEventArgs e)
    {
        HierarchySearch.Text = "";
        _hierarchyQuery = "";
        BuildHierarchy();
    }

    private void Terrain_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var window = new TerrainStudioWindow(this, _level);
        window.Applied += result =>
        {
            BuildHierarchy();
            SelectNodes(result.TerrainGroups);
            Scene.Refresh();
            MarkDirty(null, "Build terrain");
            StatusText.Text = result.Summary;
            Log("Terrain built: " + result.Summary);
        };
        window.LevelChanged += () =>
        {
            BuildHierarchy();
            Scene.Refresh();
            MarkDirty(null, "Scatter prefabs");
        };
        window.ShowDialog();
    }
    private void StructureStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var window = new StructureStudioWindow(this, _level, Scene.ViewTarget);
        window.DrawInSceneRequested += () => SelectEditorTool(EditorTool.Build);
        window.Built += (nodes, name) =>
        {
            var added = Scene.AddStructure(nodes, name);
            BuildHierarchy();
            var roots = Scene.TopLevelSelection();
            if (roots.Count > 0) SelectNodes(roots);
            Scene.Refresh();
            MarkDirty(null, "Build " + name);
            StatusText.Text = $"{name}: {added.Count} block(s) added";
            Log($"Built {name}: {added.Count} block(s)");
        };
        window.ShowDialog();
    }

    private void ValidateLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var window = new ValidationWindow(this, _level,
            node => SelectNode(node),
            report => Scene.ShowValidation(report.Issues));
        window.ShowDialog();
    }

    private void ClearValidation_Click(object sender, RoutedEventArgs e)
    {
        Scene.ClearValidation();
        StatusText.Text = "Validation overlay cleared";
    }

    private void SnapMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Scene == null || SnapModeCombo == null) return;
        Scene.SnapMode = (SmartSnapMode)Math.Clamp(SnapModeCombo.SelectedIndex, 0, 3);
        StatusText.Text = Scene.SnapMode switch
        {
            SmartSnapMode.Face => "Smart snap: Face — blocks and prefabs build on the highlighted surface",
            SmartSnapMode.Edge => "Smart snap: Edge — placement locks to block edges",
            SmartSnapMode.Center => "Smart snap: Center — placement uses the object centre",
            _ => "Smart snap: Grid — placement uses the ground grid"
        };
    }

    private void AddScriptComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_playing || _selected == null)
        {
            StatusText.Text = "Select the object that should receive the script first";
            return;
        }
        var host = _selected;
        var component = new LevelNode
        {
            Kind = NodeKind.GASM,
            Name = host.DisplayName + " Script",
            ComponentHostId = host.EditorId,
            ComponentName = "MyScript",
            Position = host.WorldPosition,
            Rotation = host.WorldRotation,
            StartActive = true,
            LayerId = host.LayerId
        };
        component.Script = CodeTemplates.NewScript(_level, component);
        _level.AddNode(component);
        BuildHierarchy();
        SelectNode(component);
        Scene.Refresh();
        MarkDirty(null, "Add script component");
        StatusText.Text = $"Added C# component to {host.DisplayName}";
    }

    private void SavePrefabVariant_Click(object sender, RoutedEventArgs e)
    {
        if (_playing || _selection.Count == 0)
        {
            MessageBox.Show(this, "Select a prefab instance first, then create a variant.", "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var source = _selected?.PrefabSource ?? "";
        string suggested = (_selected?.DisplayName ?? "Prefab") + " Variant";
        string? name = Prompt.Show(this, "Create Prefab Variant", "Variant name", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = PrefabLibrary.Save(Scene.TopLevelSelection(), name, source);
            RefreshPrefabMenu();
            StatusText.Text = string.IsNullOrWhiteSpace(source)
                ? "Saved a new prefab (the selection was not a prefab instance)"
                : "Variant saved: " + Path.GetFileName(path);
            Log($"Saved prefab variant '{name}'" + (string.IsNullOrWhiteSpace(source) ? "" : " based on " + Path.GetFileName(source)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the variant:\n\n" + ex.Message, "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Optimizer_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var window = new LevelOptimizerWindow(this, _level, result =>
        {
            _selected = null;
            _selection.Clear();
            BuildHierarchy();
            Scene.SetSelection(null);
            Scene.Refresh();
            MarkDirty(null, "Optimize level");
            StatusText.Text = result.Summary;
            Log("Level optimizer: " + result.Summary);
        }, node =>
        {
            if (node != null) SelectNode(node);
        });
        window.ShowDialog();
    }
    private void PrefabPalette_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        PrefabPaletteWindow? palette = null;
        palette = new PrefabPaletteWindow(this,
            prefab => { palette?.Close(); StartPrefabStamp(prefab); },
            prefab => { palette?.Close(); SpawnPrefab(prefab); },
            () => SavePrefab_Click(this, new RoutedEventArgs()));
        palette.ShowDialog();
    }
    private void AudioStudio_Click(object sender, RoutedEventArgs e) => OpenAudioStudio(0);

    private void NeuralLab_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var window = new NeuralLabWindow(this, _level);
        bool applied = false;
        window.Applied += () => applied = true;
        window.ShowDialog();
        if (applied)
        {
            Scene.SetLevel(_level, frame: false);
            BuildHierarchy();
            RefreshInspector();
            Scene.Refresh();
            MarkDirty(null, "Neural Lab controller");
            StatusText.Text = "Neural controller applied to the level";
        }
    }

    private void TextToSpeech_Click(object sender, RoutedEventArgs e) => OpenAudioStudio(2);

    private void OpenAudioStudio(int tab)
    {
        if (_playing) StopPlay();
        var window = new AudioStudioWindow(this, _level, () => _selected?.WorldPosition ?? new Vec3(0, 1, 0), tab);
        window.ShowDialog();
        if (window.Changed)
        {
            Scene.SetLevel(_level, frame: false);
            BuildHierarchy();
            RefreshInspector();
            MarkDirty(null, "Audio Studio");
            Log("Audio Studio updated the level's sound blocks");
        }
    }

    private void FocusHierarchySearch_Click(object sender, RoutedEventArgs e)
    {
        HierarchySearch.Focus();
        HierarchySearch.SelectAll();
    }

    private void ManageLayers_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var window = new LayerManagerWindow(this, _level, _selection)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.Changed)
        {
            foreach (var n in _selection)
                if (!string.IsNullOrWhiteSpace(window.AssignedLayerId)) n.LayerId = window.AssignedLayerId;
            BuildHierarchy();
            Scene.Refresh();
            MarkDirty(null, "Editor layers");
        }
    }

    /// <summary>Animation triggers made by the C# Animator API are generated objects,
    /// but unlike continuation pages and buses they are useful to inspect. Show them
    /// in the Hierarchy with the clip they control.</summary>
    private static bool IsScriptAnimationTrigger(LevelNode n) =>
        n.Kind == NodeKind.Trigger && n.Generated && n.ScriptCreated &&
        n.ScriptSourceKey.StartsWith("anim:", StringComparison.Ordinal);

    private bool AddTreeItem(ItemCollection items, LevelNode n)
    {
        bool animationTrigger = IsScriptAnimationTrigger(n);
        // Terrain and water are generated, but they are real scene objects the user
        // must be able to find, select, hide and delete. Keeping every generated node
        // out of the tree made freshly built terrain look like it had vanished.
        bool generatedRoot = n.Generated &&
            (n.EditorLabel is "terrain" or "terrain-water" ||
             n.Name.StartsWith("Terrain ", StringComparison.Ordinal) || n.Name == "Water");
        if (n.Generated && !animationTrigger && !generatedRoot) return false;
        bool filterActive = _hierarchyQuery.Length > 0 || _layerFilterId != null;
        bool selfMatches = !filterActive || MatchesHierarchyFilter(n);

        // Large brush strokes are collapsed by default so a voxel build does not
        // leave the hierarchy unusable; the group header still shows the stroke.
        bool collapseBrush = n.Kind == NodeKind.Group &&
            (n.Name.StartsWith("Brush ", StringComparison.Ordinal) ||
             n.Name.StartsWith("GLB ", StringComparison.Ordinal) ||
             n.Name.StartsWith("Draw ", StringComparison.Ordinal) ||
             n.Name.StartsWith("Drawn ", StringComparison.Ordinal)) &&
            n.Children.Count > 0;
        // Terrain chunks can hold hundreds of column blocks. Show the chunk in the
        // hierarchy, but keep its children folded until the user expands it.
        bool collapseTerrain = n.Kind == NodeKind.Group &&
            (n.EditorLabel == "terrain" || n.Name.StartsWith("Terrain ", StringComparison.Ordinal)) &&
            n.Children.Count > 0;
        var item = new TreeViewItem { Tag = n, IsExpanded = filterActive || !(collapseBrush || collapseTerrain) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = IconFor(n.Kind),
            Width = 18,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 12,
            Foreground = IconBrush(n.Kind),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = n.DisplayName, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(n.EditorLabel))
            row.Children.Add(new TextBlock
            {
                Text = "  \"" + n.EditorLabel + "\"",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 185, 130)),
                VerticalAlignment = VerticalAlignment.Center
            });
        if (n.ScriptCreated)
            row.Children.Add(new TextBlock
            {
                Text = "  ⚙ script",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });
        if (animationTrigger)
        {
            var clip = n.TriggerTargets.FirstOrDefault(t => t.Kind == 1)?.AnimationName;
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(clip) ? "  anim trigger" : "  anim " + clip,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 200, 120)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (n.IsLocked)
            row.Children.Add(new TextBlock { Text = " 🔒", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) });
        item.Header = row;
        item.ContextMenu = BuildNodeMenu(n);
        bool childMatches = false;
        if (selfMatches || !filterActive)
        {
            foreach (var c in n.Children) childMatches |= AddTreeItem(item.Items, c);
        }
        else
        {
            foreach (var c in n.Children) childMatches |= AddTreeItem(item.Items, c);
        }
        if (filterActive && !selfMatches && !childMatches) return false;
        items.Add(item);
        return true;
    }

    private bool MatchesHierarchyFilter(LevelNode n)
    {
        if (_layerFilterId != null)
        {
            // Show a whole branch when its group belongs to the layer, even if a child
            // is on the default layer. This matches how scene layers feel in Unity.
            bool layerMatch = n.LayerId == _layerFilterId;
            if (!layerMatch && n.Parent != null) layerMatch = n.Parent.LayerId == _layerFilterId;
            if (!layerMatch) return false;
        }
        if (_hierarchyQuery.Length == 0) return true;
        string q = _hierarchyQuery;
        return n.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || NodeKindInfo.Display(n.Kind).Contains(q, StringComparison.OrdinalIgnoreCase)
            || MaterialInfo.Display(n.Material).Contains(q, StringComparison.OrdinalIgnoreCase)
            || ShapeInfo.Display(n.Shape).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static string IconFor(NodeKind k) => k switch
    {
        NodeKind.Static => "▣",
        NodeKind.Crumbling => "▨",
        NodeKind.Group => "▤",
        NodeKind.Start => "⚑",
        NodeKind.Finish => "⚐",
        NodeKind.Sign => "T",
        NodeKind.Light => "☀",
        NodeKind.Sound => "♪",
        NodeKind.Trigger => "◎",
        NodeKind.Gravity => "↓",
        NodeKind.ParticleEmitter => "✧",
        NodeKind.GASM => "{}",
        NodeKind.LobbyTerminal => "▦",
        _ => "•"
    };

    private static Brush IconBrush(NodeKind k) => new SolidColorBrush(k switch
    {
        NodeKind.GASM => Color.FromRgb(120, 200, 255),
        NodeKind.Trigger => Color.FromRgb(240, 190, 90),
        NodeKind.Light => Color.FromRgb(255, 220, 120),
        NodeKind.Sign => Color.FromRgb(210, 180, 255),
        NodeKind.Sound => Color.FromRgb(140, 220, 190),
        NodeKind.Start => Color.FromRgb(110, 220, 130),
        NodeKind.Finish => Color.FromRgb(240, 130, 130),
        NodeKind.ParticleEmitter => Color.FromRgb(200, 160, 255),
        NodeKind.Gravity => Color.FromRgb(150, 190, 240),
        NodeKind.Crumbling => Color.FromRgb(220, 180, 140),
        NodeKind.Group => Color.FromRgb(200, 200, 200),
        _ => Color.FromRgb(180, 180, 180)
    });

    private ContextMenu BuildNodeMenu(LevelNode n)
    {
        var cm = new ContextMenu();
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => RenameNode(n);
        cm.Items.Add(rename);
        var dup = new MenuItem { Header = "Duplicate" };
        dup.Click += (_, _) => { SelectNode(n); DuplicateSelected(); };
        cm.Items.Add(dup);
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) => { SelectNode(n); DeleteSelected(); };
        cm.Items.Add(del);
        cm.Items.Add(new Separator());
        var child = new MenuItem { Header = "Create Child Group" };
        child.Click += (_, _) =>
        {
            var g = new LevelNode { Kind = NodeKind.Group, Name = "Group", Position = Vec3.Zero };
            _level.AddNode(g, n);
            BuildHierarchy();
            SelectNode(g);
            Scene.Refresh();
            MarkDirty(null, "Create group");
        };
        cm.Items.Add(child);
        var focus = new MenuItem { Header = "Focus  (F)" };
        focus.Click += (_, _) => { SelectNode(n); Scene.FocusSelected(); };
        cm.Items.Add(focus);
        return cm;
    }

    private void RenameNode(LevelNode n)
    {
        var name = Prompt.Show(this, "Rename", "Object name", n.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        n.Name = name.Trim();
        MarkDirty(null, "Rename");
        BuildHierarchy();
        SelectNode(n);
        Scene.Refresh();
    }

    private void Hierarchy_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncSelection || _playing) return;
        if (e.NewValue is TreeViewItem item && item.Tag is LevelNode n) SelectNode(n);
    }

    private void SelectNode(LevelNode? node, bool syncScene = true, bool syncTree = true)
    {
        if (_syncSelection) return;
        _syncSelection = true;
        try
        {
            _selection = node == null ? new List<LevelNode>() : new List<LevelNode> { node };
            _selected = node;
            if (!_suppressHistory) _history.UpdateSelection(node?.EditorId);
            if (syncTree) RefreshTreeSelection();
            if (syncScene) Scene.SetSelection(node);
            UpdateSelectionUi();
        }
        finally
        {
            _syncSelection = false;
        }
    }

    /// <summary>Selects several objects at once. The final object is the active one.</summary>
    private void SelectNodes(IReadOnlyList<LevelNode> nodes, bool syncScene = true, bool syncTree = true)
    {
        if (_syncSelection) return;
        _syncSelection = true;
        try
        {
            _selection = nodes.Where(n => n.Kind != NodeKind.Group || n.Parent == null || !nodes.Contains(n.Parent)).ToList();
            _selected = _selection.LastOrDefault();
            if (!_suppressHistory) _history.UpdateSelection(_selected?.EditorId);
            if (syncTree) RefreshTreeSelection();
            if (syncScene) Scene.SetSelectionRange(_selection);
            UpdateSelectionUi();
        }
        finally
        {
            _syncSelection = false;
        }
    }

    private void Scene_SelectionSetChanged(IReadOnlyList<LevelNode> nodes)
    {
        if (_playing || _syncSelection) return;
        _selection = nodes.Where(n => _level.AllNodes().Contains(n)).ToList();
        _selected = _selection.Count > 0 ? _selection[^1] : null;
        if (!_suppressHistory) _history.UpdateSelection(_selected?.EditorId);
        _syncSelection = true;
        try
        {
            RefreshTreeSelection();
            UpdateSelectionUi();
        }
        finally
        {
            _syncSelection = false;
        }
        if (!_suppressAnimationSync && _selected != null && ReferenceEquals(BottomTabs.SelectedItem, AnimationTab))
            _animationPanel?.SetSelected(_selected);
    }

    private void UpdateSelectionUi()
    {
        var node = _selected;
        if (_selection.Count > 1)
        {
            InspectorPath.Text = $"{_selection.Count} objects";
            SelectionStatus.Text = $"{_selection.Count} objects selected  •  {node?.DisplayName}";
        }
        else if (node != null)
        {
            InspectorPath.Text = node.DisplayName;
            SelectionStatus.Text = $"{NodeKindInfo.Display(node.Kind)}  •  {node.DisplayName}";
        }
        else
        {
            InspectorPath.Text = _level.Title;
            SelectionStatus.Text = "No selection — level ambience";
        }

        string script = node?.Kind == NodeKind.GASM ? node.Script : "";
        _suppressScript = true;
        CodeEditor.SetLevel(_level);
        CodeEditor.SetScript(script);
        _suppressScript = false;
        if (node?.Kind == NodeKind.GASM && _breakpointsByNode.TryGetValue(node.EditorId, out var breakpoints))
            CodeEditor.SetBreakpoints(breakpoints);
        CodeEditor.ClearExecutionLine();
        CodeEditor.SetHeader(node?.Kind == NodeKind.GASM ? node.DisplayName + ".cs" : "Select a Code Block to edit C#");
        CodeEditor.SetStatus(node?.Kind == NodeKind.GASM ? "C# → GASM" : "C# scripting", true);
        ShowGasmFor(_selection.Count == 1 ? node : null);
        UpdateEditorDiagnostics();
        RefreshInspector();
    }

    private void RefreshTreeSelection()
    {
        var selected = new HashSet<LevelNode>(_selection);
        var active = new SolidColorBrush(Color.FromRgb(0, 96, 160));
        var secondary = new SolidColorBrush(Color.FromRgb(45, 73, 91));
        active.Freeze();
        secondary.Freeze();
        TreeViewItem? activeItem = null;
        foreach (var item in AllTreeItems(HierarchyTree))
        {
            if (item.Tag is not LevelNode n) continue;
            bool isActive = ReferenceEquals(n, _selected);
            item.Background = isActive ? active : selected.Contains(n) ? secondary : Brushes.Transparent;
            if (isActive) activeItem = item;
        }
        if (activeItem != null && !activeItem.IsSelected) activeItem.IsSelected = true;
        activeItem?.BringIntoView();
    }

    private static IEnumerable<TreeViewItem> AllTreeItems(ItemsControl parent)
    {
        foreach (var obj in parent.Items)
        {
            if (obj is not TreeViewItem t) continue;
            yield return t;
            foreach (var child in AllTreeItems(t)) yield return child;
        }
    }

    private static TreeViewItem? FindTreeItem(ItemsControl parent, LevelNode node)
    {
        foreach (var obj in parent.Items)
        {
            if (obj is not TreeViewItem t) continue;
            if (ReferenceEquals(t.Tag, node)) return t;
            var found = FindTreeItem(t, node);
            if (found != null) return found;
        }
        return null;
    }

    private void Scene_SceneModified()
    {
        MarkDirty("transform", "Transform");
        HierarchyInfo.Text = $"{_level.AllNodes().Count()} objects";
        SelectionStatus.Text = _selection.Count > 1
            ? $"{_selection.Count} objects selected  •  {_selected?.DisplayName}"
            : _selected != null ? $"{NodeKindInfo.Display(_selected.Kind)}  •  {_selected.DisplayName}" : "No selection";
    }

    private void Scene_NodePlaced(LevelNode node)
    {
        BuildHierarchy();
        SelectNode(node);
        MarkDirty(null, "Place object");
    }

    // ================= inspector =================

    private void RefreshInspector()
    {
        var ctx = new InspectorContext
        {
            Level = _level,
            Node = _selected,
            Nodes = _selection,
            Changed = () =>
            {
                MarkDirty("inspector", "Inspector");
                if (_selected == null) Scene.SetAmbience(_level.Ambience);
                else Scene.Refresh();
                HierarchyInfo.Text = $"{_level.AllNodes().Count()} objects";
            },
            Rebuild = () => Dispatcher.BeginInvoke(new Action(RefreshInspector)),
            OpenCode = () =>
            {
                BottomTabs.SelectedIndex = 0;
                CodeEditor.InnerEditor.Focus();
            },
            CompileCode = () => CompileAll(),
            OpenAnimation = () => ShowAnimationPanel(),
            Duplicate = DuplicateSelected,
            Delete = DeleteSelected,
            Status = s => StatusText.Text = s,
            Select = node =>
            {
                BuildHierarchy();
                SelectNode(node);
                Scene.Refresh();
            }
        };
        InspectorBuilder.Build(InspectorHost, ctx);
    }

    // ================= editing commands =================

    private LevelNode? ParentForNew()
        => _selected is { Kind: NodeKind.Group } ? _selected : null;

    private Vec3 NextPosition()
    {
        if (_selected != null && _selected.Kind != NodeKind.Finish)
        {
            var s = _selected.WorldScale;
            // Drop the new object beside the selection and resting on top of it,
            // instead of half-burying it in the selected block.
            return new Vec3(_selected.WorldPosition.X + s.X * 0.5f + 0.9f,
                _selected.WorldPosition.Y + s.Y * 0.5f + 0.5f,
                _selected.WorldPosition.Z);
        }
        return new Vec3(0, 0.5f, 0);
    }

    private void AddShape(int shape)
    {
        if (_playing) return;
        var n = new LevelNode
        {
            Kind = NodeKind.Static,
            Shape = shape,
            Material = 8,
            Position = NextPosition(),
            Scale = Vec3.One,
            Color1 = new Col(0.72f, 0.72f, 0.72f, 1f)
        };
        _level.AddNode(n, ParentForNew());
        BuildHierarchy();
        SelectNode(n);
        Scene.Refresh();
        MarkDirty(null, "Create " + ShapeInfo.Display(shape));
        Log($"Created {ShapeInfo.Display(shape)} '{n.DisplayName}'");
    }

    private void AddComponent(string kind)
    {
        if (_playing) return;
        var n = new LevelNode { Kind = NodeKind.Static, Position = NextPosition(), Scale = Vec3.One };
        switch (kind)
        {
            case "GASM":
                n.Kind = NodeKind.GASM;
                n.StartActive = true;
                break;
            case "Trigger":
                n.Kind = NodeKind.Trigger;
                n.Scale = new Vec3(2, 2, 2);
                break;
            case "Light":
                n.Kind = NodeKind.Light;
                n.LightColor = Col.White;
                n.LightIntensity = 2.3f;
                n.LightRange = 4.3f;
                break;
            case "Sign":
                n.Kind = NodeKind.Sign;
                n.Text = "Hello!";
                break;
            case "Sound":
                n.Kind = NodeKind.Sound;
                n.SoundName = "sound";
                break;
            case "ParticleEmitter":
                n.Kind = NodeKind.ParticleEmitter;
                break;
            case "Start":
                n.Kind = NodeKind.Start;
                n.Radius = 0.5f;
                break;
            case "Finish":
                n.Kind = NodeKind.Finish;
                n.Radius = 1f;
                break;
            case "Gravity":
                n.Kind = NodeKind.Gravity;
                n.Scale = new Vec3(5, 5, 5);
                break;
            case "Crumbling":
                n.Kind = NodeKind.Crumbling;
                n.Material = 7;
                break;
            case "LobbyTerminal":
                n.Kind = NodeKind.LobbyTerminal;
                break;
        }
        _level.AddNode(n, ParentForNew());
        if (n.Kind == NodeKind.GASM) n.Script = CodeTemplates.NewScript(_level, n);
        BuildHierarchy();
        SelectNode(n);
        Scene.Refresh();
        MarkDirty(null, "Create " + NodeKindInfo.Display(n.Kind));
        Log($"Created {NodeKindInfo.Display(n.Kind)} '{n.DisplayName}'");
    }

    private void DuplicateSelected()
    {
        if (_selection.Count == 0 || _playing) return;
        var copies = new List<LevelNode>();
        foreach (var source in Scene.TopLevelSelection())
        {
            var copy = source.Clone();
            copy.Name = "";
            copy.Position = source.Position + new Vec3(1, 0, 0);
            _level.AddNode(copy, source.Parent);
            copies.Add(copy);
        }
        _level.EnsureNames();
        BuildHierarchy();
        SelectNodes(copies);
        Scene.Refresh();
        MarkDirty(null, copies.Count == 1 ? "Duplicate" : "Duplicate selection");
        Log($"Duplicated {copies.Count} object(s)");
    }

    private void DeleteSelected()
    {
        if (_selection.Count == 0 || _playing) return;
        var roots = Scene.TopLevelSelection();
        foreach (var n in roots)
        {
            if (n.Kind == NodeKind.GASM)
            {
                foreach (var child in _level.AllNodes().Where(x =>
                             x.ScriptSourceBlock == n.EditorId || x.GeneratedFor == n.EditorId).ToList())
                    _level.RemoveNode(child);
            }
            var label = n.DisplayName;
            _level.RemoveNode(n);
            Log($"Deleted '{label}'");
        }
        _level.EnsureNames();
        _selected = null;
        _selection.Clear();
        BuildHierarchy();
        SelectNode(null);
        Scene.Refresh();
        MarkDirty(null, roots.Count == 1 ? "Delete" : "Delete selection");
    }

    private void Reparent(LevelNode node, LevelNode? parent)
    {
        if (parent != null && (ReferenceEquals(parent, node) || node.Descendants().Contains(parent))) return;
        var worldPos = node.WorldPosition;
        var worldRot = node.WorldRotation;
        var worldScl = node.WorldScale;
        if (node.Parent != null) node.Parent.Children.Remove(node);
        else _level.Nodes.Remove(node);
        if (parent != null) parent.Children.Add(node);
        else _level.Nodes.Add(node);
        node.RebuildParents(parent);
        if (parent == null)
        {
            node.Position = worldPos;
            node.Rotation = worldRot;
            node.Scale = worldScl;
        }
        else
        {
            var rel = worldPos - parent.WorldPosition;
            var inv = parent.WorldRotation.Inverse();
            var local = inv.Rotate(rel);
            var ps = parent.WorldScale;
            node.Position = new Vec3(local.X / Math.Max(ps.X, 1e-5f), local.Y / Math.Max(ps.Y, 1e-5f), local.Z / Math.Max(ps.Z, 1e-5f));
            node.Rotation = inv * worldRot;
            node.Scale = new Vec3(worldScl.X / Math.Max(ps.X, 1e-5f), worldScl.Y / Math.Max(ps.Y, 1e-5f), worldScl.Z / Math.Max(ps.Z, 1e-5f));
        }
        _level.IsDirty = true;
        _level.EnsureNames();
    }

    // ================= drag & drop hierarchy =================

    private Point _dragStart;
    private TreeViewItem? _dragItem;

    private void Hierarchy_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(HierarchyTree);
        _dragItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (_dragItem?.Tag is not LevelNode node || _playing) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Scene.ToggleSelection(node);
            e.Handled = true;
            _dragItem = null;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            Scene.AddToSelection(node);
            e.Handled = true;
            _dragItem = null;
        }
    }

    private void Hierarchy_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _playing) return;
        var p = e.GetPosition(HierarchyTree);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (_dragItem.Tag is not LevelNode node) return;
        var data = new DataObject("GrabEngineNode", node);
        DragDrop.DoDragDrop(HierarchyTree, data, DragDropEffects.Move);
        _dragItem = null;
    }

    private void Hierarchy_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("GrabEngineNode") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Hierarchy_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("GrabEngineNode") is not LevelNode node) return;
        var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        LevelNode? parent = target?.Tag as LevelNode;
        Reparent(node, parent);
        BuildHierarchy();
        SelectNode(node);
        Scene.Refresh();
        MarkDirty(null, "Reparent");
        Log(parent == null ? $"Moved '{node.DisplayName}' to the scene root" : $"Moved '{node.DisplayName}' under '{parent.DisplayName}'");
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null && obj is not T)
            obj = obj is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(obj) : LogicalTreeHelper.GetParent(obj);
        return obj as T;
    }

    // ================= code =================

    private void CodeEditor_ScriptChanged(string text)
    {
        if (_suppressScript) return;
        if (_selected?.Kind != NodeKind.GASM) return;
        _selected.Script = text;
        MarkDirty("code", "C# script");
        CompileAll();
    }

    private void UpdateEditorDiagnostics()
    {
        if (_selected?.Kind == NodeKind.GASM && _selected.Compiled != null)
        {
            CodeEditor.SetDiagnostics(_selected.Compiled.Diagnostics);
            CodeEditor.SetGasCost(_selected.Compiled);
        }
        else
        {
            CodeEditor.SetDiagnostics(Array.Empty<Diagnostic>());
            CodeEditor.SetGasCost(null);
        }
    }

    private void CodeEditor_BreakpointsChanged(IReadOnlyList<int> lines)
    {
        if (_selected?.Kind != NodeKind.GASM) return;
        if (lines.Count == 0) _breakpointsByNode.Remove(_selected.EditorId);
        else _breakpointsByNode[_selected.EditorId] = lines.ToHashSet();
        if (_playing && _play != null) _play.SetBreakpoints(_selected.EditorId, lines);
        SyncDebugger();
    }

    private void ClearAllBreakpoints()
    {
        _breakpointsByNode.Clear();
        CodeEditor.SetBreakpoints(Array.Empty<int>());
        if (_play != null)
        {
            foreach (var id in _level.AllNodes().Where(n => n.Kind == NodeKind.GASM).Select(n => n.EditorId))
                _play.SetBreakpoints(id, Array.Empty<int>());
        }
        SyncDebugger();
        StatusText.Text = "All breakpoints cleared";
    }

    private void DebugContinue()
    {
        if (!_playing || _play == null) return;
        _play.ContinueDebug();
        _paused = false;
        PauseButton.Content = "❚❚";
        PlayButton.IsEnabled = false;
        StatusText.Text = "Playing";
        SyncDebugger();
    }

    private void DebugStepLine()
    {
        if (!_playing || _play == null) return;
        if (_selected?.Kind == NodeKind.GASM) _play.SetDebugTarget(_selected);
        if (!_play.StepDebugLine()) { SyncDebugger(); return; }
        _paused = true;
        PauseButton.Content = "▶";
        ApplyDebugFrame();
    }

    private void DebugStepInstruction()
    {
        if (!_playing || _play == null) return;
        if (_selected?.Kind == NodeKind.GASM) _play.SetDebugTarget(_selected);
        if (!_play.StepDebugInstruction()) { SyncDebugger(); return; }
        _paused = true;
        PauseButton.Content = "▶";
        ApplyDebugFrame();
    }

    private void ApplyDebugFrame()
    {
        if (_play == null) return;
        if (_play.AmbienceDirty) Scene.SetAmbience(_level.Ambience);
        Scene.Refresh();
        var node = _play.DebugNode;
        if (node != null && _selected?.EditorId != node.EditorId)
            SelectNode(node, syncScene: false);
        CodeEditor.SetExecutionLine(_play.DebugLine);
        BottomTabs.SelectedIndex = 5;
        SyncDebugger();
    }

    private void SyncDebugger()
    {
        if (!_playing || _play == null)
        {
            Debugger.SetStopped();
            return;
        }
        var node = _play.DebugNode;
        bool paused = _paused || _play.DebugPaused;
        var location = node == null
            ? $"Play mode · {_play.ProgramCount} GASM program(s)"
            : $"{node.DisplayName}.cs   line {_play.DebugLine}   •   GASM {_play.DebugInstruction}/{_play.DebugInstructionCount}";
        var status = paused
            ? (string.IsNullOrWhiteSpace(_play.DebugReason) ? "Paused" : _play.DebugReason)
            : $"Playing · {_play.ProgramCount} program(s)";
        var breakpoints = _play.BreakpointList()
            .Select(b => $"{b.Node.DisplayName}:{b.Line}")
            .ToList();
        Debugger.SetState(status, paused, _play.DebugVariables(node), breakpoints, location);
        if (paused && node != null && _selected?.EditorId == node.EditorId)
            CodeEditor.SetExecutionLine(_play.DebugLine);
    }

    private (int Errors, int Warnings) CompileAll()
    {
        _compiling = true;
        try
        {
        var objectIdsBefore = _level.AllNodes().Select(n => n.EditorId).ToHashSet(StringComparer.Ordinal);
        _problems.Clear();
        int errors = 0, warnings = 0;
        foreach (var node in _level.AllNodes().Where(n => n.Kind == NodeKind.GASM && !n.Generated).ToList())
        {
            if (string.IsNullOrWhiteSpace(node.Script))
            {
                node.Compiled = null;
                continue;
            }
            var result = new CSharpCompiler().Compile(_level, node, node.Script);
            foreach (var d in result.Diagnostics)
            {
                d.File = node.DisplayName + ".cs";
                if (d.Severity == DiagnosticSeverity.Error) errors++;
                else if (d.Severity == DiagnosticSeverity.Warning) warnings++;
                _problems.Add(new ProblemItem(d, node));
            }
            node.Compiled = result;
        }

        // Hot reload: when Play mode is already running, swap the compiled programs
        // under the existing session. Player position, physics and the level stay put;
        // only Code Block state restarts.
        if (_playing && _play != null && errors == 0)
        {
            _play.ReloadPrograms();
            PauseButton.Content = "❚❚";
            _paused = false;
            StatusText.Text = $"Hot reload: scripts applied · {_play.LastReloadedRegisters} live register(s) kept";
        }

        foreach (var (limitNode, severity, message) in GrabLimits.ValidateLevel(_level))
        {
            var d = new Diagnostic
            {
                Severity = severity,
                Message = message,
                Code = "GRAB limit",
                File = "Level",
                Line = 0
            };
            if (severity == DiagnosticSeverity.Error) errors++;
            else if (severity == DiagnosticSeverity.Warning) warnings++;
            _problems.Add(new ProblemItem(d, limitNode));
        }

        var objectIdsAfter = _level.AllNodes().Select(n => n.EditorId).ToHashSet(StringComparer.Ordinal);
        if (!objectIdsBefore.SetEquals(objectIdsAfter))
        {
            BuildHierarchy();
            Scene.Refresh();
            MarkDirty();
        }
        RefreshProblems(errors, warnings);
        ShowGasmFor(_selected);
        UpdateEditorDiagnostics();
        CodeEditor.SetStatus(errors == 0 ? "Compiled to GASM" : $"{errors} error(s)", errors == 0);
        if (errors == 0 && warnings == 0 && _level.AllNodes().Any(n => n.Kind == NodeKind.GASM && !string.IsNullOrWhiteSpace(n.Script)))
            StatusText.Text = "Compiled all Code Blocks to GASM";
        // Compilation can add continuation pages and object-creation blocks. Keep the
        // history's clean state in sync without turning generated helpers into an undo step.
        if (!_suppressHistory && _history.Current != null)
        {
            _history.SyncCurrent(ProjectIO.Serialize(_level), _selected?.EditorId);
            UpdateHistoryUi();
        }
        return (errors, warnings);
        }
        finally
        {
            _compiling = false;
        }
    }

    private void RefreshProblems(int errors, int warnings)
    {
        ProblemsList.ItemsSource = null;
        ProblemsList.ItemsSource = _problems;
        ProblemsTab.Header = errors + warnings > 0 ? $"Problems ({errors}✕ {warnings}⚠)" : "Problems";
    }

    private void ShowGasmFor(LevelNode? node)
    {
        if (node?.Kind != NodeKind.GASM)
        {
            GasmView.Text = "Select a Code Block to inspect the compiled GASM.";
            return;
        }
        if (node.Compiled != null)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"; {node.DisplayName}.cs  →  GASM");
            text.AppendLine($"; working registers: {node.Compiled.Program.WorkingRegisters.Count}   instructions: {node.Compiled.Program.Instructions.Count}");
            text.AppendLine();
            foreach (var line in node.Compiled.GasmText) text.AppendLine(line);
            GasmView.Text = text.ToString();
        }
        else if (node.ImportedProgram != null)
        {
            GasmView.Text = GasmDecompiler.ToCommentText(node);
        }
        else
        {
            GasmView.Text = "This Code Block has no compiled program yet. Press Compile All (F7).";
        }
    }

    private void Problems_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsList.SelectedItem is not ProblemItem p || p.Node == null) return;
        SelectNode(p.Node);
        BottomTabs.SelectedIndex = 0;
        CodeEditor.ScrollToLine(p.Line);
    }

    // ================= play mode =================

    private void StartPlay()
    {
        if (_playing) { StopPlay(); return; }
        _animationPanel?.Deactivate();
        var (errors, _) = CompileAll();
        if (errors > 0)
        {
            BottomTabs.SelectedIndex = 3;
            StatusText.Text = $"Play stopped: {errors} compile error(s)";
            MessageBox.Show(this, "Fix the compile errors before playing.\nSee the Problems tab.", "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _playBackup = ProjectIO.Serialize(_level);
        _playSelectedId = _selected?.EditorId;
        _playSelectedIds = _selection.Select(n => n.EditorId).ToList();
        _play = new PlaySession(_level);
        foreach (var pair in _breakpointsByNode) _play.SetBreakpoints(pair.Key, pair.Value);
        _playAudio = new GameAudio(_level);
        _play.SoundTarget += (sound, mode) => _playAudio?.Trigger(sound, mode);
        _playAudio.Start(_play.PlayerPosition);
        _playing = true;
        _paused = false;
        _keys.Clear();
        BrushPalette.Visibility = Visibility.Collapsed;
        Scene.PlayMode = true;
        _selection.Clear();
        _selected = null;
        Scene.SetSelection(null);
        Scene.BeginPlay(_play.PlayerPosition, _play.PlayerYaw);
        Scene.Refresh();
        Log($"Player avatar: {Scene.PlayerModelPartCount} mesh part(s)");
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "grab-play-debug.txt"),
                $"player={_play.PlayerPosition} yaw={_play.PlayerYaw:0.###}\ncamera={Scene.CameraPositionInfo}\nparts={Scene.PlayerModelPartCount}");
        }
        catch { }
        SyncDebugger();
        _playClock.Restart();
        _lastTick = 0;
        _consoleAutoOpened = false;
        _playTimer.Start();
        PlayButton.IsEnabled = false;
        UpdateHistoryUi();
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        LiveBadge.Visibility = Visibility.Visible;
        PauseButton.Content = "❚❚";
        BottomTabs.SelectedIndex = 0;
        Scene.Focus();
        StatusText.Text = $"Playing — WASD move, Space jump, Shift sprint, F10 step, Esc stop";
        Log($"Play mode started with {_play.ProgramCount} GASM program(s)");
    }

    /// <summary>Automation entry point used by --shot-project to open a saved demo and frame it.</summary>
    public void OpenProjectForScreenshot(string path)
    {
        OpenFile(path);
        var block = _level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM && !n.Generated);
        if (block != null)
        {
            SelectNode(block);
            BottomTabs.SelectedIndex = 0;
        }
        Scene.FrameAll();
    }

    public void StartPlayForScreenshot()
    {
        // Automation entry point used by --shot-play. It exercises the same play path
        // as the toolbar button so screenshots catch missing player/collision assets.
        StartPlay();
    }

    private void PausePlay()
    {
        if (!_playing) return;
        _paused = !_paused;
        PauseButton.Content = _paused ? "▶" : "❚❚";
        if (_play != null)
        {
            if (_paused) _play.PauseDebug();
            else _play.ContinueDebug();
        }
        StatusText.Text = _paused ? "Paused — F10 step line, F11 step GASM, F5 continue" : "Playing";
        SyncDebugger();
    }

    private void StopPlay()
    {
        if (!_playing) return;
        _playTimer.Stop();
        _playClock.Stop();
        _playing = false;
        _paused = false;
        LiveBadge.Visibility = Visibility.Collapsed;
        _playAudio?.Dispose();
        _playAudio = null;
        _play = null;
        Debugger.SetStopped();
        CodeEditor.ClearExecutionLine();
        Scene.PlayMode = false;
        Scene.EndPlay();
        if (_playBackup != null)
        {
            _level = ProjectIO.Deserialize(_playBackup);
            _playBackup = null;
        }
        CompileAll();
        _animationPanel?.SetLevel(_level);
        Scene.SetLevel(_level, frame: false);
        Scene.SetAmbience(_level.Ambience);
        BuildHierarchy();
        var restore = _playSelectedIds.Select(id => _level.AllNodes().FirstOrDefault(n => n.EditorId == id))
            .Where(n => n != null).Cast<LevelNode>().ToList();
        if (restore.Count == 0 && _playSelectedId != null)
        {
            var single = _level.AllNodes().FirstOrDefault(n => n.EditorId == _playSelectedId);
            if (single != null) restore.Add(single);
        }
        _playSelectedId = null;
        _playSelectedIds.Clear();
        if (restore.Count > 1) SelectNodes(restore);
        else SelectNode(restore.FirstOrDefault());
        _history.SyncCurrent(ProjectIO.Serialize(_level), _selected?.EditorId);
        if (ToolBrush.IsChecked == true) BrushPalette.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        UpdateHistoryUi();
        StatusText.Text = "Stopped — level restored";
        Log("Play mode stopped; scene restored");
    }

    private void PlayTick(object? sender, EventArgs e)
    {
        if (!_playing || _play == null) return;
        double now = _playClock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastTick, 0, 0.1);
        _lastTick = now;
        if (_paused || dt <= 0) return;

        var move = Vec3.Zero;
        if (!IsTextInputFocused())
        {
            float forward = 0, strafe = 0;
            if (_keys.Contains(Key.W)) forward += 1;
            if (_keys.Contains(Key.S)) forward -= 1;
            if (_keys.Contains(Key.D)) strafe += 1;
            if (_keys.Contains(Key.A)) strafe -= 1;
            if (forward != 0 || strafe != 0)
            {
                var wish = Scene.PlayForward * forward + Scene.PlayRight * strafe;
                wish = wish.Normalized;
                float speed = _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift) ? 8.6f : 5.5f;
                move = wish * speed;
            }
        }
        _play.DesiredVelocity = move;

        _play.Step(dt);
        _playAudio?.UpdateListener(_play.PlayerPosition);
        if (_play.AmbienceDirty) Scene.SetAmbience(_level.Ambience);
        Scene.Refresh();
        Scene.UpdatePlayView(_play.PlayerPosition, _play.PlayerYaw, _play.PlayerBob, _play.Grounded, dt);
        if (_play.DebugPaused)
        {
            _paused = true;
            PauseButton.Content = "▶";
            StatusText.Text = $"Paused at {_play.DebugNode?.DisplayName}:{_play.DebugLine} — F10 step, F5 continue";
            ApplyDebugFrame();
        }
        else if (BottomTabs.SelectedIndex == 5) SyncDebugger();
        bool logged = _play.Messages.Count > 0;
        foreach (var msg in _play.Messages) Log(msg);
        _play.Messages.Clear();
        // Open Console on the first line of a play session so output is not hidden.
        if (logged && !_consoleAutoOpened)
        {
            _consoleAutoOpened = true;
            BottomTabs.SelectedIndex = 4;
        }
    }

    private bool IsTextInputFocused()
    {
        var f = Keyboard.FocusedElement;
        if (f is TextBoxBase) return true;
        // Editable ComboBoxes host their own TextBox, and the code editor is an
        // AvalonEdit TextEditor (not a WPF TextBoxBase).
        if (f is System.Windows.Controls.ComboBox cb && cb.IsEditable) return true;
        var tn = f?.GetType().FullName;
        return tn != null && tn.StartsWith("ICSharpCode.AvalonEdit", StringComparison.Ordinal);
    }

    // ================= menus / toolbar =================

    private void NewLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        if (!ConfirmDiscard()) return;
        LoadLevel(GrabLevel.CreateDefault(), null);
        Log("New level created");
    }

    private bool ConfirmDiscard()
    {
        if (!_level.IsDirty) return true;
        var r = MessageBox.Show(this, "Save changes to the current project?", "GRAB ENGINE",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject();
        return true;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Filter = "GRAB projects and levels (*.grabproj;*.level)|*.grabproj;*.level|GRAB ENGINE Project (*.grabproj)|*.grabproj|GRAB Level (*.level)|*.level|All files (*.*)|*.*",
            Title = "Open project or GRAB level"
        };
        if (dlg.ShowDialog(this) != true) return;
        OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            if (path.EndsWith(".level", StringComparison.OrdinalIgnoreCase))
            {
                var imported = LevelIO.Load(path);
                foreach (var n in imported.AllNodes())
                    if (n.Kind == NodeKind.GASM && string.IsNullOrWhiteSpace(n.Script) && n.ImportedProgram != null)
                        n.Script = CodeTemplates.ImportedStub(imported, n);
                LoadLevel(imported, null);
                Log($"Imported GRAB level '{Path.GetFileName(path)}' ({_level.AllNodes().Count()} objects)");
                StatusText.Text = "Imported .level — save as a project to keep your C# scripts";
            }
            else
            {
                var project = ProjectIO.Load(path);
                LoadLevel(project, path);
                CompileAll();
                Log($"Opened project '{Path.GetFileName(path)}'");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the file:\n\n" + ex.Message, "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
            Log("Open failed: " + ex.Message);
        }
    }

    private bool SaveProject()
    {
        if (string.IsNullOrEmpty(_projectPath)) return SaveProjectAs();
        try
        {
            ProjectIO.Save(_level, _projectPath);
            try
            {
                WorkspaceStore.ClearAutosave(_projectPath, _level.Title);
                WorkspaceStore.CreateSnapshot(ProjectIO.Serialize(_level), _projectPath, _level.Title, "Saved");
                _lastSnapshotUtc = DateTime.UtcNow;
            }
            catch { }
            UpdateTitle();
            StatusText.Text = "Project saved";
            Log("Saved " + _projectPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Save failed:\n\n" + ex.Message, "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool SaveProjectAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = ProjectIO.Filter,
            DefaultExt = ProjectIO.Extension,
            FileName = Slug(_level.Title) + ProjectIO.Extension,
            Title = "Save GRAB ENGINE project"
        };
        if (dlg.ShowDialog(this) != true) return false;
        _projectPath = dlg.FileName;
        return SaveProject();
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject();
    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    // ================= autosave / version history =================

    private void AutoSave(bool takeSnapshot = false)
    {
        if (_playing || !_level.IsDirty) return;
        try
        {
            string json = ProjectIO.Serialize(_level);
            WorkspaceStore.WriteAutosave(json, _projectPath, _level.Title);
            if (takeSnapshot || DateTime.UtcNow - _lastSnapshotUtc > TimeSpan.FromMinutes(10))
            {
                WorkspaceStore.CreateSnapshot(json, _projectPath, _level.Title, takeSnapshot ? "Manual" : "Autosave");
                _lastSnapshotUtc = DateTime.UtcNow;
            }
            StatusText.Text = $"Autosaved {DateTime.Now:HH:mm:ss}";
            ToolbarFile.Text = (_projectPath != null ? Path.GetFileName(_projectPath) : "Untitled") + " • autosaved";
        }
        catch (Exception ex)
        {
            Log("Autosave failed: " + ex.Message);
        }
    }

    private void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WorkspaceStore.CreateSnapshot(ProjectIO.Serialize(_level), _projectPath, _level.Title, "Manual");
            _lastSnapshotUtc = DateTime.UtcNow;
            StatusText.Text = "Snapshot created";
            Log("Version history snapshot created");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create a snapshot:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void VersionHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        try
        {
            var window = new VersionHistoryWindow(this, _projectPath, _level.Title);
            window.RestoreRequested += RestoreSnapshot;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // A broken history folder should never take the whole editor down.
            Log("Version history failed: " + ex.Message);
            MessageBox.Show(this, "Could not open Version History:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreSnapshot(string path)
    {
        if (_playing) StopPlay();
        try
        {
            string json = WorkspaceStore.ReadSnapshot(path);
            // Keep the current state in history, so restoring can itself be undone by
            // picking the newer snapshot.
            if (_level.IsDirty)
                WorkspaceStore.CreateSnapshot(ProjectIO.Serialize(_level), _projectPath, _level.Title, "Before restore");
            var level = ProjectIO.Deserialize(json);
            LoadLevel(level, _projectPath);
            _level.IsDirty = true;
            UpdateTitle();
            Log("Restored version snapshot " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not restore the snapshot:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CheckAutosaveRecovery()
    {
        if (_startupRecoveryChecked) return;
        _startupRecoveryChecked = true;
        var autosave = WorkspaceStore.LatestAutosave();
        if (autosave == null || string.IsNullOrWhiteSpace(autosave.Json)) return;
        bool projectExists = !string.IsNullOrEmpty(autosave.ProjectPath) && File.Exists(autosave.ProjectPath);
        if (projectExists && autosave.SavedUtc <= File.GetLastWriteTimeUtc(autosave.ProjectPath!))
        {
            WorkspaceStore.ClearAutosave(autosave.ProjectPath, autosave.Title);
            return;
        }
        var answer = MessageBox.Show(this,
            "GRAB ENGINE found unsaved work from your previous session.\n\n" +
            $"Autosaved: {autosave.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nTitle: {autosave.Title}\n\n" +
            "Recover it?", "Recover Autosave", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            WorkspaceStore.ClearAutosave(autosave.ProjectPath, autosave.Title);
            return;
        }
        try
        {
            var level = ProjectIO.Deserialize(autosave.Json);
            LoadLevel(level, autosave.ProjectPath);
            _level.IsDirty = true;
            UpdateTitle();
            StatusText.Text = "Recovered autosave from " + autosave.SavedUtc.ToLocalTime().ToString("HH:mm");
            Log("Recovered autosaved work from the previous session");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The autosave could not be recovered:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ValidateForExport()
    {
        if (!_level.AllNodes().Any(n => n.Kind == NodeKind.Start))
        {
            MessageBox.Show(this, "GRAB levels need at least one Start point.\nAdd one from Component → Start Point, then export again.", "Export blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_level.Title)) _level.Title = "My Level";
        if (string.IsNullOrWhiteSpace(_level.Creators)) _level.Creators = "Anonymous";
        return true;
    }

    private void ExportLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var (errors, _) = CompileAll();
        if (errors > 0)
        {
            BottomTabs.SelectedIndex = 3;
            MessageBox.Show(this, "The C# scripts have compile errors, so nothing was exported.\nFix the Problems tab first.", "Export blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ValidateForExport()) return;

        string folder = !string.IsNullOrWhiteSpace(_lastExportPath)
            ? Path.GetDirectoryName(_lastExportPath) ?? DefaultExportFolder()
            : (_projectPath != null ? Path.GetDirectoryName(_projectPath) ?? DefaultExportFolder() : DefaultExportFolder());
        string defaultPath = Path.Combine(folder, Slug(_level.Title) + ".level");
        var dlg = new ExportDialog(this, _level, defaultPath);
        if (dlg.ShowDialog() != true) return;
        try
        {
            string report = PerformExport(dlg.Settings);
            StatusText.Text = $"Exported {Path.GetFileName(dlg.Settings.Path)} ({dlg.Settings.Profile})";
            Log("Exported " + dlg.Settings.Path + "  [" + dlg.Settings.Profile + "]");
            MessageBox.Show(this,
                "Exported a real GRAB level:\n\n" + dlg.Settings.Path + "\n\n" +
                (string.IsNullOrWhiteSpace(report) ? "" : report + "\n\n") +
                "Drop it into GRAB's levels folder, or use Install to GRAB for Steam/Quest.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed:\n\n" + ex.Message, "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
            Log("Export failed: " + ex.Message);
        }
    }

    private void LevelLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var library = new LevelLibraryWindow(this, OpenLevelFromLibrary, ImportLevelIntoCurrent);
        library.ShowDialog();
    }

    private void OpenLevelFromLibrary(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!ConfirmDiscard()) return;
        OpenFile(path);
    }

    private void ImportLevelIntoCurrent(string path)
    {
        if (_playing) StopPlay();
        try
        {
            var imported = LevelIO.Load(path);
            var group = new LevelNode
            {
                Kind = NodeKind.Group,
                Name = "Imported " + imported.Title,
                Position = NextPosition(),
                Scale = Vec3.One,
                LayerId = _level.ActiveLayerId
            };
            _level.AddNode(group);
            var nodes = PrefabLibrary.Instantiate(path, _level, group.Position, Quat.Identity, group);
            _level.EnsureNames();
            BuildHierarchy();
            SelectNode(group);
            Scene.Refresh();
            MarkDirty(null, "Add level to project");
            StatusText.Text = $"Added '{imported.Title}' ({nodes.Count} root object(s)) to the project";
            Log($"Added level '{path}' to the current project");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not add the level:\n\n" + ex.Message,
                "Add level", MessageBoxButton.OK, MessageBoxImage.Error);
            Log("Add level failed: " + ex.Message);
        }
    }

    private void ImportGlb_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var dlg = new GlbImportDialog(this, string.IsNullOrWhiteSpace(_lastGlbPath) ? null : _lastGlbPath);
        if (dlg.ShowDialog() != true) return;
        _lastGlbPath = dlg.FilePath;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = GlbImporter.ImportFile(dlg.FilePath, dlg.Settings);
            var group = result.Group;
            group.Name = "GLB " + Path.GetFileNameWithoutExtension(dlg.FilePath);
            _level.AddNode(group);
            BuildHierarchy();
            SelectNode(group);
            Scene.Refresh();
            MarkDirty(null, "Import .glb model");
            StatusText.Text = result.Summary;
            Log($"Imported '{Path.GetFileName(dlg.FilePath)}': {result.Summary}");
            if (result.Nodes.Count == 0)
                MessageBox.Show(this, "The file loaded, but it produced no block geometry.\n\n" +
                    "Check that the model uses triangle primitives and that Detail is not too low.",
                    "Import model", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (result.Truncated)
                MessageBox.Show(this, "The model reached the editor's safe import cap.\n\n" +
                    "Only the first " + result.Nodes.Count + " face blocks were added. Import a smaller model or use Voxels mode.",
                    "Import capped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not import the model:\n\n" + ex.Message,
                "Import model", MessageBoxButton.OK, MessageBoxImage.Error);
            Log("GLB import failed: " + ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private string DefaultExportFolder()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRAB", "levels", "user");
        try { Directory.CreateDirectory(folder); } catch { }
        return folder;
    }

    /// <summary>
    /// Writes the level with the selected profile. Debug writes compiler output as-is;
    /// Release runs only value-preserving GASM passes and normalises animation keys.
    /// </summary>
    private string PerformExport(ExportSettings settings)
    {
        var notes = new List<string>();
        int optimizedInstructions = 0;
        if (settings.Profile == ExportProfile.Release && settings.OptimizeScripts)
        {
            var seenPrograms = new HashSet<GasmProgram>();
            int removed = 0, before = 0, after = 0;
            void Optimize(GasmCompileResult? result)
            {
                if (result == null) return;
                if (seenPrograms.Add(result.Program))
                {
                    before += result.Program.Instructions.Count;
                    var stats = GasmOptimizer.Optimize(result.Program);
                    removed += stats.TotalRemoved;
                    after += result.Program.Instructions.Count;
                    result.GasmText = GasmOptimizer.ToText(result.Program).ToList();
                }
                foreach (var continuation in result.Continuations) Optimize(continuation);
            }
            foreach (var node in _level.AllNodes().Where(n => n.Compiled != null)) Optimize(node.Compiled);
            optimizedInstructions = removed;
            if (removed > 0) notes.Add($"Release optimiser removed {removed} GASM instruction(s) ({before} → {after}).");
            else notes.Add("Release optimiser: GASM was already compact.");
        }

        int normalizedClips = 0;
        if (settings.Profile == ExportProfile.Release && settings.OptimizeAnimations)
        {
            foreach (var clip in _level.AllNodes().SelectMany(n => n.Animations))
                if (AnimationSampler.SortAndDeduplicate(clip)) normalizedClips++;
            if (normalizedClips > 0) notes.Add($"Normalised key order in {normalizedClips} animation clip(s) without changing their timing.");
        }

        // Imported programs are only used when a block has no compiled program. Removing
        // them from the write (not from the project) shrinks Release levels.
        var hiddenImports = new List<(LevelNode Node, GasmProgram Program, List<GasmConnection> Connections)>();
        try
        {
            if (settings.Profile == ExportProfile.Release && settings.TrimUnusedPrograms)
            {
                foreach (var node in _level.AllNodes())
                {
                    if (node.Compiled == null || node.ImportedProgram == null) continue;
                    hiddenImports.Add((node, node.ImportedProgram, node.ImportedConnections));
                    node.ImportedProgram = null;
                    node.ImportedConnections = new List<GasmConnection>();
                }
                if (hiddenImports.Count > 0) notes.Add($"Omitted {hiddenImports.Count} unused imported program(s).");
            }
            WriteLevel(settings.Path);
        }
        finally
        {
            foreach (var (node, program, connections) in hiddenImports)
            {
                node.ImportedProgram = program;
                node.ImportedConnections = connections;
            }
        }

        if (settings.VerifyOutput) notes.Add(VerifyExportedLevel(settings.Path));
        if (settings.WriteGasmListing) WriteGasmListing(settings.Path);
        if (settings.OpenFolder)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + settings.Path + "\"") { UseShellExecute = true }); } catch { }
        }
        _lastExportPath = settings.Path;
        return string.Join("\n", notes);
    }

    private string VerifyExportedLevel(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8) throw new InvalidDataException("The exported file is empty.");
        var check = LevelIO.Read(bytes);
        int expectedNodes = _level.AllNodes().Count();
        int actualNodes = check.AllNodes().Count();
        if (actualNodes != expectedNodes)
            throw new InvalidDataException($"Verification failed: expected {expectedNodes} objects but the file contains {actualNodes}.");
        var counted = new HashSet<GasmProgram>();
        int expectedInstructions = 0;
        void Count(GasmCompileResult? result)
        {
            if (result == null || !counted.Add(result.Program)) return;
            expectedInstructions += result.Program.Instructions.Count;
            foreach (var continuation in result.Continuations) Count(continuation);
        }
        foreach (var node in _level.AllNodes()) Count(node.Compiled);
        int actualInstructions = check.AllNodes().Where(n => n.ImportedProgram != null)
            .Sum(n => n.ImportedProgram!.Instructions.Count);
        if (expectedInstructions > 0 && actualInstructions < expectedInstructions)
            throw new InvalidDataException($"Verification failed: {actualInstructions} of {expectedInstructions} GASM instructions were written.");
        if (!string.Equals(check.Title, _level.Title, StringComparison.Ordinal))
            throw new InvalidDataException("Verification failed: the level title did not round-trip.");
        return $"Verified {actualNodes} objects and {actualInstructions} GASM instructions.";
    }

    private void WriteGasmListing(string levelPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("; GRAB ENGINE listing for " + Path.GetFileName(levelPath));
        sb.AppendLine("; Generated " + DateTime.Now.ToString("u") + " — profile: " + (_lastExportPath == levelPath ? "see export dialog" : ""));
        foreach (var node in _level.AllNodes().Where(n => n.Compiled != null))
        {
            sb.AppendLine();
            sb.AppendLine("=== " + node.DisplayName + " (" + node.Kind + ") ===");
            foreach (var line in GasmOptimizer.ToText(node.Compiled!.Program)) sb.AppendLine(line);
            int page = 1;
            foreach (var continuation in node.Compiled.Continuations)
            {
                sb.AppendLine();
                sb.AppendLine("--- continuation page " + (++page) + " ---");
                foreach (var line in GasmOptimizer.ToText(continuation.Program)) sb.AppendLine(line);
            }
        }
        string path = Path.ChangeExtension(levelPath, ".gasm.txt");
        File.WriteAllText(path, sb.ToString());
        Log("Wrote GASM listing " + path);
    }

    private void WriteLevel(string path)
    {
        _level.RebuildParents();
        _level.EnsureNames();
        File.WriteAllBytes(path, LevelIO.Write(_level));
    }

    private void InstallLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        var (errors, _) = CompileAll();
        if (errors > 0)
        {
            BottomTabs.SelectedIndex = 3;
            MessageBox.Show(this, "Fix the compile errors before installing.", "Install blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ValidateForExport()) return;

        var dlg = new InstallDialog(this, _level.Title);
        if (dlg.ShowDialog() != true) return;
        var target = dlg.Settings;
        string fileName = Slug(_level.Title) + ".level";
        try
        {
            if (target.Target == InstallTarget.Steam)
            {
                Directory.CreateDirectory(target.SteamFolder);
                string file = Path.Combine(target.SteamFolder, fileName);
                var settings = new ExportSettings
                {
                    Path = file,
                    Profile = target.Profile,
                    OptimizeScripts = true,
                    OptimizeAnimations = true,
                    TrimUnusedPrograms = true,
                    VerifyOutput = true
                };
                PerformExport(settings);
                Log("Installed level to " + file);
                MessageBox.Show(this, "Installed into GRAB (Steam/PC):\n\n" + file +
                    "\n\nStart GRAB, open the level browser and it will appear in your levels.",
                    "Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Installed to GRAB (Steam)";
            }
            else
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "grabengine-install");
                Directory.CreateDirectory(tempDir);
                string tempFile = Path.Combine(tempDir, fileName);
                var settings = new ExportSettings
                {
                    Path = tempFile,
                    Profile = target.Profile,
                    OptimizeScripts = true,
                    OptimizeAnimations = true,
                    TrimUnusedPrograms = true,
                    VerifyOutput = true
                };
                PerformExport(settings);
                string remoteFolder = target.RemoteFolder.TrimEnd('/');
                RunAdb(target, $"shell mkdir -p \"{remoteFolder}\"", 20000);
                string remote = remoteFolder + "/" + fileName;
                string output = RunAdb(target, $"push \"{tempFile}\" \"{remote}\"", 120000);
                if (output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("adb push failed:\n\n" + output);
                try { File.Delete(tempFile); } catch { }
                if (target.LaunchGame)
                {
                    try { RunAdb(target, "shell monkey -p com.slindev.grab_demo -c android.intent.category.LAUNCHER 1", 20000); }
                    catch { }
                }
                Log("Installed level to Quest: " + remote);
                MessageBox.Show(this, "Installed into GRAB on the Meta Quest:\n\n" + remote +
                    "\n\nOpen GRAB on the headset and the level will appear in the level browser.",
                    "Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Installed to GRAB (Meta Quest)";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Install failed:\n\n" + ex.Message, "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
            Log("Install failed: " + ex.Message);
        }
    }

    private static string RunAdb(InstallSettings settings, string arguments, int timeoutMs)
    {
        string adb = string.IsNullOrWhiteSpace(settings.AdbPath) ? InstallDialog.FindAdb() : settings.AdbPath;
        if (!string.IsNullOrWhiteSpace(settings.DeviceSerial))
            arguments = "-s " + settings.DeviceSerial + " " + arguments;
        var psi = new ProcessStartInfo(adb, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb. Locate adb.exe in the install dialog.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("adb did not respond. Check the headset connection and USB debugging authorization.");
        }
        string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("adb exited with code " + process.ExitCode + ":\n\n" + text.Trim());
        return text;
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? "my-level" : s;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_playing) StopPlay();
        if (!_level.IsDirty) return;
        AutoSave();
        var r = MessageBox.Show(this, "Save changes to the current project before closing?", "GRAB ENGINE",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes && !SaveProject()) e.Cancel = true;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Scene.FrameAll();

    private void FrameAll_Click(object sender, RoutedEventArgs e) => Scene.FrameAll();

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var g = new LevelNode { Kind = NodeKind.Group, Name = "Group", Position = NextPosition(), Scale = Vec3.One };
        _level.AddNode(g, ParentForNew());
        BuildHierarchy();
        SelectNode(g);
        Scene.Refresh();
        MarkDirty(null, "Create group");
    }

    private void AddShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem m && int.TryParse(m.Tag?.ToString(), out int shape)) AddShape(shape);
    }

    // ================= prefabs =================

    private void RefreshPrefabMenu()
    {
        SpawnPrefabMenu.Items.Clear();
        StampPrefabMenu.Items.Clear();
        var prefabs = PrefabLibrary.List();
        if (prefabs.Count == 0)
        {
            SpawnPrefabMenu.Items.Add(new MenuItem { Header = "No prefabs yet", IsEnabled = false });
            StampPrefabMenu.Items.Add(new MenuItem { Header = "No prefabs yet", IsEnabled = false });
            return;
        }
        foreach (var prefab in prefabs)
        {
            string variant = prefab.IsVariant ? "  (variant)" : "";
            var spawn = new MenuItem
            {
                Header = $"{prefab.Name}   ({prefab.NodeCount} objects){variant}",
                Tag = prefab.FilePath,
                ToolTip = prefab.FilePath
            };
            spawn.Click += (_, _) => SpawnPrefab(prefab);
            SpawnPrefabMenu.Items.Add(spawn);

            var stamp = new MenuItem
            {
                Header = $"{prefab.Name}   ({prefab.NodeCount} objects){variant}",
                Tag = prefab.FilePath,
                ToolTip = "Stamp copies of " + prefab.Name + " with the mouse"
            };
            stamp.Click += (_, _) => StartPrefabStamp(prefab);
            StampPrefabMenu.Items.Add(stamp);
        }
    }

    private void SavePrefab_Click(object sender, RoutedEventArgs e)
    {
        if (_playing || _selection.Count == 0)
        {
            MessageBox.Show(this, "Select one or more objects first, then save them as a prefab.",
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string suggested = _selection.Count == 1 ? _selected?.DisplayName ?? "Prefab" : "Prefab";
        string? name = Prompt.Show(this, "Save Prefab", "Prefab name", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = PrefabLibrary.Save(Scene.TopLevelSelection(), name);
            RefreshPrefabMenu();
            StatusText.Text = "Prefab saved: " + Path.GetFileName(path);
            Log($"Saved prefab '{name}' ({Scene.TopLevelSelection().Count} root object(s))");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save prefab:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartPrefabStamp(PrefabInfo prefab)
    {
        if (_playing) return;
        try
        {
            _stampPrefab = prefab;
            SelectEditorTool(EditorTool.Prefab);
            Scene.SetPrefabTemplate(PrefabLibrary.LoadTemplate(prefab.FilePath), prefab.Name);
            StatusText.Text = $"Stamp {prefab.Name}: LMB place  •  [ / ] rotate  •  Esc stop";
            Log($"Prefab stamp ready: '{prefab.Name}'. Click in the scene to place copies.");
        }
        catch (Exception ex)
        {
            _stampPrefab = null;
            Scene.ClearPrefab();
            SelectEditorTool(EditorTool.Move);
            MessageBox.Show(this, "Could not open the prefab:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopPrefabStamp_Click(object sender, RoutedEventArgs e)
    {
        _stampPrefab = null;
        Scene.ClearPrefab();
        SelectEditorTool(EditorTool.Move);
        StatusText.Text = "Prefab stamp stopped";
    }

    private void Scene_PrefabStampRequested(Vec3 position, Quat rotation)
    {
        if (_playing || _stampPrefab == null) return;
        try
        {
            var nodes = PrefabLibrary.Instantiate(_stampPrefab.FilePath, _level, position, rotation);
            BuildHierarchy();
            SelectNodes(nodes);
            Scene.Refresh();
            MarkDirty(null, "Stamp prefab");
            StatusText.Text = $"Stamped {_stampPrefab.Name} ({nodes.Count} object{(nodes.Count == 1 ? "" : "s")})";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not stamp the prefab:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SpawnPrefab(PrefabInfo prefab)
    {
        if (_playing) return;
        try
        {
            var at = NextPosition();
            var nodes = PrefabLibrary.Instantiate(prefab.FilePath, _level, at);
            BuildHierarchy();
            SelectNodes(nodes);
            Scene.Refresh();
            MarkDirty(null, "Spawn prefab");
            Log($"Spawned prefab '{prefab.Name}' at {at}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not spawn prefab:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshPrefabs_Click(object sender, RoutedEventArgs e) => RefreshPrefabMenu();

    private void OpenPrefabsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PrefabLibrary.EnsureRoot();
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PrefabLibrary.Root + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the prefab folder:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem m && m.Tag is string kind) AddComponent(kind);
    }

    private void AddObjectMenu_Click(object sender, RoutedEventArgs e) => OpenAddObject();

    private void OpenAddObject()
    {
        if (_playing) return;
        var dlg = new AddObjectWindow(this);
        if (dlg.ShowDialog() != true || dlg.Request == null) return;
        var request = dlg.Request;
        if (request.IsGroup) AddGroup_Click(this, new RoutedEventArgs());
        else if (request.ShapeId is int shape) AddShape(shape);
        else if (request.ComponentTag is string tag) AddComponent(tag);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var cm = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Bottom };
        var shapes = new MenuItem { Header = "3D Object" };
        foreach (var (id, name) in ShapeInfo.All)
        {
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) => AddShape(id);
            shapes.Items.Add(mi);
        }
        cm.Items.Add(shapes);
        var components = new MenuItem { Header = "Component" };
        foreach (var kind in new[] { "GASM", "Trigger", "Light", "Sign", "Sound", "ParticleEmitter", "Start", "Finish", "Gravity", "Crumbling", "LobbyTerminal" })
        {
            var mi = new MenuItem { Header = NodeKindInfo.Display(KindFromTag(kind)) };
            mi.Click += (_, _) => AddComponent(kind);
            components.Items.Add(mi);
        }
        cm.Items.Add(components);
        cm.Items.Add(new Separator());
        var group = new MenuItem { Header = "Create Empty Group" };
        group.Click += (_, _) => AddGroup_Click(this, new RoutedEventArgs());
        cm.Items.Add(group);
        cm.IsOpen = true;
    }

    private static NodeKind KindFromTag(string tag) => tag switch
    {
        "GASM" => NodeKind.GASM,
        "Trigger" => NodeKind.Trigger,
        "Light" => NodeKind.Light,
        "Sign" => NodeKind.Sign,
        "Sound" => NodeKind.Sound,
        "ParticleEmitter" => NodeKind.ParticleEmitter,
        "Start" => NodeKind.Start,
        "Finish" => NodeKind.Finish,
        "Gravity" => NodeKind.Gravity,
        "Crumbling" => NodeKind.Crumbling,
        "LobbyTerminal" => NodeKind.LobbyTerminal,
        _ => NodeKind.Static
    };

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        var tool = tb.Tag?.ToString() switch
        {
            "Select" => EditorTool.Select,
            "Rotate" => EditorTool.Rotate,
            "Scale" => EditorTool.Scale,
            "Brush" => EditorTool.Brush,
            "Terrain" => EditorTool.Terrain,
            "Build" => EditorTool.Build,
            "Measure" => EditorTool.Measure,
            "Prefab" => EditorTool.Prefab,
            _ => EditorTool.Move
        };
        SelectEditorTool(tool);
        if (tool == EditorTool.Prefab && _stampPrefab == null) ShowPrefabPicker(tb);
    }

    // ================= draw structures =================

    private bool _syncingBuildUi;

    private void InitializeBuildPalette()
    {
        _syncingBuildUi = true;
        try
        {
            BuildMaterialCombo.ItemsSource = MaterialInfo.All.Select(m => m.Name).ToList();
            BuildMaterialCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8));
            BuildHeightSlider.Value = 3;
            BuildHeightValue.Text = "3";
            BuildHollowCheck.IsChecked = false;
            BuildFoundationCombo.SelectedIndex = 0;
            BuildFoundationDepth.Text = "4";
            BuildColorBox.Text = Scene.BuildColor.Hex;
            UpdateBuildColorSwatch();
            Scene.BuildShape = BuildShape.Box;
            Scene.BuildHeight = 3;
            Scene.BuildHollow = false;
            Scene.BuildMaterial = 8;
            Scene.BuildFoundation = TerrainFoundationMode.None;
            Scene.BuildFoundationDepth = 4;
            SyncBuildShapeButtons();
        }
        finally
        {
            _syncingBuildUi = false;
        }
    }

    private void SyncBuildShapeButtons()
    {
        if (BuildBox == null) return;
        BuildBox.IsChecked = Scene.BuildShape == BuildShape.Box;
        BuildSphere.IsChecked = Scene.BuildShape == BuildShape.Sphere;
        BuildCylinder.IsChecked = Scene.BuildShape == BuildShape.Cylinder;
        BuildCone.IsChecked = Scene.BuildShape == BuildShape.Cone;
        BuildStairs.IsChecked = Scene.BuildShape == BuildShape.Stairs;
        BuildRamp.IsChecked = Scene.BuildShape == BuildShape.Ramp;
        BuildPolygon.IsChecked = Scene.BuildShape == BuildShape.Polygon;
        BuildHint.Text = Scene.BuildShape == BuildShape.Polygon
            ? "Click points on the ground. Click the first point, double-click, or press Enter to build. Backspace removes the last point."
            : "LMB drag a footprint  •  [ / ] change height  •  Esc cancel  •  RMB orbit";
    }

    private void BuildTool_Click(object sender, RoutedEventArgs e)
    {
        SelectEditorTool(EditorTool.Build);
    }

    private void BuildShape_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingBuildUi || sender is not ToggleButton button) return;
        if (Enum.TryParse<BuildShape>(button.Tag?.ToString(), out var shape))
        {
            Scene.BuildShape = shape;
            SyncBuildShapeButtons();
            Scene.Focus();
        }
    }

    private void BuildHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BuildHeightValue == null) return;
        int value = Math.Max(1, (int)Math.Round(e.NewValue));
        BuildHeightValue.Text = value.ToString();
        if (!_syncingBuildUi) Scene.BuildHeight = value;
    }

    private void BuildHollow_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingBuildUi) Scene.BuildHollow = BuildHollowCheck.IsChecked == true;
    }

    private void BuildMaterial_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBuildUi || BuildMaterialCombo.SelectedIndex < 0) return;
        Scene.BuildMaterial = MaterialInfo.All[Math.Clamp(BuildMaterialCombo.SelectedIndex, 0, MaterialInfo.All.Length - 1)].Id;
    }

    private void BuildFoundation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBuildUi || BuildFoundationCombo.SelectedIndex < 0) return;
        Scene.BuildFoundation = BuildFoundationCombo.SelectedIndex switch
        {
            1 => TerrainFoundationMode.LevelGround,
            2 => TerrainFoundationMode.CarveFoundation,
            _ => TerrainFoundationMode.None
        };
    }

    private void BuildFoundationDepth_Changed(object sender, RoutedEventArgs e) => ApplyBuildFoundationDepth();

    private void BuildFoundationDepth_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyBuildFoundationDepth(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private void ApplyBuildFoundationDepth()
    {
        if (Scene == null) return;
        if (float.TryParse(BuildFoundationDepth.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float depth))
            Scene.BuildFoundationDepth = depth;
    }

    private void BuildColor_Changed(object sender, RoutedEventArgs e) => ApplyBuildColor();

    private void BuildColor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyBuildColor(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private void ApplyBuildColor()
    {
        var parsed = Col.FromHex(BuildColorBox.Text, Scene.BuildColor);
        BuildColorBox.Text = parsed.Hex;
        Scene.BuildColor = parsed;
        UpdateBuildColorSwatch();
    }

    private void UpdateBuildColorSwatch()
    {
        var c = Scene.BuildColor;
        BuildColorSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
    }

    private void BuildClose_Click(object sender, RoutedEventArgs e) => SelectEditorTool(EditorTool.Move);

    private void BuildFinishPolygon_Click(object sender, RoutedEventArgs e)
    {
        Scene.FinishBuildPolygon();
        Scene.Focus();
    }

    private void BuildAdvanced_Click(object sender, RoutedEventArgs e)
    {
        SelectEditorTool(EditorTool.Move);
        StructureStudio_Click(sender, e);
    }

    private void ShowPrefabPicker(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        var prefabs = PrefabLibrary.List();
        if (prefabs.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No prefabs yet — save a selection first", IsEnabled = false });
        }
        else
        {
            foreach (var prefab in prefabs)
            {
                var item = new MenuItem { Header = prefab.Name, ToolTip = prefab.FilePath };
                item.Click += (_, _) => StartPrefabStamp(prefab);
                menu.Items.Add(item);
            }
        }
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void SelectEditorTool(EditorTool tool)
    {
        ToolSelect.IsChecked = tool == EditorTool.Select;
        ToolMove.IsChecked = tool == EditorTool.Move;
        ToolRotate.IsChecked = tool == EditorTool.Rotate;
        ToolScale.IsChecked = tool == EditorTool.Scale;
        ToolBrush.IsChecked = tool == EditorTool.Brush;
        ToolTerrain.IsChecked = tool == EditorTool.Terrain;
        ToolBuild.IsChecked = tool == EditorTool.Build;
        ToolMeasure.IsChecked = tool == EditorTool.Measure;
        ToolPrefab.IsChecked = tool == EditorTool.Prefab;
        BrushPalette.Visibility = tool == EditorTool.Brush ? Visibility.Visible : Visibility.Collapsed;
        if (TerrainPalette != null) TerrainPalette.Visibility = tool == EditorTool.Terrain ? Visibility.Visible : Visibility.Collapsed;
        if (BuildPalette != null)
        {
            BuildPalette.Visibility = tool == EditorTool.Build ? Visibility.Visible : Visibility.Collapsed;
            if (tool == EditorTool.Build) SyncBuildShapeButtons();
        }
        if (tool == EditorTool.Build)
            StatusText.Text = "Draw: choose a shape, then drag a footprint in the scene";
        if (tool == EditorTool.Brush)
        {
            if (Scene.BrushMode == BlockBrushMode.None) Scene.BrushMode = _lastBrushMode;
            _lastBrushMode = Scene.BrushMode;
            SyncBrushModeButtons(Scene.BrushMode);
        }
        if (tool == EditorTool.Terrain)
        {
            SyncTerrainButtons();
            StatusText.Text = "Terrain shaper: choose Add, Subtract, Smooth, Flatten or Paint, then drag on the terrain";
        }
        if (tool != EditorTool.Prefab)
        {
            _stampPrefab = null;
            Scene.ClearPrefab();
        }
        Scene.SetTool(tool);
        if (tool == EditorTool.Prefab && _stampPrefab == null)
            StatusText.Text = "Choose a prefab under Prefabs → Stamp Prefab";
        Scene.Focus();
    }

    // ================= block brush palette =================

    private void InitializeBrushPalette()
    {
        _syncingBrushUi = true;
        try
        {
            BrushShapeCombo.ItemsSource = ShapeInfo.All.Select(s => s.Name).ToList();
            BrushShapeCombo.SelectedIndex = 0;
            BrushMaterialCombo.ItemsSource = MaterialInfo.All.Select(m => m.Name).ToList();
            BrushMaterialCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8));
            BrushCellCombo.ItemsSource = new[] { "0.25", "0.5", "1", "2" };
            BrushCellCombo.SelectedIndex = 2;
            BrushLayerCombo.ItemsSource = new[] { "1", "2", "3", "4", "6", "8", "12", "16" };
            BrushLayerCombo.SelectedIndex = 0;
            BrushHollowCheck.IsChecked = false;
            BrushSymmetryCombo.ItemsSource = new[] { "None", "X", "Z", "Both" };
            BrushSymmetryCombo.SelectedIndex = 0;
            BrushColorBox.Text = Scene.BrushColor.Hex;
            UpdateBrushColorSwatch();

            Scene.BrushSymmetry = BlockSymmetry.None;
            Scene.BrushShape = ShapeInfo.Cube;
            Scene.BrushMaterial = 8;
            Scene.BrushCellSize = 1;
            Scene.BrushLayers = 1;
            Scene.BrushHollow = false;
            Scene.BrushColor = new Col(0.72f, 0.72f, 0.72f, 1f);
            SyncBrushModeButtons(Scene.BrushMode);
            UpdateSymmetryPivotButton();
        }
        finally
        {
            _syncingBrushUi = false;
        }
        BrushColorBox.LostFocus += (_, _) => ApplyBrushColor();
        BrushColorBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyBrushColor();
            Keyboard.ClearFocus();
            e.Handled = true;
        };
        RefreshBrushPalette();
    }

    private void RefreshBrushPalette()
    {
        if (BrushRecentPanel == null || BrushFavoritesPanel == null) return;
        FillPalettePanel(BrushRecentPanel, _blockPalette.Recent, emptyText: "Use the brush once to build a recent list");
        FillPalettePanel(BrushFavoritesPanel, _blockPalette.Favorites, emptyText: "Press ☆ Save preset to keep a setup");
        UpdateBrushFavoriteButton();
    }

    private void FillPalettePanel(WrapPanel panel, IReadOnlyList<BlockPaletteEntry> entries, string emptyText)
    {
        panel.Children.Clear();
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = emptyText,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 1, 0, 1)
            });
            return;
        }
        foreach (var entry in entries)
        {
            var chip = Theme.Button(entry.Label, small: true);
            chip.Tag = entry;
            chip.ToolTip = entry.Tooltip + "\nClick to load this brush setup";
            chip.Margin = new Thickness(0, 0, 4, 4);
            chip.Click += BrushPaletteChip_Click;
            var border = new Border
            {
                BorderBrush = MaterialChipBrush(entry),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 3, 3),
                Child = chip
            };
            panel.Children.Add(border);
        }
    }

    private static Brush MaterialChipBrush(BlockPaletteEntry entry)
    {
        if (MaterialInfo.IsColorable(entry.Material))
        {
            var color = Col.FromHex(entry.Color, new Col(0.72f, 0.72f, 0.72f, 1f));
            return new SolidColorBrush(Color.FromRgb((byte)color.Ri, (byte)color.Gi, (byte)color.Bi));
        }
        return new SolidColorBrush(Color.FromRgb(90, 90, 96));
    }

    private BlockPaletteEntry CurrentBrushPaletteEntry() => new()
    {
        Material = Scene.BrushMaterial,
        Color = Scene.BrushColor.Hex,
        Shape = Scene.BrushShape,
        CellSize = Scene.BrushCellSize,
        Hollow = Scene.BrushHollow,
        Layers = Scene.BrushLayers
    };

    private void UpdateBrushFavoriteButton()
    {
        if (BrushFavoriteButton == null) return;
        bool favorite = BlockPalette.IsFavorite(_blockPalette, CurrentBrushPaletteEntry());
        BrushFavoriteButton.Content = favorite ? "★ Saved" : "☆ Save preset";
        BrushFavoriteButton.ToolTip = favorite
            ? "Remove this brush setup from favourites"
            : "Save the current material, colour, shape and cell setup as a favourite";
    }

    private void BrushFavorite_Click(object sender, RoutedEventArgs e)
    {
        bool added = BlockPalette.ToggleFavorite(_blockPalette, CurrentBrushPaletteEntry());
        RefreshBrushPalette();
        StatusText.Text = added ? "Brush preset saved to favourites" : "Brush preset removed from favourites";
    }

    private void BrushPaletteChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not BlockPaletteEntry entry) return;
        ApplyBrushPaletteEntry(entry);
    }

    private void ApplyBrushPaletteEntry(BlockPaletteEntry entry)
    {
        bool previous = _syncingBrushUi;
        _syncingBrushUi = true;
        try
        {
            int material = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == entry.Material));
            BrushMaterialCombo.SelectedIndex = material;
            Scene.BrushMaterial = entry.Material;
            Scene.BrushColor = Col.FromHex(entry.Color, Scene.BrushColor);
            BrushColorBox.Text = Scene.BrushColor.Hex;
            int shape = Math.Max(0, Array.FindIndex(ShapeInfo.All, s => s.Id == entry.Shape));
            BrushShapeCombo.SelectedIndex = shape;
            Scene.BrushShape = entry.Shape;
            Scene.BrushCellSize = entry.CellSize;
            int cellIndex = ClosestComboIndex(BrushCellCombo, entry.CellSize);
            if (cellIndex >= 0) BrushCellCombo.SelectedIndex = cellIndex;
            Scene.BrushLayers = entry.Layers;
            int layerIndex = ClosestComboIndex(BrushLayerCombo, entry.Layers);
            if (layerIndex >= 0) BrushLayerCombo.SelectedIndex = layerIndex;
            BrushHollowCheck.IsChecked = entry.Hollow;
            Scene.BrushHollow = entry.Hollow;
            bool colorable = MaterialInfo.IsColorable(entry.Material);
            BrushColorBox.IsEnabled = colorable;
            BrushColorSwatch.Opacity = colorable ? 1 : 0.35;
            UpdateBrushColorSwatch();
            SyncBrushModeButtons(Scene.BrushMode);
        }
        finally
        {
            _syncingBrushUi = previous;
        }
        RefreshBrushPalette();
        SelectEditorTool(EditorTool.Brush);
        StatusText.Text = "Brush preset: " + entry.Tooltip;
    }

    private static int ClosestComboIndex(ComboBox combo, double value)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (!double.TryParse(combo.Items[i]?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double option)) continue;
            double distance = Math.Abs(option - value);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private void SyncBrushModeButtons(BlockBrushMode mode)
    {
        bool previous = _syncingBrushUi;
        _syncingBrushUi = true;
        try
        {
            BrushFill.IsChecked = mode == BlockBrushMode.Fill;
            BrushReplace.IsChecked = mode == BlockBrushMode.Replace;
            BrushErase.IsChecked = mode == BlockBrushMode.Erase;
            BrushLine.IsChecked = mode == BlockBrushMode.Line;
            BrushBox.IsChecked = mode == BlockBrushMode.Box;
            BrushSphere.IsChecked = mode == BlockBrushMode.Sphere;
            BrushHint.Text = BlockBrushModeInfo.Hint(mode);
            BrushLayerCombo.IsEnabled = mode == BlockBrushMode.Box;
            BrushHollowCheck.IsEnabled = mode is BlockBrushMode.Box or BlockBrushMode.Sphere;
        }
        finally
        {
            _syncingBrushUi = previous;
        }
    }

    private void BrushMode_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingBrushUi || sender is not ToggleButton tb) return;
        if (!Enum.TryParse<BlockBrushMode>(tb.Tag?.ToString(), out var mode)) return;
        _lastBrushMode = mode;
        Scene.BrushMode = mode;
        SelectEditorTool(EditorTool.Brush);
        SyncBrushModeButtons(mode);
    }

    private void BrushShape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrushUi || BrushShapeCombo.SelectedIndex < 0) return;
        Scene.BrushShape = ShapeInfo.All[BrushShapeCombo.SelectedIndex].Id;
        UpdateBrushFavoriteButton();
    }

    private void BrushMaterial_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrushUi || BrushMaterialCombo.SelectedIndex < 0) return;
        Scene.BrushMaterial = MaterialInfo.All[BrushMaterialCombo.SelectedIndex].Id;
        bool colorable = MaterialInfo.IsColorable(Scene.BrushMaterial);
        BrushColorBox.IsEnabled = colorable;
        BrushColorSwatch.Opacity = colorable ? 1 : 0.35;
        UpdateBrushFavoriteButton();
    }

    private void BrushCell_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrushUi || BrushCellCombo.SelectedItem is null) return;
        if (double.TryParse(BrushCellCombo.SelectedItem.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double size))
            Scene.BrushCellSize = size;
        UpdateBrushFavoriteButton();
    }

    private void BrushLayers_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrushUi || BrushLayerCombo.SelectedItem is null) return;
        if (int.TryParse(BrushLayerCombo.SelectedItem.ToString(), out int layers))
            Scene.BrushLayers = layers;
        UpdateBrushFavoriteButton();
    }

    private void BrushHollow_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingBrushUi) return;
        Scene.BrushHollow = BrushHollowCheck.IsChecked == true;
        UpdateBrushFavoriteButton();
    }

    private void BrushClose_Click(object sender, RoutedEventArgs e) => SelectEditorTool(EditorTool.Move);

    // ================= terrain shaper palette =================

    private void InitializeTerrainPalette()
    {
        _syncingTerrainUi = true;
        try
        {
            TerrainShapeCombo.SelectedIndex = 0;
            TerrainSizeSlider.Value = 4;
            TerrainSizeValue.Text = "4";
            TerrainStrengthSlider.Value = 1;
            TerrainStrengthValue.Text = "100%";
            TerrainCellCombo.SelectedIndex = 1;
            TerrainMaterialCombo.ItemsSource = MaterialInfo.All.Select(m => m.Name).ToList();
            TerrainMaterialCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8));
            TerrainColorBox.Text = Scene.TerrainColor.Hex;
            UpdateTerrainColorSwatch();
            Scene.TerrainMode = TerrainSculptMode.Add;
            Scene.TerrainVolume = TerrainBrushVolume.Sphere;
            Scene.TerrainRadius = 4;
            Scene.TerrainCellSize = 1.0;
            Scene.TerrainStrength = 1f;
            Scene.TerrainMaterial = 8;
            SyncTerrainButtons();
        }
        finally
        {
            _syncingTerrainUi = false;
        }
    }

    private void SyncTerrainButtons()
    {
        if (TerrainAdd == null) return;
        bool previous = _syncingTerrainUi;
        _syncingTerrainUi = true;
        try
        {
            TerrainAdd.IsChecked = Scene.TerrainMode == TerrainSculptMode.Add;
            TerrainRemove.IsChecked = Scene.TerrainMode == TerrainSculptMode.Remove;
            TerrainSmooth.IsChecked = Scene.TerrainMode == TerrainSculptMode.Smooth;
            TerrainFlatten.IsChecked = Scene.TerrainMode == TerrainSculptMode.Flatten;
            TerrainPaint.IsChecked = Scene.TerrainMode == TerrainSculptMode.Paint;
            TerrainHint.Text = Scene.TerrainMode switch
            {
                TerrainSculptMode.Add => "LMB fills a solid volume of terrain blocks",
                TerrainSculptMode.Remove => "LMB erases a rounded volume from the surface",
                TerrainSculptMode.Smooth => "LMB evens the surface using the strength setting",
                TerrainSculptMode.Flatten => "LMB sets the surface to the clicked height",
                _ => "LMB paints the material and colour onto terrain"
            };
            bool colorable = MaterialInfo.IsColorable(Scene.TerrainMaterial);
            TerrainColorBox.IsEnabled = colorable;
            TerrainColorSwatch.Opacity = colorable ? 1 : 0.35;
            // Add/Erase/Paint write exact volumes; only Smooth blends with the surface.
            bool usesStrength = Scene.TerrainMode == TerrainSculptMode.Smooth;
            TerrainStrengthSlider.IsEnabled = usesStrength;
            TerrainStrengthValue.Opacity = usesStrength ? 1 : 0.4;
        }
        finally
        {
            _syncingTerrainUi = previous;
        }
    }

    private void TerrainMode_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTerrainUi || sender is not ToggleButton button) return;
        if (!Enum.TryParse<TerrainSculptMode>(button.Tag?.ToString(), out var mode)) return;
        Scene.TerrainMode = mode;
        SelectEditorTool(EditorTool.Terrain);
        SyncTerrainButtons();
        Scene.Focus();
    }

    private void TerrainShape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTerrainUi || TerrainShapeCombo.SelectedIndex < 0) return;
        Scene.TerrainVolume = TerrainShapeCombo.SelectedIndex switch
        {
            1 => TerrainBrushVolume.Box,
            2 => TerrainBrushVolume.Cylinder,
            _ => TerrainBrushVolume.Sphere
        };
    }

    private void TerrainSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TerrainSizeValue == null) return;
        int size = Math.Max(1, (int)Math.Round(e.NewValue));
        TerrainSizeValue.Text = size.ToString();
        if (!_syncingTerrainUi) Scene.TerrainRadius = size;
    }

    private void TerrainStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TerrainStrengthValue == null) return;
        TerrainStrengthValue.Text = $"{e.NewValue * 100:0}%";
        if (!_syncingTerrainUi) Scene.TerrainStrength = (float)e.NewValue;
    }

    private void TerrainCell_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTerrainUi || TerrainCellCombo.SelectedIndex < 0) return;
        Scene.TerrainCellSize = TerrainCellCombo.SelectedIndex switch
        {
            0 => 0.5,
            2 => 2.0,
            _ => 1.0
        };
    }

    private void TerrainMaterial_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTerrainUi || TerrainMaterialCombo.SelectedIndex < 0) return;
        Scene.TerrainMaterial = MaterialInfo.All[Math.Clamp(TerrainMaterialCombo.SelectedIndex, 0, MaterialInfo.All.Length - 1)].Id;
        SyncTerrainButtons();
    }

    private void TerrainColor_Changed(object sender, RoutedEventArgs e) => ApplyTerrainColor();

    private void TerrainColor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTerrainColor(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private void ApplyTerrainColor()
    {
        var parsed = Col.FromHex(TerrainColorBox.Text, Scene.TerrainColor);
        TerrainColorBox.Text = parsed.Hex;
        Scene.TerrainColor = parsed;
        UpdateTerrainColorSwatch();
    }

    private void UpdateTerrainColorSwatch()
    {
        var c = Scene.TerrainColor;
        TerrainColorSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
    }

    private void TerrainClose_Click(object sender, RoutedEventArgs e) => SelectEditorTool(EditorTool.Move);

    private void TerrainTool_Click(object sender, RoutedEventArgs e) => SelectEditorTool(EditorTool.Terrain);

    private void TerrainStudioFromPalette_Click(object sender, RoutedEventArgs e) => Terrain_Click(sender, e);

    private void Scene_TerrainSculpted(TerrainSculptResult result)
    {
        if (result.Count == 0)
        {
            StatusText.Text = result.Summary;
            return;
        }
        if (result.Added > 0 || result.Removed > 0) BuildHierarchy();
        else
        {
            HierarchyInfo.Text = $"{_level.AllNodes().Count()} objects";
            RefreshInspector();
        }
        StatusText.Text = result.Summary;
        Log(result.Summary);
        MarkDirty(null, "Terrain " + result.Description);
    }

    private void BrushSymmetry_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrushUi || BrushSymmetryCombo.SelectedIndex < 0) return;
        Scene.BrushSymmetry = BrushSymmetryCombo.SelectedIndex switch
        {
            1 => BlockSymmetry.X,
            2 => BlockSymmetry.Z,
            3 => BlockSymmetry.Both,
            _ => BlockSymmetry.None
        };
        UpdateSymmetryPivotButton();
        StatusText.Text = "Brush mirror: " + (BrushSymmetryCombo.SelectedItem?.ToString() ?? "None");
    }

    private void BrushPivot_Click(object sender, RoutedEventArgs e)
    {
        if (Scene.Selection.Count == 0)
        {
            StatusText.Text = "Select an object or group first to place the mirror pivot.";
            return;
        }
        Scene.SymmetryPivot = Scene.SelectionCenter();
        UpdateSymmetryPivotButton();
        StatusText.Text = "Mirror pivot set to the selection centre";
    }

    private void SymmetryPivot_Click(object sender, RoutedEventArgs e)
    {
        if (Scene.Selection.Count == 0)
        {
            Scene.SymmetryPivot = Vec3.Zero;
            StatusText.Text = "Mirror pivot reset to the world origin";
        }
        else
        {
            Scene.SymmetryPivot = Scene.SelectionCenter();
            StatusText.Text = "Mirror pivot set to the selection centre";
        }
        UpdateSymmetryPivotButton();
    }

    private void UpdateSymmetryPivotButton()
    {
        var p = Scene.SymmetryPivot;
        BrushPivotButton.Content = $"Pivot {p.X:0.#}, {p.Z:0.#}";
        BrushPivotButton.ToolTip = $"Mirror pivot: X {p.X:0.##}, Y {p.Y:0.##}, Z {p.Z:0.##}";
    }

    private void LocalSpace_Click(object sender, RoutedEventArgs e)
    {
        bool local = sender switch
        {
            MenuItem item => item.IsChecked,
            CheckBox check => check.IsChecked == true,
            _ => !Scene.LocalSpace
        };
        Scene.LocalSpace = local;
        LocalSpaceMenuItem.IsChecked = local;
        LocalSpaceCheck.IsChecked = local;
        StatusText.Text = local ? "Gizmo space: Local" : "Gizmo space: Global";
    }

    private void AlignSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var parts = (item.Tag?.ToString() ?? "0,1").Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int axis) || !int.TryParse(parts[1], out int mode)) return;
        Scene.AlignSelection(axis, mode);
    }

    private void DistributeSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !int.TryParse(item.Tag?.ToString(), out int axis)) return;
        Scene.DistributeSelection(axis);
    }

    private void ArrayDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_playing || _selection.Count == 0) return;
        var dlg = new ArrayDuplicateDialog(this);
        if (dlg.ShowDialog() != true) return;
        var settings = dlg.Settings;
        var roots = Scene.TopLevelSelection();
        if (roots.Count == 0) return;
        var offset = Vec3.Zero;
        offset[settings.Axis] = (float)settings.Spacing;
        var copies = new List<LevelNode>();
        try
        {
            for (int index = 1; index < settings.Count; index++)
            {
                foreach (var root in roots)
                {
                    var copy = root.Clone();
                    copy.Name = "";
                    _level.AddNode(copy, root.Parent);
                    Scene.OffsetNodeWorld(copy, offset * index);
                    copies.Add(copy);
                }
            }
            _level.EnsureNames();
            BuildHierarchy();
            SelectNodes(copies);
            Scene.Refresh();
            MarkDirty(null, "Duplicate array");
            StatusText.Text = $"Created {copies.Count} array cop{(copies.Count == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not duplicate the array:\n\n" + ex.Message,
                "Duplicate Array", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MirrorSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var parts = (item.Tag?.ToString() ?? "0,1").Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int axis) || !int.TryParse(parts[1], out int mode)) return;
        bool duplicate = mode != 0;
        var mirrored = Scene.MirrorSelection(axis, duplicate);
        if (mirrored.Count == 0) return;
        BuildHierarchy();
        SelectNodes(mirrored);
        StatusText.Text = $"Mirrored {mirrored.Count} object(s) across {(axis == 0 ? "X" : "Z")}" + (duplicate ? " (copies)" : "");
    }

    private void ApplyBrushColor()
    {
        var parsed = Col.FromHex(BrushColorBox.Text, Scene.BrushColor);
        BrushColorBox.Text = parsed.Hex;
        Scene.BrushColor = parsed;
        UpdateBrushColorSwatch();
        UpdateBrushFavoriteButton();
    }

    private void UpdateBrushColorSwatch()
    {
        var c = Scene.BrushColor;
        BrushColorSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
    }

    private void Scene_BrushApplied(BlockBrushResult result)
    {
        if (result.Count > 0)
        {
            BlockPalette.AddRecent(_blockPalette, CurrentBrushPaletteEntry());
            RefreshBrushPalette();
            if (result.Created) BuildHierarchy();
            else
            {
                HierarchyInfo.Text = $"{_level.AllNodes().Count()} objects";
                RefreshInspector();
            }
            Scene.Refresh();
            MarkDirty(null, "Brush " + result.Description);
        }
        StatusText.Text = result.Status;
        Log(result.Status);
    }

    private void SnapSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Scene == null || SnapSizeCombo?.SelectedItem is not ComboBoxItem item) return;
        if (double.TryParse(item.Content?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double size))
        {
            Scene.SnapSize = size;
            _level.GridSnap = size;
            StatusText.Text = $"Snap step {size:0.##}";
        }
    }

    private void SnapSelection_Click(object sender, RoutedEventArgs e) => Scene.SnapSelectionToGrid();

    private void SelectAllObjects_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) return;
        var nodes = _level.AllNodes().Where(n => !n.Generated).ToList();
        _selection = nodes;
        _selected = nodes.LastOrDefault();
        BuildHierarchy();
        _syncSelection = true;
        try { Scene.SetSelectionRange(nodes); }
        finally { _syncSelection = false; }
        RefreshTreeSelection();
        UpdateSelectionUi();
    }

    private void Snap_Click(object sender, RoutedEventArgs e)
    {
        Scene.SnapEnabled = SnapCheck.IsChecked == true;
        StatusText.Text = Scene.SnapEnabled ? "Grid snap 0.5" : "Grid snap off";
    }

    private void Play_Click(object sender, RoutedEventArgs e) => StartPlay();
    private void Pause_Click(object sender, RoutedEventArgs e) => PausePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlay();
    private void CompileAll_Click(object sender, RoutedEventArgs e) => CompileAll();

    private void Ambience_Click(object sender, RoutedEventArgs e) => SelectNode(null);

    private void ShowCode_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 0;
    private void ShowGasm_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 1;
    private void ShowProblems_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 3;
    private void ShowConsole_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 4;

    private void CSharpHelp_Click(object sender, RoutedEventArgs e) => OpenDocs("CSharpReference.md");
    private void GasmHelp_Click(object sender, RoutedEventArgs e) => OpenDocs("GASMReference.md");

    private void OpenDocs(string file)
    {
        if (_docsWindow == null)
        {
            _docsWindow = new DocsWindow(this);
            _docsWindow.Closed += (_, _) => _docsWindow = null;
        }
        _docsWindow.ShowDocument(file);
        if (!_docsWindow.IsVisible) _docsWindow.Show();
        _docsWindow.Activate();
    }

    private void Animation_Click(object sender, RoutedEventArgs e) => ShowAnimationPanel();

    /// <summary>Development aid for the --shot-brush screenshot.</summary>
    internal void ShowBrushForScreenshot()
    {
        Scene.BrushMode = BlockBrushMode.Box;
        Scene.BrushShape = ShapeInfo.Cube;
        Scene.BrushMaterial = 8;
        Scene.BrushCellSize = 1;
        Scene.BrushLayers = 2;
        Scene.BrushHollow = false;
        Scene.BrushColor = new Col(0.95f, 0.42f, 0.18f, 1f);
        BrushLayerCombo.SelectedIndex = 1;
        BrushColorBox.Text = Scene.BrushColor.Hex;
        UpdateBrushColorSwatch();
        SelectEditorTool(EditorTool.Brush);
        SyncBrushModeButtons(BlockBrushMode.Box);

        // Build a small demo above the default floor so the brush result and its
        // textures are visible in the automation capture.
        Scene.ApplyBrushForAutomation(new VoxelCell(-4, 1, 4), new VoxelCell(-1, 1, 5));
        Scene.ApplyBrushForAutomation(new VoxelCell(2, 1, 3), new VoxelCell(4, 1, 3));
        Scene.BrushMode = BlockBrushMode.Sphere;
        Scene.BrushHollow = true;
        Scene.ApplyBrushForAutomation(new VoxelCell(0, 3, 4), new VoxelCell(3, 3, 4));
        Scene.BrushMode = BlockBrushMode.Box;
        Scene.BrushHollow = false;
        BrushHollowCheck.IsChecked = false;
        SyncBrushModeButtons(BlockBrushMode.Box);
        BuildHierarchy();
        Scene.Refresh();
        Scene.FrameAll();
        Scene.ClearBrushPreviewForAutomation();
        StatusText.Text = "Block brush ready — Fill, Replace, Erase, Line, Box and Sphere";
    }

    /// <summary>Development aid for the --shot-build screenshot.</summary>
    internal void ShowBuildForScreenshot()
    {
        SelectEditorTool(EditorTool.Build);
        Scene.BuildShape = BuildShape.Box;
        Scene.BuildHeight = 4;
        Scene.BuildColor = new Col(0.86f, 0.58f, 0.28f, 1f);
        BuildHeightSlider.Value = 4;
        BuildColorBox.Text = Scene.BuildColor.Hex;
        UpdateBuildColorSwatch();
        SyncBuildShapeButtons();

        Scene.DrawShapeForAutomation(new VoxelCell(-7, 0, -8), new VoxelCell(-3, 0, -4), BuildShape.Box, 3);
        Scene.DrawShapeForAutomation(new VoxelCell(3, 0, -7), new VoxelCell(6, 0, -4), BuildShape.Stairs, 5);
        Scene.DrawShapeForAutomation(new VoxelCell(-1, 0, 4), new VoxelCell(3, 0, 4), BuildShape.Sphere, 4);
        BuildHierarchy();
        Scene.Refresh();
        Scene.FrameAll();
        StatusText.Text = "Draw structures — drag a footprint in the scene";
    }

    /// <summary>Development aid for the --shot-animation screenshot.</summary>
    internal void ShowAnimationForScreenshot()
    {
        var node = _level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Sign)
            ?? _level.AllNodes().FirstOrDefault(n => n.Kind != NodeKind.Group);
        if (node == null) return;
        if (node.Animations.Count == 0)
        {
            node.Animations.Add(new GrabAnimation
            {
                Name = "preview",
                Frames =
                {
                    new GrabAnimationFrame { Time = 0, Position = node.Position, Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 0.8f, Position = node.Position + new Vec3(0, 1, 0), Rotation = Quat.FromEulerDegrees(new Vec3(0, 180, 0)) },
                    new GrabAnimationFrame { Time = 1.6f, Position = node.Position + new Vec3(0, 2, 0), Rotation = Quat.FromEulerDegrees(new Vec3(0, 360, 0)) }
                }
            });
        }
        SelectNode(node);
        ShowAnimationPanel();
    }

    private void ShowAnimationPanel()
    {
        if (_playing) StopPlay();
        _animationPanel?.SetLevel(_level);
        _animationPanel?.SetSelected(_selected);
        if (ReferenceEquals(BottomTabs.SelectedItem, AnimationTab)) _animationPanel?.Activate();
        else BottomTabs.SelectedItem = AnimationTab;
        _animationPanel?.Focus();
    }

    private void BottomTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The animation timeline needs vertical room for its timeline, key table and
        // key fields. Open a taller dock when it is selected and restore the previous
        // height when the user switches back to C#, GASM, Problems or Console.
        if (!ReferenceEquals(e.OriginalSource, BottomTabs)) return;
        if (ReferenceEquals(BottomTabs.SelectedItem, AnimationTab))
        {
            if (!_animationDockExpanded)
            {
                _bottomHeightBeforeAnimation = BottomRow.Height;
                _animationDockExpanded = true;
            }
            _animationPanel?.SetLevel(_level);
            _animationPanel?.Activate();
            if (BottomRow.ActualHeight < 500) BottomRow.Height = new GridLength(540);
        }
        else if (_animationDockExpanded)
        {
            _animationPanel?.Deactivate();
            BottomRow.Height = _bottomHeightBeforeAnimation;
            _animationDockExpanded = false;
        }
    }

    /// <summary>Creates a fresh Code Block containing the Animation window's controller snippet.</summary>
    private bool CreateCodeBlockFromSnippet(string code)
    {
        if (_playing || string.IsNullOrWhiteSpace(code)) return false;
        var node = new LevelNode
        {
            Kind = NodeKind.GASM,
            Position = NextPosition(),
            Scale = Vec3.One,
            StartActive = true,
            Script = code
        };
        _level.AddNode(node, ParentForNew());
        _level.EnsureNames();
        BuildHierarchy();
        _suppressAnimationSync = true;
        try { SelectNode(node); }
        finally { _suppressAnimationSync = false; }
        Scene.Refresh();
        MarkDirty("code", "Create animation Code Block");
        var (errors, warnings) = CompileAll();
        BottomTabs.SelectedIndex = errors > 0 ? 3 : 0;
        Log($"Created animation Code Block '{node.DisplayName}' ({errors} error(s), {warnings} warning(s))");
        return true;
    }

    private void OpenDoc(string file)
    {
        // Look next to the exe first, then walk up from both the exe folder and the
        // working folder. This makes Help work from bin/Debug, bin/Release and the
        // published app/ folder without depending on the .md file association.
        var candidates = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, file));
                candidates.Add(Path.Combine(dir.FullName, "docs", file));
            }
        }
        bool found = false;
        string? foundPath = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(candidate);
            if (!File.Exists(path))
            {
                // Windows is normally case-insensitive, but a file copied from a
                // case-sensitive archive can still be found by a quick directory scan.
                var dir = Path.GetDirectoryName(path);
                if (dir == null || !Directory.Exists(dir)) continue;
                var match = Directory.EnumerateFiles(dir, "*.md")
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                path = match;
            }
            found = true;
            foundPath = path;
            // .md has no default association on many Windows installs; opening it via
            // the shell can throw (or show an Open With dialog). Use Notepad directly.
            if (string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false });
                    return;
                }
                catch { }
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; }
            catch { }
        }
        if (found)
        {
            MessageBox.Show(this, "The documentation exists but Windows could not open it.\n\n" + foundPath +
                "\n\nOpen that file manually in any text editor.", "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, "Documentation file not found.\n\nLooked in:\n" + string.Join("\n", candidates.Select(Path.GetFullPath)),
            "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "GRAB ENGINE 1.0\n\nA Unity-style level editor and C# → GASM compiler for GRAB.\n\n" +
            "• Real GRAB shapes, textures, lights, ambience and .level format\n" +
            "• C# scripting compiled to genuine GASM bytecode\n" +
            "• Place blocks, triggers, lights, signs, sounds and particle emitters\n" +
            "• Export .level files that load in the retail game\n\n" +
            "Built by studying GRAB's protobuf schema, GASM VM and asset formats.",
            "About GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Compiles and runs focused C# language tests (OOP, collections, exceptions, coroutines).</summary>
    private void RunLanguageTests(System.Text.StringBuilder sb, Action<string, bool, string> check)
    {
        (string Errors, LevelNode Node, GrabLevel Level) Build(string name, string script)
        {
            var level = GrabLevel.CreateDefault();
            var node = new LevelNode { Kind = NodeKind.GASM, Name = name, Position = new Vec3(0, 1, 0) };
            level.AddNode(node);
            level.EnsureNames();
            node.Script = script;
            var result = new CSharpCompiler().Compile(level, node, script);
            node.Compiled = result;
            string errors = string.Join(" | ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"L{d.Line}: {d.Message}"));
            return (errors, node, level);
        }
        float RunY(LevelNode node, GrabLevel level, int steps)
        {
            var play = new PlaySession(level);
            for (int i = 0; i < steps; i++) play.Step(1f / 60f);
            var target = level.FindByName("Floor");
            return target?.Position.Y ?? float.NaN;
        }

        var oop = Build("OopTest", """
using Grab;

public interface IShape { float Area(); }

public struct Vec
{
    public float X;
    public float Y;
    public float Len() { return Mathf.Sqrt(X * X + Y * Y); }
}

public class Shape : IShape
{
    public float Size = 2;
    public virtual float Area() { return Size * Size; }
}

public class Box : Shape
{
    public float Depth = 3;
    public override float Area() { return Size * Depth; }
}

public class Holder<T>
{
    public T Value;
}

public class OopTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    Box box = new Box();
    Holder<float> holder = new Holder<float>();
    Vec v;
    IShape shape;

    void Start()
    {
        v.X = 3;
        v.Y = 4;
        box.Size = 5;
        holder.Value = 7;
        shape = box;
        block.Position.Y = v.Len() + shape.Area() + holder.Value;
    }
}
""");
        check("OOP: structs, inheritance, interfaces, generics compile", oop.Errors.Length == 0, oop.Errors);
        float oopY = RunY(oop.Node, oop.Level, 3);
        check("OOP: virtual dispatch + struct methods run", Math.Abs(oopY - 27f) < 0.01f, oopY.ToString("0.###"));
        var ctor = Build("CtorTest", """
using Grab;

public struct Pair
{
    public float A;
    public float B;
    public Pair(float a, float b) { A = a; B = b; }
}

public class CtorTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    Pair p = new Pair(3, 4);
    void Start() { block.Position.Y = p.A + p.B; }
}
""");
        float ctorY = RunY(ctor.Node, ctor.Level, 3);
        check("parameterized struct constructors run", ctor.Errors.Length == 0 && Math.Abs(ctorY - 7f) < 0.01f, $"{ctor.Errors} {ctorY:0.###}");

        var collections = Build("CollectionTest", """
using Grab;

public class CollectionTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    List<float> nums = new List<float>();
    Dictionary<string, float> map = new Dictionary<string, float>();

    void Start()
    {
        nums.Add(1);
        nums.Add(2);
        nums.Add(3);
        map["a"] = 10;
        map["b"] = 20;
        float total = 0;
        foreach (float n in nums) total += n;
        foreach (var kv in map) total += kv.Value;
        total += nums.Count;
        total += map.Count;
        total += nums[1];
        total += map["a"];
        total += map.ContainsKey("b") ? 100 : 0;
        total += nums.Contains(3) ? 1000 : 0;
        block.Position.Y = total;
    }
}
""");
        check("List/Dictionary/foreach compile", collections.Errors.Length == 0, collections.Errors);
        float collY = RunY(collections.Node, collections.Level, 3);
        check("List/Dictionary/foreach run", Math.Abs(collY - 1153f) < 0.01f, collY.ToString("0.###"));

        var arrayHelpers = Build("ArrayHelperTest", """
using Grab;

public class ArrayHelperTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");

    void Start()
    {
        List<float> nums = new List<float>(4);
        nums.Add(10);
        nums.Add(30);
        nums.Insert(1, 20);
        float[] extra = new float[] { 40, 50 };
        nums.AddRange(extra);
        float[] arr = new float[] { 3, 1, 4 };
        Array.Reverse(arr);
        float idx = Array.IndexOf(arr, 1);
        Array.Fill(arr, 7);
        float[] copy = new float[3];
        Array.Copy(arr, copy, 3);
        Array.Clear(arr);
        float total = nums[0] + nums[1];
        total += nums[2] + nums[3];
        total += idx;
        total += copy[0] + arr[0];
        block.Position.Y = total;
    }
}
""");
        check("List.Insert/AddRange and Array helpers compile", arrayHelpers.Errors.Length == 0, arrayHelpers.Errors);
        float arrayY = RunY(arrayHelpers.Node, arrayHelpers.Level, 3);
        check("List.Insert/AddRange and Array helpers run", Math.Abs(arrayY - 108f) < 0.01f, arrayY.ToString("0.###"));

        var stringExtras = Build("StringExtraTest", """
using Grab;

public class StringExtraTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");

    void Start()
    {
        string title = "  grab engine  ".Trim().ToUpper();
        char c = '7';
        bool digit = char.IsDigit('7') && c == '7';
        int cmp = "beta".CompareTo("alpha");
        block.Position.Y = title.Length + (digit ? 1 : 0) + (string.Compare("a", "b") < 0 ? 100 : 0) + Convert.ToInt32("42") + cmp;
    }
}
""");
        check("char, Convert and extra string methods compile", stringExtras.Errors.Length == 0, stringExtras.Errors);
        float stringY = RunY(stringExtras.Node, stringExtras.Level, 3);
        check("char, Convert and extra string methods run", Math.Abs(stringY - 155f) < 0.01f, stringY.ToString("0.###"));

        var objectMethods = Build("ObjectMethodTest", """
using Grab;

public class ObjectMethodTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");

    void Start()
    {
        block.SetPosition(new Vector3(1, 0, 1));
        block.Translate(new Vector3(2, 0, 2));
        block.SetRotation(new Vector3(0, 30, 0));
        block.Rotate(new Vector3(0, 10, 0));
        block.SetColor(new Color(1, 0, 0));
        float angle = Vector3.Angle(new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        float clamp = Vector3.ClampMagnitude(new Vector3(10, 0, 0), 3).X;
        float proj = Vector3.Project(new Vector3(2, 3, 0), new Vector3(0, 1, 0)).Y;
        float sq = Vector3.SqrMagnitude(new Vector3(2, 3, 0));
        block.Position.Y = angle + clamp + proj + sq + Mathf.FloorToInt(3.9f) + block.Rotation.Y + block.Color.R * 10;
    }
}
""");
        check("object helpers and extra Vector3/Mathf APIs compile", objectMethods.Errors.Length == 0, objectMethods.Errors);
        float objectY = RunY(objectMethods.Node, objectMethods.Level, 3);
        var objectFloor = objectMethods.Level.FindByName("Floor");
        check("object helpers and extra Vector3/Mathf APIs run",
            Math.Abs(objectY - 162f) < 0.01f && objectFloor != null &&
            Math.Abs(objectFloor.Position.X - 3f) < 0.01f && Math.Abs(objectFloor.Position.Z - 3f) < 0.01f &&
            Math.Abs(objectFloor.WorldRotation.ToEulerDegrees().Y - 40f) < 0.01f,
            $"y={objectY:0.###} x={objectFloor?.Position.X:0.###} z={objectFloor?.Position.Z:0.###} rot={objectFloor?.WorldRotation.ToEulerDegrees().Y:0.###}");

        var readonlyTest = Build("ReadonlyTest", """
using Grab;

public class R
{
    public readonly float V;
    public R(float v) { V = v; }
}

public class ReadonlyTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    R r = new R(7);
    void Start() { block.Position.Y = r.V; }
}
""");
        check("readonly fields work in constructors", readonlyTest.Errors.Length == 0 && Math.Abs(RunY(readonlyTest.Node, readonlyTest.Level, 3) - 7f) < 0.01f,
            $"{readonlyTest.Errors} {RunY(readonlyTest.Node, readonlyTest.Level, 3):0.###}");
        var readonlyWrite = Build("ReadonlyWriteTest", """
using Grab;
public class R { public readonly float V = 1; }
public class ReadonlyWriteTest : CodeBlock
{
    R r = new R();
    void Start() { r.V = 2; }
}
""");
        check("writing a readonly field is diagnosed",
            readonlyWrite.Errors.Contains("readonly", StringComparison.OrdinalIgnoreCase), readonlyWrite.Errors);

        var bareNew = Build("BareNewTest", """
using Grab;

public class BareNewTest : CodeBlock
{
    Sign board = new Sign;
    void Start() { board.Write = "Bare"; }
}
""");
        var bareSign = bareNew.Level.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Sign);
        check("new Type without parentheses creates a scene object",
            bareNew.Errors.Length == 0 && bareSign != null && bareSign.Text == "Bare",
            $"{bareNew.Errors} {bareSign?.Text}");

        var control = Build("ControlTest", """
using Grab;

public class Res
{
    public float Used;
    public void Dispose() { Used = 1; }
}

public class ControlTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    float t;

    void Start()
    {
        t = 0;
        Res r = new Res();
        using (r) { t += 1; }
        lock (block) { t += 2; }
        try
        {
            throw new Exception("boom");
            t = 99;
        }
        catch (Exception e)
        {
            t += 3;
        }
        finally
        {
            t += 4;
        }
        goto skip;
        t = 500;
        skip:
        t += 8;
        block.Position.Y = t;
    }
}
""");
        check("try/catch/finally, using, lock, goto compile", control.Errors.Length == 0, control.Errors);
        float controlY = RunY(control.Node, control.Level, 3);
        check("try/catch/finally and goto run", Math.Abs(controlY - 18f) < 0.01f, controlY.ToString("0.###"));

        var counter = Build("CounterTest", """
using Grab;
public class CounterTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    int n;
    void Update()
    {
        n = n + 1;
        block.Position.Y = n;
    }
}
""");
        check("working registers persist between frames", Math.Abs(RunY(counter.Node, counter.Level, 10) - 10f) < 0.01f, RunY(counter.Node, counter.Level, 10).ToString("0.###"));
        var branch = Build("BranchTest", """
using Grab;
public class BranchTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    int n;
    void Update()
    {
        if (n == 0) { block.Position.Y = 1; n = 1; }
        else { block.Position.Y = 2; }
    }
}
""");
        check("conditional state changes persist", Math.Abs(RunY(branch.Node, branch.Level, 10) - 2f) < 0.01f, RunY(branch.Node, branch.Level, 10).ToString("0.###"));

        var coroutine = Build("CoroutineTest", """
using Grab;

public class CoroutineTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    bool done;

    IEnumerator Fade()
    {
        block.Position.Y = 1;
        yield return new WaitForSeconds(1);
        block.Position.Y = 2;
        yield return null;
        block.Position.Y = 3;
        done = true;
    }

    void Update()
    {
        if (!done) Fade();
    }
}
""");
        check("IEnumerator/yield compiles", coroutine.Errors.Length == 0, coroutine.Errors);
        float co1 = RunY(coroutine.Node, coroutine.Level, 2);
        float co2 = RunY(coroutine.Node, coroutine.Level, 70);
        check("yield runs across frames", Math.Abs(co1 - 1f) < 0.01f && Math.Abs(co2 - 3f) < 0.01f, $"{co1:0.###} then {co2:0.###}");

        var asyncTest = Build("AsyncTest", """
using Grab;

public class AsyncTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");

    bool done;

    async Task Blink()
    {
        block.Position.Y = 4;
        await Task.Delay(500);
        block.Position.Y = 5;
        done = true;
    }

    void Update()
    {
        if (!done) StartCoroutine(Blink());
    }
}
""");
        check("async/await compiles", asyncTest.Errors.Length == 0, asyncTest.Errors);
        float a1 = RunY(asyncTest.Node, asyncTest.Level, 2);
        float a2 = RunY(asyncTest.Node, asyncTest.Level, 40);
        check("async/await resumes after a delay", Math.Abs(a1 - 4f) < 0.01f && Math.Abs(a2 - 5f) < 0.01f, $"{a1:0.###} then {a2:0.###}");

        var bulk = new System.Text.StringBuilder();
        for (int i = 0; i < 80; i++) bulk.AppendLine("        t = t + 0.01f;");
        var split = Build("SplitTest", """
using Grab;

public class SplitTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    float t;
    bool done;

    IEnumerator Pulse()
    {
        block.Position.Y = 100;
        yield return new WaitForSeconds(0.2f);
        block.Position.Y = 200;
        yield return null;
        block.Position.Y = 300;
        done = true;
    }

    void Update()
    {
        if (!done) StartCoroutine(Pulse());
__BULK__    }
}
""".Replace("__BULK__", bulk.ToString()));
        var pages = split.Node.Compiled?.Continuations ?? new List<GasmCompileResult>();
        check("oversized script exports continuation blocks", split.Errors.Length == 0 && pages.Count > 0,
            $"{pages.Count + 1} page(s)  {split.Errors}");
        int biggest = pages.Count == 0 ? 0 : pages.Max(p => p.Program.Instructions.Count);
        if (split.Node.Compiled != null) biggest = Math.Max(biggest, split.Node.Compiled.Program.Instructions.Count);
        check("every continuation page fits GRAB's instruction limit", biggest <= GrabLimits.MaxInstructions, biggest + " / " + GrabLimits.MaxInstructions);
        float s1 = RunY(split.Node, split.Level, 5);
        float s2 = RunY(split.Node, split.Level, 70);
        check("split script keeps a coroutine running across pages", Math.Abs(s1 - 100f) < 0.01f && Math.Abs(s2 - 300f) < 0.01f, $"{s1:0.###} then {s2:0.###}");
        var splitRound = LevelIO.Read(LevelIO.Write(split.Level));
        var splitGasms = splitRound.AllNodes().Where(n => n.Kind == NodeKind.GASM).ToList();
        bool pagesFit = splitGasms.All(n => (n.ImportedProgram?.Instructions.Count ?? 0) <= GrabLimits.MaxInstructions);
        check("continuation pages survive .level export", splitGasms.Count == pages.Count + 1 && pagesFit,
            $"{splitGasms.Count} code blocks, largest {splitGasms.Max(n => n.ImportedProgram?.Instructions.Count ?? 0)}");
        var roundPlay = new PlaySession(splitRound);
        for (int i = 0; i < 70; i++) roundPlay.Step(1f / 60f);
        var roundFloor = splitRound.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static && Math.Abs(n.Scale.X - 10) < 0.001f);
        check("exported split level runs from its imported GASM",
            roundFloor != null && Math.Abs(roundFloor.Position.Y - 300f) < 0.01f,
            roundFloor?.Position.Y.ToString("0.###") ?? "missing");
        const string smallScript = "using Grab;\npublic class SplitTest : CodeBlock\n{\n    GameObject block = GameObject.Find(\"Floor\");\n    void Update() { block.Position.Y = 7; }\n}\n";
        split.Node.Script = smallScript;
        var smallResult = new CSharpCompiler().Compile(split.Level, split.Node, smallScript);
        int stalePages = split.Level.AllNodes().Count(n => n.Generated && n.Kind == NodeKind.GASM &&
            n.GeneratedFor == split.Node.EditorId && n.PageIndex > 0 && n.StartActive);
        check("shrinking a script deactivates old continuation pages", stalePages == 0 && smallResult.Continuations.Count == 0,
            $"{stalePages} stale page(s)");

        // A page can hit GRAB's connection limit long before its instruction budget.
        // The splitter must reserve a page break when the next statement would add a
        // new scene object, not wait for the old compile error.
        var connectionLevel = GrabLevel.CreateDefault();
        var names = new[] { "O00A", "O01A", "O02A", "O03A", "O04A", "O05A", "O06A", "O07A", "O08A", "O09A", "O10A", "O11A" };
        foreach (var name in names)
            connectionLevel.AddNode(new LevelNode
            {
                Kind = NodeKind.Static, Name = name, Position = new Vec3(0, 1, 0),
                Scale = Vec3.One, Shape = ShapeInfo.Cube, Material = 8
            });
        var connectionNode = new LevelNode { Kind = NodeKind.GASM, Name = "ConnectionSplit", Position = new Vec3(0, 2, 0) };
        connectionLevel.AddNode(connectionNode);
        connectionLevel.EnsureNames();
        string connectionScript = "using Grab;\npublic class ConnectionSplit : CodeBlock\n{\n" +
            string.Join("\n", names.Select((name, i) => $"    GameObject o{i} = GameObject.Find(\"{name}\");")) +
            "\n    void Update()\n    {\n" +
            string.Join("\n", Enumerable.Range(0, names.Length).Select(i => $"        o{i}.Position.Y = Time.DeltaTime;")) +
            "\n    }\n}\n";
        connectionNode.Script = connectionScript;
        var connectionResult = new CSharpCompiler().Compile(connectionLevel, connectionNode, connectionScript);
        connectionNode.Compiled = connectionResult;
        var connectionPages = new List<GasmCompileResult> { connectionResult };
        connectionPages.AddRange(connectionResult.Continuations);
        check("connection-aware split keeps large scripts inside GRAB's connection limit",
            connectionResult.Success && connectionPages.Count > 1 &&
            connectionPages.All(p => p.Connections.Count <= GrabLimits.MaxConnections),
            $"{connectionPages.Count} page(s), max {connectionPages.Max(p => p.Connections.Count)} connections");
    }

    /// <summary>End-to-end verification used by `GRABEngine.exe --selftest`.</summary>
    public string RunSelfTest()
    {
        var sb = new System.Text.StringBuilder();
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) passed++; else failed++;
            sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  —  " + detail));
        }
        static byte[] MakeTexturedTestGlb()
        {
            // A textured quad. The PNG is embedded as a data URI so the importer's
            // image decoder and UV sampling path are exercised without external files.
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 25;      // B
                pixels[i + 1] = 50;  // G
                pixels[i + 2] = 210; // R
                pixels[i + 3] = 255; // A
            }
            var png = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(png));
            using var pngStream = new MemoryStream();
            encoder.Save(pngStream);
            string pngBase64 = Convert.ToBase64String(pngStream.ToArray());

            var positions = new[] { -1f, 0f, -1f, 1f, 0f, -1f, 1f, 0f, 1f, -1f, 0f, 1f };
            var uvs = new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };
            var bin = new byte[92];
            Buffer.BlockCopy(positions, 0, bin, 0, 48);
            Buffer.BlockCopy(uvs, 0, bin, 48, 32);
            Buffer.BlockCopy(indices, 0, bin, 80, 12);
            string json = $$"""
{
  "asset": { "version": "2.0" },
  "scene": 0,
  "scenes": [ { "nodes": [0] } ],
  "nodes": [ { "mesh": 0 } ],
  "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0, "TEXCOORD_0": 1 }, "indices": 2, "material": 0, "mode": 4 } ] } ],
  "materials": [ { "pbrMetallicRoughness": { "baseColorFactor": [1, 1, 1, 1], "baseColorTexture": { "index": 0 } } } ],
  "textures": [ { "source": 0 } ],
  "images": [ { "uri": "data:image/png;base64,{{pngBase64}}" } ],
  "buffers": [ { "byteLength": {{bin.Length}} } ],
  "bufferViews": [
    { "buffer": 0, "byteOffset": 0, "byteLength": 48 },
    { "buffer": 0, "byteOffset": 48, "byteLength": 32 },
    { "buffer": 0, "byteOffset": 80, "byteLength": 12 }
  ],
  "accessors": [
    { "bufferView": 0, "componentType": 5126, "count": 4, "type": "VEC3", "min": [-1, 0, -1], "max": [1, 0, 1] },
    { "bufferView": 1, "componentType": 5126, "count": 4, "type": "VEC2" },
    { "bufferView": 2, "componentType": 5123, "count": 6, "type": "SCALAR" }
  ]
}
""";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            int total = 12 + 8 + jsonPadded + 8 + bin.Length;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(0x46546C67u);
            bw.Write(2u);
            bw.Write((uint)total);
            bw.Write((uint)jsonPadded);
            bw.Write(0x4E4F534Au);
            bw.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) bw.Write((byte)' ');
            bw.Write((uint)bin.Length);
            bw.Write(0x004E4942u);
            bw.Write(bin);
            return ms.ToArray();
        }
        static byte[] MakeTestGlb()
        {
            // A quad (two coplanar triangles sharing an edge) plus a separate triangle.
            var positions = new[]
            {
                -1f, 0f, -1f, 1f, 0f, -1f, 1f, 0f, 1f, -1f, 0f, 1f,
                -0.5f, 1f, 0f, 0.5f, 1f, 0f, 0f, 2f, 0f
            };
            var indices = new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 };
            var bin = new byte[104];
            Buffer.BlockCopy(positions, 0, bin, 0, positions.Length * sizeof(float));
            Buffer.BlockCopy(indices, 0, bin, positions.Length * sizeof(float), indices.Length * sizeof(ushort));
            string json = $$"""
{
  "asset": { "version": "2.0" },
  "scene": 0,
  "scenes": [ { "nodes": [0] } ],
  "nodes": [ { "mesh": 0 } ],
  "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1, "material": 0, "mode": 4 } ] } ],
  "materials": [ { "pbrMetallicRoughness": { "baseColorFactor": [0.8, 0.2, 0.2, 1.0] } } ],
  "buffers": [ { "byteLength": {{bin.Length}} } ],
  "bufferViews": [
    { "buffer": 0, "byteOffset": 0, "byteLength": 84 },
    { "buffer": 0, "byteOffset": 84, "byteLength": 18 }
  ],
  "accessors": [
    { "bufferView": 0, "componentType": 5126, "count": 7, "type": "VEC3", "min": [-1, 0, -1], "max": [1, 2, 1] },
    { "bufferView": 1, "componentType": 5123, "count": 9, "type": "SCALAR" }
  ]
}
""";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            int total = 12 + 8 + jsonPadded + 8 + bin.Length;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(0x46546C67u);
            bw.Write(2u);
            bw.Write((uint)total);
            bw.Write((uint)jsonPadded);
            bw.Write(0x4E4F534Au);
            bw.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) bw.Write((byte)' ');
            bw.Write((uint)bin.Length);
            bw.Write(0x004E4942u);
            bw.Write(bin);
            return ms.ToArray();
        }
        try
        {
            LoadLevel(GrabLevel.CreateDefault(), null);
            var code = new LevelNode { Kind = NodeKind.GASM, Name = "TestCode", Position = new Vec3(0, 1, 0) };
            _level.AddNode(code);
            code.Script = """
using Grab;

public class Test : CodeBlock
{
    GameObject floor = GameObject.Find("Floor");
    float t;

    void Start()
    {
        floor.Color = new Color(1, 0, 0);
        t = 0;
    }

    void Update()
    {
        t = t + Time.DeltaTime;
        floor.Position = new Vector3(0, 1 + Mathf.Sin(Time.Time * 90f) * 0.5f, 0);
        if (t > 2)
        {
            floor.Color = new Color(0, 1, 0);
        }
    }
}
""";
            var userScript = new LevelNode { Kind = NodeKind.GASM, Name = "UserScript", Position = new Vec3(0, 2, 0) };
            _level.AddNode(userScript);
            userScript.Script = """
using Grab;

public class MyScript : CodeBlock
{
    GameObject target = GameObject.Find("Floor");

    public float time = 0.0f;

    void Start()
    {
    }

    void Update()
    {
        time += Time.DeltaTime;
        target.Position.Y = Mathf.Sin(time * 10.0f);
    }
}
""";
            var (errors, warnings) = CompileAll();
            Check("C# compiles", errors == 0, $"{errors} error(s), {warnings} warning(s) " + string.Join(" | ", _problems.Select(p => p.Line + ": " + p.Message)));
            Check("user DeltaTime script compiles", userScript.Compiled?.Success == true);
            Check("user field 'time' is not the accumulator", userScript.Compiled?.Program.FindWorking("f_time") >= 0 && userScript.Compiled?.Program.FindWorking("time") < 0);
            Check("connections fit GRAB's 4-char names", userScript.Compiled?.Connections.Where(c => c.Type == GasmConnectionType.Node).All(c => c.Name.Length <= GrabLimits.MaxConnectionNameLength) == true,
                string.Join(", ", userScript.Compiled?.Connections.Select(c => c.Name + "=" + c.Name.Length) ?? Array.Empty<string>()));
            Check("connections are unique", userScript.Compiled?.Connections.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == userScript.Compiled?.Connections.Count);
            var timeInit = code.Compiled?.Program.Instructions.FirstOrDefault();
            int timeIndex = code.Compiled?.Program.FindWorking("__time") ?? -1;
            Check("Time.Time accumulator initialized", timeIndex >= 0 && timeInit?.Op == GasmOp.Set && timeInit.Operands.Count == 2 &&
                timeInit.Operands[0].Kind == GasmOperandKind.Working && timeInit.Operands[0].Index == timeIndex && timeInit.Operands[1].Value == 0);
            Check("GASM instruction limit respected", (code.Compiled?.Program.Instructions.Count ?? 0) <= GrabLimits.MaxInstructions,
                $"{code.Compiled?.Program.Instructions.Count} / {GrabLimits.MaxInstructions}");
            Check("GASM emitted", (code.Compiled?.Program.Instructions.Count ?? 0) > 10, $"{code.Compiled?.Program.Instructions.Count} instructions, {code.Compiled?.Program.WorkingRegisters.Count} registers");
            Check("GASM connections", (code.Compiled?.Connections.Count ?? 0) > 0, string.Join(", ", code.Compiled?.Connections.Select(c => c.Name) ?? Array.Empty<string>()));
            Check("disassembly text", (code.Compiled?.GasmText.Count ?? 0) > 5);

            var zone = new LevelNode { Kind = NodeKind.Trigger, Name = "Test Zone", Position = new Vec3(0, 1, 1), Scale = new Vec3(2, 2, 2) };
            _level.AddNode(zone);
            var lang = new LevelNode { Kind = NodeKind.GASM, Name = "LanguageTest", Position = new Vec3(0, 2, 0) };
            _level.AddNode(lang);
            lang.Script = """
using Grab;

public class LanguageTest : CodeBlock
{
    GameObject cube = GameObject.Find("Wood Cube");
    Trigger zone = GameObject.Find<Trigger>("Test Zone");
    float total;
    int count = 3;

    void Start()
    {
        total = 0;
    }

    void Update()
    {
        for (int i = 0; i < 4; i++)
        {
            total += i;
        }
        while (count > 0)
        {
            count--;
        }
        switch ((int)total)
        {
            case 0: total = 1; break;
            case 6: total = 2; break;
            default: total = 3; break;
        }
        float v = total > 2 ? 1 : 0;
        cube.Position = cube.Position + new Vector3(0, v * Time.DeltaTime, 0);
        Player.Vel.Y = 4;
        Level.Var1 = Level.Var1 + 1;
        if (zone.Active == 1)
        {
            cube.Color = Color.red;
        }
    }
}
""";
            var (langErrors, langWarnings) = CompileAll();
            var langProblems = _problems.Where(p => p.Node == lang).Select(p => p.Line + ": " + p.Message).ToList();
            Check("C# language coverage compiles", langProblems.Count == 0, $"{langErrors} error(s), {langWarnings} warning(s) " + string.Join(" | ", langProblems));

            var floor = _level.FindByName("Floor");
            float beforeY = floor?.Position.Y ?? 0;
            var play = new PlaySession(_level);
            for (int i = 0; i < 180; i++) play.Step(1f / 60f);
            Check("runtime runs programs", play.ProgramCount >= 1, $"{play.ProgramCount} program(s)");
            Check("runtime drives the scene", floor != null && Math.Abs(floor.Position.Y - beforeY) > 0.001f, $"Y {beforeY:0.###} → {floor?.Position.Y:0.###}");
            Check("player simulated", play.PlayerPosition.Y > 0.1f, play.PlayerPosition.ToString());
            Check("no runtime errors", play.Messages.Count == 0, string.Join(" | ", play.Messages));

            // ---- play-mode collision, jump and wall blocking ----
            var physLevel = GrabLevel.CreateDefault();
            physLevel.AddNode(new LevelNode
            {
                Name = "Test Wall", Kind = NodeKind.Static, Shape = ShapeInfo.Cube,
                Material = 8, Position = new Vec3(2, 1.5f, 4), Scale = new Vec3(1, 3, 4)
            });
            var phys = new PlaySession(physLevel);
            for (int i = 0; i < 90; i++) phys.Step(1f / 60f);
            float restY = phys.PlayerPosition.Y;
            Check("player lands on block collision instead of the grid",
                phys.Grounded && Math.Abs(restY - 0.85f) < 0.05f, $"Y={restY:0.###} grounded={phys.Grounded}");
            phys.Jump();
            phys.Step(1f / 60f);
            Check("jump leaves the ground", !phys.Grounded && phys.PlayerPosition.Y > restY + 0.02f,
                $"Y={phys.PlayerPosition.Y:0.###}");
            for (int i = 0; i < 90; i++) phys.Step(1f / 60f);
            phys.DesiredVelocity = new Vec3(5.5f, 0, 0);
            for (int i = 0; i < 120; i++) phys.Step(1f / 60f);
            Check("wall stops the player", phys.PlayerPosition.X < 1.6f && phys.PlayerPosition.X > 0.5f,
                $"X={phys.PlayerPosition.X:0.###}");

            // ---- block brush math and placement ----
            var brushLine = BlockBrushMath.Line(new VoxelCell(0, 0, 0), new VoxelCell(4, 0, 0));
            Check("brush line reaches both endpoints", brushLine.Cells.Count == 5 &&
                brushLine.Cells[0] == new VoxelCell(0, 0, 0) && brushLine.Cells[^1] == new VoxelCell(4, 0, 0),
                $"{brushLine.Cells.Count} cells");
            var brushBox = BlockBrushMath.Box(new VoxelCell(0, 0, 0), new VoxelCell(1, 0, 1), 2, false);
            Check("brush box fills its volume", brushBox.Cells.Count == 8, $"{brushBox.Cells.Count} cells");
            var brushShell = BlockBrushMath.Box(new VoxelCell(0, 0, 0), new VoxelCell(2, 0, 2), 3, true);
            Check("brush hollow box keeps only the shell", brushShell.Cells.Count == 26, $"{brushShell.Cells.Count} cells");
            var brushSphere = BlockBrushMath.Sphere(new VoxelCell(0, 2, 0), 2, false);
            var brushSphereShell = BlockBrushMath.Sphere(new VoxelCell(0, 2, 0), 2, true);
            Check("brush sphere and shell are generated", brushSphere.Cells.Count > brushSphereShell.Cells.Count &&
                brushSphereShell.Cells.Count > 0, $"{brushSphere.Cells.Count} / {brushSphereShell.Cells.Count} cells");
            var brushOcc = BlockBrushMath.Occupancy(GrabLevel.CreateDefault(), 1);
            Check("brush occupancy uses the real block layer", brushOcc.ContainsKey(new VoxelCell(0, -1, 0)) &&
                !brushOcc.ContainsKey(new VoxelCell(0, 0, 0)), $"{brushOcc.Count} occupied cells");
            var bigFloorLevel = new GrabLevel();
            bigFloorLevel.AddNode(new LevelNode
            {
                Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                Position = new Vec3(0, -0.5f, 0), Scale = new Vec3(80, 1, 80)
            });
            var bigFloorOcc = BlockBrushMath.Occupancy(bigFloorLevel, 1);
            Check("brush occupancy covers large floors", bigFloorOcc.Count > 6000 &&
                bigFloorOcc.ContainsKey(new VoxelCell(-40, -1, -40)) && bigFloorOcc.ContainsKey(new VoxelCell(40, -1, 40)),
                $"{bigFloorOcc.Count} occupied cells");
            var brushTestLevel = GrabLevel.CreateDefault();
            var brushScene = new SceneView();
            brushScene.SetLevel(brushTestLevel, frame: false);
            brushScene.BrushMode = BlockBrushMode.Box;
            brushScene.BrushCellSize = 1;
            brushScene.BrushLayers = 2;
            var brushPlaced = brushScene.ApplyBrushForAutomation(new VoxelCell(4, 1, 4), new VoxelCell(5, 1, 5));
            Check("brush places blocks on empty cells", brushPlaced.Count == 8 && brushPlaced.Created,
                $"{brushPlaced.Count} created");
            Check("brush blocks are named and sit on the grid",
                brushTestLevel.AllNodes().Count(n => n.Kind == NodeKind.Static && n.Name.StartsWith("Block", StringComparison.Ordinal)) >= 8,
                $"{brushTestLevel.AllNodes().Count()} total objects");

            brushScene.BrushMode = BlockBrushMode.Line;
            var brushLinePlacement = brushScene.ApplyBrushForAutomation(new VoxelCell(10, 1, 10), new VoxelCell(13, 1, 10));
            Check("brush line mode places the whole run", brushLinePlacement.Count == 4,
                $"{brushLinePlacement.Count} blocks");

            brushScene.BrushMode = BlockBrushMode.Sphere;
            brushScene.BrushHollow = false;
            var brushSpherePlacement = brushScene.ApplyBrushForAutomation(new VoxelCell(20, 5, 20), new VoxelCell(21, 5, 20));
            Check("brush sphere mode places a solid ball", brushSpherePlacement.Count == 7,
                $"{brushSpherePlacement.Count} blocks");
            var mirroredLine = BlockBrushMath.Mirror(brushLine, BlockSymmetry.X, Vec3.Zero, 1);
            Check("brush mirror duplicates cells across X", mirroredLine.Cells.Count == 9 &&
                mirroredLine.Cells.Contains(new VoxelCell(-4, 0, 0)) && mirroredLine.Cells.Contains(new VoxelCell(4, 0, 0)),
                $"{mirroredLine.Cells.Count} cells");
            var bothPlan = new BrushPlan();
            bothPlan.Cells.Add(new VoxelCell(2, 1, 3));
            var bothMirrored = BlockBrushMath.Mirror(bothPlan, BlockSymmetry.Both, Vec3.Zero, 1);
            Check("brush mirror supports X, Z and both at once", bothMirrored.Cells.Count == 4 &&
                bothMirrored.Cells.Contains(new VoxelCell(-2, 1, -3)), $"{bothMirrored.Cells.Count} cells");

            // ---- structure studio, silhouette extrusion and quick stairs ----
            var structureSettings = new StructureSettings
            {
                Origin = new Vec3(0, 0, 0), Width = 4, Length = 6, Height = 3, Steps = 8,
                Cell = 0.5f, Depth = 2, Material = 8, Railings = false
            };
            var quickStairs = StructureBuilder.Build(StructureKind.Stairs, structureSettings);
            Check("quick stairs build real blocks", quickStairs.Nodes.Count >= 8 &&
                quickStairs.Nodes.All(n => n.Kind == NodeKind.Static), $"{quickStairs.Nodes.Count} blocks");
            var quickRamp = StructureBuilder.Build(StructureKind.Ramp, structureSettings);
            Check("quick ramp builds a rotated slab", quickRamp.Nodes.Count >= 1 &&
                quickRamp.Nodes.Any(n => Math.Abs(n.Rotation.W) < 0.999f), $"{quickRamp.Nodes.Count} blocks");
            var outline = new List<(float X, float Z)> { (0, 0), (3, 0), (3, 2), (0, 2) };
            var extruded = StructureBuilder.ExtrudePolygon(outline, structureSettings);
            Check("silhouette extrusion fills and extrudes an outline", extruded.Count > 12 &&
                extruded.Max(n => n.Position.Y) > 0.5f, $"{extruded.Count} blocks");

            // ---- terrain erosion brush and multi-point road ----
            var erodeHeights = new float[9, 9];
            erodeHeights[4, 4] = 1f;
            TerrainBuilder.Erode(erodeHeights, 6, 0.25f);
            Check("terrain erosion moves material downhill", erodeHeights[4, 4] < 0.95f &&
                erodeHeights[4, 4] > 0f, $"peak {erodeHeights[4, 4]:0.###}");
            var roadSettings = new TerrainSettings
            {
                Width = 12, Depth = 12, CellSize = 1, HeightScale = 2, BaseHeight = 0,
                PathPoints = new List<Vec3> { new(-4, 0, -4), new(0, 0, 0), new(4, 0, 4) },
                PathWidth = 2, PathFlatten = true, ReplaceExisting = true
            };
            var roadLevel = new GrabLevel();
            var roadResult = TerrainBuilder.Build(roadLevel, roadSettings);
            Check("terrain road carves a multi-point polyline", roadResult.PathBlocks > 0,
                $"{roadResult.PathBlocks} path cell(s)");

            // Painted paths are the easy workflow: no typed coordinates, just a mask.
            var paintedPath = new bool[12, 12];
            for (int x = 2; x <= 9; x++) paintedPath[x, 6] = true;
            roadSettings.PathPoints.Clear();
            var paintedRoad = TerrainBuilder.Build(new GrabLevel(), roadSettings, null, null, paintedPath);
            Check("painted terrain paths carve without coordinates", paintedRoad.PathBlocks == 8,
                $"{paintedRoad.PathBlocks} painted path cell(s)");

            // ---- 3D terrain shaper and foundation ----
            var sculptLevel = new GrabLevel();
            var sculptGroup = new LevelNode { Kind = NodeKind.Group, Name = "Terrain", EditorLabel = "terrain" };
            sculptLevel.AddNode(sculptGroup);
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                {
                    var column = new LevelNode
                    {
                        Kind = NodeKind.Static, Name = "Terrain " + x + "," + z,
                        Shape = ShapeInfo.Cube, Material = 8, Color1 = new Col(0.33f, 0.55f, 0.28f, 1f),
                        Position = new Vec3(x, 2, z), Scale = new Vec3(1, 4, 1)
                    };
                    sculptGroup.Children.Add(column);
                    column.RebuildParents(sculptGroup);
                }
            sculptLevel.EnsureNames();
            int sculptBefore = sculptLevel.AllNodes().Count(n => n.EditorLabel == "terrain");
            var terrainAdd = TerrainSculptor.Apply(sculptLevel, new Vec3(0, 5, 0), new TerrainSculptSettings
            {
                Mode = TerrainSculptMode.Add, Volume = TerrainBrushVolume.Sphere, Radius = 2,
                Material = 8, Color = new Col(0.33f, 0.55f, 0.28f, 1f)
            });
            int sculptAfterAdd = sculptLevel.AllNodes().Count(n => n.EditorLabel == "terrain");
            Check("terrain shaper adds a solid 3D brush volume", terrainAdd.Added > 0 && sculptAfterAdd >= sculptBefore,
                $"{terrainAdd.Added} cells, {sculptAfterAdd} blocks");
            var terrainRemove = TerrainSculptor.Apply(sculptLevel, new Vec3(0, 6, 0), new TerrainSculptSettings
            {
                Mode = TerrainSculptMode.Remove, Volume = TerrainBrushVolume.Sphere, Radius = 2
            });
            Check("terrain shaper subtracts a rounded volume", terrainRemove.Removed > 0,
                $"{terrainRemove.Removed} cells, {terrainRemove.Columns} columns");
            var terrainPaint = TerrainSculptor.Apply(sculptLevel, new Vec3(0, 3, 0), new TerrainSculptSettings
            {
                Mode = TerrainSculptMode.Paint, Volume = TerrainBrushVolume.Box, Radius = 1,
                Material = 10, Color = new Col(0.9f, 0.9f, 0.95f, 1f)
            });
            Check("terrain shaper paints a material volume", terrainPaint.Changed > 0,
                $"{terrainPaint.Changed} cells");
            var fineTerrain = new GrabLevel();
            var fineAdd = TerrainSculptor.Apply(fineTerrain, new Vec3(0, 1, 0), new TerrainSculptSettings
            {
                Mode = TerrainSculptMode.Add, Volume = TerrainBrushVolume.Sphere, Radius = 2,
                Material = 8, Color = new Col(0.33f, 0.55f, 0.28f, 1f), CellSize = 0.5
            });
            Check("terrain shaper honours sub-metre grid sizes", fineAdd.Added > 0 &&
                fineTerrain.AllNodes().Where(n => n.Kind == NodeKind.Static)
                    .Any(n => Math.Abs(Math.Abs(n.Scale.X) - 0.5) < 0.01),
                $"{fineAdd.Added} cells at 0.5 m");

            var foundationLevel = new GrabLevel();
            var foundationGroup = new LevelNode { Kind = NodeKind.Group, Name = "Terrain", EditorLabel = "terrain" };
            foundationLevel.AddNode(foundationGroup);
            var foundationColumn = new LevelNode
            {
                Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                Position = new Vec3(0, 2, 0), Scale = new Vec3(1, 4, 1)
            };
            foundationGroup.Children.Add(foundationColumn);
            foundationColumn.RebuildParents(foundationGroup);
            foundationLevel.EnsureNames();
            var foundationBuilding = new LevelNode
            {
                Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                Position = new Vec3(0, 6, 0), Scale = new Vec3(2, 2, 2)
            };
            var foundationResult = TerrainSculptor.PrepareFoundation(foundationLevel,
                new[] { foundationBuilding }, TerrainFoundationMode.LevelGround, 2);
            var foundationTop = foundationLevel.AllNodes()
                .Where(n => n.EditorLabel == "terrain" && n.Kind == NodeKind.Static)
                .Select(n => n.WorldPosition.Y + Math.Abs(n.WorldScale.Y) * 0.5f)
                .DefaultIfEmpty(0).Max();
            Check("terrain-aware foundation levels ground under a structure",
                foundationResult.Columns > 0 && Math.Abs(foundationTop - 5f) < 0.01f,
                $"top {foundationTop:0.##}, {foundationResult.Summary}");

            // ---- mixed-size and duplicate optimisation ----
            var optLevel = new GrabLevel();
            LevelNode OptBlock(double x, double y, double z, double sx, double sy, double sz)
            {
                var node = new LevelNode
                {
                    Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                    Position = new Vec3((float)x, (float)y, (float)z),
                    Scale = new Vec3((float)sx, (float)sy, (float)sz)
                };
                optLevel.AddNode(node);
                return node;
            }
            OptBlock(0.5, 0.5, 0.5, 1, 1, 1);
            OptBlock(0.5, 2, 0.5, 1, 2, 1);
            OptBlock(0.5, 2, 0.5, 1, 2, 1);
            optLevel.EnsureNames();
            int optBefore = optLevel.AllNodes().Count(n => n.Kind == NodeKind.Static);
            var optResult = LevelOptimizer.Optimize(optLevel, new LevelOptimizeOptions
            {
                MergeAdjacentCubes = false, MergeMixedSizes = true, MixedSizeGrid = 1,
                CullHiddenBlocks = false, RemoveDuplicateBlocks = true, RemoveEmptyGroups = false
            });
            int optAfter = optLevel.AllNodes().Count(n => n.Kind == NodeKind.Static);
            Check("optimizer removes exact duplicates before merging",
                optResult.DuplicateBlocks == 1 && optAfter < optBefore,
                $"duplicates {optResult.DuplicateBlocks}, {optBefore} → {optAfter}");
            Check("optimizer merges different block heights on the grid",
                optResult.MergedBlocks > 0 && optAfter == 1,
                $"merged {optResult.MergedBlocks}, {optAfter} block(s) left");
            var degenerateLevel = new GrabLevel();
            degenerateLevel.AddNode(new LevelNode
            {
                Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                Position = Vec3.Zero, Scale = new Vec3(0, 1, 1)
            });
            var degenerateResult = LevelOptimizer.Optimize(degenerateLevel, new LevelOptimizeOptions
            {
                MergeAdjacentCubes = false, MergeMixedSizes = false, CullHiddenBlocks = false,
                RemoveDuplicateBlocks = false, RemoveDegenerateBlocks = true, RemoveEmptyGroups = false
            });
            Check("optimizer removes invisible zero-size blocks",
                degenerateResult.DegenerateBlocks == 1 &&
                degenerateLevel.AllNodes().Count(n => n.Kind == NodeKind.Static) == 0,
                $"{degenerateResult.DegenerateBlocks} removed");

            // The Draw tool plans and commits real blocks through the same path as the UI.
            var drawLevel = new GrabLevel();
            var drawScene = new SceneView();
            drawScene.SetLevel(drawLevel, frame: false);
            var drawnBox = drawScene.DrawShapeForAutomation(new VoxelCell(-2, 0, -2), new VoxelCell(0, 0, 0), BuildShape.Box, 3);
            Check("draw tool commits a 3D box footprint", drawnBox.Created && drawnBox.Count == 27,
                $"{drawnBox.Count} blocks");
            var drawnStairs = drawScene.DrawShapeForAutomation(new VoxelCell(3, 0, 0), new VoxelCell(6, 0, 0), BuildShape.Stairs, 4);
            Check("draw tool builds stairs from a dragged footprint", drawnStairs.Created && drawnStairs.Count > 6,
                $"{drawnStairs.Count} blocks");

            var eraseLevel = new GrabLevel();
            LevelNode EraseBlock(VoxelCell cell)
            {
                var block = new LevelNode
                {
                    Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                    Position = BlockBrushMath.Center(cell, 1), Scale = Vec3.One, Color1 = Col.Gray
                };
                eraseLevel.AddNode(block);
                return block;
            }
            EraseBlock(new VoxelCell(6, 1, 6));
            EraseBlock(new VoxelCell(-6, 1, 6));
            EraseBlock(new VoxelCell(6, 1, 7));
            eraseLevel.EnsureNames();
            var eraseScene = new SceneView();
            eraseScene.SetLevel(eraseLevel, frame: false);
            eraseScene.BrushCellSize = 1;
            eraseScene.BrushSymmetry = BlockSymmetry.X;
            var erased = eraseScene.EraseForAutomation(new[] { new VoxelCell(6, 1, 6) });
            Check("brush erase removes the clicked block and its mirrored twin", erased.Count == 2 &&
                eraseLevel.AllNodes().Count(n => n.Kind == NodeKind.Static) == 1,
                $"{erased.Count} erased, {eraseLevel.AllNodes().Count(n => n.Kind == NodeKind.Static)} left");

            // Fill/Replace operate on real nodes: a same-material island is painted,
            // and a neighbouring different material stops the flood fill.
            var paintLevel = GrabLevel.CreateDefault();
            LevelNode PaintBlock(VoxelCell cell, int material)
            {
                var block = new LevelNode
                {
                    Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = material,
                    Position = BlockBrushMath.Center(cell, 1), Scale = Vec3.One, Color1 = Col.Gray
                };
                paintLevel.AddNode(block);
                return block;
            }
            var paintA = PaintBlock(new VoxelCell(10, 6, 10), 8);
            var paintB = PaintBlock(new VoxelCell(11, 6, 10), 8);
            var paintC = PaintBlock(new VoxelCell(11, 6, 11), 8);
            var paintWood = PaintBlock(new VoxelCell(12, 6, 10), 4);
            paintLevel.EnsureNames();
            var paintScene = new SceneView();
            paintScene.SetLevel(paintLevel, frame: false);
            paintScene.BrushCellSize = 1;
            paintScene.BrushMode = BlockBrushMode.Fill;
            paintScene.BrushMaterial = 2;
            var fillResult = paintScene.ApplyMaterialBrushForAutomation(new VoxelCell(10, 6, 10));
            Check("brush fill paints the connected material island only",
                fillResult.Count == 3 && paintA.Material == 2 && paintB.Material == 2 && paintC.Material == 2 && paintWood.Material == 4,
                $"{fillResult.Count} painted, wood={paintWood.Material}");

            paintScene.BrushMode = BlockBrushMode.Replace;
            paintScene.BrushMaterial = 4;
            var replaceResult = paintScene.ApplyMaterialBrushForAutomation(new VoxelCell(10, 6, 10));
            Check("brush replace repaints one clicked block",
                replaceResult.Count == 1 && paintA.Material == 4 && paintB.Material == 2,
                $"{replaceResult.Count} replaced, A={paintA.Material} B={paintB.Material}");

            // ---- arrays, string methods and Write on any object ----
            var arrLevel = GrabLevel.CreateDefault();
            var arrSign = new LevelNode { Kind = NodeKind.Sign, Name = "Sign 1", Position = new Vec3(2, 1, 0) };
            arrLevel.AddNode(arrSign);
            var constSign = new LevelNode { Kind = NodeKind.Sign, Name = "Sign 2", Position = new Vec3(3, 1, 0) };
            arrLevel.AddNode(constSign);
            var arrNode = new LevelNode { Kind = NodeKind.GASM, Name = "ArrayTest", Position = new Vec3(0, 1, 0) };
            arrLevel.AddNode(arrNode);
            arrLevel.EnsureNames();
            arrNode.Script = """
using Grab;

public class ArrayTest : CodeBlock
{
    GameObject block = GameObject.Find("Floor");
    GameObject sign = GameObject.Find<Sign>("Sign 1");
    GameObject sign2 = GameObject.Find<Sign>("Sign 2");
    float[] heights = new float[] { 1, 2, 3, 4 };
    string tag = "  grab ";

    void Start()
    {
        block.Write = tag.Trim().ToUpper() + " #" + "abcdef".Length;
        sign.Write = 72;
        sign2.Write = "HI";
        sign2.Write = 72;
    }

    void Update()
    {
        float[] extra = new float[2];
        extra[0] = 5;
        extra[1] = 1;
        float total = 0;
        for (int i = 0; i < heights.Length; i++)
        {
            total += heights[i];
        }
        total = total + extra[0] + extra[1];
        block.Position.Y = total;
        sign.Control = 1;
        sign.Write = string.Concat("Time: ", Time.Time);
    }
}
""";
            var arrResult = new CSharpCompiler().Compile(arrLevel, arrNode, arrNode.Script);
            arrNode.Compiled = arrResult;
            var arrErrors = arrResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList();
            Check("arrays and strings compile", arrErrors.Count == 0, string.Join(" | ", arrErrors));
            var arrTarget = arrLevel.FindByName("Floor");
            Check("Write works on blocks", arrTarget?.EditorLabel == "GRAB #6", arrTarget?.EditorLabel ?? "missing");
            Check("sign.Write accepts text and numbers", constSign.Text == "HI", constSign.Text);
            var arrPlay = new PlaySession(arrLevel);
            for (int i = 0; i < 90; i++) arrPlay.Step(1f / 60f);
            Check("array indexing and loops run", arrTarget != null && Math.Abs(arrTarget.Position.Y - 16f) < 0.01f,
                arrTarget?.Position.Y.ToString("0.###") ?? "missing");
            Check("array program has no runtime errors", arrPlay.Messages.Count == 0, string.Join(" | ", arrPlay.Messages));
            string liveText = arrSign.Text;
            for (int i = 0; i < 30; i++) arrPlay.Step(1f / 60f);
            Check("live sign text (string.Concat + Time.Time)",
                liveText.StartsWith("Time: ", StringComparison.Ordinal) && liveText.Length > 6 && liveText != arrSign.Text,
                liveText + " -> " + arrSign.Text);

            // ---- Console ----
            var conLevel = GrabLevel.CreateDefault();
            var conNode = new LevelNode { Kind = NodeKind.GASM, Name = "ConsoleTest", Position = new Vec3(0, 1, 0) };
            conLevel.AddNode(conNode);
            conNode.Script = """
using Grab;

public class ConsoleTest : CodeBlock
{
    GameObject floor = GameObject.Find("Floor");

    void Start()
    {
        Console.WriteLine("Ready");
        Console.Write("Value: ");
        Console.WriteLine('A');
    }

    void Update()
    {
        Console.WriteLine("Time: {0} y: {1}", Time.Time, floor.Position.Y);
    }
}
""";
            var conResult = new CSharpCompiler().Compile(conLevel, conNode, conNode.Script);
            conNode.Compiled = conResult;
            var conErrors = conResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList();
            Check("Console.Write/WriteLine compile", conErrors.Count == 0, string.Join(" | ", conErrors));
            var conSign = conLevel.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Sign && n.Generated);
            Check("console output creates a display sign", conSign != null, conSign?.Name ?? "missing");
            var conPlay = new PlaySession(conLevel);
            for (int i = 0; i < 10; i++) conPlay.Step(1f / 60f);
            Check("Console output reaches the engine console",
                conPlay.Messages.Contains("Ready") && conPlay.Messages.Contains("Value: A") &&
                conPlay.Messages.Any(m => m.StartsWith("Time: ", StringComparison.Ordinal)),
                string.Join(" | ", conPlay.Messages.Take(6)));
            Check("console line is visible on its sign",
                conSign != null && conSign.Text.Contains("Time: ", StringComparison.Ordinal),
                conSign?.Text ?? "missing");
            var conRound = LevelIO.Read(LevelIO.Write(conLevel));
            var conRoundPlay = new PlaySession(conRound);
            for (int i = 0; i < 10; i++) conRoundPlay.Step(1f / 60f);
            Check("console output survives .level export",
                conRoundPlay.Messages.Any(m => m.StartsWith("Time: ", StringComparison.Ordinal)),
                string.Join(" | ", conRoundPlay.Messages.Take(4)));
            var readErr = new CSharpCompiler().Compile(conLevel, conNode,
                "using Grab;\npublic class C : CodeBlock\n{\n    void Update() { Console.ReadLine(); }\n}\n");
            Check("Console.ReadLine is rejected with a clear error",
                readErr.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("keyboard input")),
                string.Join(" | ", readErr.Diagnostics.Select(d => d.Message)));

            // ---- scene objects created by C# ----
            var spawnLevel = GrabLevel.CreateDefault();
            var spawnNode = new LevelNode { Kind = NodeKind.GASM, Name = "SpawnTest", Position = new Vec3(0, 1, 0) };
            spawnLevel.AddNode(spawnNode);
            spawnLevel.EnsureNames();
            spawnNode.Script = """
using Grab;

public class SpawnTest : CodeBlock
{
    Sign sign = new Sign("Hi") { Position = new Vector3(3, 2, 1), Color = Color.white };
    Block sphere = new Block(Shape.Sphere, Material.Wood) { Position = new Vector3(4, 2, 1), Scale = new Vector3(2, 2, 2), Physics = true };
    Light lamp = new Light(Color.red, 5, 9) { Position = new Vector3(5, 2, 1) };
    Trigger zone = new Trigger(new Vector3(3, 3, 3)) { Position = new Vector3(6, 2, 1) };
    ParticleEmitter sparks = new ParticleEmitter { Position = new Vector3(7, 2, 1), Rate = 50, StartColor = Color.yellow, Velocity = new Vector3(0, 3, 0) };
    Sound hum = new Sound("hum") { Position = new Vector3(8, 2, 1), Volume = 0.5f, Repeat = true };
    float cells = Voxel.Grid(new Vector3(0, 10, 0), 2, 2, 2, 1.5f, Shape.Cube, Material.DefaultColored);
    GameObject floor = GameObject.Find("Floor");

    void Start()
    {
        sign.Write = $"Spawn {8} {nameof(SpawnTest)}";
        sphere.Position.Y = 3;
        lamp.Brightness = 6;
        floor.Position.X = Vector3.MoveTowards(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 2).X;
        floor.Position.Z = Random.Range(2, 3);
        Console.WriteLine(string.Format("cells={0} atan={1}", cells, Mathf.Atan(1)));
        Debug.Log("debug {0}", Time.DeltaTime);
    }
}
""";
            var spawnResult = new CSharpCompiler().Compile(spawnLevel, spawnNode, spawnNode.Script);
            spawnNode.Compiled = spawnResult;
            var spawnErrors = spawnResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"L{d.Line}: {d.Message}").ToList();
            Check("new scene objects compile", spawnErrors.Count == 0, string.Join(" | ", spawnErrors));
            var createdSign = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Sign);
            var createdBlock = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Sphere);
            var createdLight = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Light);
            var createdTrigger = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Trigger);
            var createdEmitter = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.ParticleEmitter);
            var createdSound = spawnLevel.AllNodes().FirstOrDefault(n => n.ScriptCreated && n.Kind == NodeKind.Sound);
            int voxelCount = spawnLevel.AllNodes().Count(n => n.ScriptCreated && n.Kind == NodeKind.Static && n.Position.Y >= 9.99f);
            Check("new creates every node kind", createdSign != null && createdBlock != null && createdLight != null && createdTrigger != null && createdEmitter != null && createdSound != null,
                string.Join(", ", spawnLevel.AllNodes().Where(n => n.ScriptCreated).Select(n => n.Kind.ToString())));
            Check("object initializers set transforms and components",
                createdBlock != null && createdBlock.Material == 4 && createdBlock.PhysicsObject && Math.Abs(createdBlock.Scale.X - 2) < 0.01f &&
                createdLight != null && Math.Abs(createdLight.LightIntensity - 5) < 0.01f && Math.Abs(createdLight.LightRange - 9) < 0.01f &&
                createdTrigger != null && Math.Abs(createdTrigger.Scale.X - 3) < 0.01f &&
                createdEmitter != null && createdEmitter.ParticlesPerSecond == 50 && Math.Abs(createdEmitter.Velocity.Y - 3) < 0.01f &&
                createdSound != null && createdSound.SoundName == "hum" && Math.Abs(createdSound.SoundVolume - 0.5f) < 0.01f && createdSound.SoundRepeat,
                $"block={createdBlock?.Material}/{createdBlock?.Scale} light={createdLight?.LightIntensity} trigger={createdTrigger?.Scale} emitter={createdEmitter?.ParticlesPerSecond} sound={createdSound?.SoundName}");
            Check("Voxel.Grid creates the requested cells", voxelCount == 8, voxelCount + " cells");
            Check("interpolation, nameof, string.Format and Debug compile",
                createdSign?.Text == "Spawn 8 SpawnTest", createdSign?.Text ?? "missing");
            int spawnObjectCount = spawnLevel.AllNodes().Count(n => n.ScriptCreated);
            var spawnAgain = new CSharpCompiler().Compile(spawnLevel, spawnNode, spawnNode.Script);
            Check("recompiling does not duplicate created objects", spawnAgain.Success &&
                spawnLevel.AllNodes().Count(n => n.ScriptCreated) == spawnObjectCount, $"{spawnObjectCount} then {spawnLevel.AllNodes().Count(n => n.ScriptCreated)}");
            var spawnPlay = new PlaySession(spawnLevel);
            for (int i = 0; i < 5; i++) spawnPlay.Step(1f / 60f);
            Check("created objects are wired into the running GASM",
                createdBlock != null && Math.Abs(createdBlock.Position.Y - 3) < 0.01f &&
                createdLight != null && Math.Abs(createdLight.LightIntensity - 6) < 0.01f,
                $"blockY={createdBlock?.Position.Y} light={createdLight?.LightIntensity}");
            var spawnFloor = spawnLevel.FindByName("Floor");
            Check("Random, Mathf.Atan and Vector3.MoveTowards run",
                spawnFloor != null && Math.Abs(spawnFloor.Position.X - 2) < 0.01f && spawnFloor.Position.Z >= 2 && spawnFloor.Position.Z < 3,
                $"x={spawnFloor?.Position.X} z={spawnFloor?.Position.Z}");
            Check("Debug and string.Format reach the Console",
                spawnPlay.Messages.Any(m => m.StartsWith("cells=8 atan=", StringComparison.Ordinal)) &&
                spawnPlay.Messages.Any(m => m.StartsWith("debug ", StringComparison.Ordinal)),
                string.Join(" | ", spawnPlay.Messages.Take(6)));
            var spawnRound = LevelIO.Read(LevelIO.Write(spawnLevel));
            Check("created objects survive .level export",
                spawnRound.AllNodes().Count(n => n.Kind == NodeKind.Static && n.Position.Y >= 9.99f) == 8 &&
                spawnRound.AllNodes().Any(n => n.Kind == NodeKind.Sign && n.Text == "Spawn 8 SpawnTest"),
                $"{spawnRound.AllNodes().Count()} nodes");

            var pruneLevel = GrabLevel.CreateDefault();
            var pruneNode = new LevelNode { Kind = NodeKind.GASM, Name = "PruneTest", Position = new Vec3(0, 1, 0) };
            pruneLevel.AddNode(pruneNode);
            pruneNode.Script = "using Grab;\npublic class P : CodeBlock\n{\n    Sign temp = new Sign();\n    void Start() { }\n}\n";
            new CSharpCompiler().Compile(pruneLevel, pruneNode, pruneNode.Script);
            int withTemp = pruneLevel.AllNodes().Count(n => n.ScriptCreated);
            pruneNode.Script = "using Grab;\npublic class P : CodeBlock\n{\n    void Start() { }\n}\n";
            var pruned = new CSharpCompiler().Compile(pruneLevel, pruneNode, pruneNode.Script);
            Check("removing a new expression removes its object", pruned.Success && withTemp == 1 &&
                pruneLevel.AllNodes().Count(n => n.ScriptCreated) == 0, $"{withTemp} then {pruneLevel.AllNodes().Count(n => n.ScriptCreated)}");

            // ---- C# language expansion ----
            RunLanguageTests(sb, Check);

            // ---- editor intelligence ----
            var signSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    GameObject sign = GameObject.Find<Sign>(\"Sign 1\");\n    void Update()\n    {\n        sign.");
            Check("suggestions know Sign members",
                signSuggestions.Contains("Write") && signSuggestions.Contains("Control") && signSuggestions.Contains("Select") && !signSuggestions.Contains("Normalized"),
                string.Join(", ", signSuggestions.Take(8)));
            var consoleSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        Console.");
            Check("suggestions know Console members",
                consoleSuggestions.Contains("WriteLine") && consoleSuggestions.Contains("Write"),
                string.Join(", ", consoleSuggestions.Take(8)));
            var vectorSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        Vector3 v = new Vector3(1, 2, 3);\n        v.");
            Check("suggestions know Vector3 members",
                vectorSuggestions.Contains("Normalized") && vectorSuggestions.Contains("Length") && !vectorSuggestions.Contains("Control"),
                string.Join(", ", vectorSuggestions.Take(8)));
            var newSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        new ");
            Check("suggestions list creatable scene objects after new",
                newSuggestions.Contains("Sign") && newSuggestions.Contains("ParticleEmitter") && newSuggestions.Contains("Cube"),
                string.Join(", ", newSuggestions.Take(8)));
            var objectSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    GameObject b = GameObject.Find(\"Floor\");\n    void Update()\n    {\n        b.");
            Check("suggestions know object helper methods",
                objectSuggestions.Contains("SetPosition") && objectSuggestions.Contains("Translate") && objectSuggestions.Contains("Rotate"),
                string.Join(", ", objectSuggestions.Take(8)));
            var staticSuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        GameObject.");
            Check("suggestions know static GameObject members",
                staticSuggestions.Contains("Find"), string.Join(", ", staticSuggestions.Take(8)));
            var partialStatic = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        GameObject.F");
            Check("suggestions filter static members after typing a prefix",
                partialStatic.Contains("Find"), string.Join(", ", partialStatic.Take(8)));
            var arraySuggestions = CodeEditor.SuggestionsForTest("using Grab;\npublic class T : CodeBlock\n{\n    void Update()\n    {\n        Array.");
            Check("suggestions know Array members",
                arraySuggestions.Contains("IndexOf") && arraySuggestions.Contains("Reverse"),
                string.Join(", ", arraySuggestions.Take(8)));
            CodeEditor.SetScript(code.Script);

            // ---- animation sampler + release optimiser ----
            var sampleClip = new GrabAnimation
            {
                Name = "sample",
                Frames =
                {
                    new GrabAnimationFrame { Time = 0, Position = new Vec3(0, 0, 0), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 1, Position = new Vec3(0, 10, 0), Rotation = Quat.Identity }
                }
            };
            var sampled = AnimationSampler.Sample(sampleClip, 0.5f);
            Check("animation sampler interpolates keys", Math.Abs(sampled.Position.Y - 5f) < 0.01f, sampled.Position.ToString());
            Check("animation duration is the last key time", Math.Abs(AnimationSampler.Duration(sampleClip) - 1f) < 0.001f, AnimationSampler.Duration(sampleClip).ToString("0.###"));
            var reversed = AnimationSampler.Reversed(sampleClip, "back");
            Check("animation reverse keeps duration", Math.Abs(AnimationSampler.Duration(reversed) - 1f) < 0.001f && Math.Abs(reversed.Frames[0].Position.Y - 10f) < 0.01f, reversed.Frames[0].Position.ToString());
            var exportClip = new GrabAnimation
            {
                Frames =
                {
                    new GrabAnimationFrame { Time = 1.5f, Position = new Vec3(0, 2, 0), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 0.5f, Position = new Vec3(0, 1, 0), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 0.5f, Position = new Vec3(0, 3, 0), Rotation = Quat.Identity }
                }
            };
            bool exportClipChanged = AnimationSampler.SortAndDeduplicate(exportClip);
            Check("release animation cleanup preserves clip start",
                exportClipChanged && exportClip.Frames.Count == 2 && Math.Abs(exportClip.Frames[0].Time - 0.5f) < 0.001f,
                $"{exportClip.Frames.Count} keys, first {exportClip.Frames[0].Time:0.##}s");

            var optProgram = GasmProgram.CreateDefault();
            optProgram.Instructions.Add(new GasmInstruction(GasmOp.Noop));
            optProgram.Instructions.Add(new GasmInstruction(GasmOp.Set, GasmEmitter.Work(0), GasmEmitter.Work(0)));
            optProgram.Instructions.Add(new GasmInstruction(GasmOp.Add, GasmEmitter.Work(1), GasmOperand.Const(2), GasmOperand.Const(3)));
            var optStats = GasmOptimizer.Optimize(optProgram);
            Check("optimiser removes no-ops and self-sets", optStats.Nops >= 1 && optStats.SelfSets >= 1, optStats.ToString());
            Check("optimiser folds constants safely", optProgram.Instructions.Any(i => i.Op == GasmOp.Set && i.Operands.Count > 1 && i.Operands[1].Kind == GasmOperandKind.Constant && Math.Abs(i.Operands[1].Value - 5f) < 0.001f));

            // ---- Animator C# API generates a native animation trigger ----
            var animLevel = new GrabLevel { Title = "Animator Test" };
            var animSign = new LevelNode { Kind = NodeKind.Sign, Name = "Sign 1", Position = new Vec3(5, 5, 5) };
            animSign.Animations.Add(new GrabAnimation
            {
                Name = "spin",
                Frames =
                {
                    new GrabAnimationFrame { Time = 0, Position = new Vec3(5, 5, 5), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 1, Position = new Vec3(5, 7, 5), Rotation = Quat.Identity }
                }
            });
            animLevel.AddNode(animSign);
            var animCode = new LevelNode { Kind = NodeKind.GASM, Name = "AnimCode", Position = new Vec3(0, 1, 0) };
            animLevel.AddNode(animCode);
            animLevel.EnsureNames();
            var animResult = new CSharpCompiler().Compile(animLevel, animCode, """
using Grab;

public class AnimTest : CodeBlock
{
    GameObject target = GameObject.Find("Sign 1");
    void Start()
    {
        Animator.Play(target, "spin", true, false);
    }
    void Update()
    {
    }
}
""");
            animCode.Compiled = animResult;
            Check("Animator C# API compiles", animResult.Success, string.Join(" | ", animResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)));
            var animTrigger = animLevel.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger && n.Generated && n.TriggerTargets.Any(t => t.Kind == 1));
            Check("Animator creates a native animation trigger", animTrigger != null && animTrigger.TriggerTargets.Any(t => t.AnimationName == "spin"), animTrigger?.DisplayName ?? "missing");
            var animIds = animLevel.BuildObjectIds();
            ulong triggerId = animTrigger != null && animIds.TryGetValue(animTrigger, out var foundTriggerId) ? foundTriggerId : 0;
            Check("GRAB object ids are 1-based hierarchy indices", animIds[animSign] == 1 && triggerId == 3,
                $"Sign={animIds[animSign]}, Trigger={triggerId}");
            Check("animation trigger targets the correct object id",
                animTrigger != null && animTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.ObjectId == animIds[animSign]),
                animTrigger?.TriggerTargets.FirstOrDefault()?.ObjectId.ToString() ?? "missing");
            Check("animation trigger uses GRAB's START mode",
                animTrigger != null && animTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.TargetMode == AnimationTargetMode.Start),
                animTrigger?.TriggerTargets.FirstOrDefault()?.TargetMode.ToString() ?? "missing");
            Check("Code Block connects to the animation trigger",
                animResult.Connections.Any(c => c.Type == GasmConnectionType.Node && c.ObjectId == triggerId),
                string.Join(",", animResult.Connections.Select(c => c.ObjectId)));
            var animPlay = new PlaySession(animLevel);
            for (int i = 0; i < 30; i++) animPlay.Step(1f / 60f);
            Check("Animator plays the authored clip at runtime", Math.Abs(animSign.Position.Y - 6f) < 0.6f, animSign.Position.ToString());
            animLevel.AddNode(new LevelNode { Kind = NodeKind.Static, Name = "Later Block", Position = new Vec3(9, 0, 9) });
            var animRound = LevelIO.Read(LevelIO.Write(animLevel));
            var animMap = animRound.BuildObjectIds();
            var roundSign = animRound.FindByName("Sign 1");
            var roundTrigger = animRound.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger);
            Check("animation trigger ids survive node insertion", roundSign != null && roundTrigger != null &&
                roundTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.ObjectId == animMap[roundSign]),
                roundTrigger?.TriggerTargets.FirstOrDefault()?.ObjectName ?? "missing");
            var roundCode = animRound.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM);
            Check("exported Code Block connection points at the exported trigger",
                roundCode != null && roundTrigger != null && roundCode.ImportedConnections.Any(c => c.ObjectId == animMap[roundTrigger]),
                roundCode == null || roundTrigger == null ? "missing" : string.Join(",", roundCode.ImportedConnections.Select(c => c.ObjectId)));
            Check("exported animation target keeps START mode",
                roundTrigger != null && roundTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.TargetMode == AnimationTargetMode.Start),
                roundTrigger?.TriggerTargets.FirstOrDefault()?.TargetMode.ToString() ?? "missing");

            var stopLevel = new GrabLevel { Title = "Animator Stop Test" };
            var stopSign = new LevelNode { Kind = NodeKind.Sign, Name = "Stop Sign", Position = new Vec3(4, 4, 4) };
            stopSign.Animations.Add(new GrabAnimation
            {
                Name = "wave",
                Frames =
                {
                    new GrabAnimationFrame { Time = 0, Position = new Vec3(4, 4, 4), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 1, Position = new Vec3(4, 5, 4), Rotation = Quat.Identity }
                }
            });
            stopLevel.AddNode(stopSign);
            var stopCode = new LevelNode { Kind = NodeKind.GASM, Name = "StopCode", Position = new Vec3(0, 1, 0) };
            stopLevel.AddNode(stopCode);
            var stopResult = new CSharpCompiler().Compile(stopLevel, stopCode, """
using Grab;

public class StopTest : CodeBlock
{
    GameObject target = GameObject.Find("Stop Sign");
    void Update()
    {
        Animator.Stop(target);
    }
}
""");
            var stopTrigger = stopLevel.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger && n.Generated &&
                n.ScriptSourceKey.StartsWith("anim:", StringComparison.Ordinal));
            Check("Animator.Stop creates a native trigger block", stopResult.Success && stopTrigger != null &&
                stopTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.AnimationName == "wave"), stopTrigger?.DisplayName ?? "missing");
            Check("Animator.Stop uses GRAB's STOP animation mode",
                stopTrigger != null && stopTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.TargetMode == AnimationTargetMode.Stop),
                stopTrigger?.TriggerTargets.FirstOrDefault()?.TargetMode.ToString() ?? "missing");
            var stopIds = stopLevel.BuildObjectIds();
            ulong stopId = stopTrigger != null && stopIds.TryGetValue(stopTrigger, out var foundStopId) ? foundStopId : 0;
            Check("Animator.Stop connects the Code Block to the STOP trigger",
                stopResult.Connections.Any(c => c.ObjectId == stopId), string.Join(",", stopResult.Connections.Select(c => c.ObjectId)));
            Check("Animator.Stop trigger targets the sign object id",
                stopTrigger != null && stopIds.TryGetValue(stopSign, out var stopSignId) &&
                stopTrigger.TriggerTargets.Any(t => t.Kind == 1 && t.ObjectId == stopSignId),
                stopTrigger?.TriggerTargets.FirstOrDefault()?.ObjectId.ToString() ?? "missing");
            Check("generated animation trigger names fit GRAB's 4-character limit",
                stopTrigger != null && stopTrigger.Name.Length <= GrabLimits.MaxConnectionNameLength, stopTrigger?.Name ?? "missing");
            Check("generated animation trigger is visible in the Hierarchy",
                stopTrigger != null && IsScriptAnimationTrigger(stopTrigger), stopTrigger?.Name ?? "missing");

            // ---- lifecycle / trigger events in the paged (split) compiler ----
            var lifeLevel = new GrabLevel { Title = "Lifecycle Test" };
            var lifeBox = new LevelNode { Kind = NodeKind.Static, Name = "Flag", Position = new Vec3(0, 1, 0) };
            lifeLevel.AddNode(lifeBox);
            var lifeCode = new LevelNode { Kind = NodeKind.GASM, Name = "Life", Position = new Vec3(0, 2, 0) };
            lifeLevel.AddNode(lifeCode);
            var lifeTrigger = new LevelNode { Kind = NodeKind.Trigger, Name = "Zone", Position = new Vec3(0, 0, 0) };
            lifeTrigger.TriggerTargets.Add(new TriggerTarget { Kind = 7, Mode = 0, ObjectName = "Life", TargetMode = 1 });
            lifeLevel.AddNode(lifeTrigger);
            lifeLevel.EnsureNames();
            var lifeScript = """
using Grab;
public class LifeTest : CodeBlock
{
    GameObject flag = GameObject.Find("Flag");
    float ticks = 0;
    void OnTriggerStay()
    {
        ticks = ticks + 1;
        flag.Position.Y = ticks;
    }
}
""";
            lifeCode.Script = lifeScript;
            var lifeResult = new CSharpCompiler().Compile(lifeLevel, lifeCode, lifeScript);
            lifeCode.Compiled = lifeResult;
            Check("OnTriggerStay compiles in the paged compiler", lifeResult.Success,
                string.Join(" | ", lifeResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)));
            var lifePlay = new PlaySession(lifeLevel);
            lifePlay.TriggerForce(lifeTrigger, true);
            for (int i = 0; i < 5; i++) lifePlay.Step(1f / 60f);
            Check("OnTriggerStay drives the scene while a trigger is active", lifeBox.Position.Y > 0.5f,
                $"Y={lifeBox.Position.Y:0.###}");

            // A page full of object connections must leave room for the trigger dispatcher.
            // Without the reservation, the last body page would silently drop the event.
            var hookSplitLevel = new GrabLevel { Title = "Trigger split" };
            for (int i = 1; i <= 9; i++)
                hookSplitLevel.AddNode(new LevelNode { Kind = NodeKind.Static, Name = "O" + i, Position = new Vec3(i, 1, 0) });
            var hookSplitCode = new LevelNode { Kind = NodeKind.GASM, Name = "Hook", Position = new Vec3(0, 2, 0) };
            var hookSplitTrigger = new LevelNode { Kind = NodeKind.Trigger, Name = "Area", Position = new Vec3(0, 0, 0) };
            hookSplitTrigger.TriggerTargets.Add(new TriggerTarget { Kind = 7, Mode = 0, ObjectName = "Hook", TargetMode = 1 });
            hookSplitLevel.AddNode(hookSplitCode);
            hookSplitLevel.AddNode(hookSplitTrigger);
            hookSplitLevel.EnsureNames();
            var hookSplitScript = "using Grab;\npublic class HookSplit : CodeBlock\n{\n" +
                string.Concat(Enumerable.Range(1, 9).Select(i => $"    GameObject o{i} = GameObject.Find(\"O{i}\");\n")) +
                "    void Update()\n    {\n" +
                string.Concat(Enumerable.Repeat("        o1.Position.Y = o2.Position.Y + o3.Position.Y + o4.Position.Y + o5.Position.Y + o6.Position.Y + o7.Position.Y + o8.Position.Y + o9.Position.Y;\n", 30)) +
                "    }\n    void OnTriggerStay() { }\n}\n";
            hookSplitCode.Script = hookSplitScript;
            var hookSplitResult = new CSharpCompiler().Compile(hookSplitLevel, hookSplitCode, hookSplitScript);
            hookSplitCode.Compiled = hookSplitResult;
            var hookTriggerId = hookSplitLevel.BuildObjectIds().TryGetValue(hookSplitTrigger, out var ht) ? ht : 0;
            var allHookPages = hookSplitResult.Continuations.Prepend(hookSplitResult).ToList();
            Check("split pages reserve a trigger dispatcher connection",
                hookSplitResult.Success && hookSplitResult.Continuations.Count > 0 &&
                allHookPages.Any(page => page.Connections.Any(c => c.ObjectId == hookTriggerId)),
                $"{hookSplitResult.Continuations.Count} continuation(s), {allHookPages.Sum(p => p.Connections.Count)} connection(s)");

            // ---- params arrays are packed into registers at the call site ----
            var paramsLevel = new GrabLevel { Title = "Params Test" };
            paramsLevel.AddNode(new LevelNode { Kind = NodeKind.Static, Name = "Marker", Position = new Vec3(0, 1, 0) });
            var paramsCode = new LevelNode { Kind = NodeKind.GASM, Name = "Params", Position = new Vec3(0, 2, 0) };
            paramsLevel.AddNode(paramsCode);
            paramsLevel.EnsureNames();
            const string paramsScript = """
using Grab;
public class ParamsTest : CodeBlock
{
    GameObject marker = GameObject.Find("Marker");
    float Sum(params float[] values)
    {
        float total = 0;
        for (int i = 0; i < values.Length; i++) total = total + values[i];
        return total;
    }
    void Start()
    {
        marker.Position.Y = Sum(1, 2, 3);
    }
}
""";
            var paramsResult = new CSharpCompiler().Compile(paramsLevel, paramsCode, paramsScript);
            paramsCode.Compiled = paramsResult;
            paramsCode.StartActive = true;
            Check("params float[] packs call-site arguments", paramsResult.Success,
                string.Join(" | ", paramsResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)));
            var paramsPlay = new PlaySession(paramsLevel);
            for (int i = 0; i < 3; i++) paramsPlay.Step(1f / 60f);
            var paramsMarker = paramsLevel.FindByName("Marker");
            Check("params array reads back at run time", paramsMarker != null && Math.Abs(paramsMarker.Position.Y - 6f) < 0.01f,
                $"Y={paramsMarker?.Position.Y:0.###}");

            animSign.ActiveAnimation = -1;
            var inactiveRound = LevelIO.Read(LevelIO.Write(animLevel));
            var inactiveSign = inactiveRound.FindByName("Sign 1");
            Check("animation clips survive .level", inactiveSign != null && inactiveSign.Animations.Count == 1 &&
                inactiveSign.Animations[0].Name == "spin" && inactiveSign.Animations[0].Frames.Count == 2,
                $"{inactiveSign?.Animations.Count ?? -1} clip(s)");
            Check("Start Active off survives .level", inactiveSign != null && inactiveSign.ActiveAnimation == -1,
                inactiveSign?.ActiveAnimation.ToString() ?? "missing");
            animSign.ActiveAnimation = 0;

            var autoLevel = new GrabLevel { Title = "Start Active Test" };
            var autoSign = new LevelNode { Kind = NodeKind.Sign, Name = "Auto Sign", Position = new Vec3(0, 0, 0) };
            autoSign.Animations.Add(new GrabAnimation
            {
                Name = "rise",
                Frames =
                {
                    new GrabAnimationFrame { Time = 0, Position = new Vec3(0, 0, 0), Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 1, Position = new Vec3(0, 2, 0), Rotation = Quat.Identity }
                }
            });
            autoLevel.AddNode(autoSign);
            var autoPlay = new PlaySession(autoLevel);
            for (int i = 0; i < 30; i++) autoPlay.Step(1f / 60f);
            Check("Start Active animation plays in Play mode", Math.Abs(autoSign.Position.Y - 1f) < 0.25f, autoSign.Position.ToString());
            autoSign.ActiveAnimation = -1;
            autoSign.Position = new Vec3(0, 0, 0);
            var idlePlay = new PlaySession(autoLevel);
            for (int i = 0; i < 30; i++) idlePlay.Step(1f / 60f);
            Check("Start Active off leaves the object at rest", Math.Abs(autoSign.Position.Y) < 0.001f, autoSign.Position.ToString());

            var bytes = LevelIO.Write(_level);
            var round = LevelIO.Read(bytes);
            Check("export produces bytes", bytes.Length > 50, bytes.Length + " bytes");
            Check("level round-trip objects", round.AllNodes().Count() == _level.AllNodes().Count(), $"{round.AllNodes().Count()} vs {_level.AllNodes().Count()}");
            Check("level round-trip title", round.Title == _level.Title, round.Title);
            var rc = round.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.GASM);
            Check("GASM survives the .level file", (rc?.ImportedProgram?.Instructions.Count ?? 0) == (code.Compiled?.Program.Instructions.Count ?? -1), $"{rc?.ImportedProgram?.Instructions.Count} instructions");
            var roundFloor = round.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static);
            Check("block transforms survive", roundFloor != null && Math.Abs(roundFloor.Scale.X - 10) < 0.001f, roundFloor?.Scale.ToString() ?? "missing");
            Check("ambience survives", Math.Abs(round.Ambience.SunAltitude - _level.Ambience.SunAltitude) < 0.001f, round.Ambience.SunAltitude.ToString());
            Check("spawn id assigned", round.DefaultSpawnPointId != 0, round.DefaultSpawnPointId.ToString());

            string dir = Path.Combine(Path.GetTempPath(), "grabengine_selftest");
            Directory.CreateDirectory(dir);
            string projectPath = Path.Combine(dir, "selftest.grabproj");
            ProjectIO.Save(_level, projectPath);
            var project = ProjectIO.Load(projectPath);
            var pc = project.AllNodes().FirstOrDefault(n => n.Name == "TestCode");
            Check("project save/load", pc != null && pc.Script == code.Script, projectPath);
            Check("project keeps ambience", Math.Abs(project.Ambience.CloudHeight - _level.Ambience.CloudHeight) < 0.001f);

            string levelPath = Path.Combine(dir, "selftest.level");
            File.WriteAllBytes(levelPath, LevelIO.Write(_level));
            var reread = LevelIO.Load(levelPath);
            Check("exported file reopens", reread.AllNodes().Any(n => n.Kind == NodeKind.GASM && n.ImportedProgram != null), levelPath);

            int instructionsBeforeRelease = code.Compiled?.Program.Instructions.Count ?? 0;
            var releaseSettings = new ExportSettings
            {
                Path = Path.Combine(dir, "release.level"),
                Profile = ExportProfile.Release,
                OptimizeScripts = true,
                OptimizeAnimations = true,
                TrimUnusedPrograms = true,
                VerifyOutput = true
            };
            string releaseReport = PerformExport(releaseSettings);
            Check("release export writes and verifies a level", File.Exists(releaseSettings.Path) && releaseReport.Contains("Verified"), releaseReport.Replace("\n", " | "));
            Check("release export never grows the GASM", (code.Compiled?.Program.Instructions.Count ?? 0) <= instructionsBeforeRelease,
                $"{instructionsBeforeRelease} → {code.Compiled?.Program.Instructions.Count}");
            var releaseRead = LevelIO.Load(releaseSettings.Path);
            Check("release level keeps every object", releaseRead.AllNodes().Count() == _level.AllNodes().Count(), $"{releaseRead.AllNodes().Count()} vs {_level.AllNodes().Count()}");

            Check("all GRAB shapes", ShapeInfo.All.Length >= 7, string.Join(", ", ShapeInfo.All.Select(s => s.Name)));

            var tone = SoundSynth.Render(440f, 2, 0.005f, 0.45f, 0.2f, 0.8f);
            Check("sound synth renders a finite waveform", tone.Length > 1000 && tone.All(float.IsFinite) && tone.Max(Math.Abs) > 0.05f,
                $"{tone.Length} samples @ {SoundSynth.SampleRate} Hz");
            string tonePath = Path.Combine(Path.GetTempPath(), "grabengine_tone.wav");
            File.WriteAllBytes(tonePath, SoundSynth.ToWav(tone));
            var audioSettings = new Mp3ImportSettings
            {
                Detail = 55, MaxNotes = 40, Polyphony = 1, MaxSeconds = 1.2f,
                Scheduled = true, StartActive = false, Volume = 0.7f
            };
            var songPlan = Mp3Transcriber.Analyze(tonePath, audioSettings);
            Check("MP3 analyser finds pitches", songPlan.Notes.Count > 0,
                $"{songPlan.Notes.Count} note(s) · {songPlan.Summary}");
            var songLevel = new GrabLevel { Title = "Audio Selftest" };
            var songImport = SongImporter.Import(songLevel, songPlan, audioSettings, new Vec3(0, 3, 0));
            Check("music import creates sounds and schedulers", songImport.Sounds > 0 && songImport.Triggers > 0 && songImport.Blocks > 0,
                songImport.Summary);
            Check("music scheduler compiles to GASM",
                songLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM).All(n => n.Compiled?.Success == true),
                string.Join(" | ", songLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM)
                    .Select(n => n.Compiled?.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message)
                    .Where(m => m != null).Take(2)));
            string songPath = Path.Combine(Path.GetTempPath(), "grabengine_audio_selftest.level");
            LevelIO.Save(songLevel, songPath);
            var songRead = LevelIO.Load(songPath);
            var songIds = songRead.BuildObjectIds();
            var songTrigger = songRead.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger && n.TriggerTargets.Any(t => t.Kind == 3));
            Check("music and trigger links survive .level",
                songRead.AllNodes().Count(n => n.Kind == NodeKind.Sound) == songImport.Sounds && songTrigger != null &&
                songTrigger.TriggerTargets.Any(t => t.Kind == 3 && t.TargetMode == 1 && songIds.Any(p => p.Key.Kind == NodeKind.Sound && p.Value == t.ObjectId)),
                $"{songRead.AllNodes().Count(n => n.Kind == NodeKind.Sound)} sound block(s), trigger={(songTrigger != null)}, mode={songTrigger?.TriggerTargets.FirstOrDefault(t => t.Kind == 3)?.TargetMode}");
            var freshSound = new LevelNode { Kind = NodeKind.Sound };
            Check("new sound blocks use GRAB's open-filter defaults",
                Math.Abs(freshSound.SoundLowPass - 25000) < 0.5f && freshSound.SoundHighPass == 0 &&
                Math.Abs(freshSound.SoundFrequencyMin - 35) < 0.01f &&
                Math.Abs(freshSound.SoundDutyCycle - 0.5f) < 0.001f &&
                Math.Abs(freshSound.SoundPitchJumpMod - 1) < 0.001f,
                $"lowpass={freshSound.SoundLowPass}, highpass={freshSound.SoundHighPass}, fmin={freshSound.SoundFrequencyMin}, duty={freshSound.SoundDutyCycle}, jump={freshSound.SoundPitchJumpMod}");
            var exportedSounds = songRead.AllNodes().Where(n => n.Kind == NodeKind.Sound).ToList();
            Check("scheduled sound blocks stay inactive after .level",
                exportedSounds.Count > 0 && exportedSounds.All(n => !n.SoundStartActive),
                $"{exportedSounds.Count(n => n.SoundStartActive)} of {exportedSounds.Count} came back start-active");
            Check("exported sound blocks write an open low-pass",
                exportedSounds.Count > 0 && exportedSounds.All(n => n.SoundLowPass >= 20000 && n.RawSoundParameters != null),
                $"{exportedSounds.Count} sound(s), lowpass={(exportedSounds.Count > 0 ? exportedSounds.Min(n => n.SoundLowPass) : 0)}");
            var firstParams = exportedSounds.Count > 0 && exportedSounds[0].RawSoundParameters != null
                ? PbReader.Read(exportedSounds[0].RawSoundParameters!) : new List<PbField>();
            Check("sound parameter block carries filter and duty fields",
                PbReader.First(firstParams, 20) != null && PbReader.First(firstParams, 8) != null &&
                PbReader.First(firstParams, 15) != null && PbReader.First(firstParams, 13) != null &&
                PbReader.Float(firstParams, 20) >= 20000 && PbReader.Float(firstParams, 8) > 0,
                $"lowpass={PbReader.Float(firstParams, 20):0}, fmin={PbReader.Float(firstParams, 8):0}, duty={PbReader.Float(firstParams, 15):0.00}, jump={PbReader.Float(firstParams, 13):0.00}");
            var zeroFilterLevel = new GrabLevel { Title = "Sound Filter Fix" };
            zeroFilterLevel.AddNode(new LevelNode
            {
                Kind = NodeKind.Sound, Name = "Fixed", Position = new Vec3(0, 1, 0),
                SoundLowPass = 0, SoundHighPass = 0, SoundVolume = 6.5f
            });
            string zeroFilterPath = Path.Combine(Path.GetTempPath(), "grabengine_sound_filter.level");
            LevelIO.Save(zeroFilterLevel, zeroFilterPath);
            var zeroFilterRead = LevelIO.Load(zeroFilterPath);
            var fixedSound = zeroFilterRead.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Sound);
            Check("export repairs a zero low-pass instead of muting the block",
                fixedSound != null && Math.Abs(fixedSound.SoundLowPass - 25000) < 1,
                $"lowpass={fixedSound?.SoundLowPass}");
            Check("sound volume above 2 survives .level",
                fixedSound != null && Math.Abs(fixedSound.SoundVolume - 6.5f) < 0.01f,
                $"volume={fixedSound?.SoundVolume}");
            try { File.Delete(zeroFilterPath); } catch { }
            // A .level exported by the old editor only had fields 1-5 and 7. Loading it
            // must drop the broken raw block so the next export repairs the low-pass.
            var legacyParams = new PbWriter();
            legacyParams.Float(1, 0.7f);
            legacyParams.Varint(2, 2);
            legacyParams.Float(3, 0.01f);
            legacyParams.Float(4, 0.3f);
            legacyParams.Float(5, 0.2f);
            legacyParams.Float(7, 440f);
            var legacyLevel = new GrabLevel { Title = "Legacy Sound" };
            legacyLevel.AddNode(new LevelNode
            {
                Kind = NodeKind.Sound, Name = "Old", Position = new Vec3(0, 1, 0),
                RawSoundParameters = legacyParams.ToArray()
            });
            string legacyPath = Path.Combine(Path.GetTempPath(), "grabengine_legacy_sound.level");
            LevelIO.Save(legacyLevel, legacyPath);
            var legacyRead = LevelIO.Load(legacyPath);
            var legacySound = legacyRead.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Sound);
            Check("old sound levels are repaired on load",
                legacySound != null && Math.Abs(legacySound.SoundLowPass - 25000) < 1 &&
                Math.Abs(legacySound.SoundFrequencyMin - 35) < 0.01f,
                $"lowpass={legacySound?.SoundLowPass}, fmin={legacySound?.SoundFrequencyMin}");
            try { File.Delete(legacyPath); } catch { }
            var audioPlay = new PlaySession(songRead);
            int soundStarts = 0;
            audioPlay.SoundTarget += (sound, mode) => { if (mode == 1) soundStarts++; };
            for (int i = 0; i < 30; i++) audioPlay.Step(1f / 60f);
            Check("music scheduler fires the sound block in Play mode", soundStarts > 0,
                $"{soundStarts} START event(s)");
            try { File.Delete(tonePath); File.Delete(songPath); } catch { }

            var speechSettings = new SpeechSettings
            {
                Voice = "Neutral", PitchHz = 118, RateWpm = 170, Clarity = 3,
                Intonation = 0.6f, Volume = 0.9f, MaxSeconds = 8f
            };
            var speechPlan = SpeechSynthesizer.Build("Hello world, this is GRAB ENGINE speaking.", speechSettings);
            Check("speech converts text to phonemes", speechPlan.Phones.Count > 12 && speechPlan.Duration > 0.8f,
                $"{speechPlan.Phones.Count} phoneme(s), {speechPlan.Duration:0.00}s · {speechPlan.Summary}");
            Check("speech starts with the right phoneme",
                speechPlan.Phones.Count > 0 && speechPlan.Phones[0].Symbol == "HH",
                string.Join(" ", speechPlan.Phones.Take(8).Select(p => p.Symbol)));
            Check("speech formants are ordered",
                speechPlan.Phones.Where(p => p.Voiced).All(p => p.F1 > 100 && p.F2 > p.F1 && p.F3 > p.F2),
                speechPlan.Phones.FirstOrDefault(p => p.Voiced)?.Symbol ?? "none");
            Check("speech keeps prosody inside the voice range",
                speechPlan.Phones.All(p => p.PitchHz > 60 && p.PitchHz < 400),
                $"{(speechPlan.Phones.Count == 0 ? 0 : speechPlan.Phones.Min(p => p.PitchHz)):0}–{(speechPlan.Phones.Count == 0 ? 0 : speechPlan.Phones.Max(p => p.PitchHz)):0} Hz");
            var voiceSamples = SpeechSynthesizer.RenderVoice(speechPlan, speechSettings);
            Check("speech formant voice renders audio",
                voiceSamples.Length > speechSettings.MaxSeconds * SoundSynth.SampleRate * 0.05f &&
                voiceSamples.All(float.IsFinite) && voiceSamples.Max(Math.Abs) > 0.2f,
                $"{voiceSamples.Length / (double)SoundSynth.SampleRate:0.00}s, peak {voiceSamples.Max(Math.Abs):0.00}");
            int gapAt = -1;
            for (int i = 0; i + 1 < speechPlan.Phones.Count; i++)
            {
                if (speechPlan.Phones[i].Word != speechPlan.Phones[i + 1].Word &&
                    speechPlan.Phones[i + 1].Start - speechPlan.Phones[i].End > 0.012f)
                {
                    gapAt = i;
                    break;
                }
            }
            double GapRms(int index)
            {
                var p = speechPlan.Phones[index];
                int from = (int)(p.End * SoundSynth.SampleRate);
                int to = (int)(speechPlan.Phones[index + 1].Start * SoundSynth.SampleRate);
                double sum = 0; int count = 0;
                for (int i = from; i < to && i < voiceSamples.Length; i++) { sum += voiceSamples[i] * voiceSamples[i]; count++; }
                return count == 0 ? 0 : Math.Sqrt(sum / count);
            }
            Check("speech leaves silence between words",
                gapAt >= 0 && GapRms(gapAt) < 0.02,
                gapAt < 0 ? "no gap found" : $"gap rms {GapRms(gapAt):0.0000}");
            var blockSamples = SpeechSynthesizer.RenderSoundBlocks(speechPlan, speechSettings);
            Check("speech block preview renders the exported score",
                blockSamples.Length > 1000 && blockSamples.All(float.IsFinite) && blockSamples.Max(Math.Abs) > 0.1f,
                $"{blockSamples.Length / (double)SoundSynth.SampleRate:0.00}s, peak {blockSamples.Max(Math.Abs):0.00}");
            var speechLevel = new GrabLevel { Title = "Speech Selftest" };
            var speechImport = SpeechImporter.Import(speechLevel, speechPlan, speechSettings, new Vec3(0, 3, 0));
            Check("speech import creates sounds, triggers and schedulers",
                speechImport.Sounds > 0 && speechImport.Triggers > 0 && speechImport.Blocks > 0,
                speechImport.Summary);
            Check("speech schedulers compile to GASM",
                speechLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM).All(n => n.Compiled?.Success == true),
                string.Join(" | ", speechLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM)
                    .Select(n => n.Compiled?.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message)
                    .Where(m => m != null).Take(2)));
            string speechPath = Path.Combine(Path.GetTempPath(), "grabengine_speech_selftest.level");
            LevelIO.Save(speechLevel, speechPath);
            var speechRead = LevelIO.Load(speechPath);
            var speechIds = speechRead.BuildObjectIds();
            var speechTrig = speechRead.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger && n.TriggerTargets.Count > 0);
            Check("speech links survive .level",
                speechRead.AllNodes().Count(n => n.Kind == NodeKind.Sound) == speechImport.Sounds && speechTrig != null &&
                speechTrig.TriggerTargets.All(t => t.TargetMode == 1 && speechIds.Any(p => p.Key.Kind == NodeKind.Sound && p.Value == t.ObjectId)),
                $"{speechRead.AllNodes().Count(n => n.Kind == NodeKind.Sound)} sound block(s), triggers with links={(speechTrig != null)}");
            var speechPlay = new PlaySession(speechRead);
            int speechStarts = 0;
            speechPlay.SoundTarget += (sound, mode) => { if (mode == 1) speechStarts++; };
            for (int i = 0; i < 600; i++) speechPlay.Step(1f / 60f);
            Check("speech schedulers fire sound blocks in Play mode", speechStarts > 0, $"{speechStarts} START event(s)");
            var acronymPlan = SpeechSynthesizer.Build("API", speechSettings);
            var enginePhone = speechPlan.Phones.FirstOrDefault(p => p.Word.Equals("ENGINE", StringComparison.OrdinalIgnoreCase));
            Check("speech keeps uppercase words that are ordinary words",
                enginePhone != null && enginePhone.Symbol == "EH",
                enginePhone?.Symbol ?? "missing");
            Check("speech spells initialisms letter by letter",
                string.Join(" ", acronymPlan.Phones.Select(p => p.Symbol)) == "EH IY P IY AA IY",
                string.Join(" ", acronymPlan.Phones.Select(p => p.Symbol)));
            var dottedPlan = SpeechSynthesizer.Build("U.S.A.", speechSettings);
            Check("speech handles dotted initialisms",
                string.Join(" ", dottedPlan.Phones.Select(p => p.Symbol)) == "Y UW EH S EH IY",
                string.Join(" ", dottedPlan.Phones.Select(p => p.Symbol)));
            var maxSettings = new SpeechSettings { Voice = "Neutral", PitchHz = 118, MaxSeconds = 10f };
            SpeechSynthesizer.ApplyPreset(maxSettings, "Max clarity");
            Check("speech clarity presets change the voice",
                maxSettings.Clarity == 4 && maxSettings.ConsonantBoost > 2f && maxSettings.RateWpm < 130,
                $"clarity {maxSettings.Clarity}, consonants {maxSettings.ConsonantBoost:0.0}, {maxSettings.RateWpm} wpm");
            var contrasted = SpeechSynthesizer.Build("hello", maxSettings);
            var ao = contrasted.Phones.FirstOrDefault(p => p.Symbol == "AO");
            Check("speech vowel contrast separates the formants",
                ao != null && ao.F2 > ao.F1 + 380f && contrasted.Phones.Where(p => p.Voiced).All(p => p.F4 > p.F3),
                ao == null ? "missing vowel" : $"F1 {ao.F1:0}, F2 {ao.F2:0}, F4 {ao.F4:0}");
            var clearLevel = new GrabLevel { Title = "Speech Clarity" };
            var clearImport = SpeechImporter.Import(clearLevel, contrasted, maxSettings, new Vec3(0, 3, 0));
            Check("speech clarity 4 imports four formant blocks",
                clearImport.Sounds >= 12 && clearImport.Blocks == clearImport.Triggers,
                clearImport.Summary);

            var sharedSongSettings = new Mp3ImportSettings
            {
                Detail = 55, MaxNotes = 6, Polyphony = 1, MaxSeconds = 1.2f,
                Scheduled = true, SingleTrigger = true, Volume = 0.7f
            };
            var sharedSongLevel = new GrabLevel { Title = "Shared Music" };
            var sharedSong = SongImporter.Import(sharedSongLevel, songPlan, sharedSongSettings, new Vec3(0, 3, 0));
            var sharedSongTriggers = sharedSongLevel.AllNodes().Where(n => n.Kind == NodeKind.Trigger).ToList();
            var sharedSongMaster = sharedSongTriggers.FirstOrDefault(t => t.Name.Contains("Music Start", StringComparison.OrdinalIgnoreCase));
            var sharedSongSlices = sharedSongTriggers.Where(t => !ReferenceEquals(t, sharedSongMaster)).ToList();
            var sharedSongBlocks = sharedSongLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM).ToList();
            Check("music shared mode gates the timed score instead of firing every sound",
                sharedSong.Triggers == sharedSong.Blocks + 1 && sharedSong.Blocks > 0 && sharedSongMaster != null &&
                sharedSongMaster.TriggerSources.Count > 0 && sharedSongMaster.TriggerTargets.Count == Math.Min(sharedSongBlocks.Count, GrabLimits.MaxTriggerTargets) &&
                sharedSongMaster.TriggerTargets.All(t => t.Kind == 7 && t.TargetMode == 1 && t.ObjectId != 0) &&
                sharedSongSlices.Count == sharedSong.Blocks &&
                sharedSongSlices.Sum(t => t.TriggerTargets.Count) == sharedSong.Sounds &&
                sharedSongSlices.All(t => t.TriggerTargets.All(x => x.ObjectId != 0)) &&
                sharedSongLevel.AllNodes().Where(n => n.Kind == NodeKind.Sound).All(n => !n.SoundStartActive) &&
                sharedSongBlocks.All(n => n.Compiled?.Success == true && n.Script.Contains("Music Start")),
                $"{sharedSong.Summary}, master targets={(sharedSongMaster?.TriggerTargets.Count ?? 0)}, slice links={sharedSongSlices.Sum(t => t.TriggerTargets.Count)}, blocks={sharedSongBlocks.Count}");
            string sharedSongPath = Path.Combine(Path.GetTempPath(), "grabengine_shared_music.level");
            LevelIO.Save(sharedSongLevel, sharedSongPath);
            var sharedSongRead = LevelIO.Load(sharedSongPath);
            var sharedSongIds = sharedSongRead.BuildObjectIds();
            // Trigger names are not stored in the retail format; the start gate is the
            // only trigger with an activation source. It owns the START links to the
            // scheduler Code Blocks; the slice triggers are force-only and own the sounds.
            var sharedSongMasterRead = sharedSongRead.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger &&
                n.TriggerSources.Count > 0);
            var sharedSongSliceRead = sharedSongRead.AllNodes().Where(n => n.Kind == NodeKind.Trigger && !ReferenceEquals(n, sharedSongMasterRead)).ToList();
            Check("music shared start gate and slice links survive .level",
                sharedSongMasterRead != null &&                sharedSongMasterRead.TriggerSources.Count > 0 &&
                sharedSongMasterRead.TriggerTargets.Count == Math.Min(sharedSong.Blocks, GrabLimits.MaxTriggerTargets) &&
                sharedSongMasterRead.TriggerTargets.All(t => t.Kind == 7 && t.TargetMode == 1 &&
                    sharedSongIds.Any(p => p.Key.Kind == NodeKind.GASM && p.Value == t.ObjectId)) &&
                sharedSongSliceRead.Count == sharedSong.Blocks &&
                sharedSongSliceRead.Sum(t => t.TriggerTargets.Count) == sharedSong.Sounds &&
                sharedSongSliceRead.SelectMany(t => t.TriggerTargets).All(t => t.TargetMode == 1 &&
                    sharedSongIds.Any(p => p.Key.Kind == NodeKind.Sound && p.Value == t.ObjectId)),
                $"master={(sharedSongMasterRead != null)}, {sharedSongMasterRead?.TriggerTargets.Count ?? 0} program link(s), {sharedSongSliceRead.Count} slice trigger(s), {sharedSongSliceRead.Sum(t => t.TriggerTargets.Count)} sound link(s) after reload");
            var exportedPlay = new PlaySession(sharedSongRead);
            int exportedStarts = 0;
            exportedPlay.SoundTarget += (sound, mode) => { if (mode == 1) exportedStarts++; };
            // Stand exactly on the stacked slice triggers: the exported level must stay
            // silent there, then play from the start gate like it does in retail GRAB.
            exportedPlay.PlayerPosition = sharedSongSliceRead.FirstOrDefault()?.WorldPosition ?? Vec3.Zero;
            for (int i = 0; i < 30; i++) exportedPlay.Step(1f / 60f);
            int exportedBefore = exportedStarts;
            if (sharedSongMasterRead != null) exportedPlay.TriggerForce(sharedSongMasterRead, true);
            for (int i = 0; i < 60; i++) exportedPlay.Step(1f / 60f);
            Check("exported shared score ignores the slice stack and starts from its gate",
                exportedBefore == 0 && exportedStarts > 0, $"before={exportedBefore}, after={exportedStarts}");

            var sharedSpeechSettings = new SpeechSettings
            {
                Voice = "Neutral", PitchHz = 118, RateWpm = 170, Clarity = 3,
                Intonation = 0.6f, Volume = 0.9f, MaxSeconds = 8f, SingleTrigger = true
            };
            var sharedSpeechLevel = new GrabLevel { Title = "Shared Speech" };
            var sharedSpeechImport = SpeechImporter.Import(sharedSpeechLevel, speechPlan, sharedSpeechSettings, new Vec3(0, 3, 0));
            var sharedSpeechTriggers = sharedSpeechLevel.AllNodes().Where(n => n.Kind == NodeKind.Trigger).ToList();
            var sharedSpeechMaster = sharedSpeechTriggers.FirstOrDefault(t => t.Name.Contains("Speech Start", StringComparison.OrdinalIgnoreCase));
            var sharedSpeechSlices = sharedSpeechTriggers.Where(t => !ReferenceEquals(t, sharedSpeechMaster)).ToList();
            var sharedSpeechBlocks = sharedSpeechLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM).ToList();
            Check("speech shared mode gates the spoken score instead of firing every phoneme",
                sharedSpeechImport.Triggers == sharedSpeechImport.Blocks + 1 && sharedSpeechImport.Blocks > 0 &&
                sharedSpeechMaster != null &&                sharedSpeechMaster.TriggerSources.Count > 0 &&
                sharedSpeechMaster.TriggerTargets.Count == Math.Min(sharedSpeechBlocks.Count, GrabLimits.MaxTriggerTargets) &&
                sharedSpeechMaster.TriggerTargets.All(t => t.Kind == 7 && t.TargetMode == 1 && t.ObjectId != 0) &&
                sharedSpeechSlices.Count == sharedSpeechImport.Blocks &&
                sharedSpeechSlices.Sum(t => t.TriggerTargets.Count) == sharedSpeechImport.Sounds &&
                sharedSpeechSlices.All(t => t.TriggerTargets.All(x => x.ObjectId != 0)) &&
                sharedSpeechLevel.AllNodes().Where(n => n.Kind == NodeKind.Sound).All(n => !n.SoundStartActive) &&
                sharedSpeechBlocks.All(n => n.Compiled?.Success == true && n.Script.Contains("Speech Start")),
                $"{sharedSpeechImport.Summary}, master targets={(sharedSpeechMaster?.TriggerTargets.Count ?? 0)}, {sharedSpeechSlices.Sum(t => t.TriggerTargets.Count)} sound link(s)");

            // The shared mode must keep the note timing: silent until the start trigger,
            // then one slice at a time (not the whole bank at once), and replayable.
            var gatePlan = new SongPlan { FileName = "gate", Duration = 1.2f, AnalyzedSeconds = 1.2f };
            gatePlan.Notes.Add(new SongNote { Start = 0.05f, Duration = 0.10f, Frequency = 440f, Midi = 69, Amplitude = 0.8f, Wave = 2 });
            gatePlan.Notes.Add(new SongNote { Start = 0.42f, Duration = 0.10f, Frequency = 660f, Midi = 76, Amplitude = 0.8f, Wave = 2 });
            gatePlan.Notes.Add(new SongNote { Start = 0.82f, Duration = 0.10f, Frequency = 880f, Midi = 81, Amplitude = 0.8f, Wave = 2 });
            var gateLevel = new GrabLevel { Title = "Gate Test" };
            var gateImport = SongImporter.Import(gateLevel, gatePlan,
                new Mp3ImportSettings { Scheduled = true, SingleTrigger = true, MaxNotes = 10 }, new Vec3(0, 3, 0));
            var gateMaster = gateLevel.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Trigger &&
                n.Name.Contains("Music Start", StringComparison.OrdinalIgnoreCase));
            var gateBlocks = gateLevel.AllNodes().Where(n => n.Kind == NodeKind.GASM).ToList();
            Check("music start gate schedulers compile",
                gateMaster != null && gateBlocks.Count == gateImport.Blocks && gateBlocks.All(n => n.Compiled?.Success == true),
                string.Join(" | ", gateBlocks.Select(n => n.Compiled?.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message).Where(m => m != null)));
            var gatePlay = new PlaySession(gateLevel);
            var gateUnique = new HashSet<string>();
            int gateStarts = 0;
            gatePlay.SoundTarget += (sound, mode) =>
            {
                if (mode != 1) return;
                gateStarts++;
                gateUnique.Add(sound.EditorId);
            };
            for (int i = 0; i < 10; i++) gatePlay.Step(1f / 60f);
            // The slice triggers sit on top of each other at the import point. They are
            // script-only (no sources), so standing in them must never fire the bank.
            gatePlay.PlayerPosition = gateImport.Group?.WorldPosition ?? Vec3.Zero;
            for (int i = 0; i < 20; i++) gatePlay.Step(1f / 60f);
            int gateStacked = gateUnique.Count;
            Check("script-only slice triggers ignore the player standing on them",
                gateStacked == 0, $"{gateStacked} sound(s) started from the stack");
            int gateBefore = gateUnique.Count;
            gatePlay.PlayerPosition = gateMaster!.WorldPosition;
            for (int i = 0; i < 12; i++) gatePlay.Step(1f / 60f);
            int gateFirst = gateUnique.Count;
            for (int i = 0; i < 18; i++) gatePlay.Step(1f / 60f);
            int gateSecond = gateUnique.Count;
            for (int i = 0; i < 40; i++) gatePlay.Step(1f / 60f);
            int gateThird = gateUnique.Count;
            Check("music start trigger waits, then plays the score in order",
                gateBefore == 0 && gateFirst > 0 && gateFirst < gateSecond && gateSecond < gateThird && gateThird == gateImport.Sounds,
                $"before={gateBefore}, 0.2s={gateFirst}, 0.5s={gateSecond}, 1.2s={gateThird} of {gateImport.Sounds}");
            int replayBase = gateStarts;
            gatePlay.PlayerPosition = new Vec3(0, 1.2f, 30);
            for (int i = 0; i < 20; i++) gatePlay.Step(1f / 60f);
            gatePlay.PlayerPosition = gateMaster.WorldPosition;
            for (int i = 0; i < 12; i++) gatePlay.Step(1f / 60f);
            Check("music start trigger replays after the score ends",
                gateStarts > replayBase, $"{replayBase} -> {gateStarts} START event(s)");
            try { File.Delete(sharedSongPath); } catch { }
            try { File.Delete(speechPath); } catch { }

            var testGlb = MakeTestGlb();
            var faceImport = GlbImporter.Import(testGlb, new GlbImportSettings
            {
                Mode = GlbImportMode.Faces, Detail = 10, DetectQuads = true, Material = 8
            }, "selftest");
            Check("GLB importer recovers quads", faceImport.QuadFaces == 1, faceImport.Summary);
            Check("GLB importer keeps remaining triangles", faceImport.TriangleFaces == 1, faceImport.Summary);
            Check("GLB quads and triangles become oriented blocks",
                faceImport.Nodes.Count == 2 && faceImport.Nodes.Any(n => n.Shape == ShapeInfo.Cube) &&
                faceImport.Nodes.Any(n => n.Shape == ShapeInfo.Prism) &&
                faceImport.Nodes.All(n => float.IsFinite(n.Position.X) && float.IsFinite(n.Rotation.W) && n.Scale.X > 0 && n.Scale.Y > 0 && n.Scale.Z > 0),
                $"{faceImport.Nodes.Count} block(s)");
            Check("GLB material colour reaches the block colour",
                faceImport.Nodes.Count > 0 && Math.Abs(faceImport.Nodes[0].Color1.R - 0.906f) < 0.02f,
                faceImport.Nodes.FirstOrDefault()?.Color1.Hex ?? "missing");
            var texturedImport = GlbImporter.Import(MakeTexturedTestGlb(), new GlbImportSettings
            {
                Mode = GlbImportMode.Faces, Detail = 10, DetectQuads = true, Material = 8,
                UseModelTextures = true, UseModelColors = true
            }, "textured");
            Check("GLB samples embedded base-colour textures",
                texturedImport.Nodes.Count > 0 && Math.Abs(texturedImport.Nodes[0].Color1.R - 210f / 255f) < 0.04f &&
                Math.Abs(texturedImport.Nodes[0].Color1.G - 50f / 255f) < 0.04f,
                texturedImport.Nodes.FirstOrDefault()?.Color1.Hex ?? "missing");
            var voxelImport = GlbImporter.Import(testGlb, new GlbImportSettings
            {
                Mode = GlbImportMode.Voxels, Detail = 5, Material = 8, UseModelColors = true
            }, "selftest-voxels");
            Check("GLB voxel mode builds a cube grid", voxelImport.VoxelCount > 0 && voxelImport.Nodes.Count > 0,
                voxelImport.Summary);
            var cubeMesh = SceneBuilder.ShapeMesh(ShapeInfo.Cube);
            Check("mesh UVs generated for textured blocks", cubeMesh.TextureCoordinates.Count == cubeMesh.Positions.Count && cubeMesh.Positions.Count > 0, $"{cubeMesh.Positions.Count} vertices, {cubeMesh.TextureCoordinates.Count} UVs");
            Check("only Default Colored accepts paint", MaterialInfo.IsColorable(8) && !MaterialInfo.IsColorable(2) && !MaterialInfo.IsColorable(4),
                $"default_colored={MaterialInfo.IsColorable(8)} ice={MaterialInfo.IsColorable(2)} wood={MaterialInfo.IsColorable(4)}");

            byte[] MaterialPixel(LevelNode node)
            {
                var mat = (MaterialGroup)SceneBuilder.BlockMaterial(node);
                var brush = (ImageBrush)((DiffuseMaterial)mat.Children[0]).Brush;
                var bmp = (BitmapSource)brush.ImageSource;
                var px = new byte[4];
                bmp.CopyPixels(new Int32Rect(bmp.PixelWidth / 2, bmp.PixelHeight / 2, 1, 1), px, 4, 0);
                return px; // B, G, R, A
            }
            var paintedPx = MaterialPixel(new LevelNode { Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8, Color1 = Col.White });
            Check("texture alpha is not double-darkened", paintedPx[0] > 210 && paintedPx[1] > 210 && paintedPx[2] > 210,
                $"{paintedPx[2]},{paintedPx[1]},{paintedPx[0]}");
            var icePx = MaterialPixel(new LevelNode { Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 2, Color1 = new Col(1f, 0f, 0f) });
            var iceRaw = TextureLibrary.RawTexture("ice");
            var rawPx = new byte[4];
            iceRaw.CopyPixels(new Int32Rect(iceRaw.PixelWidth / 2, iceRaw.PixelHeight / 2, 1, 1), rawPx, 4, 0);
            Check("fixed materials ignore the paint colour", Math.Abs(icePx[0] - rawPx[0]) < 4 && Math.Abs(icePx[1] - rawPx[1]) < 4 && Math.Abs(icePx[2] - rawPx[2]) < 4,
                $"ice {icePx[2]},{icePx[1]},{icePx[0]} vs texture {rawPx[2]},{rawPx[1]},{rawPx[0]}");

            var lightLevel = new GrabLevel();
            lightLevel.Nodes.Add(new LevelNode
            {
                Kind = NodeKind.Light, Position = new Vec3(0, 3, 0), LightRange = 4.3f, LightIntensity = 2.3f
            });
            var lightScene = SceneBuilder.Build(lightLevel, (LevelNode?)null);
            var builtPointLight = lightScene.Group.Children.OfType<PointLight>().FirstOrDefault();
            Check("point lights use the real GRAB range", builtPointLight != null && Math.Abs(builtPointLight.Range - 4.3) < 0.001,
                builtPointLight?.Range.ToString("0.###") ?? "missing");
            var bigBlock = new LevelNode { Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Position = new Vec3(0, -0.5f, 0), Scale = new Vec3(10, 1, 10) };
            var nearLight = new LevelNode { Kind = NodeKind.Light, Position = new Vec3(0, 3, 0), LightRange = 8 };
            var farLight = new LevelNode { Kind = NodeKind.Light, Position = new Vec3(100, 3, 0), LightRange = 2 };
            var litFloorMesh = SceneBuilder.BlockMesh(bigBlock, new[] { nearLight });
            var farFloorMesh = SceneBuilder.BlockMesh(bigBlock, new[] { farLight });
            Check("large faces are tessellated near lights", litFloorMesh.Positions.Count > 1000 && farFloorMesh.Positions.Count < 100,
                $"near={litFloorMesh.Positions.Count} far={farFloorMesh.Positions.Count}");
            Check("C# reference bundled", File.Exists(Path.Combine(AppContext.BaseDirectory, "CSharpReference.md")));
            Check("SGM meshes bundled", File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "cube.sgm")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "sphere.sgm")));
            Check("textures bundled", File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "wood.png")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "grid.png")));
            var playerMesh = PlayerModel.Create();
            Check("real GRAB player avatar loads", playerMesh.Children.Count >= 4,
                $"{playerMesh.Children.Count} mesh part(s)");
            var facingPlay = new PlaySession(GrabLevel.CreateDefault());
            Check("default spawn faces into the level", Math.Abs(Math.Abs(facingPlay.PlayerYaw) - 180f) < 0.1f,
                $"yaw={facingPlay.PlayerYaw:0.###}");
            Check("scene builds", Scene.Level == _level);
            var poseScene = SceneBuilder.Build(_level, (LevelNode?)null);
            Check("animation preview can pose node visuals", poseScene.Visuals.Count > 0 && poseScene.Visuals.Values.All(v => !v.IsFrozen),
                $"{poseScene.Visuals.Count} visuals, frozen={poseScene.Visuals.Values.Count(v => v.IsFrozen)}");
            var skyCam = new PerspectiveCamera(new Point3D(0, 0, 0), new Vector3D(0.35, 0.22, -1), new Vector3D(0, 1, 0), 55);
            SkyBackdrop MakeSky(AmbienceSettings amb, double advance = 0)
            {
                var sky = new SkyBackdrop { Width = 640, Height = 360 };
                sky.Measure(new Size(640, 360));
                sky.Arrange(new Rect(0, 0, 640, 360));
                sky.Update(amb, skyCam);
                if (advance > 0) sky.Advance(advance);
                return sky;
            }
            BitmapSource RenderSky(SkyBackdrop sky)
            {
                var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(sky);
                return bmp;
            }
            byte[] Pixels(BitmapSource bmp)
            {
                var data = new byte[640 * 360 * 4];
                bmp.CopyPixels(data, 640 * 4, 0);
                return data;
            }
            long PixelDelta(byte[] x, byte[] y)
            {
                long d = 0;
                for (int i = 0; i < x.Length; i += 4) d += Math.Abs(x[i] - y[i]) + Math.Abs(x[i + 1] - y[i + 1]) + Math.Abs(x[i + 2] - y[i + 2]);
                return d;
            }

            var skyTest = MakeSky(_level.Ambience);
            var skyA = Pixels(RenderSky(skyTest));
            var ambStars = _level.Ambience.Clone();
            ambStars.StarDensity = 1;
            ambStars.StarVisibility = 1;
            ambStars.SunAltitude = -30;
            ambStars.FogDensity = 0.02f;
            ambStars.CloudDensity = 0.12f;
            ambStars.SkyZenithColor = new Col(0.02f, 0.03f, 0.09f, 1);
            ambStars.SkyHorizonColor = new Col(0.07f, 0.09f, 0.16f, 1);
            var skyStar = MakeSky(ambStars);
            try
            {
                var enc2 = new PngBitmapEncoder();
                enc2.Frames.Add(BitmapFrame.Create(RenderSky(skyTest)));
                using var fs2 = File.Create(Path.Combine(Path.GetTempPath(), "grabengine_selftest", "sky.png"));
                enc2.Save(fs2);
            }
            catch { }
            Check("sky clouds built", skyTest.CloudPuffCount > 0, skyTest.CloudPuffCount + " puffs");
            Check("sky renders", skyA.Sum(v => (long)v) > 640L * 360 * 90, $"byte sum {skyA.Sum(v => (long)v)}");
            Check("sky star field builds", skyStar.StarCount > 100, skyStar.StarCount + " stars");

            // Snapshot history is the shared undo stack for every editor surface.
            var history = new EditHistory();
            history.Reset("{\"v\":0}", "a");
            history.Record("{\"v\":1}", "a", "Edit", "drag");
            history.Record("{\"v\":2}", "a", "Edit", "drag");
            var historyUndo = history.Undo("{\"v\":2}", "a", out _);
            Check("history coalesces a continuous gesture", historyUndo?.Json == "{\"v\":0}", historyUndo?.Json ?? "none");
            var historyRedo = history.Redo("{\"v\":0}", "a", out _);
            Check("history redo restores the latest state", historyRedo?.Json == "{\"v\":2}", historyRedo?.Json ?? "none");
            var discrete = new EditHistory();
            discrete.Reset("{\"v\":0}", "a");
            discrete.Record("{\"v\":1}", "a", "One");
            discrete.Record("{\"v\":2}", "a", "Two");
            Check("history keeps discrete edits separate",
                discrete.Undo("{\"v\":2}", "a", out var undoLabel)?.Json == "{\"v\":1}" && undoLabel == "Two",
                undoLabel ?? "none");

            // Exercise the real editor integration, not just the stack.
            LoadLevel(GrabLevel.CreateDefault(), null);
            var undoFloor = _level.AllNodes().First(n => n.Kind == NodeKind.Static);
            float originalY = undoFloor.Position.Y;
            undoFloor.Position = new Vec3(undoFloor.Position.X, 7f, undoFloor.Position.Z);
            MarkDirty(null, "Self test edit");
            Undo();
            var undoRestored = _level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static);
            Check("editor undo restores a scene edit", undoRestored != null && Math.Abs(undoRestored.Position.Y - originalY) < 0.001f,
                undoRestored?.Position.Y.ToString("0.###") ?? "missing");
            Redo();
            var redoRestored = _level.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static);
            Check("editor redo reapplies the edit", redoRestored != null && Math.Abs(redoRestored.Position.Y - 7f) < 0.001f,
                redoRestored?.Position.Y.ToString("0.###") ?? "missing");

            var toolLevel = GrabLevel.CreateDefault();
            toolLevel.EnsureNames();
            var toolTarget = toolLevel.AllNodes().First(n => n.Kind == NodeKind.Static);
            var toolPanel = new AnimationPanel(new AnimationWindowContext { Level = toolLevel, Selected = toolTarget });
            string toolErrors = toolPanel.RunToolSelfTest();
            Check("animation frame tools (stop-motion, in-between, duplicate, lerp, bake)",
                string.IsNullOrEmpty(toolErrors), toolErrors);

            // ---- neural network demo ----
            var neuralOptions = new NeuralTrainingOptions
            {
                Hidden = 4,
                Population = 96,
                Generations = 80,
                Patience = 18,
                MaxSteps = 480,
                Seed = 20261029
            };
            var neuralReport = new NeuralTrainer(neuralOptions).Train();
            Check("neural trainer learns across generations",
                neuralReport.History.Count >= 2 &&
                neuralReport.History[^1].BestDistance < neuralReport.History[0].BestDistance,
                FormattableString.Invariant($"{neuralReport.History.FirstOrDefault()?.BestDistance:0.000} -> {neuralReport.BestDistance:0.000}"));
            Check("neural trainer solves the arena and stops",
                neuralReport.Solved && neuralReport.StopReason.StartsWith("Solved", StringComparison.Ordinal),
                neuralReport.StopReason);
            Check("neural demo run reaches the target",
                neuralReport.DemoRun?.Reached == true,
                neuralReport.DemoRun?.Describe() ?? "no demo run");

            var neuralLevel = NeuralDemoBuilder.BuildLevel(neuralReport);
            var neuralBlock = neuralLevel.AllNodes().First(n => n.Kind == NodeKind.GASM && !n.Generated);
            var neuralCompile = neuralBlock.Compiled;
            var neuralPages = neuralCompile == null
                ? new List<GasmCompileResult>()
                : new[] { neuralCompile }.Concat(neuralCompile.Continuations).ToList();
            Check("neural C# compiles to valid GASM",
                neuralCompile?.Success == true && neuralPages.Count > 0,
                neuralCompile == null ? "no compile" : $"{neuralPages.Count} page(s)");
            Check("neural GASM respects every Code Block limit",
                neuralPages.All(p => p.Program.Instructions.Count <= GrabLimits.MaxInstructions &&
                                     p.Connections.Count <= GrabLimits.MaxConnections),
                string.Join(", ", neuralPages.Select(p => p.Program.Instructions.Count)));

            var neuralLoaded = ProjectIO.Deserialize(ProjectIO.Serialize(neuralLevel));
            var loadedNeuralBlock = neuralLoaded.AllNodes().First(n => n.Kind == NodeKind.GASM && !n.Generated);
            var loadedNeuralCompile = new CSharpCompiler().Compile(neuralLoaded, loadedNeuralBlock, loadedNeuralBlock.Script);
            loadedNeuralBlock.Compiled = loadedNeuralCompile;
            var neuralPlay = new PlaySession(neuralLoaded);
            for (int i = 0; i < 600; i++) neuralPlay.Step(1f / 60f);
            var neuralBall = neuralLoaded.FindByName(NeuralDemoBuilder.BallName);
            var neuralTarget = neuralLoaded.FindByName(NeuralDemoBuilder.TargetName);
            float neuralDistance = 999f;
            if (neuralBall != null && neuralTarget != null)
            {
                float ndx = neuralBall.WorldPosition.X - neuralTarget.WorldPosition.X;
                float ndz = neuralBall.WorldPosition.Z - neuralTarget.WorldPosition.Z;
                neuralDistance = MathF.Sqrt(ndx * ndx + ndz * ndz);
            }
            Check("neural project reload drives the ball to the target",
                neuralDistance <= NeuralArena.SuccessRadius,
                neuralDistance.ToString("0.000"));

            string neuralLevelPath = Path.Combine(Path.GetTempPath(), "grabengine_neural_selftest.level");
            File.WriteAllBytes(neuralLevelPath, LevelIO.Write(neuralLevel));
            var neuralExported = LevelIO.Load(neuralLevelPath);
            int parkedBuses = neuralExported.AllNodes().Count(n =>
                n.Kind == NodeKind.Static && n.Position.Y < -500f && n.Scale.X <= 0.1f);
            int hiddenBuses = neuralExported.AllNodes().Count(n =>
                n.Kind == NodeKind.Static && n.Position.Y < -500f && n.Scale.X <= 0.1f &&
                n.Generated && n.HideModel);
            Check("imported .level hides parked GASM buses",
                parkedBuses > 0 && hiddenBuses == parkedBuses,
                $"{hiddenBuses}/{parkedBuses} marked");
            var neuralExportedPlay = new PlaySession(neuralExported);
            for (int i = 0; i < 600; i++) neuralExportedPlay.Step(1f / 60f);
            var exportedBall = neuralExported.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Sphere);
            var exportedTarget = neuralExported.AllNodes().FirstOrDefault(n => n.Kind == NodeKind.Static &&
                Math.Abs(n.WorldPosition.X - NeuralArena.TargetX) < 0.01f &&
                Math.Abs(n.WorldPosition.Z - NeuralArena.TargetZ) < 0.01f);
            float exportedDistance = 999f;
            if (exportedBall != null && exportedTarget != null)
            {
                float edx = exportedBall.WorldPosition.X - exportedTarget.WorldPosition.X;
                float edz = exportedBall.WorldPosition.Z - exportedTarget.WorldPosition.Z;
                exportedDistance = MathF.Sqrt(edx * edx + edz * edz);
            }
            Check("exported neural .level runs the same GASM and reaches the target",
                exportedDistance <= NeuralArena.SuccessRadius,
                exportedDistance.ToString("0.000"));

            var earlyOptions = new NeuralTrainingOptions
            {
                Hidden = 0, Population = 8, Generations = 40, Patience = 1, Seed = 7,
                StopWhenSolved = false, MutationRate = 0f, MutationScale = 0f
            };
            var earlyReport = new NeuralTrainer(earlyOptions).Train();
            Check("neural trainer early-stops a stalled run",
                earlyReport.GenerationsRun < earlyOptions.Generations &&
                earlyReport.StopReason.Contains("Early stop", StringComparison.Ordinal),
                $"{earlyReport.GenerationsRun} generations: {earlyReport.StopReason}");

            // ---- Neural Lab: configurable graph, property bindings and export ----
            var labLevel = GrabLevel.CreateDefault();
            labLevel.Title = "Neural Lab Self Test";
            var labBall = new LevelNode
            {
                Name = "Brain Ball", Kind = NodeKind.Static, Shape = ShapeInfo.Sphere, Material = 8,
                Position = new Vec3(-4f, 0.6f, -4f), Color1 = new Col(0.1f, 0.8f, 1f), IsNeon = true
            };
            var labGoal = new LevelNode
            {
                Name = "Brain Goal", Kind = NodeKind.Static, Shape = ShapeInfo.Cube, Material = 8,
                Position = new Vec3(4f, 0.6f, 4f), Color1 = new Col(0.2f, 1f, 0.4f), IsNeon = true
            };
            labLevel.AddNode(labBall);
            labLevel.AddNode(labGoal);
            labLevel.EnsureNames();
            var labConfig = NeuralLabConfig.CreateStarter(labLevel, labBall.EditorId, labGoal.EditorId);
            labConfig.HiddenLayers = new List<NeuralLayer> { new() { Neurons = 6, Activation = NeuralActivation.ReLU } };
            labConfig.Training.Population = 40;
            labConfig.Training.Generations = 35;
            labConfig.Training.Patience = 12;
            labConfig.Training.EpisodeSteps = 300;
            labConfig.Training.Starts = 3;
            labConfig.Training.Seed = 12345;
            var labTrainer = new NeuralLabTrainer(labConfig, labLevel);
            var labReport = labTrainer.Train();
            Check("neural lab configurable trainer improves fitness",
                labReport.History.Count >= 2 &&
                labReport.History[^1].BestDistance <= labReport.History[0].BestDistance,
                FormattableString.Invariant($"{labReport.History.FirstOrDefault()?.BestDistance:0.000} -> {labReport.BestDistance:0.000}"));

            var movingConfig = labConfig.Clone();
            movingConfig.Training.TargetMotion = NeuralTargetMotion.Orbit;
            movingConfig.Training.TargetSpeed = 120f;
            movingConfig.Training.TargetRadius = 2.5f;
            movingConfig.Training.Population = 16;
            movingConfig.Training.Generations = 2;
            movingConfig.Training.Starts = 1;
            movingConfig.Training.EpisodeSteps = 90;
            movingConfig.EnsureWeights();
            var movingReport = new NeuralLabTrainer(movingConfig, labLevel).Train();
            float movedGoal = movingReport.DemoRun == null
                ? 0f
                : (movingReport.DemoRun.GoalStart - movingReport.DemoRun.GoalEnd).Length;
            Check("neural lab moving-target training samples the live goal",
                movingReport.History.Count == 2 && movingReport.DemoRun != null && movedGoal > 0.1f,
                $"generations {movingReport.History.Count}, target {movingConfig.Training.TargetMotion}, moved {movedGoal:0.000}");

            // Animate a clone so the PlaySession export checks below still see the
            // original static goal at (4, 4).
            var animatedLevel = ProjectIO.Clone(labLevel);
            var animatedGoal = animatedLevel.AllNodes().First(n => n.EditorId == labGoal.EditorId);
            animatedGoal.Animations.Add(new GrabAnimation
            {
                Name = "Patrol",
                Speed = 1f,
                Frames =
                {
                    new GrabAnimationFrame { Time = 0f, Position = animatedGoal.Position, Rotation = Quat.Identity },
                    new GrabAnimationFrame { Time = 1f, Position = animatedGoal.Position + new Vec3(2f, 0f, -2f), Rotation = Quat.Identity }
                }
            });
            var animatedConfig = labConfig.Clone();
            animatedConfig.Training.TargetMotion = NeuralTargetMotion.Animated;
            animatedConfig.Training.Population = 12;
            animatedConfig.Training.Generations = 1;
            animatedConfig.Training.Starts = 1;
            animatedConfig.Training.EpisodeSteps = 60;
            var animatedReport = new NeuralLabTrainer(animatedConfig, animatedLevel).Train();
            float animatedGoalMoved = animatedReport.DemoRun == null
                ? 0f
                : (animatedReport.DemoRun.GoalStart - animatedReport.DemoRun.GoalEnd).Length;
            Check("neural lab animated-target training follows the clip",
                animatedReport.DemoRun != null && animatedGoalMoved > 0.1f,
                $"target {animatedConfig.Training.TargetMotion}, moved {animatedGoalMoved:0.000}");

            // A known linear controller verifies the generated GASM independently of
            // genetic training. Inputs are target-minus-ball * 0.1 and outputs add
            // position, so an identity matrix is a valid steer-to-target policy.
            labConfig.Training.Mode = NeuralTrainingMode.Editor;
            labConfig.HiddenLayers.Clear();
            labConfig.Weights = new float[3 * 3 + 3];
            for (int o = 0; o < 3; o++)
            {
                for (int i = 0; i < 3; i++)
                    labConfig.Weights[NeuralLayout.WeightIndex(labConfig.LayerSizes, 0, o, i)] = o == i ? 1f : 0f;
                labConfig.Weights[NeuralLayout.BiasIndex(labConfig.LayerSizes, 0, o)] = 0f;
            }
            var labApply = NeuralCodeBuilder.Apply(labLevel, labConfig);
            var labPages = labApply.Compile == null
                ? new List<GasmCompileResult>()
                : new[] { labApply.Compile }.Concat(labApply.Compile.Continuations).ToList();
            Check("neural lab generated C# compiles to GASM",
                labApply.Success && labPages.Count > 0,
                labApply.Success ? labApply.Summary : labApply.Error);
            Check("neural lab GASM respects Code Block limits",
                labPages.All(p => p.Program.Instructions.Count <= GrabLimits.MaxInstructions &&
                                  p.Connections.Count <= GrabLimits.MaxConnections),
                string.Join(", ", labPages.Select(p => p.Program.Instructions.Count)));

            var signLevel = ProjectIO.Clone(labLevel);
            var sign = new LevelNode
            {
                Name = "Chat Sign", Kind = NodeKind.Sign, Shape = ShapeInfo.Cube,
                Position = new Vec3(0f, 2f, 0f), Text = ""
            };
            signLevel.AddNode(sign);
            signLevel.EnsureNames();
            var signConfig = NeuralLabConfig.CreateStarter(signLevel, labBall.EditorId, labGoal.EditorId);
            signConfig.Training.Mode = NeuralTrainingMode.Editor;
            signConfig.HiddenLayers.Clear();
            signConfig.Outputs.Clear();
            signConfig.Outputs.Add(new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = sign.EditorId,
                Property = NeuralProperty.SignWrite,
                Min = 0f,
                Max = 255f,
                Clamp = true
            });
            signConfig.EnsureWeights();
            var signApply = NeuralCodeBuilder.Apply(signLevel, signConfig);
            var signPages = signApply.Compile == null
                ? new List<GasmCompileResult>()
                : new[] { signApply.Compile }.Concat(signApply.Compile.Continuations).ToList();
            Check("neural lab sign ASCII output compiles",
                signApply.Success && signPages.Count > 0 &&
                signPages.All(p => p.Program.Instructions.Count <= GrabLimits.MaxInstructions &&
                                   p.Connections.Count <= GrabLimits.MaxConnections),
                signApply.Success ? signApply.Summary : signApply.Error);
            var labPlay = new PlaySession(labLevel);
            for (int i = 0; i < 700; i++) labPlay.Step(1f / 60f);
            var playedBall = labLevel.FindByName("Brain Ball");
            var playedGoal = labLevel.FindByName("Brain Goal");
            float labDistance = playedBall != null && playedGoal != null
                ? (playedBall.Position - playedGoal.Position).Length : 999f;
            Check("neural lab property bindings drive the scene",
                labDistance < 1.0f,
                $"{labDistance:0.000} ball {playedBall?.Position} goal {playedGoal?.Position}");
            var labRoundTrip = ProjectIO.Deserialize(ProjectIO.Serialize(labLevel));
            Check("neural lab graph survives project reload",
                labRoundTrip.NeuralLab != null &&
                labRoundTrip.NeuralLab.Inputs.Count == 3 &&
                labRoundTrip.NeuralLab.Outputs.Count == 3 &&
                labRoundTrip.NeuralLab.Weights.Length == labConfig.WeightCount,
                labRoundTrip.NeuralLab == null ? "no config" : $"{labRoundTrip.NeuralLab.Inputs.Count}/{labRoundTrip.NeuralLab.Outputs.Count}");

            var runtimeConfig = labConfig.Clone();
            runtimeConfig.Training.Mode = NeuralTrainingMode.Both;
            runtimeConfig.Training.RuntimeTuneLimit = 12;
            var runtimeLevel = ProjectIO.Clone(labLevel);
            var runtimeApply = NeuralCodeBuilder.Apply(runtimeLevel, runtimeConfig);
            var runtimePages = runtimeApply.Compile == null
                ? new List<GasmCompileResult>()
                : new[] { runtimeApply.Compile }.Concat(runtimeApply.Compile.Continuations).ToList();
            var runtimeTrainerPages = runtimeApply.TrainerCompile == null
                ? new List<GasmCompileResult>()
                : new[] { runtimeApply.TrainerCompile }.Concat(runtimeApply.TrainerCompile.Continuations).ToList();
            Check("neural lab runtime tuner compiles",
                runtimeApply.Success && runtimeApply.TrainerCompile?.Success == true &&
                runtimeApply.Block != null && runtimeApply.TrainerBlock != null,
                runtimeApply.Success ? runtimeApply.Summary : runtimeApply.Error);
            Check("neural lab runtime tuner respects Code Block limits",
                runtimePages.Concat(runtimeTrainerPages).All(p =>
                    p.Program.Instructions.Count <= GrabLimits.MaxInstructions &&
                    p.Connections.Count <= GrabLimits.MaxConnections),
                $"controller {runtimeApply.Instructions} / trainer {runtimeApply.TrainerInstructions} instructions, " +
                $"{runtimeApply.WeightBuses} weight bus(es)");
            var runtimePlay = new PlaySession(runtimeLevel);
            for (int i = 0; i < 1500; i++) runtimePlay.Step(1f / 60f);
            var runtimeBall = runtimeLevel.FindByName("Brain Ball");
            var runtimeGoal = runtimeLevel.FindByName("Brain Goal");
            float runtimeDistance = runtimeBall != null && runtimeGoal != null
                ? (runtimeBall.Position - runtimeGoal.Position).Length : 999f;
            Check("neural lab in-game trainer actually runs and keeps the controller useful",
                runtimeDistance < 3.0f && runtimeApply.TrainerCompile?.Success == true,
                $"distance {runtimeDistance:0.000}, trainer pages {runtimeTrainerPages.Count}");
            Check("neural lab runtime buses are hidden and persistent",
                runtimeApply.WeightBuses > 0 &&
                runtimeLevel.AllNodes().Count(n => n.GeneratedFor == NeuralRuntimeLayout.BusMarkerPrefix + runtimeConfig.RuntimeBusPrefix) == runtimeApply.WeightBuses,
                $"{runtimeApply.WeightBuses} bus(es)");
            var runtimeRoundTrip = ProjectIO.Deserialize(ProjectIO.Serialize(runtimeLevel));
            Check("neural lab runtime settings and trainer block survive project reload",
                runtimeRoundTrip.NeuralLab != null &&
                runtimeRoundTrip.NeuralLab.TrainerBlockId.Length > 0 &&
                runtimeRoundTrip.NeuralLab.RuntimeBusPrefix == runtimeConfig.RuntimeBusPrefix &&
                runtimeRoundTrip.AllNodes().Any(n => n.Script.Contains(NeuralTrainerBlock.Marker, StringComparison.Ordinal)),
                runtimeRoundTrip.NeuralLab == null
                    ? "no config"
                    : $"prefix={runtimeRoundTrip.NeuralLab.RuntimeBusPrefix}, trainer='{runtimeRoundTrip.NeuralLab.TrainerBlockId}'");
            var previewRuntimeConfig = labConfig.Clone();
            previewRuntimeConfig.Training.Mode = NeuralTrainingMode.Runtime;
            string previewRuntimeSource = NeuralCodeBuilder.BuildSource(labLevel, previewRuntimeConfig);
            Check("neural lab runtime C# preview works before Apply creates the buses",
                previewRuntimeSource.Contains("nnw0", StringComparison.Ordinal) &&
                previewRuntimeSource.Contains("__NN_", StringComparison.Ordinal),
                previewRuntimeSource.Length + " chars");
            var maxRuntimeConfig = NeuralLabConfig.CreateStarter(labLevel, labBall.EditorId, labGoal.EditorId);
            maxRuntimeConfig.Training.Mode = NeuralTrainingMode.Runtime;
            maxRuntimeConfig.Training.RuntimeTuneLimit = NeuralRuntimeLayout.MaxTunedWeights;
            var maxRuntimeApply = NeuralCodeBuilder.Apply(ProjectIO.Clone(labLevel), maxRuntimeConfig);
            Check("neural lab runtime supports the maximum tuned-weight budget",
                maxRuntimeApply.Success && maxRuntimeApply.WeightBuses == 2,
                maxRuntimeApply.Success ? maxRuntimeApply.Summary : maxRuntimeApply.Error);
            string runtimeLevelPath = Path.Combine(Path.GetTempPath(), "grabengine_neural_runtime.level");
            File.WriteAllBytes(runtimeLevelPath, LevelIO.Write(runtimeLevel));
            var runtimeExported = LevelIO.Load(runtimeLevelPath);
            var runtimeExportedPlay = new PlaySession(runtimeExported);
            for (int i = 0; i < 1200; i++) runtimeExportedPlay.Step(1f / 60f);
            var exportedRuntimeBall = runtimeExported.AllNodes().FirstOrDefault(n =>
                n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Sphere && n.IsNeon);
            var exportedRuntimeGoal = runtimeExported.AllNodes().FirstOrDefault(n =>
                n.Kind == NodeKind.Static && n.Shape == ShapeInfo.Cube && n.IsNeon &&
                Math.Abs(n.WorldPosition.X - 4f) < 0.05f && Math.Abs(n.WorldPosition.Z - 4f) < 0.05f);
            float exportedRuntimeDistance = exportedRuntimeBall != null && exportedRuntimeGoal != null
                ? (exportedRuntimeBall.WorldPosition - exportedRuntimeGoal.WorldPosition).Length : 999f;
            int exportedRuntimeBlocks = runtimeExported.AllNodes().Count(n => n.Kind == NodeKind.GASM);
            Check("neural lab runtime controller and trainer survive a real .level export",
                exportedRuntimeBlocks >= 2 && exportedRuntimeDistance < 3.0f,
                $"distance {exportedRuntimeDistance:0.000}, code blocks {exportedRuntimeBlocks}");
            int exportedRuntimeBuses = runtimeExported.AllNodes().Count(n =>
                n.Kind == NodeKind.Static && n.HideModel && n.Position.Y < -500f);
            Check("neural lab weight buses are hidden after a real .level import",
                exportedRuntimeBuses >= runtimeApply.WeightBuses,
                $"{exportedRuntimeBuses} hidden parked bus(es)");
            Check("neural weight layout keeps each bias with its output row",
                NeuralLayout.WeightIndex(new[] { 3, 2, 3 }, 1, 1, 1) ==
                NeuralLayout.BiasIndex(new[] { 3, 2, 3 }, 1, 1) - 1,
                NeuralLayout.WeightIndex(new[] { 3, 2, 3 }, 1, 1, 1).ToString());
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine("FAIL  unhandled exception: " + ex);
        }
        sb.AppendLine();
        sb.AppendLine($"{passed} passed, {failed} failed");
        return sb.ToString();
    }

    // ================= keyboard =================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Global history shortcuts must win over the local undo stacks in text boxes
        // and the C# editor; otherwise Ctrl+Z would undo typing instead of the app.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Redo();
                else Undo();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Y)
            {
                Redo();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                if (!IsTextInputFocused()) SelectAllObjects_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: NewLevel_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.O: OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S:
                    if (Keyboard.IsKeyDown(Key.LeftShift)) SaveProjectAs();
                    else SaveProject();
                    e.Handled = true; return;
                case Key.E: ExportLevel_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.D: DuplicateSelected(); e.Handled = true; return;
                case Key.A:
                    if (!IsTextInputFocused())
                    {
                        OpenAddObject();
                        e.Handled = true;
                        return;
                    }
                    break;
            }
        }
        if (!_playing && e.Key == Key.Escape && Scene.PrefabActive)
        {
            _stampPrefab = null;
            Scene.ClearPrefab();
            SelectEditorTool(EditorTool.Move);
            StatusText.Text = "Prefab stamp stopped";
            e.Handled = true;
            return;
        }
        if (_playing)
        {
            _keys.Add(e.Key);
            if (e.Key == Key.Escape) { StopPlay(); e.Handled = true; return; }
            if (e.Key == Key.F5) { DebugContinue(); e.Handled = true; return; }
            if (e.Key == Key.F10) { DebugStepLine(); e.Handled = true; return; }
            if (e.Key == Key.F11) { DebugStepInstruction(); e.Handled = true; return; }
            if (e.Key == Key.Space && !e.IsRepeat && !IsTextInputFocused()) _play?.Jump();
            return;
        }
        // Single-letter shortcuts must never fire while the user is typing in the
        // Inspector, the code editor or any dialog field. Typing "f" used to frame
        // the selection instead of entering the letter.
        bool typing = IsTextInputFocused();
        bool flying = Scene.IsMouseOver && Mouse.RightButton == MouseButtonState.Pressed;
        bool inAnimationPanel = ReferenceEquals(BottomTabs.SelectedItem, AnimationTab)
            && _animationPanel?.IsKeyboardFocusWithin == true;
        switch (e.Key)
        {
            case Key.F5: if (!typing) { StartPlay(); e.Handled = true; } break;
            case Key.F7: if (!typing) { CompileAll(); e.Handled = true; } break;
            case Key.F: if (!typing && !inAnimationPanel) { Scene.FocusSelected(); e.Handled = true; } break;
            case Key.Delete:
                if (!typing)
                {
                    if (inAnimationPanel) _animationPanel?.DeleteSelectedKey();
                    else DeleteSelected();
                    e.Handled = true;
                }
                break;
            case Key.Q: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Select); break;
            case Key.W: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Move); break;
            case Key.E: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Rotate); break;
            case Key.R: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Scale); break;
            case Key.B: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Brush); break;
            case Key.T: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Terrain); break;
            case Key.G: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Build); break;
            case Key.M: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Measure); break;
            case Key.P: if (!typing && !flying && !inAnimationPanel) SetToolFromKey(EditorTool.Prefab); break;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => _keys.Remove(e.Key);

    private void SetToolFromKey(EditorTool tool) => SelectEditorTool(tool);

    public sealed class ProblemItem
    {
        public ProblemItem(Diagnostic d, LevelNode node)
        {
            Severity = d.Severity;
            Line = d.Line;
            Code = string.IsNullOrEmpty(d.Code) ? (d.Severity == DiagnosticSeverity.Error ? "error" : "warning") : d.Code;
            Message = d.Message;
            Node = node;
        }

        public DiagnosticSeverity Severity { get; }
        public string Icon => Severity == DiagnosticSeverity.Error ? "✕" : Severity == DiagnosticSeverity.Warning ? "⚠" : "ℹ";
        public int Line { get; }
        public string Code { get; }
        public string Message { get; }
        public LevelNode? Node { get; }
    }
}
