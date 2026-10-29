using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// Thumbnail-first browser for GRAB levels found on Steam, in Documents and on a connected
/// Meta Quest. Cards can open a level, duplicate it into the user's level folder, or merge
/// its objects into the project that is currently open.
/// </summary>
public sealed class LevelLibraryWindow : Window
{
    private readonly Action<string> _openRequested;
    private readonly Action<string> _importRequested;
    private readonly WrapPanel _cards = new();
    private readonly TextBox _search;
    private readonly ComboBox _sourceFilter;
    private readonly TextBlock _status;
    private readonly Button _questButton;
    private readonly List<LevelEntry> _entries = new();
    private readonly Dictionary<LevelEntry, Image> _thumbnails = new();
    private readonly Queue<LevelEntry> _pendingThumbnails = new();
    private readonly DispatcherTimer _thumbnailTimer;

    public LevelLibraryWindow(Window owner, Action<string> openRequested, Action<string> importRequested)
    {
        Owner = owner;
        _openRequested = openRequested;
        _importRequested = importRequested;
        Title = "Level Library";
        Width = 1220;
        Height = 820;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel();
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = "LEVEL LIBRARY",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Steam, Documents and Meta Quest levels · thumbnails generated from block geometry",
            FontSize = 11,
            Foreground = Theme.Dim,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Child = headerStack;
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
        _search = Theme.TextBox();
        _search.Width = 250;
        _search.ToolTip = "Search title, creator or file name";
        _search.TextChanged += (_, _) => RefreshCards();
        left.Children.Add(new TextBlock
        {
            Text = "Search",
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        left.Children.Add(_search);
        left.Children.Add(new TextBlock
        {
            Text = "Source",
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 7, 0)
        });
        _sourceFilter = Theme.Combo(new[] { "All sources", "My Levels", "Steam", "Workshop", "Meta Quest" });
        _sourceFilter.Width = 150;
        _sourceFilter.SelectionChanged += (_, _) => RefreshCards();
        left.Children.Add(_sourceFilter);
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _questButton = Theme.Button("Scan Meta Quest", small: true);
        _questButton.Margin = new Thickness(0, 0, 6, 0);
        _questButton.Click += (_, _) => ScanQuest();
        right.Children.Add(_questButton);
        var refresh = Theme.Button("Refresh", small: true);
        refresh.Click += (_, _) => ReloadLocal();
        right.Children.Add(refresh);
        var folder = Theme.Button("Open My Levels", small: true);
        folder.Margin = new Thickness(6, 0, 0, 0);
        folder.Click += (_, _) => OpenMyLevels();
        right.Children.Add(folder);
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
        _status = new TextBlock { Text = "Scanning local levels...", Foreground = Theme.Dim, FontSize = 11 };
        footer.Child = _status;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14)
        };
        _cards.Margin = new Thickness(0);
        scroll.Content = _cards;
        root.Children.Add(scroll);
        Content = root;

