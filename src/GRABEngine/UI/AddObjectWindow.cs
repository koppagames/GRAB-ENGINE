using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

public sealed class AddRequest
{
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ComponentTag { get; init; }
    public int? ShapeId { get; init; }
    public bool IsGroup { get; init; }
}

/// <summary>Unity-style "Add GameObject / Component" browser opened with Ctrl+A.</summary>
public sealed class AddObjectWindow : Window
{
    private readonly TreeView _tree;
    private readonly TextBox _search;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _detailText;
    private readonly TextBlock _detailCategory;
    private readonly Button _add;
    private AddRequest? _selected;
    private readonly List<(TreeViewItem Item, AddRequest Request)> _entries = new();

    public AddObjectWindow(Window owner)
    {
        Title = "Add GameObject or Component";
        Width = 720;
        Height = 540;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("Add GameObject or Component"),
                Theme.Hint("Search, pick an item and press Add (or double-click). Ctrl+A opens this from anywhere in the scene.")
            }
        }));

        var split = new Grid { Margin = new Thickness(12, 8, 12, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 200 });

        var left = new DockPanel();
        var searchBorder = new Border { Margin = new Thickness(0, 0, 8, 8), Background = Theme.Field, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        var searchGrid = new DockPanel();
        var searchIcon = new TextBlock { Text = "⌕", Foreground = Theme.Dim, FontSize = 18, Margin = new Thickness(7, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchGrid.Children.Add(searchIcon);
        _search = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddSelected();
            if (e.Key == Key.Down)
            {
                _tree.Focus();
                if (_tree.SelectedItem is TreeViewItem item) item.Focus();
                e.Handled = true;
            }
        };
        searchGrid.Children.Add(_search);
        searchBorder.Child = searchGrid;
        DockPanel.SetDock(searchBorder, Dock.Top);
        left.Children.Add(searchBorder);

        _tree = new TreeView { BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(33, 33, 33)), Padding = new Thickness(4) };
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is TreeViewItem item && item.Tag is AddRequest request) Select(request);
            else if (e.NewValue is TreeViewItem category) { category.IsSelected = false; }
        };
        _tree.MouseDoubleClick += (_, _) => AddSelected();
        _tree.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddSelected();
            if (e.Key == Key.Escape) Close();
        };
        left.Children.Add(_tree);
        split.Children.Add(left);

        var detail = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        _detailCategory = new TextBlock { Foreground = Theme.Accent, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        _detailTitle = new TextBlock { Text = "Nothing selected", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        _detailText = new TextBlock { Foreground = Theme.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        detail.Children.Add(_detailCategory);
        detail.Children.Add(_detailTitle);
        detail.Children.Add(_detailText);
        var closePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _add = Theme.Button("Add", accent: true);
        _add.MinWidth = 100;
        _add.IsEnabled = false;
        _add.Margin = new Thickness(0, 16, 0, 6);
        _add.Click += (_, _) => AddSelected();
        var cancel = Theme.Button("Cancel");
        cancel.Click += (_, _) => Close();
        closePanel.Children.Add(_add);
        closePanel.Children.Add(cancel);
        detail.Children.Add(closePanel);
        Grid.SetColumn(detail, 1);
        split.Children.Add(detail);
        Grid.SetRow(split, 1);
        root.Children.Add(split);

        var footer = new Border { Padding = new Thickness(14, 0, 14, 10) };
        footer.Child = new TextBlock
        {
            Text = "Tip: 3D shapes become real GRAB blocks with the correct model, texture and material.",
            FontSize = 10.5,
            Foreground = Theme.Dim
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        BuildTree();
        Loaded += (_, _) => { _search.Focus(); };
    }

    public AddRequest? Request { get; private set; }

    private void BuildTree()
    {
        _tree.Items.Clear();
        _entries.Clear();

        var shapes = new TreeViewItem { Header = CategoryHeader("3D OBJECTS", "Shapes that exist in GRAB"), IsExpanded = true };
        foreach (var (id, name) in ShapeInfo.All)
        {
            var entry = new AddRequest
            {
                Category = "3D Object",
                Name = name,
                ShapeId = id,
                Description = ShapeDescription(name)
            };
            AddEntry(shapes, entry);
        }
        _tree.Items.Add(shapes);

        var components = new TreeViewItem { Header = CategoryHeader("COMPONENTS", "Behaviours and gameplay objects"), IsExpanded = true };
        foreach (var request in ComponentRequests()) AddEntry(components, request);
        _tree.Items.Add(components);

        var empty = new TreeViewItem { Header = CategoryHeader("EMPTY", "Organise your hierarchy"), IsExpanded = true };
        AddEntry(empty, new AddRequest
        {
            Category = "Empty",
            Name = "Create Empty Group",
            IsGroup = true,
            Description = "A transform-only group. Child objects move, rotate and scale with it, exactly like a Unity parent."
        });
        _tree.Items.Add(empty);
    }

    private static IEnumerable<AddRequest> ComponentRequests()
    {
        yield return new AddRequest { Category = "Component", Name = "Code Block (C#)", ComponentTag = "GASM", Description = "A GASM program written in C#. Open it in the C# editor; Play or Export translates it into real GRAB bytecode, registers and connections." };
        yield return new AddRequest { Category = "Component", Name = "Trigger", ComponentTag = "Trigger", Description = "A trigger zone. Fire hand/head/grapple/feet/block sources at animation, ambience, light, sound or program targets." };
        yield return new AddRequest { Category = "Component", Name = "Point Light", ComponentTag = "Light", Description = "A point or spot light with GRAB's colour, intensity, range, angle and falloff settings." };
        yield return new AddRequest { Category = "Component", Name = "Sign", ComponentTag = "Sign", Description = "A text sign. C# can write live text and numbers, or switch it to a number display." };
        yield return new AddRequest { Category = "Component", Name = "Sound", ComponentTag = "Sound", Description = "A positional sound emitter with volume, range, wave type, pitch and envelope settings." };
        yield return new AddRequest { Category = "Component", Name = "Particle Emitter", ComponentTag = "ParticleEmitter", Description = "A particle source. Tune rate, lifetime, size, colours, velocity and acceleration; previews live in the scene." };
        yield return new AddRequest { Category = "Component", Name = "Start Point", ComponentTag = "Start", Description = "A spawn point. Every level needs at least one." };
        yield return new AddRequest { Category = "Component", Name = "Finish Point", ComponentTag = "Finish", Description = "The level finish radius." };
        yield return new AddRequest { Category = "Component", Name = "Gravity Zone", ComponentTag = "Gravity", Description = "Overrides gravity inside the zone: default, no legs or monkey movement, with a custom direction." };
        yield return new AddRequest { Category = "Component", Name = "Crumbling Block", ComponentTag = "Crumbling", Description = "A block that breaks after a stable time and respawns after a delay." };
        yield return new AddRequest { Category = "Component", Name = "Lobby Terminal", ComponentTag = "LobbyTerminal", Description = "A lobby terminal that opens the level browser in game." };
    }

    private static string ShapeDescription(string name) => name switch
    {
        "Cube" => "The classic GRAB block. Uses cube.sgm and the selected material texture.",
        "Sphere" => "A smooth sphere with GRAB's sphere model.",
        "Cylinder" => "A round pillar. Good for poles, pipes and platforms.",
        "Pyramid" => "A triangular pyramid.",
        "Prism" => "A three-sided prism.",
        "Cone" => "A cone, useful for spikes and decorations.",
        "Pyramid (Square Base)" => "A four-sided pyramid with a square base.",
        _ => "A real GRAB shape."
    };

    private static UIElement CategoryHeader(string title, string hint)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)) });
        stack.Children.Add(new TextBlock { Text = hint, FontSize = 9.5, Foreground = Theme.Dim });
        return stack;
    }

    private void AddEntry(TreeViewItem categoryNode, AddRequest request)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = request.ShapeId != null ? "■" : request.ComponentTag != null ? "◆" : "▣",
            Foreground = request.ShapeId != null ? new SolidColorBrush(Color.FromRgb(150, 186, 218)) : new SolidColorBrush(Color.FromRgb(198, 168, 96)),
            Margin = new Thickness(0, 1, 6, 0),
            FontSize = 10
        });
        panel.Children.Add(new TextBlock { Text = request.Name, Foreground = Theme.Text });
        var item = new TreeViewItem { Header = panel, Tag = request };
        categoryNode.Items.Add(item);
        _entries.Add((item, request));
    }

    private void Filter()
    {
        string query = _search.Text.Trim();
        foreach (var (item, request) in _entries)
        {
            bool match = query.Length == 0 || request.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || request.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || request.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (TreeViewItem category in _tree.Items)
        {
            category.IsExpanded = true;
            bool any = category.Items.OfType<TreeViewItem>().Any(x => x.Visibility == Visibility.Visible);
            category.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        var first = _entries.FirstOrDefault(e => e.Item.Visibility == Visibility.Visible);
        if (first.Item != null)
        {
            _selected = first.Request;
            first.Item.IsSelected = true;
            first.Item.BringIntoView();
        }
        else
        {
            _selected = null;
            _detailTitle.Text = "No matches";
            _detailText.Text = "Try a different search term.";
            _detailCategory.Text = "";
            _add.IsEnabled = false;
        }
    }

    private void Select(AddRequest request)
    {
        _selected = request;
        _detailCategory.Text = request.Category.ToUpperInvariant();
        _detailTitle.Text = request.Name;
        _detailText.Text = request.Description;
        _add.IsEnabled = true;
    }

    private void AddSelected()
    {
        if (_selected == null) return;
        Request = _selected;
        DialogResult = true;
    }
}
