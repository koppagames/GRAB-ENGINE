using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>Thumbnail palette for prefabs, with stamp/spawn actions and direct folder access.</summary>
public sealed class PrefabPaletteWindow : Window
{
    private readonly Action<PrefabInfo> _stamp;
    private readonly Action<PrefabInfo> _spawn;
    private readonly Action _saveSelection;
    private readonly WrapPanel _cards = new();
    private readonly TextBox _search = Theme.TextBox();
    private readonly TextBlock _status = Theme.Hint("");
    private readonly List<PrefabInfo> _prefabs = new();
    private readonly Dictionary<PrefabInfo, Image> _images = new();
    private readonly Queue<PrefabInfo> _pending = new();
    private readonly DispatcherTimer _timer;

    public PrefabPaletteWindow(Window owner, Action<PrefabInfo> stamp, Action<PrefabInfo> spawn, Action saveSelection)
    {
        _stamp = stamp;
        _spawn = spawn;
        _saveSelection = saveSelection;
        Owner = owner;
        Title = "Prefab & Asset Palette";
        Width = 1040;
        Height = 740;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel();
        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("PREFAB & ASSET PALETTE"),
                Theme.Hint("Reusable objects saved in GRAB ENGINE's prefab folder. Stamp places copies with the mouse; Spawn adds one at the current selection.")
            }
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var toolbar = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8, 14, 8)
        };
        var bar = new DockPanel();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock
        {
            Text = "Search",
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        _search.Width = 270;
        _search.ToolTip = "Filter prefabs by name";
        _search.TextChanged += (_, _) => RefreshCards();
        left.Children.Add(_search);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Theme.Button("Save Selection…", small: true);
        save.Click += (_, _) => { _saveSelection(); Reload(); };
        right.Children.Add(save);
        var folder = Theme.Button("Open Folder", small: true);
        folder.Margin = new Thickness(6, 0, 0, 0);
        folder.Click += (_, _) => OpenFolder();
        right.Children.Add(folder);
        var refresh = Theme.Button("Refresh", small: true);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.Click += (_, _) => Reload();
        right.Children.Add(refresh);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        toolbar.Child = bar;
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };
        footer.Child = _status;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14)
        };
        scroll.Content = _cards;
        root.Children.Add(scroll);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _timer.Tick += (_, _) => Pump();
        Loaded += (_, _) => { Reload(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Reload()
    {
        _prefabs.Clear();
        _prefabs.AddRange(PrefabLibrary.List());
        RefreshCards();
        _status.Text = _prefabs.Count == 0
            ? "No prefabs yet. Select objects in the Scene, then choose Save Selection."
            : $"{_prefabs.Count} prefab(s) in {PrefabLibrary.Root}";
    }

    private void RefreshCards()
    {
        _cards.Children.Clear();
        _images.Clear();
        _pending.Clear();
        string query = _search.Text.Trim();
        var visible = _prefabs.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (visible.Count == 0)
        {
            _cards.Children.Add(new TextBlock
            {
                Text = _prefabs.Count == 0 ? "No prefabs yet" : "No prefabs match this search",
                Foreground = Theme.Dim,
                FontSize = 15,
                Margin = new Thickness(35)
            });
            return;
        }
        foreach (var prefab in visible)
            _cards.Children.Add(BuildCard(prefab));
    }

    private FrameworkElement BuildCard(PrefabInfo prefab)
    {
        var image = new Image
        {
            Width = 290,
            Height = 163,
            Stretch = Stretch.UniformToFill,
            Source = LevelThumbnailRenderer.TryCached(prefab.FilePath)
        };
        if (image.Source == null) _pending.Enqueue(prefab);
        _images[prefab] = image;
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 26, 30)),
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            ClipToBounds = true,
            Child = image
        });
        var body = new StackPanel { Margin = new Thickness(9, 8, 9, 9) };
        body.Children.Add(new TextBlock
        {
            Text = prefab.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{prefab.NodeCount} objects  ·  {prefab.Modified:yyyy-MM-dd HH:mm}",
            FontSize = 10,
            Foreground = Theme.Dim,
            Margin = new Thickness(0, 2, 0, 7)
        });
        var buttons = new WrapPanel();
        var stamp = Theme.Button("Stamp", small: true);
        stamp.ToolTip = "Place copies with the mouse";
        stamp.Click += (_, _) => _stamp(prefab);
        buttons.Children.Add(stamp);
        var spawn = Theme.Button("Spawn", small: true);
        spawn.Margin = new Thickness(5, 0, 0, 0);
        spawn.Click += (_, _) => _spawn(prefab);
        buttons.Children.Add(spawn);
        var reveal = Theme.Button("Show File", small: true);
        reveal.Margin = new Thickness(5, 0, 0, 0);
        reveal.Click += (_, _) => Reveal(prefab.FilePath);
        buttons.Children.Add(reveal);
        body.Children.Add(buttons);
        stack.Children.Add(body);
        var card = new Border
        {
            Width = 310,
            Margin = new Thickness(0, 0, 12, 12),
            Background = new SolidColorBrush(Color.FromRgb(41, 41, 43)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 62)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack,
            ToolTip = prefab.FilePath
        };
        card.MouseEnter += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        card.MouseLeave += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 62));
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) _stamp(prefab);
        };
        return card;
    }

    private void Pump()
    {
        if (_pending.Count == 0) return;
        var prefab = _pending.Dequeue();
        if (!_images.TryGetValue(prefab, out var image)) return;
        try { image.Source = LevelThumbnailRenderer.RenderFile(prefab.FilePath); }
        catch { image.Source = null; }
    }

    private void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show prefab", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder()
    {
        try
        {
            PrefabLibrary.EnsureRoot();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + PrefabLibrary.Root + "\"")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open prefab folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