        _thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _thumbnailTimer.Tick += (_, _) => PumpThumbnail();
        Loaded += (_, _) =>
        {
            ReloadLocal();
            _thumbnailTimer.Start();
        };
        Closed += (_, _) => _thumbnailTimer.Stop();
    }

    private void ReloadLocal()
    {
        _status.Text = "Scanning local levels...";
        _questButton.IsEnabled = true;
        List<LevelEntry> entries;
        try { entries = LevelCatalog.ScanLocal().ToList(); }
        catch (Exception ex)
        {
            _status.Text = "Could not scan local levels: " + ex.Message;
            return;
        }
        _entries.RemoveAll(e => !e.Remote);
        _entries.AddRange(entries);
        SortEntries();
        RefreshCards();
        _status.Text = $"{_entries.Count} level(s) ready. Double-click a card to open it.";
    }

    private void ScanQuest()
    {
        _questButton.IsEnabled = false;
        _status.Text = "Checking for a connected Meta Quest...";
        var adb = LevelCatalog.FindAdb();
        Task.Run(() => LevelCatalog.ScanQuest(adb, msg => Dispatcher.Invoke(() => _status.Text = msg)))
            .ContinueWith(task =>
            {
                Dispatcher.Invoke(() =>
                {
                    _entries.RemoveAll(e => e.Source == LevelSource.MetaQuest);
                    if (task.Result.Count > 0) _entries.AddRange(task.Result);
                    SortEntries();
                    RefreshCards();
                    _status.Text = task.Result.Count > 0
                        ? $"Pulled {task.Result.Count} level(s) from Meta Quest."
                        : "No Quest levels found. Connect the headset, enable USB debugging and try again.";
                    _questButton.IsEnabled = true;
                });
            });
    }

    private void SortEntries()
    {
        _entries.Sort((a, b) =>
        {
            int source = SourceOrder(a.Source).CompareTo(SourceOrder(b.Source));
            if (source != 0) return source;
            return b.Modified.CompareTo(a.Modified);
        });
    }

    private static int SourceOrder(LevelSource source) => source switch
    {
        LevelSource.MyLevels => 0,
        LevelSource.MetaQuest => 1,
        LevelSource.SteamInternal => 2,
        LevelSource.SteamWorkshop => 3,
        _ => 4
    };

    private void RefreshCards()
    {
        _cards.Children.Clear();
        _thumbnails.Clear();
        _pendingThumbnails.Clear();
        string search = _search.Text.Trim();
        int filter = _sourceFilter.SelectedIndex;
        var visible = _entries.Where(entry =>
        {
            if (filter > 0 && (int)entry.Source != filter) return false;
            if (search.Length == 0) return true;
            return entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Creators.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.FileName.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (visible.Count == 0)
        {
            var empty = new StackPanel
            {
                Margin = new Thickness(45),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            empty.Children.Add(new TextBlock
            {
                Text = _entries.Count == 0 ? "No levels found" : "No levels match this filter",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            empty.Children.Add(new TextBlock
            {
                Text = "Export a level from GRAB ENGINE or copy one into Documents\\GRAB\\levels\\user.",
                FontSize = 11,
                Foreground = Theme.Dim,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
            _cards.Children.Add(empty);
            return;
        }

        foreach (var entry in visible)
        {
            var card = BuildCard(entry);
            _cards.Children.Add(card);
        }
    }

    private FrameworkElement BuildCard(LevelEntry entry)
    {
        var stack = new StackPanel();
        var cached = LevelThumbnailRenderer.TryCached(entry.Path);
        var image = new Image
        {
            Width = 300,
            Height = 169,
            Stretch = Stretch.UniformToFill,
            Source = cached ?? Placeholder()
        };
        _thumbnails[entry] = image;
        if (cached == null) _pendingThumbnails.Enqueue(entry);
        var imageBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 26, 30)),
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            ClipToBounds = true,
            Child = image
        };
        stack.Children.Add(imageBorder);

        var body = new StackPanel { Margin = new Thickness(9, 8, 9, 9) };
        body.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var meta = $"{entry.SourceLabel}  ·  {entry.ObjectCount} objects  ·  {FormatSize(entry.Size)}";
        body.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 10,
            Foreground = Theme.Dim,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(new TextBlock
        {
            Text = entry.FileName,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(112, 136, 154)),
            Margin = new Thickness(0, 2, 0, 7),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Path
        });

        var buttons = new WrapPanel();
        var open = Theme.Button("Open", small: true);
        open.Click += (_, _) => { _openRequested(entry.Path); Close(); };
        buttons.Children.Add(open);
        var add = Theme.Button("Add to Level", small: true);
        add.Margin = new Thickness(5, 0, 0, 0);
        add.ToolTip = "Merge this level's objects into the open project as a new group";
        add.Click += (_, _) =>
        {
            try { _importRequested(entry.Path); Close(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import level", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        buttons.Children.Add(add);
        var duplicate = Theme.Button("Duplicate", small: true);
        duplicate.Margin = new Thickness(5, 0, 0, 0);
        duplicate.ToolTip = "Copy this .level into Documents\\GRAB\\levels\\user";
        duplicate.Click += (_, _) =>
        {
            try
            {
                string path = LevelCatalog.DuplicateToMyLevels(entry);
                _status.Text = "Duplicated to " + path;
                ReloadLocal();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not duplicate the level:\n\n" + ex.Message,
                    "Duplicate level", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        buttons.Children.Add(duplicate);
        body.Children.Add(buttons);
        stack.Children.Add(body);

        var card = new Border
        {
            Width = 320,
            Margin = new Thickness(0, 0, 12, 12),
            Background = new SolidColorBrush(Color.FromRgb(41, 41, 43)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 62)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack,
            ToolTip = entry.Path,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        card.MouseDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _openRequested(entry.Path); Close(); }
        };
        card.MouseEnter += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        card.MouseLeave += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 62));
        return card;
    }

    private void PumpThumbnail()
    {
        if (_pendingThumbnails.Count == 0) return;
        var entry = _pendingThumbnails.Dequeue();
        if (!_thumbnails.TryGetValue(entry, out var image)) return;
        try
        {
            image.Source = LevelThumbnailRenderer.RenderFile(entry.Path);
        }
        catch
        {
            image.Source = Placeholder();
        }
        if (_pendingThumbnails.Count == 0)
            _status.Text = $"{_entries.Count} level(s) · thumbnails ready";
    }

    private static BitmapSource Placeholder()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(31, 44, 54), Color.FromRgb(16, 21, 26), 90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, 300, 169));
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), 1);
            for (int x = 0; x < 300; x += 24) dc.DrawLine(grid, new Point(x, 0), new Point(x, 169));
            for (int y = 0; y < 169; y += 24) dc.DrawLine(grid, new Point(0, y), new Point(300, y));
            var text = new FormattedText("GENERATING THUMBNAIL", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 9.5,
                new SolidColorBrush(Color.FromArgb(150, 210, 226, 238)), 1.0);
            dc.DrawText(text, new Point(16, 143));
        }
        var rtb = new RenderTargetBitmap(300, 169, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private void OpenMyLevels()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRAB", "levels", "user");
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
    }
}
