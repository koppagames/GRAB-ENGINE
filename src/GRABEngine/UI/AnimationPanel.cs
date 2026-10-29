using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrabEngine.Core;

namespace GrabEngine.UI;

public sealed class AnimationWindowContext
{
    public GrabLevel Level { get; set; } = null!;
    public LevelNode? Selected { get; set; }
    public Action<LevelNode, Vec3, Quat> PreviewPose { get; set; } = (_, _, _) => { };
    public Action ClearPreview { get; set; } = () => { };
    /// <summary>True for a continuous gesture (drag/scrub), false for a discrete action.</summary>
    public Action<bool> Changed { get; set; } = _ => { };
    public Func<string, bool> CreateCodeBlock { get; set; } = _ => false;
}

/// <summary>
/// Timeline-based animation editor. Authors exactly the animation data GRAB stores in
/// a .level (keys, time, direction, speed, interpolation), previews it on the real
/// scene objects and can emit the C# needed to control the clip at runtime.
/// </summary>
public sealed class AnimationPanel : UserControl
{
    private readonly AnimationWindowContext _ctx;
    private readonly ComboBox _objects;
    private readonly ListBox _clips;
    private readonly TimelineControl _timeline;
    private readonly ListView _frames;
    private readonly TextBox _name, _speed, _frameTime;
    private readonly TextBox _px, _py, _pz, _rx, _ry, _rz;
    private readonly ComboBox _direction, _interpolation;
    private readonly TextBlock _duration, _status, _snippetInfo;
    private readonly TextBox _snippet;
    private readonly CheckBox _loop;
    private readonly CheckBox _startActive;
    private readonly ComboBox _previewSpeed;
    private readonly Button _playButton;
    private readonly CheckBox _stopMotion;
    private readonly TextBox _stopInterval;
    private readonly ComboBox _easeMode;
    private readonly TextBox _bakeInterval;
    private readonly TextBlock _timelineHint;

    /// <summary>Keys this clip may still add without exceeding GRAB's 1000-key object limit.</summary>
    private int MaxFrames => _node == null ? 1 : Math.Max(1, GrabLimits.MaxAnimationFrames -
        (_node.Animations.Sum(a => a.Frames.Count) - (_clip?.Frames.Count ?? 0)));
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    private LevelNode _node;
    private GrabAnimation? _clip;
    private int _frameIndex = -1;
    private bool _playing;
    private bool _active;
    private float _playOffset;
    private bool _updating;
    private static GrabAnimation? _clipboard;

