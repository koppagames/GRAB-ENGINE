using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Code;

namespace GrabEngine.Controls;

/// <summary>Docked debugger for GASM play sessions: source stepping, watch values and breakpoints.</summary>
public sealed class DebuggerPanel : UserControl
{
    private readonly TextBlock _status = new();
    private readonly TextBlock _location = new();
    private readonly ListView _variables = new();
    private readonly ListView _breakpoints = new();
    private readonly Button _continue;
    private readonly Button _stepLine;
    private readonly Button _stepInstruction;
    private readonly Button _pause;
    private readonly Button _stop;

    public event Action? ContinueRequested;
    public event Action? StepLineRequested;
    public event Action? StepInstructionRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;
    public event Action? SourceRequested;
    public event Action? ClearBreakpointsRequested;

    public DebuggerPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 4, 6, 4)
        };
        var dock = new DockPanel { LastChildFill = true };
        _continue = Button("▶ Continue", true);
        _continue.Click += (_, _) => ContinueRequested?.Invoke();
        _stepLine = Button("⤵ Step Line", false);
        _stepLine.Click += (_, _) => StepLineRequested?.Invoke();
        _stepInstruction = Button("→ Step GASM", false);
        _stepInstruction.Click += (_, _) => StepInstructionRequested?.Invoke();
        _pause = Button("❚❚ Pause", false);
        _pause.Click += (_, _) => PauseRequested?.Invoke();
        _stop = Button("■ Stop", false);
        _stop.Click += (_, _) => StopRequested?.Invoke();
        var source = Button("C# Source", false);
        source.Click += (_, _) => SourceRequested?.Invoke();
        foreach (var b in new[] { _continue, _stepLine, _stepInstruction, _pause, _stop, source })
        {
            b.Margin = new Thickness(0, 0, 4, 0);
            b.Padding = new Thickness(8, 2, 8, 2);
            DockPanel.SetDock(b, Dock.Left);
            dock.Children.Add(b);
        }
        _status.Text = "Not playing";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        _status.FontSize = 11;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(8, 0, 0, 0);
        dock.Children.Add(_status);
        bar.Child = dock;
        root.Children.Add(bar);
        Grid.SetRow(bar, 0);

        var body = new Grid { Margin = new Thickness(6) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var left = new DockPanel();
        _location.Text = "No active script";
        _location.Foreground = new SolidColorBrush(Color.FromRgb(160, 200, 230));
        _location.FontSize = 11;
        _location.Padding = new Thickness(2, 2, 2, 4);
        DockPanel.SetDock(_location, Dock.Top);
        left.Children.Add(_location);
        _variables.Background = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        _variables.Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210));
        _variables.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 55));
        _variables.BorderThickness = new Thickness(1);
        var varsView = new GridView();
        varsView.Columns.Add(new GridViewColumn { Header = "Watch", Width = 210, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(VariableRow.Name)) });
        varsView.Columns.Add(new GridViewColumn { Header = "Value", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(VariableRow.Value)) });
        _variables.View = varsView;
        left.Children.Add(_variables);
        body.Children.Add(left);
        Grid.SetColumn(left, 0);

        var right = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        var bpHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var bpLabel = new TextBlock
        {
            Text = "BREAKPOINTS  (F9 in the C# editor)",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            VerticalAlignment = VerticalAlignment.Center
        };
        bpHeader.Children.Add(bpLabel);
        var clear = new Button
        {
            Content = "Clear",
            Padding = new Thickness(7, 1, 7, 1),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(clear, Dock.Right);
        clear.Click += (_, _) => ClearBreakpointsRequested?.Invoke();
        bpHeader.Children.Add(clear);
        DockPanel.SetDock(bpHeader, Dock.Top);
        right.Children.Add(bpHeader);
        var hint = new TextBlock
        {
            Text = "Click the red gutter dot or press F9. Breakpoints pause Play mode before that C# line runs.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        right.Children.Add(hint);
        _breakpoints.Background = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        _breakpoints.Foreground = new SolidColorBrush(Color.FromRgb(230, 175, 150));
        _breakpoints.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 55));
        _breakpoints.BorderThickness = new Thickness(1);
        right.Children.Add(_breakpoints);
        body.Children.Add(right);
        Grid.SetColumn(right, 1);

        root.Children.Add(body);
        Grid.SetRow(body, 1);
        Content = root;
        SetState("Not playing", false, Array.Empty<DebugVariable>(), Array.Empty<string>(), null);
    }

    private static Button Button(string text, bool accent)
    {
        var b = new Button { Content = text, FontSize = 11 };
        if (accent) b.SetResourceReference(StyleProperty, "AccentButton");
        return b;
    }

    public void SetState(string status, bool paused, IReadOnlyList<DebugVariable> variables,
        IReadOnlyList<string> breakpoints, string? location)
    {
        _status.Text = status;
        _status.Foreground = new SolidColorBrush(paused
            ? Color.FromRgb(240, 185, 90)
            : Color.FromRgb(125, 200, 135));
        _location.Text = location ?? "No active script";
        _variables.ItemsSource = variables
            .Select(v => new VariableRow { Name = v.Name, Value = v.Value.ToString("0.###", CultureInfo.InvariantCulture) })
            .ToList();
        _breakpoints.ItemsSource = breakpoints;
        _continue.IsEnabled = paused;
        _stepLine.IsEnabled = paused;
        _stepInstruction.IsEnabled = paused;
        _pause.IsEnabled = !paused;
        _stop.IsEnabled = true;
    }

    public void SetStopped()
    {
        _status.Text = "Not playing";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        _location.Text = "No active script";
        _variables.ItemsSource = null;
        _breakpoints.ItemsSource = null;
        _continue.IsEnabled = false;
        _stepLine.IsEnabled = false;
        _stepInstruction.IsEnabled = false;
        _pause.IsEnabled = false;
        _stop.IsEnabled = false;
    }

    private sealed class VariableRow
    {
        public string Name { get; init; } = "";
        public string Value { get; init; } = "";
    }
}
