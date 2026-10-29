using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GrabEngine.AI;
using GrabEngine.Core;
using GrabEngine.Formats;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace GrabEngine.UI;

/// <summary>
/// The Neural Lab. Unlike the first version, this is a general controller authoring
/// tool: any number of dense layers, any readable/writable GRAB property, real
/// object connections, scene-based training, and a C#/GASM exporter.
/// </summary>
public sealed class NeuralLabWindow : Window
{
    public event Action? Applied;

    private readonly GrabLevel _level;
    private NeuralLabConfig _config;
    private readonly bool _standalone;
    private bool _closed;
    private bool _suspend;
    private bool _training;
    private CancellationTokenSource? _cts;
    private NeuralLabReport? _report;
    private readonly List<NeuralLabGeneration> _history = new();

    private readonly TextBlock _status = Theme.Hint("Ready.");
    private readonly TextBlock _summary = new()
    {
        FontSize = 11,
        Foreground = Theme.Dim,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right
    };
    private readonly StackPanel _inputList = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _outputList = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _inspectorHost = new() { Margin = new Thickness(0, 0, 0, 0) };
    private readonly Canvas _graph = new() { Background = Theme.Field, ClipToBounds = true, MinHeight = 300 };
    private readonly Canvas _chart = new() { Background = Theme.Field, ClipToBounds = true, MinHeight = 190 };
    private readonly TextBox _log = Theme.TextBox("", multiline: true, height: 130);
    private readonly TextBox _codePreview = Theme.TextBox("", multiline: true, height: 240);
    private readonly TextBlock _generationValue = ValueText("—");
    private readonly TextBlock _distanceValue = ValueText("—");
    private readonly TextBlock _successValue = ValueText("—");
    private readonly TextBlock _modeValue = ValueText("—");
    private readonly TextBlock _solvedValue = ValueText("—");
    private readonly TextBlock _architectureLabel = new()
    {
        Foreground = Theme.Dim,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0)
    };

    private ComboBox _goalCombo = null!;
    private ComboBox _controlCombo = null!;
    private ComboBox _connectCombo = null!;
    private ComboBox _modeCombo = null!;
    private ComboBox _motionCombo = null!;
    private TextBlock _motionHint = null!;
    private TextBox _targetSpeedBox = null!;
    private TextBox _targetRadiusBox = null!;
    private TextBox _targetTeleportBox = null!;
    private TextBox _successBox = null!;
    private TextBox _strengthBox = null!;
    private TextBox _rotationBox = null!;
    private TextBox _spreadBox = null!;
    private TextBox _startsBox = null!;
    private TextBox _populationBox = null!;
    private TextBox _generationsBox = null!;
    private TextBox _patienceBox = null!;
    private TextBox _stepsBox = null!;
    private TextBox _seedBox = null!;
    private TextBox _mutationRateBox = null!;
    private TextBox _mutationScaleBox = null!;
    private TextBox _runtimeIntervalBox = null!;
    private TextBox _runtimeStepBox = null!;
    private TextBox _runtimeLimitBox = null!;
    private CheckBox _stopWhenSolved = null!;
    private CheckBox _autoApply = null!;
    private TextBox _nameBox = null!;
    private CheckBox _startActive = null!;
    private CheckBox _lateUpdate = null!;

    private Button _trainButton = null!;
    private Button _stopButton = null!;
    private Button _applyButton = null!;
    private Button _exportButton = null!;

    private NeuralBinding? _selectedBinding;
    private int _selectedGraphLayer = -1;
    private int _selectedGraphNeuron = -1;
    private int _selectedBindingIndex = -1;

    public NeuralLabWindow(Window owner) : this(owner, CreatePreviewLevel(), standalone: true)
    {
    }

    public NeuralLabWindow(Window owner, GrabLevel level) : this(owner, level, standalone: false)
    {
    }

    private NeuralLabWindow(Window owner, GrabLevel level, bool standalone)
    {
        Owner = owner;
        _level = level;
        _standalone = standalone;
        _level.EnsureNames();
        _config = (_level.NeuralLab?.Clone()) ?? NeuralLabConfig.CreateStarter(_level);
        _config.EnsureValid();

        Title = standalone ? "Neural Lab — network editor" : "Neural Lab — " + Path.GetFileName(level.FilePath ?? level.Title);
        Width = 1460;
        Height = 900;
        MinWidth = 1120;
        MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); };

        _log.IsReadOnly = true;
        _log.FontFamily = new FontFamily("Consolas");
        _log.FontSize = 11;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _codePreview.IsReadOnly = true;
        _codePreview.FontFamily = new FontFamily("Consolas");
        _codePreview.FontSize = 11;
        _codePreview.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _codePreview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        Content = BuildRoot();
        _graph.SizeChanged += (_, _) => DrawGraph();
        _chart.SizeChanged += (_, _) => DrawChart();

        RebuildAll();
        Log("Neural Lab ready.");
        Log("Connect scene objects and properties, shape the layers, train in the editor or in-game, then Apply to Level. The result is normal C# compiled to real GASM.");
        if (_standalone) Log("This is a preview level; use Apply to Level only on a real project.");
    }

    private static GrabLevel CreatePreviewLevel()
    {
        var level = GrabLevel.CreateDefault();
        level.Title = "Neural Lab Preview";
        var target = new LevelNode
        {
            Name = "Target",
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = 8,
            Position = new Vec3(4.5f, 0.75f, -2.5f),
            Scale = new Vec3(1.3f, 1.3f, 1.3f),
            Color1 = new Col(0.2f, 1f, 0.4f),
            IsNeon = true
        };
        level.AddNode(target);
        level.EnsureNames();
        level.NeuralLab = NeuralLabConfig.CreateStarter(level, goalId: target.EditorId);
        return level;
    }

    private UIElement BuildRoot()
    {
        var root = new DockPanel();

        var headerContent = new Grid();
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(Theme.Heading("NEURAL LAB"));
        titleStack.Children.Add(new TextBlock
        {
            Text = "Connect scene objects, build a dense network, train it here or inside Play mode, and bake it into real GASM.",
            Foreground = Theme.Dim,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        headerContent.Children.Add(titleStack);
        Grid.SetColumn(_summary, 1);
        headerContent.Children.Add(_summary);
        var header = Theme.AccentBar(headerContent);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footerContent = new Grid();
        footerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 12, 0);
        footerContent.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _trainButton = Theme.Button("Train", accent: true);
        _trainButton.Margin = new Thickness(0, 0, 6, 0);
        _trainButton.Click += (_, _) => StartTraining();
        buttons.Children.Add(_trainButton);
        _stopButton = Theme.Button("Stop");
        _stopButton.IsEnabled = false;
        _stopButton.Margin = new Thickness(0, 0, 6, 0);
        _stopButton.Click += (_, _) => { _cts?.Cancel(); _status.Text = "Stopping training…"; };
        buttons.Children.Add(_stopButton);
        _exportButton = Theme.Button("Export .level…");
        _exportButton.Margin = new Thickness(0, 0, 6, 0);
        _exportButton.Click += (_, _) => ExportLevel();
        buttons.Children.Add(_exportButton);
        _applyButton = Theme.Button("Apply to Level", accent: true);
        _applyButton.Margin = new Thickness(0, 0, 6, 0);
        _applyButton.Click += (_, _) => ApplyToLevel(closeAfter: true);
        buttons.Children.Add(_applyButton);
        var close = Theme.Button("Close");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        footerContent.Children.Add(buttons);
        var footer = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Child = footerContent
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var left = BuildBindingsPanel();
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var center = BuildCenter();
        center.Margin = new Thickness(8, 8, 8, 8);
        Grid.SetColumn(center, 1);
        body.Children.Add(center);

        var right = BuildInspectorPanel();
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        root.Children.Add(body);
        return root;
    }

    private ScrollViewer BuildBindingsPanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 8, 12)
        };
        var stack = new StackPanel();

        stack.Children.Add(Theme.SectionHeader("START HERE"));
        stack.Children.Add(Theme.Hint("1. Pick the TARGET — what the network should chase.\n2. Pick the CONTROLLED object — what the network moves.\n3. Press Build chase controller, then Train at the bottom. The Training mode on the right chooses Editor, In-game, or Both."));
        _goalCombo = new ComboBox { Height = 24, DisplayMemberPath = "Label" };
        _controlCombo = new ComboBox { Height = 24, DisplayMemberPath = "Label" };
        _goalCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || _goalCombo.SelectedItem is not NodeChoice choice) return;
            _config.Training.GoalObjectId = choice.Id;
            RefreshSummary();
            RefreshInspector();
            RefreshCodePreview();
        };
        _controlCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || _controlCombo.SelectedItem is not NodeChoice choice) return;
            _config.Training.ControlObjectId = choice.Id;
            RefreshSummary();
            RefreshInspector();
            RefreshCodePreview();
        };
        stack.Children.Add(Theme.Row("Target", _goalCombo));
        stack.Children.Add(Theme.Hint("TARGET — the object the network should reach, follow or catch. Its position is what the network sees."));
        stack.Children.Add(Theme.Row("Controlled", _controlCombo));
        stack.Children.Add(Theme.Hint("CONTROLLED — the object the network moves. It gets the Position outputs."));

        var quickCard = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 4)
        };
        var quickStack = new StackPanel();
        quickStack.Children.Add(new TextBlock
        {
            Text = "QUICK START",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Accent
        });
        quickStack.Children.Add(Theme.Hint("Build chase controller creates a ready 3 → 8 → 3 network: target direction in, movement out, trained with the settings on the right."));
        var quickBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var chase = Theme.Button("Build chase controller", accent: true, small: true);
        chase.ToolTip = "Replaces the current inputs, outputs and weights with a steer-to-target controller using the two objects above.";
        chase.Click += (_, _) => BuildChaseController();
        quickBar.Children.Add(chase);
        var targetIn = Theme.Button("+ Target inputs", small: true);
        targetIn.Margin = new Thickness(5, 0, 0, 0);
        targetIn.ToolTip = "Adds the target's current position relative to the controlled object (three inputs).";
        targetIn.Click += (_, _) => { AddTargetDelta(); RebuildAll(); };
        quickBar.Children.Add(targetIn);
        var moveOut = Theme.Button("+ Movement outputs", small: true);
        moveOut.Margin = new Thickness(5, 0, 0, 0);
        moveOut.ToolTip = "Adds Position X/Y/Z outputs on the controlled object.";
        moveOut.Click += (_, _) => { AddControlOutputs(); RebuildAll(); };
        quickBar.Children.Add(moveOut);
        quickStack.Children.Add(quickBar);
        quickStack.Children.Add(Theme.Hint("Then press Train at the bottom. When the success rate looks good, press Apply to Level and use Play mode or export."));
        quickCard.Child = quickStack;
        stack.Children.Add(quickCard);

        stack.Children.Add(Theme.SectionHeader("TARGET BEHAVIOUR (EDITOR TRAINING)"));
        stack.Children.Add(Theme.Hint("Static trains against the target's current position. Orbit, Random walk and Teleport move the goal during each training episode so the network learns to follow the live target instead of driving to its starting position."));
        _motionCombo = new ComboBox { Height = 24, DisplayMemberPath = "Label" };
        _motionCombo.ItemsSource = new List<MotionChoice>
        {
            new(NeuralTargetMotion.Static, "Static — fixed starting position"),
            new(NeuralTargetMotion.Animated, "Animated — follow the target's clip"),
            new(NeuralTargetMotion.Orbit, "Orbit — circle around the start"),
            new(NeuralTargetMotion.RandomWalk, "Random walk — wander in a radius"),
            new(NeuralTargetMotion.Teleport, "Teleport — jump inside a radius")
        };
        _motionCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || _motionCombo.SelectedItem is not MotionChoice choice) return;
            _config.Training.TargetMotion = choice.Value;
            UpdateMotionFields();
            RefreshSummary();
            RefreshCodePreview();
        };
        stack.Children.Add(Theme.Row("Target motion", _motionCombo));
        _targetSpeedBox = NumBox(_config.Training.TargetSpeed);
        _targetRadiusBox = NumBox(_config.Training.TargetRadius);
        _targetTeleportBox = NumBox(_config.Training.TargetTeleportFrames);
        stack.Children.Add(Theme.Row("Target speed", _targetSpeedBox));
        stack.Children.Add(Theme.Row("Radius", _targetRadiusBox));
        stack.Children.Add(Theme.Row("Teleport every", _targetTeleportBox));
        _motionHint = Theme.Hint("");
        stack.Children.Add(_motionHint);

        stack.Children.Add(Theme.SectionHeader("ADVANCED — CONNECT ANY PROPERTY"));
        _connectCombo = new ComboBox { Height = 24, DisplayMemberPath = "Label" };
        stack.Children.Add(_connectCombo);
        var connectBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        var readAll = Theme.Button("Read every property → Inputs", small: true);
        readAll.Click += (_, _) => ConnectObject(read: true);
        connectBar.Children.Add(readAll);
        var writeAll = Theme.Button("Write every property → Outputs", small: true);
        writeAll.Margin = new Thickness(5, 0, 0, 0);
        writeAll.Click += (_, _) => ConnectObject(read: false);
        connectBar.Children.Add(writeAll);
        stack.Children.Add(connectBar);
        var connectBar2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        var posIn = Theme.Button("+ Position XYZ in", small: true);
        posIn.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.PositionX, read: true);
        connectBar2.Children.Add(posIn);
        var rotIn = Theme.Button("+ Rotation XYZ in", small: true);
        rotIn.Margin = new Thickness(5, 0, 0, 0);
        rotIn.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.RotationX, read: true);
        connectBar2.Children.Add(rotIn);
        var posOut = Theme.Button("+ Position XYZ out", small: true);
        posOut.Margin = new Thickness(5, 0, 0, 0);
        posOut.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.PositionX, read: false);
        connectBar2.Children.Add(posOut);
        var rotOut = Theme.Button("+ Rotation XYZ out", small: true);
        rotOut.Margin = new Thickness(5, 0, 0, 0);
        rotOut.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.RotationX, read: false);
        connectBar2.Children.Add(rotOut);
        stack.Children.Add(connectBar2);
        var connectBar3 = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var colorIn = Theme.Button("+ Color RGB in", small: true);
        colorIn.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.ColorR, read: true);
        connectBar3.Children.Add(colorIn);
        var colorOut = Theme.Button("+ Color RGB out", small: true);
        colorOut.Margin = new Thickness(5, 0, 0, 0);
        colorOut.Click += (_, _) => ConnectPropertyGroup(NeuralProperty.ColorR, read: false);
        connectBar3.Children.Add(colorOut);
        var asGoal = Theme.Button("Use as goal", small: true);
        asGoal.Margin = new Thickness(5, 0, 0, 0);
        asGoal.Click += (_, _) => UseConnectedObject(asGoal: true);
        connectBar3.Children.Add(asGoal);
        var asControl = Theme.Button("Use as controlled", small: true);
        asControl.Margin = new Thickness(5, 0, 0, 0);
        asControl.Click += (_, _) => UseConnectedObject(asGoal: false);
        connectBar3.Children.Add(asControl);
        stack.Children.Add(connectBar3);
        stack.Children.Add(Theme.Hint("Pick any block, light, sign, trigger or group. Read creates inputs from every readable property; Write creates outputs for every writable one. The quick XYZ buttons add just that vector."));

        var quick = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var auto = Theme.Button("Auto-wire steer-to-target", small: true);
        auto.Click += (_, _) => AutoWire();
        quick.Children.Add(auto);
        var reset = Theme.Button("Reset weights", small: true);
        reset.Margin = new Thickness(5, 0, 0, 0);
        reset.Click += (_, _) => { _config.RandomizeWeights(new Random().Next(1, 999999)); AfterWeightsChanged("Weights randomized"); };
        quick.Children.Add(reset);
        stack.Children.Add(quick);

        stack.Children.Add(Theme.SectionHeader("INPUTS"));
        var inBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var addIn = Theme.Button("+ Input", small: true);
        addIn.Click += (_, _) => { AddInput(); RebuildAll(); };
        inBar.Children.Add(addIn);
        var targetDelta = Theme.Button("+ Target ΔXYZ", small: true);
        targetDelta.Margin = new Thickness(5, 0, 0, 0);
        targetDelta.Click += (_, _) => { AddTargetDelta(); RebuildAll(); };
        inBar.Children.Add(targetDelta);
        var self = Theme.Button("+ Control XYZ", small: true);
        self.Margin = new Thickness(5, 0, 0, 0);
        self.Click += (_, _) => { AddControlInput(); RebuildAll(); };
        inBar.Children.Add(self);
        var targetRot = Theme.Button("+ Target rot", small: true);
        targetRot.Margin = new Thickness(5, 0, 0, 0);
        targetRot.Click += (_, _) => { AddGoalInputGroup(NeuralProperty.RotationX); RebuildAll(); };
        inBar.Children.Add(targetRot);
        var targetColor = Theme.Button("+ Target color", small: true);
        targetColor.Margin = new Thickness(5, 0, 0, 0);
        targetColor.Click += (_, _) => { AddGoalInputGroup(NeuralProperty.ColorR); RebuildAll(); };
        inBar.Children.Add(targetColor);
        var registersIn = Theme.Button("+ Registers", small: true);
        registersIn.Margin = new Thickness(5, 0, 0, 0);
        registersIn.Click += (_, _) => { AddRegisterBindings(read: true); RebuildAll(); };
        inBar.Children.Add(registersIn);
        stack.Children.Add(inBar);
        stack.Children.Add(Theme.Hint("Inputs are evaluated every frame. Relative-to subtracts another object, so Target.Position relative to the controlled object becomes a direction vector."));
        stack.Children.Add(_inputList);

        stack.Children.Add(Theme.SectionHeader("OUTPUTS"));
        var outBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var addOut = Theme.Button("+ Output", small: true);
        addOut.Click += (_, _) => { AddOutput(); RebuildAll(); };
        outBar.Children.Add(addOut);
        var controlPos = Theme.Button("+ Control XYZ", small: true);
        controlPos.Margin = new Thickness(5, 0, 0, 0);
        controlPos.Click += (_, _) => { AddControlOutputs(); RebuildAll(); };
        outBar.Children.Add(controlPos);
        var controlRot = Theme.Button("+ Control rot", small: true);
        controlRot.Margin = new Thickness(5, 0, 0, 0);
        controlRot.Click += (_, _) => { AddControlOutputGroup(NeuralProperty.RotationX); RebuildAll(); };
        outBar.Children.Add(controlRot);
        var controlColor = Theme.Button("+ Control color", small: true);
        controlColor.Margin = new Thickness(5, 0, 0, 0);
        controlColor.Click += (_, _) => { AddControlOutputGroup(NeuralProperty.ColorR); RebuildAll(); };
        outBar.Children.Add(controlColor);
        var registersOut = Theme.Button("+ Registers", small: true);
        registersOut.Margin = new Thickness(5, 0, 0, 0);
        registersOut.Click += (_, _) => { AddRegisterBindings(read: false); RebuildAll(); };
        outBar.Children.Add(registersOut);
        var signText = Theme.Button("+ Sign text (ASCII)", small: true);
        signText.Margin = new Thickness(5, 0, 0, 0);
        signText.ToolTip = "Adds Sign.Write on the first Sign in the level. One network output writes one character code per frame.";
        signText.Click += (_, _) => { AddSignTextOutput(); RebuildAll(); };
        outBar.Children.Add(signText);
        stack.Children.Add(outBar);
        stack.Children.Add(Theme.Hint("Position/Rotation outputs use Add by default: each frame the network writes a velocity, not a teleport. Set means write the value directly. To write text, connect a Sign and choose Sign Write (ASCII code): one output writes one character per frame, so several signs can spell a message. Use Level registers to hand values between controllers."));
        stack.Children.Add(_outputList);

        scroll.Content = stack;
        return scroll;
    }

    private Grid BuildCenter()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            Child = new DockPanel()
        };
        var bar = new WrapPanel();
        var addLayer = Theme.Button("+ Layer", small: true);
        addLayer.Click += (_, _) => AddHiddenLayer();
        bar.Children.Add(addLayer);
        var removeLayer = Theme.Button("- Layer", small: true);
        removeLayer.Margin = new Thickness(4, 0, 0, 0);
        removeLayer.Click += (_, _) => RemoveHiddenLayer();
        bar.Children.Add(removeLayer);
        var addNeuron = Theme.Button("+ Neuron", small: true);
        addNeuron.Margin = new Thickness(4, 0, 0, 0);
        addNeuron.Click += (_, _) => ChangeNeuronCount(1);
        bar.Children.Add(addNeuron);
        var removeNeuron = Theme.Button("- Neuron", small: true);
        removeNeuron.Margin = new Thickness(4, 0, 0, 0);
        removeNeuron.Click += (_, _) => ChangeNeuronCount(-1);
        bar.Children.Add(removeNeuron);
        var zero = Theme.Button("Zero", small: true);
        zero.Margin = new Thickness(4, 0, 0, 0);
        zero.Click += (_, _) => { _config.ZeroWeights(); AfterWeightsChanged("All weights set to 0"); };
        bar.Children.Add(zero);
        bar.Children.Add(_architectureLabel);
        toolbar.Child = bar;
        Grid.SetRow(toolbar, 0);
        grid.Children.Add(toolbar);

        var tabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };
        tabs.SelectedIndex = 0;
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2) RefreshCodePreview();
            if (tabs.SelectedIndex == 1) DrawChart();
        };

        var graphTab = new TabItem { Header = "Network" };
        var graphContainer = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2),
            Child = _graph
        };
        graphTab.Content = graphContainer;
        tabs.Items.Add(graphTab);

        var trainingTab = new TabItem { Header = "Training" };
        var trainingGrid = new Grid();
        trainingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        trainingGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        trainingGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(135) });
        var stats = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        for (int i = 0; i < 5; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddStat(stats, 0, "GENERATION", _generationValue);
        AddStat(stats, 1, "BEST MEAN DISTANCE", _distanceValue);
        AddStat(stats, 2, "SUCCESS", _successValue);
        AddStat(stats, 3, "TRAINING", _modeValue);
        AddStat(stats, 4, "STATE", _solvedValue);
        Grid.SetRow(stats, 0);
        trainingGrid.Children.Add(stats);
        var chartBorder = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            Margin = new Thickness(6, 0, 6, 6),
            Child = _chart
        };
        Grid.SetRow(chartBorder, 1);
        trainingGrid.Children.Add(chartBorder);
        var logBorder = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3),
            Margin = new Thickness(6, 0, 6, 6),
            Child = _log
        };
        Grid.SetRow(logBorder, 2);
        trainingGrid.Children.Add(logBorder);
        trainingTab.Content = trainingGrid;
        tabs.Items.Add(trainingTab);

        var codeTab = new TabItem { Header = "C# preview" };
        var codeGrid = new Grid();
        codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        codeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var codeBar = new WrapPanel { Margin = new Thickness(6, 4, 6, 6) };
        var refresh = Theme.Button("Refresh", small: true);
        refresh.Click += (_, _) => RefreshCodePreview();
        codeBar.Children.Add(refresh);
        var copy = Theme.Button("Copy", small: true);
        copy.Margin = new Thickness(5, 0, 0, 0);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_codePreview.Text); _status.Text = "Copied generated C#."; }
            catch (Exception ex) { _status.Text = "Clipboard unavailable: " + ex.Message; }
        };
        codeBar.Children.Add(copy);
        codeBar.Children.Add(new TextBlock
        {
            Text = "This is what the C# to GASM compiler receives.",
            Foreground = Theme.Dim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        Grid.SetRow(codeBar, 0);
        codeGrid.Children.Add(codeBar);
        var codeBorder = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3),
            Margin = new Thickness(6, 0, 6, 6),
            Child = _codePreview
        };
        Grid.SetRow(codeBorder, 1);
        codeGrid.Children.Add(codeBorder);
        codeTab.Content = codeGrid;
        tabs.Items.Add(codeTab);

        Grid.SetRow(tabs, 1);
        grid.Children.Add(tabs);
        return grid;
    }

    private ScrollViewer BuildInspectorPanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 8, 12, 12)
        };
        var stack = new StackPanel();
        stack.Children.Add(Theme.SectionHeader("INSPECTOR"));
        stack.Children.Add(_inspectorHost);

        stack.Children.Add(Theme.SectionHeader("MODEL"));
        _nameBox = Theme.TextBox(_config.Name);
        _nameBox.TextChanged += (_, _) => { if (!_suspend) { _config.Name = _nameBox.Text.Trim(); RefreshSummary(); RefreshCodePreview(); } };
        stack.Children.Add(Theme.Row("Name", _nameBox));
        _startActive = Theme.Check("Start Active", _config.StartActive);
        _startActive.Checked += (_, _) => { _config.StartActive = true; RefreshCodePreview(); };
        _startActive.Unchecked += (_, _) => { _config.StartActive = false; RefreshCodePreview(); };
        stack.Children.Add(_startActive);
        _lateUpdate = Theme.Check("Late Update", _config.LateUpdate);
        _lateUpdate.Checked += (_, _) => { _config.LateUpdate = true; RefreshCodePreview(); };
        _lateUpdate.Unchecked += (_, _) => { _config.LateUpdate = false; RefreshCodePreview(); };
        stack.Children.Add(_lateUpdate);

        stack.Children.Add(Theme.SectionHeader("TRAINING"));
        _modeCombo = new ComboBox { Height = 24, DisplayMemberPath = "Label" };
        _modeCombo.ItemsSource = new List<ModeChoice>
        {
            new(NeuralTrainingMode.Editor, "Editor — bake final weights"),
            new(NeuralTrainingMode.Runtime, "In-game — tune while playing"),
            new(NeuralTrainingMode.Both, "Editor + in-game tuning")
        };
        _modeCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || _modeCombo.SelectedItem is not ModeChoice choice) return;
            _config.Training.Mode = choice.Value;
            _modeValue.Text = ModeLabel(choice.Value);
            RefreshCodePreview();
            RefreshSummary();
        };
        stack.Children.Add(Theme.Row("Train in", _modeCombo));
        stack.Children.Add(Theme.Hint("Editor trains here and bakes all weights. In-game exports a second trainer Code Block that keeps tuning the shared output weights in Play mode and retail GRAB; Both trains here first, then keeps adapting in game."));

        _successBox = NumBox(_config.Training.SuccessRadius);
        _strengthBox = NumBox(_config.Training.MoveSpeed);
        _rotationBox = NumBox(_config.Training.RotationSpeed);
        _spreadBox = NumBox(_config.Training.StartSpread);
        _startsBox = NumBox(_config.Training.Starts);
        _populationBox = NumBox(_config.Training.Population);
        _generationsBox = NumBox(_config.Training.Generations);
        _patienceBox = NumBox(_config.Training.Patience);
        _stepsBox = NumBox(_config.Training.EpisodeSteps);
        _seedBox = NumBox(_config.Training.Seed);
        _mutationRateBox = NumBox(_config.Training.MutationRate);
        _mutationScaleBox = NumBox(_config.Training.MutationScale);
        _runtimeIntervalBox = NumBox(_config.Training.RuntimeIntervalFrames);
        _runtimeStepBox = NumBox(_config.Training.RuntimeStep);
        _runtimeLimitBox = NumBox(_config.Training.RuntimeTuneLimit);

        stack.Children.Add(Theme.SectionHeader("OPTIMIZER"));
        stack.Children.Add(Theme.Row("Success radius", _successBox));
        stack.Children.Add(Theme.Row("Move speed", _strengthBox));
        stack.Children.Add(Theme.Row("Turn speed", _rotationBox));
        stack.Children.Add(Theme.Row("Start spread", _spreadBox));
        stack.Children.Add(Theme.Row("Training starts", _startsBox));
        stack.Children.Add(Theme.Row("Population", _populationBox));
        stack.Children.Add(Theme.Row("Generations", _generationsBox));
        stack.Children.Add(Theme.Row("Patience", _patienceBox));
        stack.Children.Add(Theme.Row("Episode steps", _stepsBox));
        stack.Children.Add(Theme.Row("Seed", _seedBox));
        stack.Children.Add(Theme.Row("Mutation rate", _mutationRateBox));
        stack.Children.Add(Theme.Row("Mutation scale", _mutationScaleBox));
        var seedBar = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var randomSeed = Theme.Button("Random seed", small: true);
        randomSeed.Click += (_, _) => { _seedBox.Text = new Random().Next(1, 99999999).ToString(CultureInfo.InvariantCulture); };
        seedBar.Children.Add(randomSeed);
        var defaults = Theme.Button("Defaults", small: true);
        defaults.Margin = new Thickness(5, 0, 0, 0);
        defaults.Click += (_, _) => ResetTrainingDefaults();
        seedBar.Children.Add(defaults);
        stack.Children.Add(seedBar);
        _stopWhenSolved = Theme.Check("Stop when every start reaches the goal", _config.Training.StopWhenSolved);
        stack.Children.Add(_stopWhenSolved);
        _autoApply = Theme.Check("Apply to the level when training finishes", _config.Training.AutoApplyAfterTraining);
        stack.Children.Add(_autoApply);

        stack.Children.Add(Theme.SectionHeader("RUNTIME TUNER"));
        stack.Children.Add(Theme.Hint("Only used by the in-game modes. A separate trainer Code Block nudges the shared output weights and keeps a nudge when it reduces the distance to the goal. The weights live in hidden bus objects so both blocks stay inside GRAB's 150-instruction and 10-connection limits."));
        stack.Children.Add(Theme.Row("Hold frames", _runtimeIntervalBox));
        stack.Children.Add(Theme.Row("Step size", _runtimeStepBox));
        stack.Children.Add(Theme.Row("Weights tuned in game", _runtimeLimitBox));
        stack.Children.Add(Theme.Hint("Each tuned weight slot costs a hidden bus connection. 12 slots is the verified maximum for a 3-output controller; lowering this value is the quickest way to fix a connection-budget warning."));

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var apply = Theme.Button("Apply", accent: true);
        apply.Click += (_, _) => ApplyToLevel(closeAfter: false);
        actions.Children.Add(apply);
        var preview = Theme.Button("Validate C#", small: true);
        preview.Margin = new Thickness(5, 0, 0, 0);
        preview.Click += (_, _) => ValidateCode();
        actions.Children.Add(preview);
        stack.Children.Add(actions);
        stack.Children.Add(Theme.Hint("GRAB limits: 150 instructions per block, 10 object connections per Code Block, and 4-character connection names. The exporter splits long programs into continuation pages automatically."));

        scroll.Content = stack;
        return scroll;
    }

    private static TextBox NumBox(double value) => Theme.TextBox(value.ToString("0.####", CultureInfo.InvariantCulture), height: 24);

    private static TextBlock ValueText(string text) => new()
    {
        Text = text,
        Foreground = Theme.Text,
        FontSize = 12,
        FontFamily = new FontFamily("Consolas"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 7)
    };

    private static void AddStat(Grid grid, int column, string label, TextBlock value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Foreground = Theme.Dim,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(value);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private sealed record NodeChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record SourceChoice(NeuralSourceKind Source, string Id, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record PropertyChoice(NeuralProperty Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record RelChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record ModeChoice(NeuralTrainingMode Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record MotionChoice(NeuralTargetMotion Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record ActivationChoice(NeuralActivation Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static string ModeLabel(NeuralTrainingMode mode) => mode switch
    {
        NeuralTrainingMode.Editor => "Editor",
        NeuralTrainingMode.Runtime => "In-game",
        _ => "Editor + in-game"
    };

    private void RebuildAll()
    {
        bool old = _suspend;
        _suspend = true;
        _config.EnsureValid();
        RefreshGoalControlCombos();
        SyncTrainingFields();
        RebuildBindingLists();
        _suspend = old;
        SyncSelection();
        DrawGraph();
        RefreshSummary();
        RefreshInspector();
        RefreshCodePreview();
    }

    private void RefreshGoalControlCombos()
    {
        bool old = _suspend;
        _suspend = true;
        var choices = _level.AllNodes()
            .Where(n => !n.Generated && n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM))
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NodeChoice(n.EditorId, n.DisplayName + "  ·  " + NodeKindInfo.Display(n.Kind)))
            .ToList();
        _goalCombo.ItemsSource = choices;
        _controlCombo.ItemsSource = choices;
        if (_connectCombo != null)
        {
            var selectedConnect = (_connectCombo.SelectedItem as NodeChoice)?.Id;
            _connectCombo.ItemsSource = choices;
            _connectCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedConnect)
                                         ?? choices.FirstOrDefault(c => c.Id == _config.Training.ControlObjectId)
                                         ?? choices.FirstOrDefault();
        }
        var goal = choices.FirstOrDefault(c => c.Id == _config.Training.GoalObjectId);
        if (goal == null && choices.Count > 0)
        {
            goal = choices[0];
            _config.Training.GoalObjectId = goal.Id;
        }
        var control = choices.FirstOrDefault(c => c.Id == _config.Training.ControlObjectId);
        if (control == null && choices.Count > 1)
        {
            control = choices.FirstOrDefault(c => c.Id != goal?.Id) ?? choices[0];
            _config.Training.ControlObjectId = control.Id;
        }
        _goalCombo.SelectedItem = goal;
        _controlCombo.SelectedItem = control;
        _suspend = old;
    }

    private void RebuildBindingLists()
    {
        _inputList.Children.Clear();
        _outputList.Children.Clear();
        if (_config.Inputs.Count == 0)
            _inputList.Children.Add(Theme.Hint("No inputs yet. Add one, or use + Target ΔXYZ to build a steering controller."));
        if (_config.Outputs.Count == 0)
            _outputList.Children.Add(Theme.Hint("No outputs yet. Connect at least one object property."));
        for (int i = 0; i < _config.Inputs.Count; i++)
            _inputList.Children.Add(BuildBindingRow(_config.Inputs[i], false, i));
        for (int i = 0; i < _config.Outputs.Count; i++)
            _outputList.Children.Add(BuildBindingRow(_config.Outputs[i], true, i));
    }

    private UIElement BuildBindingRow(NeuralBinding binding, bool output, int index)
    {
        bool selected = ReferenceEquals(_selectedBinding, binding);
        var border = new Border
        {
            Background = selected ? new SolidColorBrush(Color.FromRgb(50, 58, 64)) : new SolidColorBrush(Color.FromRgb(44, 44, 44)),
            BorderBrush = selected ? Theme.Accent : Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 4, 5, 4),
            Margin = new Thickness(0, 2, 0, 2),
            Opacity = binding.Enabled ? 1.0 : 0.55
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            _selectedBinding = binding;
            _selectedBindingIndex = index;
            _selectedGraphLayer = -1;
            _selectedGraphNeuron = -1;
            RebuildBindingLists();
            RefreshInspector();
            e.Handled = false;
        };

        var stack = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sources = SourceChoices(output);
        var sourceCombo = new ComboBox
        {
            Height = 23,
            ItemsSource = sources,
            DisplayMemberPath = "Label",
            SelectedItem = sources.FirstOrDefault(c => c.Source == binding.Source && c.Id == binding.ObjectId) ?? sources.FirstOrDefault(),
            Margin = new Thickness(0, 0, 4, 0)
        };
        sourceCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || sourceCombo.SelectedItem is not SourceChoice choice) return;
            if (choice.Source == binding.Source && choice.Id == binding.ObjectId) return;
            binding.Source = choice.Source;
            binding.ObjectId = choice.Id;
            var properties = PropertyChoicesFor(_level, binding, output);
            if (!properties.Contains(binding.Property))
                binding.Property = properties.Count > 0 ? properties[0] : NeuralProperty.PositionX;
            if (binding.Source != NeuralSourceKind.Object || string.IsNullOrEmpty(binding.RelativeToId) ||
                _level.FindByEditorId(binding.RelativeToId) == null)
                binding.RelativeToId = "";
            _selectedBinding = binding;
            RebuildBindingLists();
            RefreshInspector();
            RefreshSummary();
            RefreshCodePreview();
        };
        Grid.SetColumn(sourceCombo, 0);
        grid.Children.Add(sourceCombo);

        var properties = PropertyChoicesFor(_level, binding, output);
        var propertyChoices = properties.Select(p => new PropertyChoice(p, NeuralPropertyInfo.Display(p))).ToList();
        var propertyCombo = new ComboBox
        {
            Height = 23,
            ItemsSource = propertyChoices,
            DisplayMemberPath = "Label",
            SelectedItem = propertyChoices.FirstOrDefault(c => c.Value == binding.Property),
            Margin = new Thickness(0, 0, 4, 0)
        };
        propertyCombo.SelectionChanged += (_, _) =>
        {
            if (_suspend || propertyCombo.SelectedItem is not PropertyChoice choice) return;
            if (binding.Property == choice.Value) return;
            binding.Property = choice.Value;
            var range = NeuralPropertyInfo.DefaultRange(choice.Value);
            binding.Min = range.Min;
            binding.Max = range.Max;
            _selectedBinding = binding;
            RebuildBindingLists();
            RefreshInspector();
            RefreshSummary();
            RefreshCodePreview();
        };
        Grid.SetColumn(propertyCombo, 1);
        grid.Children.Add(propertyCombo);

        var remove = Theme.Button("×", small: true);
        remove.Width = 24;
        remove.Padding = new Thickness(0);
        remove.ToolTip = output ? "Remove output" : "Remove input";
        remove.Click += (_, _) =>
        {
            if (output) _config.Outputs.Remove(binding); else _config.Inputs.Remove(binding);
            _selectedBinding = null;
            EnsureArchitectureWeights("Binding removed — weights reinitialized");
            RebuildAll();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        stack.Children.Add(grid);

        if (!output && binding.Source == NeuralSourceKind.Object)
        {
            var rel = new ComboBox
            {
                Height = 21,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                DisplayMemberPath = "Label"
            };
            var relChoices = new List<RelChoice> { new("", "Relative to: none") };
            relChoices.AddRange(_level.AllNodes()
                .Where(n => !n.Generated && n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM))
                .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(n => new RelChoice(n.EditorId, "Relative to: " + n.DisplayName)));
            rel.ItemsSource = relChoices;
            rel.SelectedItem = relChoices.FirstOrDefault(c => c.Id == binding.RelativeToId) ?? relChoices[0];
            rel.SelectionChanged += (_, _) =>
            {
                if (_suspend || rel.SelectedItem is not RelChoice choice) return;
                binding.RelativeToId = choice.Id;
                RefreshSummary();
                RefreshCodePreview();
            };
            stack.Children.Add(rel);
        }
        border.Child = stack;
        return border;
    }

    private List<SourceChoice> SourceChoices(bool output)
    {
        var choices = new List<SourceChoice>();
        foreach (var n in _level.AllNodes()
                     .Where(n => !n.Generated && n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM))
                     .Where(n => !output || HasAnyOutput(n))
                     .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
            choices.Add(new SourceChoice(NeuralSourceKind.Object, n.EditorId, n.DisplayName));
        choices.Add(new SourceChoice(NeuralSourceKind.Player, "", output ? "Player (velocity)" : "Player"));
        choices.Add(new SourceChoice(NeuralSourceKind.Level, "", "Level registers"));
        if (!output) choices.Add(new SourceChoice(NeuralSourceKind.Time, "", "Time"));
        return choices;
    }

    private static bool HasAnyOutput(LevelNode node) =>
        NeuralPropertyInfo.AllFor(NeuralSourceKind.Object).Any(p => NeuralPropertyInfo.IsWritable(p, node));

    private static List<NeuralProperty> PropertyChoices(NeuralSourceKind source, bool output)
    {
        var list = NeuralPropertyInfo.AllFor(source).ToList();
        if (output && source == NeuralSourceKind.Player)
            list = list.Where(NeuralPropertyInfo.IsWritableForPlayer).ToList();
        if (output && source == NeuralSourceKind.Time)
            list.Clear();
        return list;
    }

    private List<NeuralProperty> PropertyChoicesFor(GrabLevel level, NeuralBinding binding, bool output)
    {
        if (binding.Source != NeuralSourceKind.Object)
            return PropertyChoices(binding.Source, output);
        var node = level.FindByEditorId(binding.ObjectId);
        if (node == null) return new List<NeuralProperty>();
        return NeuralPropertyInfo.AllFor(NeuralSourceKind.Object)
            .Where(p => output ? NeuralPropertyInfo.IsWritable(p, node) : NeuralPropertyInfo.IsReadable(p, node))
            .ToList();
    }

    private void AddInput()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId);
        var goal = _level.FindByEditorId(_config.Training.GoalObjectId);
        var binding = new NeuralBinding
        {
            Source = NeuralSourceKind.Object,
            ObjectId = goal?.EditorId ?? control?.EditorId ?? FirstObjectId(),
            Property = NeuralProperty.PositionX,
            RelativeToId = goal != null && control != null ? control.EditorId : "",
            Scale = goal != null && control != null ? 0.1f : 1f,
            Min = -1f,
            Max = 1f
        };
        _config.Inputs.Add(binding);
        _selectedBinding = binding;
        EnsureArchitectureWeights("Input added — weights reinitialized");
    }

    private void AddTargetDelta()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId);
        var goal = _level.FindByEditorId(_config.Training.GoalObjectId);
        if (control == null || goal == null)
        {
            _status.Text = "Pick a Target and a Controlled object first.";
            return;
        }
        int added = 0;
        foreach (var property in new[] { NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ })
        {
            if (_config.Inputs.Any(b => b.Source == NeuralSourceKind.Object &&
                                        b.ObjectId == goal.EditorId &&
                                        b.Property == property &&
                                        b.RelativeToId == control.EditorId))
                continue;
            var binding = new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = goal.EditorId,
                RelativeToId = control.EditorId,
                Property = property,
                Scale = 0.1f,
                Min = -1f,
                Max = 1f
            };
            _config.Inputs.Add(binding);
            _selectedBinding = binding;
            added++;
        }
        EnsureArchitectureWeights(added == 0 ? "Target direction inputs are already connected" : "Target direction inputs added");
    }

    /// <summary>One-click starter that is deliberately obvious to first-time users.</summary>
    private void BuildChaseController()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId);
        var goal = _level.FindByEditorId(_config.Training.GoalObjectId);
        if (control == null || goal == null)
        {
            _status.Text = "Pick a Target and a Controlled object first.";
            return;
        }
        if (ReferenceEquals(control, goal))
        {
            _status.Text = "Target and Controlled must be different objects.";
            return;
        }
        var starter = NeuralLabConfig.CreateStarter(_level, control.EditorId, goal.EditorId);
        if (starter.Inputs.Count == 0 || starter.Outputs.Count == 0)
        {
            _status.Text = "This object cannot be moved; pick a block or group as the Controlled object.";
            return;
        }
        starter.HiddenLayers = new List<NeuralLayer> { new() { Neurons = 8, Activation = NeuralActivation.ReLU } };
        starter.OutputActivation = NeuralActivation.Linear;
        starter.Name = string.IsNullOrWhiteSpace(_config.Name) ? "Neural Brain" : _config.Name;
        starter.CodeBlockId = _config.CodeBlockId;
        starter.StartActive = _config.StartActive;
        starter.LateUpdate = _config.LateUpdate;
        starter.Training = _config.Training.Clone();
        starter.Training.ControlObjectId = control.EditorId;
        starter.Training.GoalObjectId = goal.EditorId;
        starter.EnsureWeights();
        _config = starter;
        _selectedBinding = _config.Inputs.FirstOrDefault();
        _selectedGraphLayer = -1;
        _selectedGraphNeuron = -1;
        RebuildAll();
        _status.Text = $"Chase controller ready: {control.DisplayName} will chase {goal.DisplayName}. Press Train, then Apply to Level.";
    }

    private void AddControlInput()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId) ?? _level.FindByEditorId(FirstObjectId());
        if (control == null) return;
        foreach (var property in new[] { NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ })
        {
            var binding = new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = control.EditorId,
                Property = property,
                Scale = 0.1f,
                Min = -1f,
                Max = 1f
            };
            _config.Inputs.Add(binding);
            _selectedBinding = binding;
        }
        EnsureArchitectureWeights("Control position inputs added");
    }

    private void AddGoalInputGroup(NeuralProperty start)
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId);
        var goal = _level.FindByEditorId(_config.Training.GoalObjectId);
        if (control == null || goal == null)
        {
            _status.Text = "Choose a goal and a controlled object first.";
            return;
        }
        var properties = new List<NeuralProperty>();
        switch (start)
        {
            case NeuralProperty.RotationX:
                properties.AddRange(new[] { NeuralProperty.RotationX, NeuralProperty.RotationY, NeuralProperty.RotationZ });
                break;
            case NeuralProperty.ColorR:
                properties.AddRange(goal.Kind == NodeKind.Light
                    ? new[] { NeuralProperty.LightR, NeuralProperty.LightG, NeuralProperty.LightB }
                    : new[] { NeuralProperty.ColorR, NeuralProperty.ColorG, NeuralProperty.ColorB });
                break;
            default:
                properties.Add(start);
                break;
        }
        foreach (var property in properties)
        {
            if (!NeuralPropertyInfo.IsReadable(property, goal)) continue;
            var range = NeuralPropertyInfo.DefaultRange(property);
            _config.Inputs.Add(new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = goal.EditorId,
                RelativeToId = control.EditorId,
                Property = property,
                Scale = NeuralPropertyInfo.SuggestedScale(property),
                Min = range.Min,
                Max = range.Max,
                Clamp = true
            });
        }
        EnsureArchitectureWeights("Target property inputs added");
    }

    private void AddControlOutputGroup(NeuralProperty start)
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId);
        if (control == null) { _status.Text = "Choose a controlled object first."; return; }
        var properties = new List<NeuralProperty>();
        switch (start)
        {
            case NeuralProperty.RotationX:
                properties.AddRange(new[] { NeuralProperty.RotationX, NeuralProperty.RotationY, NeuralProperty.RotationZ });
                break;
            case NeuralProperty.ColorR:
                properties.AddRange(control.Kind == NodeKind.Light
                    ? new[] { NeuralProperty.LightR, NeuralProperty.LightG, NeuralProperty.LightB }
                    : new[] { NeuralProperty.ColorR, NeuralProperty.ColorG, NeuralProperty.ColorB });
                break;
            default:
                properties.Add(start);
                break;
        }
        foreach (var property in properties)
        {
            if (!NeuralPropertyInfo.IsWritable(property, control)) continue;
            var range = NeuralPropertyInfo.DefaultRange(property);
            _config.Outputs.Add(new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = control.EditorId,
                Property = property,
                WriteMode = property is NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ
                    ? NeuralWriteMode.Add
                    : NeuralWriteMode.Set,
                Min = range.Min,
                Max = range.Max,
                Clamp = true
            });
        }
        EnsureArchitectureWeights("Control property outputs added");
    }

    private void AddSignTextOutput()
    {
        var sign = _level.AllNodes().FirstOrDefault(n => !n.Generated && n.Kind == NodeKind.Sign);
        if (sign == null)
        {
            _status.Text = "Add a Sign object first, then add its ASCII text output.";
            return;
        }
        if (_config.Outputs.Any(b => b.Source == NeuralSourceKind.Object &&
                                     b.ObjectId == sign.EditorId && b.Property == NeuralProperty.SignWrite))
        {
            _status.Text = "That Sign already has an ASCII text output.";
            return;
        }
        var binding = new NeuralBinding
        {
            Source = NeuralSourceKind.Object,
            ObjectId = sign.EditorId,
            Property = NeuralProperty.SignWrite,
            Min = 0f,
            Max = 255f,
            Clamp = true
        };
        _config.Outputs.Add(binding);
        _selectedBinding = binding;
        EnsureArchitectureWeights("Sign ASCII text output added");
    }

    private void AddRegisterBindings(bool read)
    {
        int added = 0;
        for (int i = 0; i < 5; i++)
        {
            var property = (NeuralProperty)((int)NeuralProperty.LevelVar1 + i);
            var list = read ? _config.Inputs : _config.Outputs;
            if (list.Any(b => b.Source == NeuralSourceKind.Level && b.Property == property)) continue;
            list.Add(new NeuralBinding
            {
                Source = NeuralSourceKind.Level,
                Property = property,
                Scale = 1f,
                Min = -1000f,
                Max = 1000f,
                Clamp = false,
                WriteMode = read ? NeuralWriteMode.Set : NeuralWriteMode.Set
            });
            added++;
        }
        EnsureArchitectureWeights($"{added} level register binding(s) added");
    }

    private void AddOutput()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId) ?? _level.FindByEditorId(FirstObjectId());
        if (control == null) return;
        var binding = new NeuralBinding
        {
            Source = NeuralSourceKind.Object,
            ObjectId = control.EditorId,
            Property = NeuralProperty.PositionX,
            WriteMode = NeuralWriteMode.Add,
            Min = -1f,
            Max = 1f
        };
        _config.Outputs.Add(binding);
        _selectedBinding = binding;
        EnsureArchitectureWeights("Output added — weights reinitialized");
    }

    private void AddControlOutputs()
    {
        var control = _level.FindByEditorId(_config.Training.ControlObjectId) ?? _level.FindByEditorId(FirstObjectId());
        if (control == null) return;
        int added = 0;
        foreach (var property in new[] { NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ })
        {
            if (_config.Outputs.Any(b => b.Source == NeuralSourceKind.Object &&
                                        b.ObjectId == control.EditorId && b.Property == property))
                continue;
            var binding = new NeuralBinding
            {
                Source = NeuralSourceKind.Object,
                ObjectId = control.EditorId,
                Property = property,
                WriteMode = NeuralWriteMode.Add,
                Min = -1f,
                Max = 1f
            };
            _config.Outputs.Add(binding);
            _selectedBinding = binding;
            added++;
        }
        EnsureArchitectureWeights(added == 0 ? "Movement outputs are already connected" : "Position outputs added");
    }

    private string FirstObjectId() => _level.AllNodes()
        .FirstOrDefault(n => !n.Generated && n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM))?.EditorId ?? "";

    private LevelNode? ConnectedNode()
    {
        var id = (_connectCombo?.SelectedItem as NodeChoice)?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : _level.FindByEditorId(id);
    }

    private void ConnectObject(bool read)
    {
        var node = ConnectedNode();
        if (node == null)
        {
            _status.Text = "Pick a scene object to connect first.";
            return;
        }
        var properties = NeuralPropertyInfo.AllFor(NeuralSourceKind.Object)
            .Where(p => read ? NeuralPropertyInfo.IsReadable(p, node) : NeuralPropertyInfo.IsWritable(p, node))
            .ToList();
        int room = read ? Math.Max(0, 24 - _config.Inputs.Count) : Math.Max(0, 12 - _config.Outputs.Count);
        int added = 0;
        foreach (var property in properties.Take(room))
            if (AddBinding(node, property, read)) added++;
        EnsureArchitectureWeights(read
            ? $"Connected {added} readable {node.DisplayName} propert{(added == 1 ? "y" : "ies")} as inputs"
            : $"Connected {added} writable {node.DisplayName} propert{(added == 1 ? "y" : "ies")} as outputs");
        if (added == 0) _status.Text = "Nothing new to connect — these properties are already bound or the list is full.";
        RebuildAll();
    }

    private void ConnectPropertyGroup(NeuralProperty firstAxis, bool read)
    {
        var node = ConnectedNode();
        if (node == null) { _status.Text = "Pick a scene object to connect first."; return; }
        var axes = new List<NeuralProperty>();
        switch (firstAxis)
        {
            case NeuralProperty.PositionX:
                axes.AddRange(new[] { NeuralProperty.PositionX, NeuralProperty.PositionY, NeuralProperty.PositionZ });
                break;
            case NeuralProperty.RotationX:
                axes.AddRange(new[] { NeuralProperty.RotationX, NeuralProperty.RotationY, NeuralProperty.RotationZ });
                break;
            case NeuralProperty.ColorR:
                axes.AddRange(node.Kind == NodeKind.Light
                    ? new[] { NeuralProperty.LightR, NeuralProperty.LightG, NeuralProperty.LightB }
                    : new[] { NeuralProperty.ColorR, NeuralProperty.ColorG, NeuralProperty.ColorB });
                break;
            default:
                axes.Add(firstAxis);
                break;
        }
        int added = 0;
        foreach (var property in axes)
            if (AddBinding(node, property, read)) added++;
        EnsureArchitectureWeights($"Added {added} {NeuralPropertyInfo.Display(firstAxis)} component{(added == 1 ? "" : "s")}");
        RebuildAll();
    }

    private bool AddBinding(LevelNode node, NeuralProperty property, bool read)
    {
        var existing = read ? _config.Inputs : _config.Outputs;
        if (existing.Any(b => b.Enabled && b.Source == NeuralSourceKind.Object &&
                              b.ObjectId == node.EditorId && b.Property == property))
            return false;
        var range = NeuralPropertyInfo.DefaultRange(property);
        var binding = new NeuralBinding
        {
            Source = NeuralSourceKind.Object,
            ObjectId = node.EditorId,
            Property = property,
            Scale = NeuralPropertyInfo.SuggestedScale(property),
            Min = range.Min,
            Max = range.Max,
            Clamp = true,
            WriteMode = !read && property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ or
                         NeuralProperty.RotationX or NeuralProperty.RotationY or NeuralProperty.RotationZ
                ? NeuralWriteMode.Add
                : NeuralWriteMode.Set
        };
        if (read && node.EditorId == _config.Training.GoalObjectId)
        {
            binding.RelativeToId = _config.Training.ControlObjectId;
            if (property is NeuralProperty.PositionX or NeuralProperty.PositionY or NeuralProperty.PositionZ)
                binding.Scale = 0.1f;
        }
        if (read) _config.Inputs.Add(binding); else _config.Outputs.Add(binding);
        _selectedBinding = binding;
        return true;
    }

    private void UseConnectedObject(bool asGoal)
    {
        var node = ConnectedNode();
        if (node == null) { _status.Text = "Pick a scene object first."; return; }
        if (asGoal) _config.Training.GoalObjectId = node.EditorId;
        else _config.Training.ControlObjectId = node.EditorId;
        _status.Text = $"{node.DisplayName} is now the {(asGoal ? "goal" : "controlled")} object.";
        RebuildAll();
    }

    private void AutoWire()
    {
        var result = MessageBox.Show(this,
            "Replace the current inputs and outputs with a target-seeking setup?\n\nThis keeps your network shape and training settings, but resets the weights to match the new input/output count.",
            "Neural Lab", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var starter = NeuralLabConfig.CreateStarter(_level, _config.Training.ControlObjectId, _config.Training.GoalObjectId);
        if (starter.Inputs.Count == 0 || starter.Outputs.Count == 0)
        {
            MessageBox.Show(this, "There are not enough scene objects to auto-wire. Add a target block and a controllable block first.",
                "Neural Lab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        starter.HiddenLayers = _config.HiddenLayers.Select(l => l.Clone()).ToList();
        starter.OutputActivation = _config.OutputActivation;
        starter.Name = _config.Name;
        starter.CodeBlockId = _config.CodeBlockId;
        starter.StartActive = _config.StartActive;
        starter.LateUpdate = _config.LateUpdate;
        starter.Training.Mode = _config.Training.Mode;
        starter.Training.TargetMotion = _config.Training.TargetMotion;
        starter.Training.TargetSpeed = _config.Training.TargetSpeed;
        starter.Training.TargetRadius = _config.Training.TargetRadius;
        starter.Training.TargetTeleportFrames = _config.Training.TargetTeleportFrames;
        starter.Training.Population = _config.Training.Population;
        starter.Training.Generations = _config.Training.Generations;
        starter.Training.Patience = _config.Training.Patience;
        starter.Training.EpisodeSteps = _config.Training.EpisodeSteps;
        starter.Training.Seed = _config.Training.Seed;
        starter.EnsureWeights();
        _config = starter;
        _selectedBinding = _config.Inputs.FirstOrDefault();
        RebuildAll();
        _status.Text = "Auto-wired target direction and position control.";
    }

    private void AddHiddenLayer()
    {
        if (_config.HiddenLayers.Count >= 4)
        {
            _status.Text = "Four hidden layers is the maximum for one exported controller.";
            return;
        }
        _config.HiddenLayers.Add(new NeuralLayer { Neurons = 8, Activation = NeuralActivation.ReLU });
        _selectedGraphLayer = _config.LayerSizes.Length - 2;
        _selectedGraphNeuron = 0;
        EnsureArchitectureWeights("Hidden layer added — weights reinitialized");
        RebuildAll();
    }

    private void RemoveHiddenLayer()
    {
        if (_config.HiddenLayers.Count == 0)
        {
            _status.Text = "There is no hidden layer to remove.";
            return;
        }
        int index = _selectedGraphLayer > 0 && _selectedGraphLayer <= _config.HiddenLayers.Count
            ? _selectedGraphLayer - 1
            : _config.HiddenLayers.Count - 1;
        _config.HiddenLayers.RemoveAt(index);
        _selectedGraphLayer = -1;
        _selectedGraphNeuron = -1;
        EnsureArchitectureWeights("Hidden layer removed — weights reinitialized");
        RebuildAll();
    }

    private void ChangeNeuronCount(int delta)
    {
        int graphLayer = _selectedGraphLayer;
        if (graphLayer < 1 || graphLayer >= _config.LayerSizes.Length - 1)
        {
            if (_config.HiddenLayers.Count == 0)
            {
                _status.Text = "Add a hidden layer first, or change the number of input/output bindings.";
                return;
            }
            graphLayer = _config.LayerSizes.Length - 2;
        }
        var layer = _config.HiddenLayers[graphLayer - 1];
        layer.Neurons = Math.Clamp(layer.Neurons + delta, 1, 32);
        _selectedGraphLayer = graphLayer;
        _selectedGraphNeuron = Math.Clamp(_selectedGraphNeuron, 0, layer.Neurons - 1);
        EnsureArchitectureWeights("Neuron count changed — weights reinitialized");
        RebuildAll();
    }

    private void EnsureArchitectureWeights(string status)
    {
        _config.EnsureWeights();
        _status.Text = status;
        RefreshSummary();
        RefreshCodePreview();
    }

    private void AfterWeightsChanged(string status)
    {
        _config.EnsureWeights();
        _status.Text = status;
        DrawGraph();
        RefreshInspector();
        RefreshSummary();
        RefreshCodePreview();
    }

    private void SyncSelection()
    {
        if (_selectedBinding != null && !_config.Inputs.Contains(_selectedBinding) && !_config.Outputs.Contains(_selectedBinding))
            _selectedBinding = null;
        if (_selectedGraphLayer < 0 && _selectedBinding == null && _config.Inputs.Count > 0)
            _selectedBinding = _config.Inputs[0];
        if (_selectedGraphLayer < 0 && _selectedBinding == null && _config.Outputs.Count > 0)
            _selectedBinding = _config.Outputs[0];
        if (_selectedGraphLayer >= _config.LayerSizes.Length)
            _selectedGraphLayer = -1;
    }

    private void RefreshSummary()
    {
        int objects = CountObjects();
        var layout = NeuralRuntimeLayout.IsRuntime(_config) ? NeuralRuntimeLayout.For(_config) : null;
        int busCount = layout?.BusCount ?? 0;
        int tuned = layout?.TunedFieldCount ?? 0;
        bool over = layout != null && objects + busCount > NeuralRuntimeLayout.MaxSceneConnections;
        _summary.Text = $"{_config.Architecture}   ·   {_config.WeightCount} weights\n{_config.Inputs.Count} inputs → {_config.Outputs.Count} outputs   ·   " +
                        (layout == null
                            ? $"{objects} scene objects · {NeuralRuntimeLayout.MaxSceneConnections} connection limit"
                            : $"{objects} scene + {busCount} buses / 9 connections · {tuned} tuned in game") +
                        (over ? "  ·  over the connection budget" : "");
        _summary.Foreground = over ? Theme.Warning : Theme.Dim;
        _architectureLabel.Text = "Graph: " + _config.Architecture;
        _modeValue.Text = ModeLabel(_config.Training.Mode);
        _applyButton.IsEnabled = !_training;
        _exportButton.IsEnabled = !_training;
    }

    private int CountObjects()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in _config.Inputs.Concat(_config.Outputs))
        {
            if (!b.Enabled || b.Source != NeuralSourceKind.Object) continue;
            if (!string.IsNullOrEmpty(b.ObjectId)) ids.Add(b.ObjectId);
            if (!string.IsNullOrEmpty(b.RelativeToId)) ids.Add(b.RelativeToId);
        }
        if (_config.Training.Mode is NeuralTrainingMode.Runtime or NeuralTrainingMode.Both)
        {
            if (!string.IsNullOrEmpty(_config.Training.ControlObjectId)) ids.Add(_config.Training.ControlObjectId);
            if (!string.IsNullOrEmpty(_config.Training.GoalObjectId)) ids.Add(_config.Training.GoalObjectId);
        }
        return ids.Count;
    }

    private void DrawGraph()
    {
        if (_closed) return;
        _graph.Children.Clear();
        if (_config.Inputs.Count == 0 || _config.Outputs.Count == 0)
        {
            _graph.Children.Add(Label("Add inputs and outputs to see the network.", 28, 24, Theme.Dim, 13));
            return;
        }
        double width = _graph.ActualWidth, height = _graph.ActualHeight;
        if (width < 120 || height < 120) return;
        double left = 54, right = 54, top = 36, bottom = 42;
        var sizes = _config.LayerSizes;
        int layerCount = sizes.Length;
        double usableW = Math.Max(20, width - left - right);
        double usableH = Math.Max(20, height - top - bottom);

        var xs = new double[layerCount];
        var ys = new List<double>[layerCount];
        for (int g = 0; g < layerCount; g++)
        {
            xs[g] = layerCount == 1 ? width / 2 : left + usableW * g / (layerCount - 1.0);
            ys[g] = new List<double>();
            int count = sizes[g];
            double gap = count <= 1 ? 0 : Math.Min(36, usableH / (count + 1));
            double total = gap * (count - 1);
            double startY = top + (usableH - total) / 2;
            for (int i = 0; i < count; i++) ys[g].Add(startY + gap * i);
        }

        for (int g = 0; g < layerCount - 1; g++)
        {
            for (int o = 0; o < sizes[g + 1]; o++)
            {
                for (int i = 0; i < sizes[g]; i++)
                {
                    float weight = WeightAt(g, o, i);
                    bool highlight = _selectedGraphLayer == g + 1 && _selectedGraphNeuron == o;
                    byte alpha = (byte)(highlight ? 210 : Math.Clamp(36 + (int)(Math.Abs(weight) * 30), 28, 150));
                    var brush = new SolidColorBrush(weight >= 0 ? Color.FromArgb(alpha, 79, 163, 227) : Color.FromArgb(alpha, 224, 123, 90));
                    brush.Freeze();
                    _graph.Children.Add(new Line
                    {
                        X1 = xs[g], Y1 = ys[g][i], X2 = xs[g + 1], Y2 = ys[g + 1][o],
                        Stroke = brush,
                        StrokeThickness = highlight ? 2.2 : Math.Clamp(0.35 + Math.Abs(weight) * 0.55, 0.35, 2.4)
                    });
                }
            }
        }

        for (int g = 0; g < layerCount; g++)
        {
            string title = g == 0 ? "INPUTS" : g == layerCount - 1 ? "OUTPUTS" : "HIDDEN " + g;
            var titleBlock = Label(title, xs[g] - 34, 10, g == _selectedGraphLayer && _selectedGraphNeuron < 0 ? Theme.Accent : Theme.Dim, 9.5, bold: true);
            titleBlock.Width = 68;
            titleBlock.IsHitTestVisible = true;
            titleBlock.Cursor = Cursors.Hand;
            int layerIndex = g;
            titleBlock.MouseLeftButtonDown += (_, e) =>
            {
                _selectedBinding = null;
                _selectedGraphLayer = layerIndex;
                _selectedGraphNeuron = -1;
                RebuildBindingLists();
                DrawGraph();
                RefreshInspector();
                e.Handled = true;
            };
            _graph.Children.Add(titleBlock);
            int count = sizes[g];
            for (int i = 0; i < count; i++)
            {
                double r = count > 24 ? 4.5 : count > 14 ? 6 : 8;
                var fill = g == 0 ? new SolidColorBrush(Color.FromRgb(51, 91, 133)) :
                           g == layerCount - 1 ? new SolidColorBrush(Color.FromRgb(150, 105, 48)) :
                           new SolidColorBrush(Color.FromRgb(46, 118, 100));
                bool selected = g == _selectedGraphLayer && i == _selectedGraphNeuron;
                var node = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Fill = fill,
                    Stroke = selected ? Theme.Accent : new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    StrokeThickness = selected ? 2.5 : 0.7,
                    Cursor = Cursors.Hand,
                    ToolTip = g == 0 ? InputLabel(i) : g == layerCount - 1 ? OutputLabel(i) : $"Hidden {g}, neuron {i + 1}"
                };
                int gg = g, ii = i;
                node.MouseLeftButtonDown += (_, e) =>
                {
                    if (gg == 0)
                    {
                        if (ii < _config.Inputs.Count)
                        {
                            _selectedBinding = _config.Inputs[ii];
                            _selectedGraphLayer = -1;
                            _selectedGraphNeuron = -1;
                        }
                    }
                    else
                    {
                        _selectedBinding = null;
                        _selectedGraphLayer = gg;
                        _selectedGraphNeuron = ii;
                    }
                    RebuildBindingLists();
                    DrawGraph();
                    RefreshInspector();
                    e.Handled = true;
                };
                Canvas.SetLeft(node, xs[g] - r);
                Canvas.SetTop(node, ys[g][i] - r);
                _graph.Children.Add(node);
                if (count <= 16)
                {
                    var number = Label((i + 1).ToString(CultureInfo.InvariantCulture), xs[g] - 6, ys[g][i] - 8, Brushes.White, 8.5);
                    number.IsHitTestVisible = false;
                    _graph.Children.Add(number);
                }
                if (g == 0 && count <= 16)
                    _graph.Children.Add(Label(InputLabel(i), xs[g] - 38, ys[g][i] + 10, Theme.Dim, 8.5, width: 76));
                if (g == layerCount - 1 && count <= 16)
                    _graph.Children.Add(Label(OutputLabel(i), xs[g] - 38, ys[g][i] + 10, Theme.Dim, 8.5, width: 76));
            }
        }
    }

    private string InputLabel(int index)
    {
        if (index < 0 || index >= _config.Inputs.Count) return "in" + index;
        var b = _config.Inputs[index];
        string source = b.Source == NeuralSourceKind.Object
            ? (_level.FindByEditorId(b.ObjectId)?.DisplayName ?? "?")
            : b.Source.ToString();
        return source + "\n" + NeuralPropertyInfo.Display(b.Property);
    }

    private string OutputLabel(int index)
    {
        if (index < 0 || index >= _config.Outputs.Count) return "out" + index;
        var b = _config.Outputs[index];
        string source = b.Source == NeuralSourceKind.Object
            ? (_level.FindByEditorId(b.ObjectId)?.DisplayName ?? "?")
            : b.Source.ToString();
        return source + "\n" + NeuralPropertyInfo.Display(b.Property);
    }

    private float WeightAt(int transition, int neuron, int input)
    {
        int at = NeuralLayout.WeightIndex(_config.LayerSizes, transition, neuron, input);
        return at >= 0 && at < _config.Weights.Length ? _config.Weights[at] : 0f;
    }

    private float BiasAt(int transition, int neuron)
    {
        int at = NeuralLayout.BiasIndex(_config.LayerSizes, transition, neuron);
        return at >= 0 && at < _config.Weights.Length ? _config.Weights[at] : 0f;
    }

    private void SetWeight(int transition, int neuron, int input, float value)
    {
        int at = NeuralLayout.WeightIndex(_config.LayerSizes, transition, neuron, input);
        if (at >= 0 && at < _config.Weights.Length) _config.Weights[at] = value;
    }

    private void SetBias(int transition, int neuron, float value)
    {
        int at = NeuralLayout.BiasIndex(_config.LayerSizes, transition, neuron);
        if (at >= 0 && at < _config.Weights.Length) _config.Weights[at] = value;
    }

    private void RefreshInspector()
    {
        _inspectorHost.Children.Clear();
        if (_selectedBinding != null)
        {
            BuildBindingInspector(_selectedBinding);
            return;
        }
        if (_selectedGraphLayer >= 0 && _selectedGraphLayer < _config.LayerSizes.Length)
        {
            if (_selectedGraphNeuron < 0) BuildLayerInspector(_selectedGraphLayer);
            else BuildNeuronInspector(_selectedGraphLayer, _selectedGraphNeuron);
            return;
        }
        _inspectorHost.Children.Add(Theme.Hint("Select a binding, a layer title, or a hidden neuron to inspect it."));
    }

    private void BuildBindingInspector(NeuralBinding binding)
    {
        bool output = _config.Outputs.Contains(binding);
        int index = output ? _config.Outputs.IndexOf(binding) : _config.Inputs.IndexOf(binding);
        _inspectorHost.Children.Add(new TextBlock
        {
            Text = (output ? "OUTPUT " : "INPUT ") + (index + 1),
            Foreground = Theme.Accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        _inspectorHost.Children.Add(Theme.Hint(binding.Describe(_level)));
        var enabled = Theme.Check("Enabled", binding.Enabled);
        enabled.Checked += (_, _) => { binding.Enabled = true; RebuildBindingLists(); RefreshSummary(); RefreshCodePreview(); };
        enabled.Unchecked += (_, _) => { binding.Enabled = false; RebuildBindingLists(); RefreshSummary(); RefreshCodePreview(); };
        _inspectorHost.Children.Add(enabled);

        if (!output && binding.Source == NeuralSourceKind.Object)
        {
            var relative = new ComboBox { Height = 23, DisplayMemberPath = "Label" };
            var choices = new List<RelChoice> { new("", "None") };
            choices.AddRange(_level.AllNodes()
                .Where(n => !n.Generated && n.Kind is not (NodeKind.Start or NodeKind.Finish or NodeKind.GASM))
                .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(n => new RelChoice(n.EditorId, n.DisplayName)));
            relative.ItemsSource = choices;
            relative.SelectedItem = choices.FirstOrDefault(c => c.Id == binding.RelativeToId) ?? choices[0];
            relative.SelectionChanged += (_, _) =>
            {
                if (_suspend || relative.SelectedItem is not RelChoice choice) return;
                binding.RelativeToId = choice.Id;
                RebuildBindingLists();
                RefreshCodePreview();
            };
            _inspectorHost.Children.Add(Theme.Row("Relative to", relative));
        }

        _inspectorHost.Children.Add(Theme.Row("Scale", BindNumber(binding.Scale, v => binding.Scale = v)));
        _inspectorHost.Children.Add(Theme.Row("Offset", BindNumber(binding.Offset, v => binding.Offset = v)));
        var clamp = Theme.Check("Clamp value", binding.Clamp);
        clamp.Checked += (_, _) => { binding.Clamp = true; RefreshCodePreview(); };
        clamp.Unchecked += (_, _) => { binding.Clamp = false; RefreshCodePreview(); };
        _inspectorHost.Children.Add(clamp);
        _inspectorHost.Children.Add(Theme.Row("Min", BindNumber(binding.Min, v => binding.Min = v)));
        _inspectorHost.Children.Add(Theme.Row("Max", BindNumber(binding.Max, v => binding.Max = v)));

        if (output)
        {
            var mode = new ComboBox { Height = 23, DisplayMemberPath = "Label" };
            mode.ItemsSource = new List<ModeChoice>
            {
                new(NeuralTrainingMode.Editor, "Set — write the value"),
                new(NeuralTrainingMode.Runtime, "Add — add to the value")
            };
            mode.SelectedIndex = binding.WriteMode == NeuralWriteMode.Add ? 1 : 0;
            mode.SelectionChanged += (_, _) =>
            {
                if (_suspend || mode.SelectedIndex < 0) return;
                binding.WriteMode = mode.SelectedIndex == 1 ? NeuralWriteMode.Add : NeuralWriteMode.Set;
                RefreshCodePreview();
            };
            _inspectorHost.Children.Add(Theme.Row("Write", mode));
        }
        var remove = Theme.Button("Remove binding", small: true);
        remove.Margin = new Thickness(0, 8, 0, 0);
        remove.Click += (_, _) =>
        {
            if (output) _config.Outputs.Remove(binding); else _config.Inputs.Remove(binding);
            _selectedBinding = null;
            EnsureArchitectureWeights("Binding removed — weights reinitialized");
            RebuildAll();
        };
        _inspectorHost.Children.Add(remove);
        _inspectorHost.Children.Add(Theme.Hint("Scale multiplies the value, Offset adds to it. For target direction, Position relative to the controlled object scaled by 0.1 gives a useful [-1, 1] input."));
    }

    private TextBox BindNumber(float value, Action<float> set)
    {
        var box = Theme.TextBox(value.ToString("0.#####", CultureInfo.InvariantCulture), height: 23);
        void Commit()
        {
            if (!float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return;
            set(v);
            RefreshCodePreview();
            RefreshSummary();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; } };
        return box;
    }

    private void BuildLayerInspector(int graphLayer)
    {
        var sizes = _config.LayerSizes;
        if (graphLayer < 0 || graphLayer >= sizes.Length) return;
        bool input = graphLayer == 0;
        bool output = graphLayer == sizes.Length - 1;
        _inspectorHost.Children.Add(new TextBlock
        {
            Text = input ? "INPUT LAYER" : output ? "OUTPUT LAYER" : "HIDDEN LAYER " + graphLayer,
            Foreground = Theme.Accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _inspectorHost.Children.Add(Theme.Hint(input
            ? sizes[graphLayer] + " input bindings. Add or remove them in the Inputs list."
            : output
                ? sizes[graphLayer] + " output bindings and the final activation."
                : sizes[graphLayer] + " neurons. Click a neuron in the graph to edit its weights and bias."));

        if (input)
        {
            var addInput = Theme.Button("Add input binding", small: true);
            addInput.Click += (_, _) => { AddInput(); RebuildAll(); };
            _inspectorHost.Children.Add(addInput);
            return;
        }

        var activation = new ComboBox { Height = 23, DisplayMemberPath = "Label" };
        activation.ItemsSource = new List<ActivationChoice>
        {
            new(NeuralActivation.Linear, "Linear"),
            new(NeuralActivation.ReLU, "ReLU"),
            new(NeuralActivation.LeakyReLU, "Leaky ReLU"),
            new(NeuralActivation.Tanh, "Tanh (approx)"),
            new(NeuralActivation.Sigmoid, "Sigmoid (approx)")
        };
        var current = output ? _config.OutputActivation : _config.HiddenLayers[graphLayer - 1].Activation;
        activation.SelectedItem = ((List<ActivationChoice>)activation.ItemsSource).First(c => c.Value == current);
        activation.SelectionChanged += (_, _) =>
        {
            if (_suspend || activation.SelectedItem is not ActivationChoice choice) return;
            if (output) _config.OutputActivation = choice.Value;
            else _config.HiddenLayers[graphLayer - 1].Activation = choice.Value;
            DrawGraph();
            RefreshCodePreview();
        };
        _inspectorHost.Children.Add(Theme.Row("Activation", activation));

        if (!output)
        {
            var neuronGrid = new Grid();
            neuronGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            neuronGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            neuronGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var count = new TextBlock
            {
                Text = sizes[graphLayer].ToString(CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Text
            };
            neuronGrid.Children.Add(count);
            var minus = Theme.Button("−", small: true);
            minus.Width = 26;
            minus.Padding = new Thickness(0);
            minus.Click += (_, _) => ChangeNeuronCount(-1);
            Grid.SetColumn(minus, 1);
            neuronGrid.Children.Add(minus);
            var plus = Theme.Button("+", small: true);
            plus.Width = 26;
            plus.Padding = new Thickness(0);
            plus.Margin = new Thickness(4, 0, 0, 0);
            plus.Click += (_, _) => ChangeNeuronCount(1);
            Grid.SetColumn(plus, 2);
            neuronGrid.Children.Add(plus);
            _inspectorHost.Children.Add(Theme.Row("Neurons", neuronGrid));

            var remove = Theme.Button("Remove this hidden layer", small: true);
            remove.Margin = new Thickness(0, 8, 0, 0);
            remove.Click += (_, _) => RemoveHiddenLayer();
            _inspectorHost.Children.Add(remove);
        }
        _inspectorHost.Children.Add(Theme.Hint("Activation is applied to every neuron in this layer. Output activation is applied after the final weighted sum."));
    }

    private void BuildNeuronInspector(int graphLayer, int neuron)
    {
        var sizes = _config.LayerSizes;
        if (graphLayer <= 0 || graphLayer >= sizes.Length || neuron < 0) return;
        bool output = graphLayer == sizes.Length - 1;
        _inspectorHost.Children.Add(new TextBlock
        {
            Text = output ? "OUTPUT NEURON " + (neuron + 1) : "HIDDEN " + graphLayer + " · NEURON " + (neuron + 1),
            Foreground = Theme.Accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (output && neuron < _config.Outputs.Count)
            _inspectorHost.Children.Add(Theme.Hint(_config.Outputs[neuron].Describe(_level)));
        else if (!output)
            _inspectorHost.Children.Add(Theme.Hint("This neuron receives " + sizes[graphLayer - 1] + " values and applies the layer activation."));

        var activation = new ComboBox { Height = 23, DisplayMemberPath = "Label" };
        activation.ItemsSource = new List<ActivationChoice>
        {
            new(NeuralActivation.Linear, "Linear"),
            new(NeuralActivation.ReLU, "ReLU"),
            new(NeuralActivation.LeakyReLU, "Leaky ReLU"),
            new(NeuralActivation.Tanh, "Tanh (approx)"),
            new(NeuralActivation.Sigmoid, "Sigmoid (approx)")
        };
        var currentActivation = output ? _config.OutputActivation : _config.HiddenLayers[graphLayer - 1].Activation;
        activation.SelectedItem = ((List<ActivationChoice>)activation.ItemsSource).First(c => c.Value == currentActivation);
        activation.SelectionChanged += (_, _) =>
        {
            if (_suspend || activation.SelectedItem is not ActivationChoice choice) return;
            if (output) _config.OutputActivation = choice.Value;
            else _config.HiddenLayers[graphLayer - 1].Activation = choice.Value;
            DrawGraph();
            RefreshCodePreview();
        };
        _inspectorHost.Children.Add(Theme.Row("Activation", activation));
        _inspectorHost.Children.Add(Theme.Row("Bias", BindBias(graphLayer - 1, neuron, BiasAt(graphLayer - 1, neuron))));

        _inspectorHost.Children.Add(Theme.SectionHeader("INCOMING WEIGHTS"));
        for (int i = 0; i < sizes[graphLayer - 1]; i++)
        {
            string label = graphLayer == 1
                ? (i < _config.Inputs.Count ? "in " + (i + 1) : "in " + (i + 1))
                : "h" + (graphLayer - 1) + "." + (i + 1);
            var box = BindWeight(graphLayer - 1, neuron, i, WeightAt(graphLayer - 1, neuron, i));
            _inspectorHost.Children.Add(Theme.Row(label, box, 78));
        }
        var zero = Theme.Button("Zero incoming", small: true);
        zero.Margin = new Thickness(0, 5, 0, 0);
        zero.Click += (_, _) =>
        {
            for (int i = 0; i < sizes[graphLayer - 1]; i++) SetWeight(graphLayer - 1, neuron, i, 0f);
            SetBias(graphLayer - 1, neuron, 0f);
            DrawGraph();
            RefreshInspector();
            RefreshCodePreview();
        };
        _inspectorHost.Children.Add(zero);
    }

    private TextBox BindWeight(int transition, int neuron, int input, float value)
    {
        var box = Theme.TextBox(value.ToString("0.#####", CultureInfo.InvariantCulture), height: 22);
        void Commit()
        {
            if (!float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return;
            SetWeight(transition, neuron, input, Math.Clamp(v, -20f, 20f));
            DrawGraph();
            RefreshCodePreview();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; } };
        return box;
    }

    private TextBox BindBias(int transition, int neuron, float value)
    {
        var box = Theme.TextBox(value.ToString("0.#####", CultureInfo.InvariantCulture), height: 22);
        void Commit()
        {
            if (!float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return;
            SetBias(transition, neuron, Math.Clamp(v, -20f, 20f));
            DrawGraph();
            RefreshCodePreview();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; } };
        return box;
    }

    private void SyncTrainingFields()
    {
        if (_nameBox == null) return;
        _nameBox.Text = _config.Name;
        _startActive.IsChecked = _config.StartActive;
        _lateUpdate.IsChecked = _config.LateUpdate;
        _modeCombo.SelectedItem = ((List<ModeChoice>)_modeCombo.ItemsSource).First(c => c.Value == _config.Training.Mode);
        _motionCombo.SelectedItem = ((List<MotionChoice>)_motionCombo.ItemsSource).First(c => c.Value == _config.Training.TargetMotion);
        _targetSpeedBox.Text = _config.Training.TargetSpeed.ToString("0.####", CultureInfo.InvariantCulture);
        _targetRadiusBox.Text = _config.Training.TargetRadius.ToString("0.####", CultureInfo.InvariantCulture);
        _targetTeleportBox.Text = _config.Training.TargetTeleportFrames.ToString(CultureInfo.InvariantCulture);
        _successBox.Text = _config.Training.SuccessRadius.ToString("0.####", CultureInfo.InvariantCulture);
        _strengthBox.Text = _config.Training.MoveSpeed.ToString("0.####", CultureInfo.InvariantCulture);
        _rotationBox.Text = _config.Training.RotationSpeed.ToString("0.####", CultureInfo.InvariantCulture);
        _spreadBox.Text = _config.Training.StartSpread.ToString("0.####", CultureInfo.InvariantCulture);
        _startsBox.Text = _config.Training.Starts.ToString(CultureInfo.InvariantCulture);
        _populationBox.Text = _config.Training.Population.ToString(CultureInfo.InvariantCulture);
        _generationsBox.Text = _config.Training.Generations.ToString(CultureInfo.InvariantCulture);
        _patienceBox.Text = _config.Training.Patience.ToString(CultureInfo.InvariantCulture);
        _stepsBox.Text = _config.Training.EpisodeSteps.ToString(CultureInfo.InvariantCulture);
        _seedBox.Text = _config.Training.Seed.ToString(CultureInfo.InvariantCulture);
        _mutationRateBox.Text = _config.Training.MutationRate.ToString("0.####", CultureInfo.InvariantCulture);
        _mutationScaleBox.Text = _config.Training.MutationScale.ToString("0.####", CultureInfo.InvariantCulture);
        _runtimeIntervalBox.Text = _config.Training.RuntimeIntervalFrames.ToString(CultureInfo.InvariantCulture);
        _runtimeStepBox.Text = _config.Training.RuntimeStep.ToString("0.####", CultureInfo.InvariantCulture);
        _runtimeLimitBox.Text = _config.Training.RuntimeTuneLimit.ToString(CultureInfo.InvariantCulture);
        _stopWhenSolved.IsChecked = _config.Training.StopWhenSolved;
        _autoApply.IsChecked = _config.Training.AutoApplyAfterTraining;
        bool runtime = _config.Training.Mode != NeuralTrainingMode.Editor;
        _runtimeIntervalBox.IsEnabled = runtime;
        _runtimeStepBox.IsEnabled = runtime;
        _runtimeLimitBox.IsEnabled = runtime;
        _modeValue.Text = ModeLabel(_config.Training.Mode);
        UpdateMotionFields();
    }

    private void UpdateMotionFields()
    {
        if (_motionCombo == null || _motionHint == null) return;
        var motion = _motionCombo.SelectedItem is MotionChoice c ? c.Value : _config.Training.TargetMotion;
        bool moving = motion != NeuralTargetMotion.Static;
        _targetSpeedBox.IsEnabled = motion is NeuralTargetMotion.Orbit or NeuralTargetMotion.RandomWalk;
        _targetRadiusBox.IsEnabled = motion is NeuralTargetMotion.Orbit or NeuralTargetMotion.RandomWalk or NeuralTargetMotion.Teleport;
        _targetTeleportBox.IsEnabled = motion == NeuralTargetMotion.Teleport;
        _motionHint.Text = motion switch
        {
            NeuralTargetMotion.Static =>
                "Static: the target stays at its current level position. The network can memorise that position, which is why a moving target sometimes looks like it only reaches the starting spot.",
            NeuralTargetMotion.Animated =>
                "Animated: the target follows its first animation clip while the network trains. This is the closest match to an animated or moving target in the real level. Add a clip in the Animation tab, then retrain.",
            NeuralTargetMotion.Orbit =>
                "Orbit: the target circles its starting point. Target speed is degrees per second; radius is measured in blocks. The goal position is updated before every input read.",
            NeuralTargetMotion.RandomWalk =>
                "Random walk: the target moves at Target speed (blocks per second) inside the radius and reflects off the edge. Trains tracking, not memorising.",
            _ =>
                "Teleport: the target jumps to a new random point inside the radius every N frames. Use a longer episode so the network sees several jumps."
        };
    }

    private void ReadTrainingSettings()
    {
        if (_nameBox == null) return;
        if (!string.IsNullOrWhiteSpace(_nameBox.Text)) _config.Name = _nameBox.Text.Trim();
        _config.StartActive = _startActive.IsChecked == true;
        _config.LateUpdate = _lateUpdate.IsChecked == true;
        if (_modeCombo.SelectedItem is ModeChoice mode) _config.Training.Mode = mode.Value;
        if (_motionCombo.SelectedItem is MotionChoice motion) _config.Training.TargetMotion = motion.Value;
        _config.Training.TargetSpeed = ReadFloat(_targetSpeedBox, 0f, 720f, _config.Training.TargetSpeed);
        _config.Training.TargetRadius = ReadFloat(_targetRadiusBox, 0.05f, 100f, _config.Training.TargetRadius);
        _config.Training.TargetTeleportFrames = ReadInt(_targetTeleportBox, 2, 600, _config.Training.TargetTeleportFrames);
        _config.Training.SuccessRadius = ReadFloat(_successBox, 0.05f, 100f, _config.Training.SuccessRadius);
        _config.Training.MoveSpeed = ReadFloat(_strengthBox, 0.1f, 200f, _config.Training.MoveSpeed);
        _config.Training.RotationSpeed = ReadFloat(_rotationBox, 0f, 1440f, _config.Training.RotationSpeed);
        _config.Training.StartSpread = ReadFloat(_spreadBox, 0f, 100f, _config.Training.StartSpread);
        _config.Training.Starts = ReadInt(_startsBox, 1, 12, _config.Training.Starts);
        _config.Training.Population = ReadInt(_populationBox, 8, 400, _config.Training.Population);
        _config.Training.Generations = ReadInt(_generationsBox, 1, 500, _config.Training.Generations);
        _config.Training.Patience = ReadInt(_patienceBox, 1, 200, _config.Training.Patience);
        _config.Training.EpisodeSteps = ReadInt(_stepsBox, 60, 3000, _config.Training.EpisodeSteps);
        _config.Training.Seed = ReadInt(_seedBox, int.MinValue, int.MaxValue, _config.Training.Seed);
        _config.Training.MutationRate = ReadFloat(_mutationRateBox, 0f, 1f, _config.Training.MutationRate);
        _config.Training.MutationScale = ReadFloat(_mutationScaleBox, 0f, 5f, _config.Training.MutationScale);
        _config.Training.RuntimeIntervalFrames = ReadInt(_runtimeIntervalBox, 1, 120, _config.Training.RuntimeIntervalFrames);
        _config.Training.RuntimeStep = ReadFloat(_runtimeStepBox, 0.0001f, 1f, _config.Training.RuntimeStep);
        _config.Training.RuntimeTuneLimit = ReadInt(_runtimeLimitBox, 1, NeuralRuntimeLayout.MaxTunedWeights, _config.Training.RuntimeTuneLimit);
        _config.Training.StopWhenSolved = _stopWhenSolved.IsChecked == true;
        _config.Training.AutoApplyAfterTraining = _autoApply.IsChecked == true;
    }

    private static int ReadInt(TextBox box, int min, int max, int fallback) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, min, max) : fallback;

    private static float ReadFloat(TextBox box, float min, float max, float fallback) =>
        float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? Math.Clamp(v, min, max) : fallback;

    private void ResetTrainingDefaults()
    {
        var defaults = new NeuralTrainingSettings { Mode = _config.Training.Mode };
        _config.Training.TargetMotion = defaults.TargetMotion;
        _config.Training.TargetSpeed = defaults.TargetSpeed;
        _config.Training.TargetRadius = defaults.TargetRadius;
        _config.Training.TargetTeleportFrames = defaults.TargetTeleportFrames;
        _config.Training.SuccessRadius = defaults.SuccessRadius;
        _config.Training.MoveSpeed = defaults.MoveSpeed;
        _config.Training.RotationSpeed = defaults.RotationSpeed;
        _config.Training.StartSpread = defaults.StartSpread;
        _config.Training.Starts = defaults.Starts;
        _config.Training.Population = defaults.Population;
        _config.Training.Generations = defaults.Generations;
        _config.Training.Patience = defaults.Patience;
        _config.Training.EpisodeSteps = defaults.EpisodeSteps;
        _config.Training.Seed = defaults.Seed;
        _config.Training.MutationRate = defaults.MutationRate;
        _config.Training.MutationScale = defaults.MutationScale;
        _config.Training.StopWhenSolved = defaults.StopWhenSolved;
        _config.Training.AutoApplyAfterTraining = defaults.AutoApplyAfterTraining;
        _config.Training.RuntimeIntervalFrames = defaults.RuntimeIntervalFrames;
        _config.Training.RuntimeStep = defaults.RuntimeStep;
        _config.Training.RuntimeTuneLimit = defaults.RuntimeTuneLimit;
        SyncTrainingFields();
        _status.Text = "Training settings reset.";
    }

    private void StartTraining()
    {
        if (_training) return;
        ReadTrainingSettings();
        var errors = _config.Validate(_level, includeTraining: true);
        if (errors.Count > 0) { ShowErrors("Training cannot start", errors); return; }
        _config.EnsureWeights();
        _training = true;
        _history.Clear();
        _report = null;
        _log.Clear();
        _chart.Children.Clear();
        _trainButton.IsEnabled = false;
        _stopButton.IsEnabled = true;
        _applyButton.IsEnabled = false;
        _exportButton.IsEnabled = false;
        _generationValue.Text = "0 / " + _config.Training.Generations;
        _distanceValue.Text = "—";
        _successValue.Text = "—";
        _solvedValue.Text = "training…";
        _status.Text = "Training…";
        Log($"Training {_config.Architecture} with {_config.WeightCount} weights, {_config.Training.Population} population, {_config.Training.Generations} generations.");

        var snapshot = _config.Clone();
        snapshot.EnsureWeights();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() =>
        {
            try
            {
                var trainer = new NeuralLabTrainer(snapshot, _level);
                var report = trainer.Train(stat =>
                {
                    if (_closed) return;
                    Dispatcher.BeginInvoke(() => OnProgress(stat, snapshot));
                }, token);
                if (_closed) return;
                Dispatcher.BeginInvoke(() => FinishTraining(report, snapshot));
            }
            catch (OperationCanceledException)
            {
                if (_closed) return;
                Dispatcher.BeginInvoke(FinishCancelled);
            }
            catch (Exception ex)
            {
                if (_closed) return;
                Dispatcher.BeginInvoke(() => FinishFailed(ex));
            }
        }, token);
    }

    private void OnProgress(NeuralLabGeneration stat, NeuralLabConfig snapshot)
    {
        if (_closed) return;
        _history.Add(stat);
        _generationValue.Text = $"{stat.Generation} / {snapshot.Training.Generations}";
        _distanceValue.Text = stat.BestDistance.ToString("0.000", CultureInfo.InvariantCulture);
        _successValue.Text = stat.SuccessRate.ToString("P0", CultureInfo.InvariantCulture);
        _solvedValue.Text = stat.Solved ? "solved" : "improving";
        _status.Text = $"Generation {stat.Generation} · distance {stat.BestDistance:0.000} · success {stat.SuccessRate:P0}";
        DrawChart();
        if (stat.Generation == 1 || stat.Generation % 5 == 0 || stat.Solved)
            Log($"gen {stat.Generation}: fitness {stat.BestFitness:0.00}, distance {stat.BestDistance:0.000}, success {stat.SuccessRate:P0}");
    }

    private void FinishTraining(NeuralLabReport report, NeuralLabConfig snapshot)
    {
        _training = false;
        _report = report;
        _trainButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        _applyButton.IsEnabled = true;
        _exportButton.IsEnabled = true;
        if (report.BestWeights.Length == _config.WeightCount)
        {
            _config.Weights = (float[])report.BestWeights.Clone();
        }
        _generationValue.Text = $"{report.GenerationsRun} / {report.Options.Generations}";
        _distanceValue.Text = report.BestDistance.ToString("0.000", CultureInfo.InvariantCulture);
        _successValue.Text = report.SuccessRate.ToString("P0", CultureInfo.InvariantCulture);
        _solvedValue.Text = report.Solved ? "solved" : "stopped";
        _status.Text = report.Solved
            ? $"Solved in {report.GenerationsRun} generations — apply the controller when ready."
            : $"Stopped after {report.GenerationsRun} generations — {report.StopReason}";
        Log("");
        Log(report.Describe());
        if (report.DemoRun != null) Log("demo: " + report.DemoRun.Describe());
        DrawChart();
        DrawGraph();
        RefreshSummary();
        RefreshInspector();
        RefreshCodePreview();
        if (_config.Training.AutoApplyAfterTraining && report.Solved)
            ApplyToLevel(closeAfter: false);
    }

    private void FinishCancelled()
    {
        _training = false;
        _trainButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        _applyButton.IsEnabled = true;
        _exportButton.IsEnabled = true;
        _solvedValue.Text = "stopped";
        _status.Text = "Training stopped by user.";
        Log("Training stopped by the user.");
    }

    private void FinishFailed(Exception ex)
    {
        _training = false;
        _trainButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        _applyButton.IsEnabled = true;
        _exportButton.IsEnabled = true;
        _solvedValue.Text = "failed";
        _status.Text = "Training failed: " + ex.Message;
        Log("Training failed: " + ex);
        MessageBox.Show(this, "Training failed:\n\n" + ex.Message, "Neural Lab", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void DrawChart()
    {
        if (_closed) return;
        _chart.Children.Clear();
        double width = _chart.ActualWidth, height = _chart.ActualHeight;
        if (width < 80 || height < 80) return;
        double left = 42, right = 14, top = 22, bottom = 24;
        double plotW = Math.Max(10, width - left - right);
        double plotH = Math.Max(10, height - top - bottom);
        double maxDistance = Math.Max(1.0, _history.Count == 0 ? 8.0 : _history.Max(g => g.BestDistance));
        maxDistance = Math.Max(maxDistance, _config.Training.SuccessRadius * 3f);
        for (int i = 0; i <= 4; i++)
        {
            double value = maxDistance * (4 - i) / 4.0;
            double y = top + plotH * i / 4.0;
            _chart.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(52, 52, 52)), StrokeThickness = 1 });
            _chart.Children.Add(Label(value.ToString("0.0", CultureInfo.InvariantCulture), 3, y - 8, Theme.Dim, 9));
        }
        _chart.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = top + plotH, Y2 = top + plotH, Stroke = Theme.Border });
        _chart.Children.Add(Label("best mean distance (lower is better)", left, 3, Theme.Dim, 9.5));
        if (_history.Count == 0)
        {
            _chart.Children.Add(Label("Press Train to watch the learning curve.", left + 12, top + plotH / 2 - 8, Theme.Dim, 12));
            return;
        }
        double targetY = top + plotH * (1.0 - Math.Min(1.0, _config.Training.SuccessRadius / maxDistance));
        _chart.Children.Add(new Line
        {
            X1 = left, X2 = left + plotW, Y1 = targetY, Y2 = targetY,
            Stroke = Theme.Ok, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
        });
        _chart.Children.Add(Label("success radius", left + plotW - 74, targetY - 14, Theme.Ok, 9));
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(79, 163, 227)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (int i = 0; i < _history.Count; i++)
        {
            double x = _history.Count == 1 ? left + plotW / 2 : left + plotW * i / (_history.Count - 1.0);
            double y = top + plotH * (1.0 - Math.Min(1.0, _history[i].BestDistance / maxDistance));
            polyline.Points.Add(new Point(x, y));
        }
        _chart.Children.Add(polyline);
        if (_history[^1].Solved)
            _chart.Children.Add(Label("target reached", left + 8, top + 4, Theme.Ok, 10));
    }

    private static TextBlock Label(string text, double x, double y, Brush foreground, double size,
        bool bold = false, double width = double.NaN)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = width,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        return label;
    }

    private void RefreshCodePreview()
    {
        if (_codePreview == null) return;
        try
        {
            var source = NeuralCodeBuilder.BuildSource(_level, _config);
            if (NeuralRuntimeLayout.IsRuntime(_config))
            {
                try
                {
                    source += "\n\n// ================= IN-GAME TRAINER CODE BLOCK =================\n\n" +
                              NeuralTrainerBlock.BuildSource(_level, _config);
                }
                catch (Exception trainerError)
                {
                    source += "\n\n// Trainer preview unavailable: " + trainerError.Message.Replace("\n", "\n// ");
                }
            }
            _codePreview.Text = source;
        }
        catch (Exception ex)
        {
            _codePreview.Text = "// Neural Lab could not generate this controller.\n// " + ex.Message.Replace("\n", "\n// ");
        }
    }

    private bool ApplyToLevel(bool closeAfter)
    {
        ReadTrainingSettings();
        var errors = _config.Validate(_level, includeTraining: false);
        if (_config.Training.Mode is NeuralTrainingMode.Runtime or NeuralTrainingMode.Both)
            errors.AddRange(_config.Validate(_level, includeTraining: true));
        if (errors.Count > 0) { ShowErrors("Cannot apply controller", errors); return false; }
        _config.EnsureWeights();
        var result = NeuralCodeBuilder.Apply(_level, _config);
        if (!result.Success)
        {
            _status.Text = "Apply failed — see the diagnostics.";
            Log("Apply failed:");
            Log(result.Error);
            ShowErrors("The generated controller did not compile", result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            return false;
        }
        _status.Text = "Applied to level · " + result.Summary;
        Log($"Applied '{result.Block?.DisplayName}': {result.Summary}");
        RefreshSummary();
        RefreshCodePreview();
        if (closeAfter)
        {
            Applied?.Invoke();
            Close();
        }
        return true;
    }

    private void ExportLevel()
    {
        if (!ApplyToLevel(closeAfter: false)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "GRAB Level (*.level)|*.level|All files (*.*)|*.*",
            DefaultExt = ".level",
            FileName = string.IsNullOrWhiteSpace(_level.Title) ? "level" : _level.Title,
            Title = "Export level with neural controller"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, LevelIO.Write(_level));
            _status.Text = "Exported " + Path.GetFileName(dialog.FileName);
            Log("Exported level: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            _status.Text = "Export failed: " + ex.Message;
            MessageBox.Show(this, "Could not export the level:\n\n" + ex.Message, "Neural Lab", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ValidateCode()
    {
        ReadTrainingSettings();
        try
        {
            var copy = ProjectIO.Clone(_level);
            var result = NeuralCodeBuilder.Apply(copy, _config.Clone());
            if (result.Success)
            {
                _status.Text = "Validation passed · " + result.Summary;
                MessageBox.Show(this, "The controller compiles to real GASM.\n\n" + result.Summary,
                    "Neural Lab validation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else ShowErrors("Validation failed", result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
        }
        catch (Exception ex)
        {
            ShowErrors("Validation failed", new List<string> { ex.Message });
        }
    }

    private void ShowErrors(string title, List<string> errors)
    {
        _status.Text = title + ": " + errors.FirstOrDefault();
        Log(title + ":");
        foreach (var error in errors) Log("  " + error);
        MessageBox.Show(this, string.Join("\n", errors.Take(12)), title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Log(string text)
    {
        if (_closed) return;
        _log.AppendText(text + Environment.NewLine);
        _log.ScrollToEnd();
        if (_log.LineCount > 5000)
            _log.Text = string.Join(Environment.NewLine, _log.Text.Split('\n').TakeLast(4000));
    }
}
