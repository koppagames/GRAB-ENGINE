using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>Small Unity-style layer manager: create layers, toggle visibility/locking,
/// recolour them and assign the current selection.</summary>
public sealed class LayerManagerWindow : Window
{
    private readonly GrabLevel _level;
    private readonly IReadOnlyList<LevelNode> _selection;
    private readonly ListBox _list = new();
    private readonly TextBox _name = Theme.TextBox();
    private readonly TextBox _color = Theme.TextBox();
    private readonly CheckBox _visible = Theme.Check("Visible");
    private readonly CheckBox _locked = Theme.Check("Locked");
    private readonly TextBlock _summary = Theme.Hint("");
    private bool _syncing;

    public bool Changed { get; private set; }
    public string AssignedLayerId { get; private set; } = "";

    public LayerManagerWindow(Window owner, GrabLevel level, IReadOnlyList<LevelNode> selection)
    {
        _level = level;
        _selection = selection;
        Owner = owner;
        Title = "Editor Layers";
        Width = 620;
        Height = 480;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        header.Children.Add(Theme.Heading("EDITOR LAYERS"));
        header.Children.Add(Theme.Hint("Layers are editor-only: they never change the exported GRAB level. Hiding a layer removes its objects from the Scene; locking it prevents viewport selection."));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var assign = Theme.Button("Assign Selection", accent: true);
        assign.Click += (_, _) => AssignSelection();
        footer.Children.Add(assign);
        var close = Theme.Button("Close");
        close.Margin = new Thickness(6, 0, 0, 0);
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _list.Margin = new Thickness(0, 0, 12, 0);
        _list.DisplayMemberPath = "Name";
        _list.SelectionChanged += (_, _) => LoadSelected();
        body.Children.Add(_list);

        var side = new StackPanel();
        side.Children.Add(Theme.SectionHeader("LAYER PROPERTIES"));
        side.Children.Add(Theme.Row("Name", _name));
        side.Children.Add(Theme.Row("Colour", ColorInputs.Field(_color)));
        _color.ToolTip = "#RRGGBB";
        side.Children.Add(_visible);
        side.Children.Add(_locked);
        _summary.Margin = new Thickness(0, 6, 0, 8);
        side.Children.Add(_summary);

        var buttons = new WrapPanel();
        var add = Theme.Button("+ New", small: true);
        add.Click += (_, _) => AddLayer();
        buttons.Children.Add(add);
        var remove = Theme.Button("Delete", small: true);
        remove.Margin = new Thickness(5, 0, 0, 0);
        remove.Click += (_, _) => DeleteLayer();
        buttons.Children.Add(remove);
        var apply = Theme.Button("Apply", small: true);
        apply.Margin = new Thickness(5, 0, 0, 0);
        apply.Click += (_, _) => ApplyProperties();
        buttons.Children.Add(apply);
        side.Children.Add(buttons);
        side.Children.Add(new TextBlock
        {
            Text = "Tip: assign selected objects to the active layer with Assign Selection, then hide or lock that layer while working on something else.",
            Foreground = Theme.Dim,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        root.Children.Add(body);
        Content = root;

        Reload(level.ActiveLayerId);
    }

    private EditorLayer? Selected => _list.SelectedItem as EditorLayer;

    private void Reload(string? selectId = null)
    {
        _level.EnsureLayers();
        string? keep = selectId ?? Selected?.Id;
        _list.ItemsSource = null;
        _list.ItemsSource = _level.EditorLayers.ToList();
        _list.SelectedItem = _level.EditorLayers.FirstOrDefault(l => l.Id == keep) ?? _level.EditorLayers.FirstOrDefault();
        LoadSelected();
    }

    private void LoadSelected()
    {
        var layer = Selected;
        _syncing = true;
        try
        {
            bool enabled = layer != null;
            _name.IsEnabled = enabled;
            _color.IsEnabled = enabled;
            _visible.IsEnabled = enabled;
            _locked.IsEnabled = enabled;
            if (layer == null)
            {
                _name.Text = "";
                _color.Text = "";
                _visible.IsChecked = false;
                _locked.IsChecked = false;
                return;
            }
            _name.Text = layer.Name;
            _color.Text = layer.Color;
            _visible.IsChecked = layer.Visible;
            _locked.IsChecked = layer.Locked;
            int count = _level.AllNodes().Count(n => n.LayerId == layer.Id);
            _summary.Text = $"{count} object(s) directly on this layer." +
                (layer.IsDefault ? " The Default layer cannot be deleted." : "");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyProperties()
    {
        var layer = Selected;
        if (layer == null) return;
        string name = string.IsNullOrWhiteSpace(_name.Text) ? "Layer" : _name.Text.Trim();
        if (_level.EditorLayers.Any(l => l.Id != layer.Id && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            name += " " + (_level.EditorLayers.Count + 1);
        layer.Name = name;
        if (System.Text.RegularExpressions.Regex.IsMatch(_color.Text.Trim(), "^#[0-9a-fA-F]{6}$"))
            layer.Color = _color.Text.Trim();
        layer.Visible = _visible.IsChecked == true;
        layer.Locked = _locked.IsChecked == true;
        Changed = true;
        Reload(layer.Id);
    }

    private void AddLayer()
    {
        string[] palette = { "#4A90D9", "#D98C4A", "#66B36A", "#B36AC8", "#D9C64A", "#D95A5A", "#4AC8C8" };
        string name = "Layer " + (_level.EditorLayers.Count + 1);
        var layer = new EditorLayer
        {
            Name = name,
            Color = palette[(_level.EditorLayers.Count - 1) % palette.Length]
        };
        _level.EditorLayers.Add(layer);
        _level.ActiveLayerId = layer.Id;
        Changed = true;
        Reload(layer.Id);
    }

    private void DeleteLayer()
    {
        var layer = Selected;
        if (layer == null || layer.IsDefault) return;
        foreach (var node in _level.AllNodes().Where(n => n.LayerId == layer.Id))
            node.LayerId = GrabLevel.DefaultLayerId;
        if (_level.ActiveLayerId == layer.Id) _level.ActiveLayerId = GrabLevel.DefaultLayerId;
        _level.EditorLayers.Remove(layer);
        Changed = true;
        Reload(GrabLevel.DefaultLayerId);
    }

    private void AssignSelection()
    {
        var layer = Selected;
        if (layer == null) return;
        AssignedLayerId = layer.Id;
        _level.ActiveLayerId = layer.Id;
        Changed = true;
        _summary.Text = $"Assigning {_selection.Count} selected object(s) to '{layer.Name}' when this window closes.";
    }
}
