using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>Level budget dashboard and conservative geometry optimiser.</summary>
public sealed class LevelOptimizerWindow : Window
{
    private readonly GrabLevel _level;
    private readonly Action<LevelOptimizeResult> _applied;
    private readonly Action<LevelNode?> _selectNode;
    private readonly Dictionary<string, TextBlock> _stats = new();
    private readonly ListBox _issues = new();
    private readonly TextBlock _result = new() { Foreground = Theme.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _merge = Theme.Check("Merge adjacent identical cubes", true);
    private readonly CheckBox _mergeMixed = Theme.Check("Merge different block sizes on a shared grid", true);
    private readonly ComboBox _mixedGrid = Theme.Combo(new[] { "0.25 m", "0.5 m", "1 m", "2 m", "4 m" }, 2);
    private readonly CheckBox _cull = Theme.Check("Remove fully enclosed blocks", true);
    private readonly CheckBox _duplicates = Theme.Check("Remove exact duplicate blocks", true);
    private readonly CheckBox _degenerate = Theme.Check("Remove zero-size blocks", true);
    private readonly CheckBox _emptyGroups = Theme.Check("Remove empty groups", false);
    private readonly CheckBox _protect = Theme.Check("Protect physics groups and locked objects", true);
    private int _lastObjectCount;

    public LevelOptimizerWindow(Window owner, GrabLevel level,
        Action<LevelOptimizeResult> applied, Action<LevelNode?> selectNode)
    {
        _level = level;
        _applied = applied;
        _selectNode = selectNode;
        Owner = owner;
        Title = "Level Optimizer & Budget";
        Width = 900;
        Height = 700;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        header.Children.Add(Theme.Heading("LEVEL BUDGET & GEOMETRY OPTIMIZER"));
        header.Children.Add(Theme.Hint("GRAB has hard limits on code, transparent blocks, physics groups, spawns and animation frames. This dashboard shows where the level stands; the optimiser removes redundant geometry without changing its visible shape."));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = Theme.Button("Refresh", small: true);
        refresh.Click += (_, _) => RefreshBudget();
        buttons.Children.Add(refresh);
        var optimize = Theme.Button("Optimize Level", accent: true);
        optimize.Margin = new Thickness(6, 0, 0, 0);
        optimize.Click += (_, _) => RunOptimizer();
        buttons.Children.Add(optimize);
        var close = Theme.Button("Close");
        close.Margin = new Thickness(6, 0, 0, 0);
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statsPanel = new StackPanel();
        statsPanel.Children.Add(Theme.SectionHeader("LEVEL BUDGET"));
        var statsGrid = new Grid();
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var key in new[] { "Objects", "Blocks", "Groups", "Physics groups", "Transparent", "Lights", "Triggers", "Signs", "Sounds", "Particles", "Code blocks", "GASM instructions", "Connections", "Spawn points", "Animation frames" })
        {
            int index = _stats.Count;
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
                        new TextBlock { Text = key.ToUpperInvariant(), FontSize = 9, Foreground = Theme.Dim },
                        new TextBlock { Text = "–", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Tag = key }
                    }
                }
            };
            var value = (TextBlock)((StackPanel)card.Child).Children[1];
            _stats[key] = value;
            statsGrid.Children.Add(card);
            Grid.SetColumn(card, index % 2);
            Grid.SetRow(card, index / 2);
            statsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        statsPanel.Children.Add(statsGrid);
        statsPanel.Children.Add(Theme.Hint("Limits: 150 instructions and 10 connections per Code Block, 20 transparent blocks, 100 physics groups, 10 spawns, 150 sign characters, 1000 animation frames per object."));
        content.Children.Add(statsPanel);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Theme.SectionHeader("ISSUES"));
        _issues.Height = 150;
        _issues.MouseDoubleClick += (_, _) =>
        {
            if (_issues.SelectedItem is IssueRow row && row.Node != null) _selectNode(row.Node);
        };
        _issues.DisplayMemberPath = "Text";
        _issues.ToolTip = "Double-click an issue to select its object in the Hierarchy";
        right.Children.Add(_issues);
        right.Children.Add(Theme.SectionHeader("OPTIMISATION"));
        right.Children.Add(_merge);
        right.Children.Add(_mergeMixed);
        right.Children.Add(Theme.Row("Grid", _mixedGrid, 92));
        right.Children.Add(_cull);
        right.Children.Add(_duplicates);
        right.Children.Add(_degenerate);
        right.Children.Add(_emptyGroups);
        right.Children.Add(_protect);
        right.Children.Add(Theme.Hint("Mixed-size merging rebuilds identical-material blocks on the selected grid, so a tall terrain column and its cap can become one block. Merging runs before culling, so a solid region can collapse to one large block first. Animated objects, scripts, locked objects and physics groups are never modified."));
        _mixedGrid.SelectionChanged += (_, _) => { if (_mergeMixed.IsChecked == true) _merge.IsChecked = false; };
        right.Children.Add(_result);
        var note = new TextBlock
        {
            Text = "Undo (Ctrl+Z) restores the level if the result is not what you expected.",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        right.Children.Add(note);
        Grid.SetColumn(right, 1);
        content.Children.Add(right);
        root.Children.Add(content);
        Content = root;

        Loaded += (_, _) => RefreshBudget();
    }

    private void RefreshBudget()
    {
        var budget = LevelOptimizer.Analyze(_level);
        _lastObjectCount = budget.Objects;
        Set("Objects", budget.Objects);
        Set("Blocks", budget.Blocks);
        Set("Groups", budget.Groups);
        Set("Physics groups", budget.PhysicsGroups);
        Set("Transparent", budget.TransparentBlocks);
        Set("Lights", budget.Lights);
        Set("Triggers", budget.Triggers);
        Set("Signs", budget.Signs);
        Set("Sounds", budget.Sounds);
        Set("Particles", budget.ParticleEmitters);
        Set("Code blocks", budget.CodeBlocks);
        Set("GASM instructions", budget.Instructions);
        Set("Connections", budget.Connections);
        Set("Spawn points", budget.SpawnPoints);
        Set("Animation frames", budget.AnimationFrames);
        _issues.ItemsSource = budget.Issues.Select(i => new IssueRow
        {
            Text = (i.Severity == DiagnosticSeverity.Error ? "ERROR  " : "WARN  ") + i.Message,
            Node = i.Node
        }).ToList();
        _result.Text = $"{budget.Objects} objects · {budget.ErrorCount} error(s), {budget.WarningCount} warning(s).";
        _result.Foreground = budget.ErrorCount > 0 ? Theme.Error : budget.WarningCount > 0 ? Theme.Warning : Theme.Ok;
    }

    private void Set(string name, int value)
    {
        if (!_stats.TryGetValue(name, out var text)) return;
        text.Text = value.ToString("N0");
    }

    private void RunOptimizer()
    {
        bool merge = _merge.IsChecked == true;
        bool mergeMixed = _mergeMixed.IsChecked == true;
        bool cull = _cull.IsChecked == true;
        bool duplicates = _duplicates.IsChecked == true;
        bool degenerate = _degenerate.IsChecked == true;
        bool emptyGroups = _emptyGroups.IsChecked == true;
        if (!merge && !mergeMixed && !cull && !duplicates && !degenerate && !emptyGroups)
        {
            _result.Text = "Choose at least one optimisation.";
            _result.Foreground = Theme.Warning;
            return;
        }
        try
        {
            double grid = _mixedGrid.SelectedIndex switch { 0 => 0.25, 1 => 0.5, 3 => 2.0, 4 => 4.0, _ => 1.0 };
            var result = LevelOptimizer.Optimize(_level, new LevelOptimizeOptions
            {
                MergeAdjacentCubes = merge,
                MergeMixedSizes = mergeMixed,
                MixedSizeGrid = grid,
                CullHiddenBlocks = cull,
                RemoveDuplicateBlocks = duplicates,
                RemoveDegenerateBlocks = degenerate,
                RemoveEmptyGroups = emptyGroups,
                ProtectPhysics = _protect.IsChecked == true,
                ProtectLocked = _protect.IsChecked == true,
                ProtectAnimated = true
            });
            _result.Text = result.Summary;
            _result.Foreground = Theme.Ok;
            _applied(result);
            RefreshBudget();
        }
        catch (Exception ex)
        {
            _result.Text = "Optimizer failed: " + ex.Message;
            _result.Foreground = Theme.Error;
        }
    }

    private sealed class IssueRow
    {
        public string Text { get; init; } = "";
        public LevelNode? Node { get; init; }
    }
}
