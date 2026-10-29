using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Core;
using Microsoft.Win32;

namespace GrabEngine.UI;

public enum ExportProfile { Debug, Release }

public sealed class ExportSettings
{
    public ExportProfile Profile { get; set; } = ExportProfile.Release;
    public string Path { get; set; } = "";
    public bool OptimizeScripts { get; set; } = true;
    public bool OptimizeAnimations { get; set; } = true;
    public bool TrimUnusedPrograms { get; set; } = true;
    public bool VerifyOutput { get; set; } = true;
    public bool WriteGasmListing { get; set; }
    public bool OpenFolder { get; set; }
}

/// <summary>Export settings window shown by File → Export .level and by Install to GRAB.</summary>
public sealed class ExportDialog : Window
{
    private readonly GrabLevel _level;
    private readonly ExportSettings _settings;
    private readonly TextBox _path;
    private readonly TextBlock _summary;
    private readonly TextBlock _profileNote;
    private readonly CheckBox _optimizeScripts;
    private readonly CheckBox _optimizeAnimations;
    private readonly CheckBox _trimPrograms;
    private readonly CheckBox _verify;
    private readonly CheckBox _listing;
    private readonly CheckBox _openFolder;
    private readonly ComboBox _profile;
    private bool _ready;

    public ExportDialog(Window owner, GrabLevel level, string defaultPath, ExportSettings? initial = null)
    {
        _level = level;
        _settings = initial ?? new ExportSettings { Path = defaultPath };
        if (string.IsNullOrWhiteSpace(_settings.Path)) _settings.Path = defaultPath;

        Title = "Export GRAB Level";
        Width = 650;
        Height = 690;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("Export a real GRAB .level"),
                Theme.Hint("The file uses GRAB's protobuf format and can be dropped into the game's levels/user folder or installed with adb.")
            }
        }));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 2, 16, 12) };
        var body = new StackPanel();
        scroll.Content = body;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // ---- Build profile ----
        body.Children.Add(Theme.SectionHeader("PROJECT EXPORT TYPE"));
        _profile = Theme.Combo(new[] { "Debug", "Release" }, (int)_settings.Profile);
        _profile.Width = 170;
        _profile.SelectionChanged += (_, _) => ApplyProfile();
        body.Children.Add(Theme.Row("Profile", _profile));
        _profileNote = Theme.Hint("");
        body.Children.Add(_profileNote);

        var compare = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        compare.Children.Add(ProfileCard("DEBUG", "Normal export. Keeps the GASM exactly as the compiler emitted it, includes verbose names and writes an optional listing for debugging.", Theme.Accent, 0));
        var release = ProfileCard("RELEASE", "Ships the same gameplay with safe optimisations: constant folding, no-op removal, dead code after jumps, animation cleanup and unused program trimming.", Theme.Ok, 1);
        compare.Children.Add(release);
        Grid.SetColumn(compare.Children[0], 0);
        Grid.SetColumn(release, 1);
        body.Children.Add(compare);

        // ---- Scripts ----
        body.Children.Add(Theme.SectionHeader("SCRIPT OPTIMISATION"));
        _optimizeScripts = Theme.Check("Run the safe GASM optimiser (constant folding, dead code, no-ops)", _settings.OptimizeScripts);
        _optimizeAnimations = Theme.Check("Clean animation clips (sort keys, remove duplicate times, keep duration)", _settings.OptimizeAnimations);
        _trimPrograms = Theme.Check("Trim imported GASM copies that a compiled Code Block no longer needs", _settings.TrimUnusedPrograms);
        body.Children.Add(_optimizeScripts);
        body.Children.Add(_optimizeAnimations);
        body.Children.Add(_trimPrograms);
        body.Children.Add(Theme.Hint("The Release optimiser only applies passes that cannot change register values, control flow, animation timing or gameplay. A verification pass re-reads the exported file before it is reported as successful."));

        // ---- Output ----
        body.Children.Add(Theme.SectionHeader("OUTPUT"));
        var pathPanel = new DockPanel();
        _path = Theme.TextBox(_settings.Path);
        var browse = Theme.Button("Browse…", small: true);
        browse.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => Browse();
        pathPanel.Children.Add(browse);
        pathPanel.Children.Add(_path);
        body.Children.Add(Theme.Row("File", pathPanel));

        _verify = Theme.Check("Verify the file after writing (re-read nodes, IDs, programs and animations)", _settings.VerifyOutput);
        _listing = Theme.Check("Write a human-readable GASM listing next to the .level (Debug aid)", _settings.WriteGasmListing);
        _openFolder = Theme.Check("Open the export folder when finished", _settings.OpenFolder);
        body.Children.Add(_verify);
        body.Children.Add(_listing);
        body.Children.Add(_openFolder);

        _summary = new TextBlock { Foreground = Theme.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(_summary);

        // ---- Footer ----
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Theme.Button("Cancel");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var export = Theme.Button("Export .level", accent: true);
        export.IsDefault = true;
        export.MinWidth = 130;
        export.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(export);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;

        _ready = false;
        ApplyProfile();
        UpdateSummary();
        _path.TextChanged += (_, _) => { _settings.Path = _path.Text.Trim(); UpdateSummary(); };
        _ready = true;
    }

    public ExportSettings Settings => _settings;

    private Border ProfileCard(string title, string text, Brush accent, int column)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = accent, FontSize = 11 });
        stack.Children.Add(new TextBlock { Text = text, Foreground = Theme.Dim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var card = Theme.Card(stack, new Thickness(8, 6, 8, 6));
        card.Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0);
        return card;
    }

    private void ApplyProfile()
    {
        if (_profile.SelectedIndex < 0) return;
        _settings.Profile = (ExportProfile)_profile.SelectedIndex;
        bool release = _settings.Profile == ExportProfile.Release;
        if (_ready)
        {
            _optimizeScripts.IsChecked = release;
            _optimizeAnimations.IsChecked = release;
            _trimPrograms.IsChecked = release;
        }
        _optimizeScripts.IsEnabled = release;
        _optimizeAnimations.IsEnabled = release;
        _trimPrograms.IsEnabled = release;
        _profileNote.Text = release
            ? "Release: optimised output. Everything is verified after writing, so a broken optimisation can never be reported as a successful export."
            : "Debug: unmodified compiler output with full names. Best for inspecting GASM or reporting a bug.";
        UpdateSummary();
    }

    private void Browse()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "GRAB Level (*.level)|*.level|All files (*.*)|*.*",
            DefaultExt = ".level",
            FileName = Path.GetFileName(_path.Text),
            Title = "Export playable GRAB level"
        };
        try
        {
            var dir = Path.GetDirectoryName(_path.Text);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }
        if (dlg.ShowDialog(this) != true) return;
        _path.Text = dlg.FileName;
    }

    private void UpdateSummary()
    {
        int nodes = _level.AllNodes().Count();
        int code = _level.AllNodes().Count(n => n.Kind == NodeKind.GASM);
        int animations = _level.AllNodes().Sum(n => n.Animations.Count);
        int tris = 0;
        try { tris = _level.AllNodes().Count(n => !n.Generated); } catch { }
        _summary.Text = $"{nodes} objects  •  {code} C# code blocks  •  {animations} animation clips  •  {tris} visible objects"
            + (string.IsNullOrWhiteSpace(_settings.Path) ? "" : "\nDestination: " + _settings.Path);
    }

    private void Confirm()
    {
        _settings.Profile = (ExportProfile)Math.Max(0, _profile.SelectedIndex);
        _settings.Path = _path.Text.Trim();
        _settings.OptimizeScripts = _optimizeScripts.IsChecked == true;
        _settings.OptimizeAnimations = _optimizeAnimations.IsChecked == true;
        _settings.TrimUnusedPrograms = _trimPrograms.IsChecked == true;
        _settings.VerifyOutput = _verify.IsChecked == true;
        _settings.WriteGasmListing = _listing.IsChecked == true;
        _settings.OpenFolder = _openFolder.IsChecked == true;

        if (string.IsNullOrWhiteSpace(_settings.Path))
        {
            MessageBox.Show(this, "Choose where the .level file should be written.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_settings.Path.EndsWith(".level", StringComparison.OrdinalIgnoreCase))
            _settings.Path += ".level";
        string? dir = null;
        try { dir = Path.GetDirectoryName(_settings.Path); } catch { }
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "That destination is not a valid file path.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export folder could not be created:\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