    public AnimationPanel(AnimationWindowContext context)
    {
        _ctx = context;
        _node = context.Selected != null && context.Selected.Kind != NodeKind.Group
            ? context.Selected
            : context.Level.AllNodes().FirstOrDefault(n => n.Animations.Count > 0 && n.Kind != NodeKind.Group)
              ?? context.Level.AllNodes().FirstOrDefault(n => n.Kind != NodeKind.Group)
              ?? context.Level.Nodes.FirstOrDefault()!;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        // ---------------- header ----------------
        var header = new DockPanel { Margin = new Thickness(10, 5, 10, 5) };
        var titleStack = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "ANIMATION",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Dim
        });
        DockPanel.SetDock(titleStack, Dock.Left);
        header.Children.Add(titleStack);
        var addClip = Theme.Button("+ New Clip", small: true, accent: true);
        addClip.Margin = new Thickness(0, 0, 6, 0);
        addClip.Click += (_, _) => NewClip();
        DockPanel.SetDock(addClip, Dock.Right);
        header.Children.Add(addClip);

        _startActive = Theme.Check("Start Active", true);
        _startActive.VerticalAlignment = VerticalAlignment.Center;
        _startActive.Margin = new Thickness(0, 0, 12, 0);
        _startActive.ToolTip = "GRAB's Animate-menu Start Active setting. On: this object plays the selected clip when the level starts. Off: it waits for a trigger or Animator call.";
        _startActive.Checked += (_, _) => ApplyStartActive();
        _startActive.Unchecked += (_, _) => ApplyStartActive();
        DockPanel.SetDock(_startActive, Dock.Right);
        header.Children.Add(_startActive);

        var objectStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        objectStack.Children.Add(new TextBlock { Text = "OBJECT", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _objects = new ComboBox { Width = 240, Height = 24, ItemsSource = BuildObjectItems() };
        _objects.SelectedItem = _objects.Items.Cast<NodeItem>().FirstOrDefault(x => ReferenceEquals(x.Node, _node));
        _objects.SelectionChanged += (_, _) =>
        {
            if (_updating || _objects.SelectedItem is not NodeItem item) return;
            _node = item.Node;
            _ctx.Selected = item.Node;
            _clip = null;
            RefreshClipList(selectIndex: 0);
        };
        objectStack.Children.Add(_objects);
        header.Children.Add(objectStack);
        root.Children.Add(Theme.AccentBar(header));

        // ---------------- body ----------------
        var body = new Grid { Margin = new Thickness(8, 0, 8, 4) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

        // left: clips + clip settings + tools
        var left = new DockPanel();
        var leftScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        var leftStack = new StackPanel();
        leftScroll.Content = leftStack;
        left.Children.Add(leftScroll);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var quick = new StackPanel();
        quick.Children.Add(new TextBlock
        {
            Text = "START HERE",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(0, 0, 0, 5)
        });
        quick.Children.Add(StepLine("1", "Pick the object you want to animate above."));
        quick.Children.Add(StepLine("2", "Select a clip, or press + New Clip."));
        quick.Children.Add(StepLine("3", "Move the playhead, pose the object in the Scene, then press + Frame."));
        quick.Children.Add(StepLine("4", "Press Play Preview, or use the tools below for in-betweens, eases and stop motion."));
        quick.Children.Add(Theme.Hint("Keys: K add frame  ·  Space preview  ·  [ ] previous / next key"));
        var quickCard = Theme.Card(quick, new Thickness(8, 6, 8, 4));
        quickCard.Margin = new Thickness(0, 4, 0, 0);
        leftStack.Children.Add(quickCard);

        leftStack.Children.Add(Theme.SectionHeader("CLIPS"));
        _clips = new ListBox { Height = 96, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(33, 33, 33)) };
        _clips.SelectionChanged += (_, _) => SelectClip(_clips.SelectedIndex);
        leftStack.Children.Add(_clips);

        var clipActions = new WrapPanel { Margin = new Thickness(0, 3, 0, 2) };
        Button ClipAction(string text, string tip, Action action)
        {
            var button = Theme.Button(text, small: true);
            button.Margin = new Thickness(0, 0, 3, 3);
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            clipActions.Children.Add(button);
            return button;
        }
        ClipAction("Duplicate", "Create an independent copy of this clip on this object.", DuplicateClip);
        ClipAction("Copy", "Copy this clip and its settings.", CopyClip);
        ClipAction("Paste", "Paste the copied clip as a new clip on this object.", PasteClip);
        ClipAction("Delete", "Remove this clip from the object.", DeleteClip);
        leftStack.Children.Add(clipActions);

        leftStack.Children.Add(Theme.SectionHeader("CLIP SETTINGS"));
        _name = Theme.TextBox();
        _name.LostFocus += (_, _) => ApplyClipSettings();
        _speed = Theme.TextBox();
        _speed.LostFocus += (_, _) => ApplyClipSettings();
        _direction = Theme.Combo(new[] { "Restart (loop)", "Ping-pong" });
        _direction.SelectionChanged += (_, _) => ApplyClipSettings();
        _interpolation = Theme.Combo(new[] { "Linear", "Smooth (Catmull-Rom)" });
        _interpolation.SelectionChanged += (_, _) => ApplyClipSettings();
        leftStack.Children.Add(Theme.Row("Name", _name, 82));
        leftStack.Children.Add(Theme.Row("Speed", _speed, 82));
        leftStack.Children.Add(Theme.Row("Direction", _direction, 82));
        leftStack.Children.Add(Theme.Row("Interpolation", _interpolation, 82));
        _duration = new TextBlock { Foreground = Theme.Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        leftStack.Children.Add(_duration);

        // center: primary actions, advanced tools, preview, timeline, key table
        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 82 });
        Grid.SetColumn(center, 2);
        body.Children.Add(center);

        var primaryBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        Button PrimaryAction(string text, string tip, Action action, bool accent = false)
        {
            var button = Theme.Button(text, small: true, accent: accent);
            button.Margin = new Thickness(0, 0, 4, 3);
            button.MinHeight = 26;
            button.Padding = new Thickness(9, 2, 9, 2);
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            primaryBar.Children.Add(button);
            return button;
        }
        PrimaryAction("● + Frame", "Add a key at the playhead using the object's current position and rotation. (K)", CaptureKey, accent: true);
        PrimaryAction("＋ In-between", "Insert a sampled key halfway between the selected key and the next one.", AddInBetween);
        PrimaryAction("⇤ Prev", "Move the playhead to the previous key.", GoPreviousKey);
        PrimaryAction("Next ⇥", "Move the playhead to the next key.", GoNextKey);
        PrimaryAction("Delete Key", "Delete the selected key from this clip.", DeleteSelectedKey);
        Grid.SetRow(primaryBar, 0);
        center.Children.Add(primaryBar);

        var toolOptions = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        toolOptions.Children.Add(new TextBlock
        {
            Text = "TOOLS",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        toolOptions.Children.Add(new TextBlock { Text = "Ease", Foreground = Theme.Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        _easeMode = Theme.Combo(new[] { "Lerp / linear", "Ease in", "Ease out", "Ease in-out", "Stop motion" }, 3);
        _easeMode.Width = 118;
        _easeMode.Height = 22;
        _easeMode.Margin = new Thickness(0, 0, 10, 2);
        _easeMode.ToolTip = "Curve used by Bake Ease. Baking writes the curve into keys so GRAB plays it identically.";
        toolOptions.Children.Add(_easeMode);
        toolOptions.Children.Add(new TextBlock { Text = "Sample", Foreground = Theme.Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        _bakeInterval = Theme.TextBox("0.05");
        _bakeInterval.Width = 44;
        _bakeInterval.Height = 22;
        _bakeInterval.MinHeight = 22;
        _bakeInterval.Margin = new Thickness(0, 0, 2, 2);
        _bakeInterval.ToolTip = "Seconds between baked samples. 0.05 keeps fast motion smooth without adding hundreds of keys.";
        toolOptions.Children.Add(_bakeInterval);
        toolOptions.Children.Add(new TextBlock { Text = "s", Foreground = Theme.Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(toolOptions, 1);
        center.Children.Add(toolOptions);

        var toolBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        Button ToolButton(string text, string tip, Action action)
        {
            var button = Theme.Button(text, small: true);
            button.Margin = new Thickness(0, 0, 3, 2);
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            toolBar.Children.Add(button);
            return button;
        }
        ToolButton("Append Key", "Add a key half a second after the last one using the object's current pose.", AddKeyAtEnd);
        ToolButton("Duplicate Key", "Copy the selected key to the playhead.", DuplicateKey);
        ToolButton("Lerp 50%", "Move the selected key halfway toward the next key (position and rotation).", LerpSelectedToNext);
        ToolButton("Key → Object", "Move the scene object to the selected key so you can tweak it.", ApplyKeyToObject);
        ToolButton("Reverse Clip", "Mirror every key in time. Motion plays backwards with the same duration.", ReverseClip);
        ToolButton("Normalise", "Sort keys, remove duplicate timestamps and start the clip at 0.", NormalizeClip);
        ToolButton("Bake Ease", "Bake the selected ease curve into sampled keys so the exported clip plays it in GRAB.", BakeEase);
        ToolButton("Make Step", "Insert hold keys so motion snaps from key to key (stop-motion look).", MakeStopMotion);
        ToolButton("Bake Interval", "Resample the whole clip at the Sample interval. Useful before export.", BakeInterval);
        Grid.SetRow(toolBar, 2);
        center.Children.Add(toolBar);

        var previewBar = new WrapPanel { Margin = new Thickness(0, 2, 0, 3) };
        _playButton = Theme.Button("▶  Play Preview", small: true);
        _playButton.Margin = new Thickness(0, 0, 4, 2);
        _playButton.Click += (_, _) => TogglePreview();
        previewBar.Children.Add(_playButton);
        var stop = Theme.Button("■", small: true);
        stop.Margin = new Thickness(0, 0, 8, 2);
        stop.Click += (_, _) => StopPreview();
        previewBar.Children.Add(stop);
        _loop = Theme.Check("Loop", true);
        _loop.Margin = new Thickness(0, 0, 10, 0);
        previewBar.Children.Add(_loop);
        previewBar.Children.Add(new TextBlock { Text = "Speed", Foreground = Theme.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        _previewSpeed = Theme.Combo(new[] { "0.25x", "0.5x", "1x", "1.5x", "2x" }, 2);
        _previewSpeed.Width = 70;
        _previewSpeed.Margin = new Thickness(0, 0, 12, 2);
        previewBar.Children.Add(_previewSpeed);
        _stopMotion = Theme.Check("Stop-motion", false);
        _stopMotion.Margin = new Thickness(0, 0, 5, 0);
        _stopMotion.ToolTip = "When checked, + Frame adds the current pose and advances the playhead by the interval, ready for the next handmade frame.";
        previewBar.Children.Add(_stopMotion);
        previewBar.Children.Add(new TextBlock { Text = "every", Foreground = Theme.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        _stopInterval = Theme.TextBox("0.1");
        _stopInterval.Width = 40;
        _stopInterval.Height = 22;
        _stopInterval.MinHeight = 22;
        _stopInterval.Margin = new Thickness(0, 0, 2, 2);
        _stopInterval.ToolTip = "Stop-motion interval in seconds.";
        previewBar.Children.Add(_stopInterval);
        previewBar.Children.Add(new TextBlock { Text = "s", Foreground = Theme.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(previewBar, 3);
        center.Children.Add(previewBar);

        _timeline = new TimelineControl { Margin = new Thickness(0, 0, 0, 1), Height = 92, MinHeight = 82 };
        _timeline.Scrub += t => { StopPreview(); SetTime(t); };
        _timeline.FrameSelected += i => SelectFrame(i);
        _timeline.FrameMoved += (i, t) =>
        {
            if (_clip == null || i < 0 || i >= _clip.Frames.Count) return;
            _clip.Frames[i].Time = t;
            SelectFrame(i, rebuild: false);
            Dirty();
        };
        _timeline.DeleteFrame += DeleteFrame;
        Grid.SetRow(_timeline, 4);
        center.Children.Add(_timeline);

        _timelineHint = new TextBlock
        {
            Text = "Drag diamonds · Right-click delete · K add · [ ] prev/next",
            FontSize = 10,
            Foreground = Theme.Dim,
            Margin = new Thickness(1, 0, 0, 3)
        };
        Grid.SetRow(_timelineHint, 5);
        center.Children.Add(_timelineHint);

        var frameHost = new DockPanel();
        var frameHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = false };
        frameHeader.Children.Add(new TextBlock { Text = "KEYFRAMES", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
        var frameHint = new TextBlock { Text = "Click a row to edit it below", FontSize = 10, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(frameHint, Dock.Right);
        frameHeader.Children.Add(frameHint);
        DockPanel.SetDock(frameHeader, Dock.Top);
        frameHost.Children.Add(frameHeader);

        _frames = new ListView { BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "#", Width = 36, DisplayMemberBinding = new System.Windows.Data.Binding("Index") });
        view.Columns.Add(new GridViewColumn { Header = "Time", Width = 66, DisplayMemberBinding = new System.Windows.Data.Binding("Time") });
        view.Columns.Add(new GridViewColumn { Header = "Position", Width = 190, DisplayMemberBinding = new System.Windows.Data.Binding("Position") });
        view.Columns.Add(new GridViewColumn { Header = "Rotation", Width = 190, DisplayMemberBinding = new System.Windows.Data.Binding("Rotation") });
        // GridView headers and rows are template-generated, so an implicit Application
        // style is not always picked up. These local styles keep the keyframe table dark.
        view.ColumnHeaderContainerStyle = BuildFrameHeaderStyle();
        _frames.ItemContainerStyle = BuildFrameItemStyle();
        _frames.View = view;
        _frames.SelectionChanged += (_, _) =>
        {
            if (_updating || _frames.SelectedIndex < 0) return;
            SelectFrame(_frames.SelectedIndex, rebuild: false, fromList: true);
        };
        frameHost.Children.Add(_frames);
        Grid.SetRow(frameHost, 6);
        center.Children.Add(frameHost);

        // right: selected key fields, clip info, C# snippet
        var right = new DockPanel();
        var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 0, 0, 0) };
        var rightStack = new StackPanel();
        rightScroll.Content = rightStack;
        right.Children.Add(rightScroll);
        Grid.SetColumn(right, 4);
        body.Children.Add(right);

        var keyCard = new StackPanel();
        var keyTop = new DockPanel();
        keyTop.Children.Add(new TextBlock { Text = "SELECTED KEY", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var apply = Theme.Button("Apply", small: true, accent: true);
        apply.Click += (_, _) => ApplyKeyFields();
        DockPanel.SetDock(apply, Dock.Right);
        keyTop.Children.Add(apply);
        var fromObject = Theme.Button("From Object", small: true);
        fromObject.Margin = new Thickness(0, 0, 6, 0);
        fromObject.Click += (_, _) => CaptureSelectedFrameFromObject();
        DockPanel.SetDock(fromObject, Dock.Right);
        keyTop.Children.Add(fromObject);
        DockPanel.SetDock(keyTop, Dock.Top);
        keyCard.Children.Add(keyTop);

        _frameTime = Theme.TextBox();
        _px = Theme.TextBox(); _py = Theme.TextBox(); _pz = Theme.TextBox();
        _rx = Theme.TextBox(); _ry = Theme.TextBox(); _rz = Theme.TextBox();
        var keyFields = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        keyFields.Children.Add(Theme.Row("Time", _frameTime, 72));
        keyFields.Children.Add(ThreeFields("Position", _px, _py, _pz, 72));
        keyFields.Children.Add(ThreeFields("Rotation", _rx, _ry, _rz, 72));
        keyCard.Children.Add(keyFields);
        var keyBorder = Theme.Card(keyCard, new Thickness(8, 5, 8, 6));
        keyBorder.Margin = new Thickness(0, 3, 0, 3);
        rightStack.Children.Add(keyBorder);

        rightStack.Children.Add(Theme.SectionHeader("CLIP INFO"));
        _snippetInfo = new TextBlock { Foreground = Theme.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
        rightStack.Children.Add(_snippetInfo);
        var help = Theme.Hint("Keys are stored in the same fields GRAB uses in a .level. Direction and interpolation map directly to the game, so the exported motion is identical to the preview.");
        rightStack.Children.Add(help);

        rightStack.Children.Add(Theme.SectionHeader("CONTROL FROM C#"));
        rightStack.Children.Add(Theme.Hint("Copy this into a Code Block. Animator creates a real GRAB trigger connected to this object (shown in the Hierarchy), so the real game plays the same clip."));
        _snippet = Theme.TextBox("", multiline: true, height: 170);
        _snippet.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _snippet.FontSize = 11;
        _snippet.TextWrapping = TextWrapping.NoWrap;
        _snippet.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _snippet.IsReadOnly = true;
        rightStack.Children.Add(_snippet);
        var copyCode = Theme.Button("Copy C# Snippet", small: true, accent: true);
        copyCode.Margin = new Thickness(0, 7, 0, 0);
        copyCode.Click += (_, _) => CopySnippet();
        rightStack.Children.Add(copyCode);
        var addCode = Theme.Button("Create Code Block", small: true);
        addCode.Margin = new Thickness(0, 5, 0, 0);
        addCode.Click += (_, _) => InsertSnippet();
        rightStack.Children.Add(addCode);
        rightStack.Children.Add(Theme.Hint("Creates a new C# Code Block in the scene with this snippet already compiled."));

        rightStack.Children.Add(Theme.SectionHeader("SHORTCUTS"));
        rightStack.Children.Add(Theme.Hint(
            "K  add a frame from the object's current pose\n" +
            "Space  play / pause the preview\n" +
            "[  ]  jump to the previous / next key\n" +
            "Delete  remove the selected key\n\n" +
            "In-between samples between two keys. Bake Ease writes the chosen curve into keys. Make Step adds hold keys for a stop-motion snap. Ctrl+Z / Ctrl+Y undo and redo the whole edit."));

        // ---------------- footer ----------------
        var footer = new Border { Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)), Padding = new Thickness(10, 2, 10, 2) };
        _status = new TextBlock { Foreground = Theme.Dim, FontSize = 10.5 };
        footer.Child = _status;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
        PreviewKeyDown += AnimationPanel_PreviewKeyDown;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => PreviewTick(), Dispatcher);
        Unloaded += (_, _) =>
        {
            Deactivate();
            _playButton.Content = "▶  Play Preview";
        };

        RefreshClipList(selectIndex: 0);
        _status.Text = "Ready. Press + Frame to add the object's current pose at the playhead.";
        Loaded += (_, _) => _timeline.Focus();
    }

    /// <summary>Called by the editor when a project is opened or a new level is created.</summary>
    public void SetLevel(GrabLevel level)
    {
        if (level == null) return;
        bool sameLevel = ReferenceEquals(_ctx.Level, level);
        _ctx.Level = level;
        if (!sameLevel || _node is null || !level.AllNodes().Contains(_node))
        {
            _node = level.AllNodes().FirstOrDefault(n => n.Animations.Count > 0 && n.Kind != NodeKind.Group && !n.Generated)
                ?? level.AllNodes().FirstOrDefault(n => n.Kind != NodeKind.Group && !n.Generated)
                ?? level.Nodes.FirstOrDefault()!;
            _ctx.Selected = _node;
            StopPreview();
            _ctx.ClearPreview();
            _clip = null;
            _frameIndex = -1;
            _timeline.Clip = null;
            _frames.ItemsSource = null;
            RefreshClipList(selectIndex: 0);
        }
        RebuildObjectList();
    }

    /// <summary>Keeps the animation target in sync with the Hierarchy selection.</summary>
    public void SetSelected(LevelNode? node)
    {
        if (node == null || node.Kind == NodeKind.Group || node.Generated) return;
        if (!_ctx.Level.AllNodes().Contains(node) || ReferenceEquals(_node, node)) return;
        _node = node;
        _ctx.Selected = node;
        StopPreview();
        _ctx.ClearPreview();
        _clip = null;
        _frameIndex = -1;
        _timeline.Clip = null;
        _frames.ItemsSource = null;
        RebuildObjectList();
        RefreshClipList(selectIndex: 0);
    }

    /// <summary>Stops preview playback and restores the object's real transform.</summary>
    public void Deactivate()
    {
        _active = false;
        StopPreview();
        _ctx.ClearPreview();
    }

    /// <summary>Deletes the selected keyframe without touching the scene object.</summary>
    public void DeleteSelectedKey()
    {
        if (_clip == null || _frameIndex < 0)
        {
            _status.Text = "Select a keyframe first.";
            return;
        }
        DeleteFrame(_frameIndex);
    }

    private void RebuildObjectList()
    {
        _updating = true;
        var items = BuildObjectItems().ToList();
        _objects.ItemsSource = items;
        _objects.SelectedItem = items.FirstOrDefault(x => ReferenceEquals(x.Node, _node));
        _updating = false;
    }

    /// <summary>Re-reads the target's clips. Used when the tab is opened, because clips
    /// can also be created from the Inspector while this panel is hidden.</summary>
    public void Activate()
    {
        _active = true;
        RebuildObjectList();
        RefreshClipList(Math.Max(0, _clips.SelectedIndex));
    }

    private sealed class NodeItem
    {
        public NodeItem(LevelNode node) { Node = node; }
        public LevelNode Node { get; }
        public override string ToString() => Node.DisplayName;
    }

    private sealed class FrameRow
    {
        public FrameRow(int index, GrabAnimationFrame frame)
        {
            Index = index + 1;
            Time = frame.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s";
            Position = frame.Position.ToString();
            Rotation = frame.Rotation.ToEulerDegrees().ToString();
        }
        public int Index { get; }
        public string Time { get; }
        public string Position { get; }
        public string Rotation { get; }
    }

    private IEnumerable<NodeItem> BuildObjectItems() =>
        _ctx.Level.AllNodes().Where(n => n.Kind != NodeKind.Group && !n.Generated).Select(n => new NodeItem(n)).ToList();

    private static Style BuildFrameHeaderStyle()
    {
        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(48, 48, 48))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(68, 68, 68))));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        var template = new ControlTemplate(typeof(GridViewColumnHeader));
        var border = new FrameworkElementFactory(typeof(Border), "Hdr");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        border.AppendChild(content);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildFrameItemStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Text));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 20.0));

        var template = new ControlTemplate(typeof(ListViewItem));
        var border = new FrameworkElementFactory(typeof(Border), "Row");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(0));
        var presenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        presenter.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(GridViewRowPresenter.ColumnsProperty, new Binding("View.Columns")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListView), 1)
        });
        border.AppendChild(presenter);
        template.VisualTree = border;

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(44, 93, 135)), "Row"));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(selected);
        var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(51, 51, 51)), "Row"));
        template.Triggers.Add(hover);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static StackPanel ThreeFields(string label, TextBox a, TextBox b, TextBox c, double labelWidth = 84)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), MinHeight = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = Theme.FieldLabel(label);
        Grid.SetColumn(lbl, 0);
        row.Children.Add(lbl);
        var values = new UniformGrid { Columns = 3, Margin = new Thickness(0) };
        values.Children.Add(a);
        values.Children.Add(b);
        values.Children.Add(c);
        Grid.SetColumn(values, 1);
        row.Children.Add(values);
        var host = new StackPanel();
        host.Children.Add(row);
        return host;
    }

    private void RefreshClipList(int selectIndex)
    {
        if (_node is null || !_ctx.Level.AllNodes().Contains(_node))
        {
            _clip = null;
            _frameIndex = -1;
            _clips.Items.Clear();
            _timeline.Clip = null;
            _frames.ItemsSource = null;
            _name.IsEnabled = _speed.IsEnabled = _direction.IsEnabled = _interpolation.IsEnabled = false;
            _startActive.IsEnabled = false;
            _startActive.IsChecked = false;
            _duration.Text = "No object selected.";
            _status.Text = "Select an object in the Hierarchy to animate.";
            UpdateSnippet();
            return;
        }
        _updating = true;
        _clips.Items.Clear();
        for (int i = 0; i < _node.Animations.Count; i++)
        {
            var clip = _node.Animations[i];
            _clips.Items.Add($"{clip.Name}    {clip.Frames.Count} keys · {AnimationSampler.Duration(clip):0.##}s");
        }
        _updating = false;
        if (_clips.Items.Count == 0)
        {
            _clip = null;
            _timeline.Clip = null;
            _frames.ItemsSource = null;
            _ctx.ClearPreview();
            UpdateClipSettings();
            UpdateSnippet();
            return;
        }
        _clips.SelectedIndex = Math.Clamp(selectIndex, 0, _clips.Items.Count - 1);
        SelectClip(_clips.SelectedIndex);
    }

    private void SelectClip(int index)
    {
        if (_updating || index < 0 || index >= _node.Animations.Count) return;
        StopPreview();
        _clip = _node.Animations[index];
        _frameIndex = -1;
        _timeline.Clip = _clip;
        _timeline.SelectedFrame = -1;
        _timeline.Time = 0;
        // GRAB stores the clip that Start Active should play as activeAnimation.
        // Selecting a clip while Start Active is on makes that clip the startup one.
        if (!_updating && _node.ActiveAnimation >= 0 && _node.ActiveAnimation != index)
        {
            _node.ActiveAnimation = index;
            _ctx.Changed(false);
        }
        UpdateClipSettings();
        RefreshFrameTable();
        UpdateSnippet();
        if (_clip.Frames.Count > 0) SelectFrame(0);
        _status.Text = $"{_clip.Name}: {_clip.Frames.Count} keys, {AnimationSampler.Duration(_clip):0.##}s, speed {_clip.Speed:0.##}x, {( _clip.Direction == 1 ? "ping-pong" : "restart")}";
    }

    private void UpdateClipSettings()
    {
        _updating = true;
        bool has = _clip != null;
        _name.IsEnabled = _speed.IsEnabled = _direction.IsEnabled = _interpolation.IsEnabled = has;
        _startActive.IsEnabled = _node != null && _node.Animations.Count > 0;
        _startActive.IsChecked = has && _node != null && _node.ActiveAnimation >= 0;
        _name.Text = _clip?.Name ?? "";
        _speed.Text = (_clip?.Speed ?? 1f).ToString("0.##", CultureInfo.InvariantCulture);
        _direction.SelectedIndex = Math.Clamp(_clip?.Direction ?? 0, 0, 1);
        _interpolation.SelectedIndex = Math.Clamp(_clip?.Interpolation ?? 0, 0, 1);
        _duration.Text = has
            ? $"Length {AnimationSampler.Duration(_clip!):0.##}s  ·  {_clip!.Frames.Count} keys\nPlayback {( _clip.Direction == 1 ? "ping-pong" : "restart")}, {(_clip.Interpolation == 1 ? "smooth" : "linear")}, {_clip.Speed:0.##}x"
            : "No clip selected. Press New Clip.";
        _updating = false;
    }

    private void ApplyStartActive()
    {
        if (_updating || _node == null) return;
        if (_node.Animations.Count == 0)
        {
            _updating = true;
            _startActive.IsChecked = false;
            _updating = false;
            _status.Text = "Add a clip before setting Start Active.";
            return;
        }

        if (_startActive.IsChecked == true)
        {
            int index = _clips.SelectedIndex >= 0 ? _clips.SelectedIndex : Math.Clamp(_node.ActiveAnimation, 0, _node.Animations.Count - 1);
            _node.ActiveAnimation = Math.Clamp(index, 0, _node.Animations.Count - 1);
            _status.Text = $"Start Active: {_node.Animations[_node.ActiveAnimation].Name} plays when the level starts.";
        }
        else
        {
            _node.ActiveAnimation = -1;
            _status.Text = "Start Active off: the object waits for a Trigger or Animator call.";
        }
        Dirty(continuous: false);
    }

    private void ApplyClipSettings()
    {
        if (_updating || _clip == null) return;
        _clip.Name = _name.Text.Trim();
        if (float.TryParse(_speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            _clip.Speed = Math.Clamp(speed, 0.01f, 20f);
        _clip.Direction = Math.Max(0, _direction.SelectedIndex);
        _clip.Interpolation = Math.Max(0, _interpolation.SelectedIndex);
        RefreshClipList(_clips.SelectedIndex);
        Dirty(continuous: false);
    }

    private void RefreshFrameTable()
    {
        if (_clip == null) { _frames.ItemsSource = null; _timeline.Clip = null; return; }
        _updating = true;
        _frames.ItemsSource = _clip.Frames.Select((f, i) => new FrameRow(i, f)).ToList();
        _updating = false;
        _timeline.Clip = _clip;
        UpdateClipSettings();
    }

    private void SelectFrame(int index, bool rebuild = true, bool fromList = false)
    {
        if (_clip == null) return;
        if (index < 0 || index >= _clip.Frames.Count) { _frameIndex = -1; return; }
        _frameIndex = index;
        if (rebuild) RefreshFrameTable();
        _timeline.SelectedFrame = index;
        if (!fromList)
        {
            _updating = true;
            _frames.SelectedIndex = index;
            _updating = false;
            if (_frames.SelectedItem != null)
                Dispatcher.BeginInvoke(new Action(() => { if (_frames.SelectedItem != null) _frames.ScrollIntoView(_frames.SelectedItem); }), DispatcherPriority.Background);
        }
        var f = _clip.Frames[index];
        _updating = true;
        _frameTime.Text = f.Time.ToString("0.###", CultureInfo.InvariantCulture);
        _px.Text = f.Position.X.ToString("0.###", CultureInfo.InvariantCulture);
        _py.Text = f.Position.Y.ToString("0.###", CultureInfo.InvariantCulture);
        _pz.Text = f.Position.Z.ToString("0.###", CultureInfo.InvariantCulture);
        var e = f.Rotation.ToEulerDegrees();
        _rx.Text = e.X.ToString("0.##", CultureInfo.InvariantCulture);
        _ry.Text = e.Y.ToString("0.##", CultureInfo.InvariantCulture);
        _rz.Text = e.Z.ToString("0.##", CultureInfo.InvariantCulture);
        _updating = false;
        SetTime(f.Time);
        PreviewFrame(f);
    }

    private void SetTime(float time)
    {
        _timeline.Time = time;
        _timeline.InvalidateVisual();
    }

    private void PreviewFrame(GrabAnimationFrame frame)
    {
        if (_active) _ctx.PreviewPose(_node, frame.Position, frame.Rotation);
    }

    private void PreviewAt(float time)
    {
        if (!_active || _clip == null) return;
        _timeline.Clip = _clip;
        SetTime(time);
        var (pos, rot) = AnimationSampler.Sample(_clip, time, _loop.IsChecked == true);
        _ctx.PreviewPose(_node, pos, rot);
    }

    private float PreviewSpeed => _previewSpeed.SelectedIndex switch { 0 => 0.25f, 1 => 0.5f, 3 => 1.5f, 4 => 2f, _ => 1f };

    private void TogglePreview()
    {
        if (_playing) { StopPreview(); return; }
        if (_clip == null || _clip.Frames.Count == 0) { _status.Text = "Add a key before playing the preview."; return; }
        _playing = true;
        _playButton.Content = "❚❚  Pause Preview";
        _playOffset = _timeline.Time;
        _clock.Restart();
        _timer.Start();
    }

    private void StopPreview()
    {
        if (!_playing) return;
        _playing = false;
        _timer.Stop();
        _playButton.Content = "▶  Play Preview";
    }

    private void PreviewTick()
    {
        if (!_playing || _clip == null) return;
        float length = AnimationSampler.Duration(_clip);
        float t = _playOffset + (float)_clock.Elapsed.TotalSeconds * PreviewSpeed;
        if (length <= 0.0001f) { t = 0; }
        else if (t > length)
        {
            if (_loop.IsChecked == true) t %= length;
            else { t = length; StopPreview(); }
        }
        PreviewAt(t);
        _status.Text = $"Preview {t:0.00}s / {length:0.##}s  ·  {_clip.Name}";
        if (!_playing) PreviewAt(length);
    }

    private void NewClip()
    {
        if (_node is null || !_ctx.Level.AllNodes().Contains(_node)) { _status.Text = "Select an object in the Hierarchy first."; return; }
        var clip = new GrabAnimation
        {
            Name = UniqueClipName("Clip"),
            Frames = { new GrabAnimationFrame { Time = 0, Position = _node.Position, Rotation = _node.Rotation } }
        };
        _node.Animations.Add(clip);
        Dirty(continuous: false);
        RefreshClipList(_node.Animations.Count - 1);
        _status.Text = "Created " + clip.Name + ". Pose the object and press + Frame.";
    }

    private string UniqueClipName(string start)
    {
        int i = 1;
        string name = start;
        while (_node.Animations.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = start + " " + (++i);
        return name;
    }

    private void CaptureKey()
    {
        bool createdClip = false;
        if (_clip == null)
        {
            NewClip();
            if (_clip == null) return;
            createdClip = true;
        }
        if (_clip.Frames.Count >= MaxFrames)
        {
            _status.Text = $"This object already has {GrabLimits.MaxAnimationFrames} animation keys, GRAB's limit. Delete a key first.";
            return;
        }

        bool stopMotion = _stopMotion.IsChecked == true;
        float interval = StopMotionInterval;
        float t = RoundTime(_timeline.Time);
        GrabAnimationFrame? existing = _clip.Frames.FirstOrDefault(f => MathF.Abs(f.Time - t) < 0.0015f);

        if (createdClip)
        {
            // A brand-new clip already has its first key at t=0. Update that key rather
            // than adding a duplicate frame and eating a history step.
            existing = _clip.Frames[0];
            t = existing.Time;
        }

        if (stopMotion && existing != null && !createdClip)
        {
            // In a stop-motion session the playhead may still sit on the previous key.
            // Put the next handmade pose after it instead of overwriting it.
            t = RoundTime(_clip.Frames.Max(f => f.Time) + interval);
            existing = null;
        }

        if (existing != null)
        {
            existing.Position = _node.Position;
            existing.Rotation = _node.Rotation;
        }
        else
        {
            t = EnsureFreeTime(t);
            _clip.Frames.Add(new GrabAnimationFrame { Time = t, Position = _node.Position, Rotation = _node.Rotation });
            SortFrames(_clip);
        }

        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        int index = FindFrameAt(t);
        SelectFrame(index);
        if (stopMotion)
        {
            SetTime(RoundTime(t + interval));
            _status.Text = $"Stop-motion frame at {t:0.###}s. Playhead advanced to {_timeline.Time:0.###}s — move the object and press + Frame again.";
        }
        else
        {
            _status.Text = existing != null ? $"Updated the key at {t:0.###}s." : $"Added a key at {t:0.###}s.";
        }
    }

    private void AddInBetween()
    {
        if (_clip == null) { NewClip(); return; }
        if (_clip.Frames.Count < 2) { _status.Text = "Add a second key first, then In-between can sample between them."; return; }
        if (_clip.Frames.Count >= MaxFrames) { _status.Text = $"This object is at GRAB's {GrabLimits.MaxAnimationFrames}-key animation limit."; return; }
        SortFrames(_clip);

        int aIndex;
        if (_frameIndex >= 0 && _frameIndex < _clip.Frames.Count - 1) aIndex = _frameIndex;
        else if (_frameIndex > 0) aIndex = _frameIndex - 1;
        else aIndex = 0;
        var a = _clip.Frames[aIndex];
        var b = _clip.Frames[aIndex + 1];
        float span = b.Time - a.Time;
        if (span < 0.002f) { _status.Text = "These two keys are too close together to insert between."; return; }

        float t = EnsureFreeTime(RoundTime(a.Time + span * 0.5f));
        var pose = InterpolatePose(a, b, 0.5f, _clip.Interpolation == 1);
        _clip.Frames.Add(new GrabAnimationFrame { Time = t, Position = pose.Position, Rotation = pose.Rotation });
        SortFrames(_clip);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(FindFrameAt(t));
        _status.Text = $"Inserted an in-between at {t:0.###}s between {a.Time:0.##}s and {b.Time:0.##}s.";
    }

    private void DuplicateKey()
    {
        if (_clip == null || _frameIndex < 0 || _frameIndex >= _clip.Frames.Count) { _status.Text = "Select a key to duplicate."; return; }
        if (_clip.Frames.Count >= MaxFrames) { _status.Text = $"This object is at GRAB's {GrabLimits.MaxAnimationFrames}-key animation limit."; return; }
        var source = _clip.Frames[_frameIndex];
        float t = RoundTime(_timeline.Time);
        if (MathF.Abs(t - source.Time) < 0.005f) t = RoundTime(source.Time + MathF.Max(0.1f, StopMotionInterval));
        t = EnsureFreeTime(t);
        _clip.Frames.Add(new GrabAnimationFrame { Time = t, Position = source.Position, Rotation = source.Rotation });
        SortFrames(_clip);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(FindFrameAt(t));
        _status.Text = $"Duplicated the key at {t:0.###}s.";
    }

    private void GoPreviousKey() => GoToKey(-1);
    private void GoNextKey() => GoToKey(1);

    private void GoToKey(int direction)
    {
        if (_clip == null || _clip.Frames.Count == 0) return;
        StopPreview();
        SortFrames(_clip);
        float current = _timeline.Time;
        int target = direction < 0
            ? _clip.Frames.FindLastIndex(f => f.Time < current - 0.001f)
            : _clip.Frames.FindIndex(f => f.Time > current + 0.001f);
        if (target < 0) target = direction < 0 ? 0 : _clip.Frames.Count - 1;
        SelectFrame(target);
        _status.Text = $"Key {target + 1} of {_clip.Frames.Count} at {_clip.Frames[target].Time:0.###}s.";
    }

    private void LerpSelectedToNext()
    {
        if (_clip == null || _frameIndex < 0 || _frameIndex >= _clip.Frames.Count) { _status.Text = "Select a key first."; return; }
        SortFrames(_clip);
        if (_frameIndex >= _clip.Frames.Count - 1) { _status.Text = "The last key has no next key to lerp toward."; return; }
        var a = _clip.Frames[_frameIndex];
        var b = _clip.Frames[_frameIndex + 1];
        a.Position = Vec3.Lerp(a.Position, b.Position, 0.5f);
        a.Rotation = Quat.Lerp(a.Rotation, b.Rotation, 0.5f);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(_frameIndex);
        _status.Text = "Moved the selected key halfway toward the next key.";
    }

    private void MakeStopMotion()
    {
        if (_clip == null || _clip.Frames.Count < 2) { _status.Text = "Add at least two keys first."; return; }
        SortFrames(_clip);
        var result = new List<GrabAnimationFrame>();
        for (int i = 0; i < _clip.Frames.Count; i++)
        {
            var f = _clip.Frames[i];
            result.Add(CloneFrame(f));
            if (i >= _clip.Frames.Count - 1) continue;
            var next = _clip.Frames[i + 1];
            float gap = next.Time - f.Time;
            if (gap <= 0.003f) continue;
            float holdAt = next.Time - MathF.Min(0.0015f, gap * 0.01f);
            if (holdAt > f.Time + 0.0005f)
                result.Add(new GrabAnimationFrame { Time = holdAt, Position = f.Position, Rotation = f.Rotation });
        }
        if (result.Count > MaxFrames)
        {
            _status.Text = $"Stop-motion would exceed GRAB's {GrabLimits.MaxAnimationFrames}-key object limit ({result.Count} keys). Use fewer keys or a shorter clip.";
            return;
        }
        _clip.Frames = result;
        _clip.Interpolation = 0;
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(0);
        _status.Text = $"Made the clip step from key to key ({result.Count} keys, linear interpolation).";
    }

    private void BakeEase()
    {
        if (_clip == null || _clip.Frames.Count < 2) { _status.Text = "Add at least two keys first."; return; }
        SortFrames(_clip);
        var frames = _clip.Frames.ToList();
        float duration = frames[^1].Time - frames[0].Time;
        if (duration <= 0.001f) { _status.Text = "The keys span no time, so there is nothing to bake."; return; }
        float interval = MathF.Max(0.016f, BakeStep);
        int ease = Math.Clamp(_easeMode.SelectedIndex, 0, 4);
        var result = new List<GrabAnimationFrame>();
        for (int i = 0; i < frames.Count; i++)
        {
            result.Add(CloneFrame(frames[i]));
            if (i >= frames.Count - 1) continue;
            var a = frames[i];
            var b = frames[i + 1];
            float gap = b.Time - a.Time;
            if (gap <= 0.001f) continue;
            int steps = Math.Clamp((int)MathF.Ceiling(gap / interval), 1, 60);
            for (int k = 1; k < steps; k++)
            {
                if (result.Count >= MaxFrames - 1) break;
                float u = k / (float)steps;
                var pose = InterpolatePose(a, b, EaseValue(u, ease), smooth: false);
                result.Add(new GrabAnimationFrame { Time = a.Time + gap * u, Position = pose.Position, Rotation = pose.Rotation });
            }
        }
        result.Sort((x, y) => x.Time.CompareTo(y.Time));
        if (result.Count > MaxFrames)
        {
            var final = result[^1];
            result = result.Take(MaxFrames - 1).ToList();
            if (!result.Contains(final)) result.Add(final);
        }
        _clip.Frames = result;
        _clip.Interpolation = 0;
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(0);
        _status.Text = $"Baked {EaseName(ease)} into {result.Count} keys. The exported clip uses linear interpolation.";
    }

    private void BakeInterval()
    {
        if (_clip == null || _clip.Frames.Count < 2) { _status.Text = "Add at least two keys first."; return; }
        SortFrames(_clip);
        var frames = _clip.Frames.ToList();
        float duration = frames[^1].Time - frames[0].Time;
        if (duration <= 0.001f) { _status.Text = "The keys span no time, so there is nothing to bake."; return; }
        float interval = MathF.Max(0.016f, BakeStep);
        int steps = Math.Clamp((int)MathF.Ceiling(duration / interval), 1, MaxFrames - 1);
        bool smooth = _clip.Interpolation == 1;
        var baked = new List<GrabAnimationFrame>();
        for (int i = 0; i <= steps; i++)
        {
            float t = frames[0].Time + duration * (i / (float)steps);
            var pose = SampleFrames(frames, t, smooth);
            baked.Add(new GrabAnimationFrame { Time = t, Position = pose.Position, Rotation = pose.Rotation });
        }
        _clip.Frames = baked;
        _clip.Interpolation = 0;
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(0);
        _status.Text = $"Resampled the clip every {BakeStep:0.###}s into {baked.Count} keys.";
    }

    private float StopMotionInterval => Math.Clamp(Parse(_stopInterval.Text, 0.1f), 0.02f, 2f);
    private float BakeStep => Math.Clamp(Parse(_bakeInterval.Text, 0.05f), 0.016f, 1f);

    private static float RoundTime(float t) => MathF.Round(MathF.Max(0, t) * 1000f) / 1000f;

    private float EnsureFreeTime(float t)
    {
        if (_clip == null) return t;
        for (int i = 0; i < 40; i++, t = RoundTime(t + 0.01f))
            if (!_clip.Frames.Any(f => MathF.Abs(f.Time - t) < 0.0015f)) return t;
        return RoundTime(t + 0.04f);
    }

    private static void SortFrames(GrabAnimation clip) => clip.Frames.Sort((a, b) => a.Time.CompareTo(b.Time));

    private static GrabAnimationFrame CloneFrame(GrabAnimationFrame f) =>
        new() { Time = f.Time, Position = f.Position, Rotation = f.Rotation };

    private static (Vec3 Position, Quat Rotation) InterpolatePose(GrabAnimationFrame a, GrabAnimationFrame b, float u, bool smooth)
    {
        u = Math.Clamp(u, 0f, 1f);
        if (smooth) u = u * u * (3f - 2f * u);
        return (Vec3.Lerp(a.Position, b.Position, u), Quat.Lerp(a.Rotation, b.Rotation, u));
    }

    private static (Vec3 Position, Quat Rotation) SampleFrames(List<GrabAnimationFrame> frames, float time, bool smooth)
    {
        if (frames.Count == 0) return (Vec3.Zero, Quat.Identity);
        if (time <= frames[0].Time) return (frames[0].Position, frames[0].Rotation);
        if (time >= frames[^1].Time) return (frames[^1].Position, frames[^1].Rotation);
        for (int i = 0; i < frames.Count - 1; i++)
        {
            var a = frames[i];
            var b = frames[i + 1];
            if (time > b.Time) continue;
            float span = b.Time - a.Time;
            float u = span <= 0.0001f ? 0f : (time - a.Time) / span;
            return InterpolatePose(a, b, u, smooth);
        }
        return (frames[^1].Position, frames[^1].Rotation);
    }

    private static float EaseValue(float u, int ease) => ease switch
    {
        1 => u * u,
        2 => 1f - (1f - u) * (1f - u),
        3 => u * u * (3f - 2f * u),
        4 => 0f,
        _ => u
    };

    private static string EaseName(int ease) => ease switch
    {
        1 => "ease in",
        2 => "ease out",
        3 => "ease in-out",
        4 => "stop-motion holds",
        _ => "linear lerp"
    };

    private void AnimationPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase || (Keyboard.FocusedElement is ComboBox combo && combo.IsEditable)) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        switch (e.Key)
        {
            case Key.K:
                CaptureKey();
                e.Handled = true;
                break;
            case Key.Space:
                TogglePreview();
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                GoPreviousKey();
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                GoNextKey();
                e.Handled = true;
                break;
        }
    }

    private int FindFrameAt(float t)
    {
        if (_clip == null || _clip.Frames.Count == 0) return -1;
        int best = 0;
        float delta = float.MaxValue;
        for (int i = 0; i < _clip.Frames.Count; i++)
        {
            float d = MathF.Abs(_clip.Frames[i].Time - t);
            if (d < delta) { delta = d; best = i; }
        }
        return best;
    }

    private void AddKeyAtEnd()
    {
        if (_clip == null) { NewClip(); if (_clip == null) return; }
        if (_clip.Frames.Count >= MaxFrames) { _status.Text = $"This object is at GRAB's {GrabLimits.MaxAnimationFrames}-key animation limit."; return; }
        float t = EnsureFreeTime(RoundTime(AnimationSampler.Duration(_clip) + MathF.Max(0.1f, StopMotionInterval)));
        _clip.Frames.Add(new GrabAnimationFrame { Time = t, Position = _node.Position, Rotation = _node.Rotation });
        SortFrames(_clip);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(FindFrameAt(t));
        _status.Text = $"Appended a key at {t:0.###}s.";
    }

    private void ApplyKeyFields()
    {
        if (_clip == null || _frameIndex < 0 || _frameIndex >= _clip.Frames.Count) return;
        var f = _clip.Frames[_frameIndex];
        if (float.TryParse(_frameTime.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) f.Time = MathF.Max(0, t);
        f.Position = new Vec3(Parse(_px.Text, f.Position.X), Parse(_py.Text, f.Position.Y), Parse(_pz.Text, f.Position.Z));
        f.Rotation = Quat.FromEulerDegrees(new Vec3(Parse(_rx.Text, 0), Parse(_ry.Text, 0), Parse(_rz.Text, 0)));
        SortFrames(_clip);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(_clip.Frames.IndexOf(f));
        _status.Text = "Key updated.";
    }

    private static float Parse(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void CaptureSelectedFrameFromObject()
    {
        if (_clip == null || _frameIndex < 0 || _frameIndex >= _clip.Frames.Count) return;
        _clip.Frames[_frameIndex].Position = _node.Position;
        _clip.Frames[_frameIndex].Rotation = _node.Rotation;
        Dirty(continuous: false);
        SelectFrame(_frameIndex);
        _status.Text = "Key updated from the scene object.";
    }

    private void ApplyKeyToObject()
    {
        if (_clip == null || _frameIndex < 0 || _frameIndex >= _clip.Frames.Count) return;
        var f = _clip.Frames[_frameIndex];
        _node.Position = f.Position;
        _node.Rotation = f.Rotation;
        _ctx.Changed(false);
        _ctx.PreviewPose(_node, f.Position, f.Rotation);
        _status.Text = "Scene object moved to the selected key.";
    }

    private void DeleteFrame(int index)
    {
        if (_clip == null || index < 0 || index >= _clip.Frames.Count) return;
        if (_clip.Frames.Count <= 1) { _status.Text = "A clip needs at least one key."; return; }
        _clip.Frames.RemoveAt(index);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        SelectFrame(Math.Clamp(index, 0, _clip.Frames.Count - 1));
        _status.Text = "Key deleted.";
    }

    private void ReverseClip()
    {
        if (_clip == null) return;
        int index = _clips.SelectedIndex;
        var reversed = AnimationSampler.Reversed(_clip, _clip.Name);
        reversed.Name = _clip.Name;
        _node.Animations[index] = reversed;
        Dirty(continuous: false);
        RefreshClipList(index);
        _status.Text = "Clip reversed in time.";
    }

    private void NormalizeClip()
    {
        if (_clip == null) return;
        AnimationSampler.Normalize(_clip);
        Dirty(continuous: false);
        RefreshClipList(_clips.SelectedIndex);
        _status.Text = "Keys sorted and de-duplicated.";
    }

    private void DuplicateClip()
    {
        if (_clip == null) return;
        var copy = new GrabAnimation
        {
            Name = UniqueClipName(_clip.Name + " Copy"),
            Direction = _clip.Direction, Speed = _clip.Speed, Interpolation = _clip.Interpolation,
            Frames = _clip.Frames.Select(f => new GrabAnimationFrame { Time = f.Time, Position = f.Position, Rotation = f.Rotation }).ToList()
        };
        _node.Animations.Add(copy);
        Dirty(continuous: false);
        RefreshClipList(_node.Animations.Count - 1);
    }

    private void CopyClip()
    {
        if (_clip == null) { _status.Text = "Select a clip to copy."; return; }
        _clipboard = new GrabAnimation
        {
            Name = _clip.Name, Direction = _clip.Direction, Speed = _clip.Speed, Interpolation = _clip.Interpolation,
            Frames = _clip.Frames.Select(f => new GrabAnimationFrame { Time = f.Time, Position = f.Position, Rotation = f.Rotation }).ToList()
        };
        _status.Text = "Copied " + _clip.Name + ". Switch objects and press Paste Clip.";
    }

    private void PasteClip()
    {
        if (_clipboard == null) { _status.Text = "Copy a clip first."; return; }
        var pasted = new GrabAnimation
        {
            Name = UniqueClipName(_clipboard.Name),
            Direction = _clipboard.Direction, Speed = _clipboard.Speed, Interpolation = _clipboard.Interpolation,
            Frames = _clipboard.Frames.Select(f => new GrabAnimationFrame { Time = f.Time, Position = f.Position, Rotation = f.Rotation }).ToList()
        };
        _node.Animations.Add(pasted);
        Dirty(continuous: false);
        RefreshClipList(_node.Animations.Count - 1);
        _status.Text = "Pasted " + pasted.Name + " onto " + _node.DisplayName + ".";
    }

    private void DeleteClip()
    {
        if (_clip == null) return;
        _node.Animations.Remove(_clip);
        if (_node.ActiveAnimation >= _node.Animations.Count)
            _node.ActiveAnimation = _node.Animations.Count > 0 ? 0 : -1;
        Dirty(continuous: false);
        RefreshClipList(0);
        _status.Text = "Clip deleted.";
    }

    private void UpdateSnippet()
    {
        if (_clip == null)
        {
            _snippet.Text = "Create a clip to generate C# control code.";
            _snippetInfo.Text = "No clip selected.";
            return;
        }
        string literal = _clip.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        _snippet.Text =
            "using Grab;\n\n" +
            "public class AnimControl : CodeBlock\n" +
            "{\n" +
            "    GameObject target = GameObject.Find(\"" + _node.DisplayName.Replace("\"", "") + "\");\n\n" +
            "    void Start()\n    {\n" +
            "        Animator.Play(target, \"" + literal + "\", " + (_loop.IsChecked == true ? "true" : "false") + ");\n" +
            "    }\n\n" +
            "    void Update()\n    {\n" +
            "        // Animator.Stop(target);\n" +
            "        // Animator.Reverse(target, \"" + literal + "\");\n" +
            "        // Animator.Pause(target);  Animator.Resume(target);\n" +
            "    }\n}\n";
        _snippetInfo.Text = $"{_clip.Frames.Count} keys · {AnimationSampler.Duration(_clip):0.##}s · {( _clip.Direction == 1 ? "ping-pong" : "restart")} · {(_clip.Interpolation == 1 ? "smooth" : "linear")} · {_clip.Speed:0.##}x\nObject: {_node.DisplayName}";
    }

    private void CopySnippet()
    {
        try { Clipboard.SetText(_snippet.Text); _status.Text = "C# snippet copied to the clipboard."; }
        catch (Exception ex) { _status.Text = "Clipboard unavailable: " + ex.Message; }
    }

    private void InsertSnippet()
    {
        if (_clip == null) { _status.Text = "Create a clip first."; return; }
        if (_ctx.CreateCodeBlock(_snippet.Text))
        {
            _status.Text = "Created a new Code Block with this animation controller.";
            return;
        }
        try
        {
            Clipboard.SetText(_snippet.Text);
            _status.Text = "Code block creation is unavailable here; the snippet was copied instead.";
        }
        catch (Exception ex) { _status.Text = "Clipboard unavailable: " + ex.Message; }
    }

    /// <summary>Exercises the authoring tools without a visible window (used by --selftest).</summary>
    internal string RunToolSelfTest()
    {
        var errors = new List<string>();
        try
        {
            _node = _ctx.Level.AllNodes().FirstOrDefault(n => n.Kind is NodeKind.Static or NodeKind.Sign or NodeKind.Crumbling)
                ?? _ctx.Level.Nodes.FirstOrDefault()!;
            if (_node == null) return "no scene object";
            _node.Animations.Clear();
            _node.Position = new Vec3(0, 0, 0);
            _clip = null;
            _frameIndex = -1;
            _stopMotion.IsChecked = true;
            _stopInterval.Text = "0.1";
            _timeline.Time = 0;
            CaptureKey();
            if (_timeline.Time < 0.09f || _timeline.Time > 0.11f) errors.Add("stop-motion did not advance the playhead");
            _node.Position = new Vec3(1, 0, 0);
            CaptureKey();
            if (_clip == null || _clip.Frames.Count != 2) errors.Add("stop-motion capture count " + (_clip?.Frames.Count ?? -1));

            _stopMotion.IsChecked = false;
            _node.Position = new Vec3(2, 0, 0);
            _timeline.Time = 1f;
            CaptureKey();
            if (_clip == null || _clip.Frames.Count != 3) errors.Add("capture count " + (_clip?.Frames.Count ?? -1));
            _frameIndex = 0;
            AddInBetween();
            if (_clip == null || _clip.Frames.Count != 4) errors.Add("in-between count " + (_clip?.Frames.Count ?? -1));
            _frameIndex = 1;
            DuplicateKey();
            if (_clip == null || _clip.Frames.Count != 5) errors.Add("duplicate count " + (_clip?.Frames.Count ?? -1));
            _frameIndex = 0;
            LerpSelectedToNext();
            MakeStopMotion();
            if (_clip == null || _clip.Interpolation != 0) errors.Add("make-step did not switch to linear");
            _easeMode.SelectedIndex = 3;
            _bakeInterval.Text = "0.05";
            BakeEase();
            if (_clip == null || _clip.Frames.Count < 3 || _clip.Interpolation != 0) errors.Add("bake ease failed");
            BakeInterval();
            if (_clip == null || _clip.Frames.Count < 2 || _clip.Frames.Count > GrabLimits.MaxAnimationFrames)
                errors.Add("bake interval count " + (_clip?.Frames.Count ?? -1));
            _node.Animations.Clear();
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
        }
        return string.Join(" | ", errors);
    }

    private void Dirty(bool continuous = true)
    {
        _ctx.Changed(continuous);
        _timeline.InvalidateVisual();
        if (_clip != null) PreviewAt(_timeline.Time);
    }

    private static UIElement StepLine(string number, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 2), LastChildFill = true };
        var badge = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(198, 198, 198)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15
        });
        return row;
    }
}
