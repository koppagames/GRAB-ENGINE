using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Core;
using GrabEngine.Formats;
using Microsoft.Win32;

namespace GrabEngine.UI;

/// <summary>
/// Unity-style import settings for a .glb model. The importer turns quads into
/// oriented panels and triangles into oriented prisms, or rasterises the model
/// into a grid of cubes. Preview uses the real importer, so the reported counts
/// match the objects that will be added to the level.
/// </summary>
public sealed class GlbImportDialog : Window
{
    private readonly TextBox _path;
    private readonly ComboBox _mode;
    private readonly Slider _detail;
    private readonly TextBlock _detailValue;
    private readonly CheckBox _quads;
    private readonly TextBox _scale;
    private readonly TextBox _thickness;
    private readonly CheckBox _solid;
    private readonly ComboBox _material;
    private readonly CheckBox _modelColors;
    private readonly CheckBox _modelTextures;
    private readonly CheckBox _vertexColors;
    private readonly CheckBox _center;
    private readonly CheckBox _drop;
    private readonly TextBlock _stats;
    private readonly TextBlock _colorNote;
    private bool _ready;

    public GlbImportDialog(Window owner, string? initialPath = null)
    {
        Title = "Import .glb Model";
        Width = 690;
        Height = 720;
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
                Theme.Heading("Import a .glb model"),
                Theme.Hint("glTF 2.0 binary. Mesh transforms, indexed meshes, strips/fans, material colours, COLOR_0 and embedded base-colour textures are supported. Quads are rebuilt from paired coplanar triangles, and each face samples TEXCOORD_0 so textured models import with their painted colours.")
            }
        }));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 2, 16, 12) };
        var body = new StackPanel();
        scroll.Content = body;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        body.Children.Add(Theme.SectionHeader("SOURCE"));
        var pathPanel = new DockPanel();
        _path = Theme.TextBox(initialPath ?? "");
        _path.TextChanged += (_, _) => { if (_ready) _stats.Text = "Press Preview to inspect this model."; };
        var browse = Theme.Button("Browse…", small: true);
        browse.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => Browse();
        pathPanel.Children.Add(browse);
        pathPanel.Children.Add(_path);
        body.Children.Add(Theme.Row("Model file", pathPanel));

        body.Children.Add(Theme.SectionHeader("IMPORT MODE"));
        _mode = Theme.Combo(new[]
        {
            "Faces — quads become panels, triangles become prisms",
            "Voxels — surface sampled into cubes"
        }, 0);
        _mode.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            UpdateModeAvailability();
            _stats.Text = "Settings changed — press Preview.";
        };
        body.Children.Add(Theme.Row("Build", _mode));
        body.Children.Add(Theme.Hint("Faces preserves the model's exact silhouette and material colours. Voxels is better for chunky props, terrain and models you want to edit block-by-block."));

        var detailRow = new Grid();
        detailRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        _detail = new Slider
        {
            Minimum = 1,
            Maximum = 10,
            Value = 6,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _detail.ValueChanged += (_, _) =>
        {
            _detailValue.Text = ((int)_detail.Value).ToString();
            if (_ready) _stats.Text = "Settings changed — press Preview.";
        };
        _detailValue = new TextBlock
        {
            Text = "6",
            Foreground = Theme.Text,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        detailRow.Children.Add(_detail);
        Grid.SetColumn(_detailValue, 1);
        detailRow.Children.Add(_detailValue);
        body.Children.Add(Theme.Row("Detail", detailRow));
        body.Children.Add(Theme.Hint("Lower = fewer faces or coarser voxels; higher = every face or a finer voxel grid. At 10 all detected faces are kept."));

        body.Children.Add(Theme.SectionHeader("GEOMETRY"));
        _quads = Theme.Check("Detect quads (pair two coplanar triangles that share an edge)", true);
        _solid = Theme.Check("Solid voxels (fill the interior; useful for terrain and props)", false);
        body.Children.Add(_quads);
        body.Children.Add(_solid);

        var scaleRow = new Grid();
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        _scale = Theme.TextBox("1.0");
        _thickness = Theme.TextBox("0.06");
        _scale.Margin = new Thickness(0, 0, 8, 0);
        _thickness.Margin = new Thickness(8, 0, 0, 0);
        var scaleCaption = Theme.FieldLabel("Scale");
        var thicknessCaption = Theme.FieldLabel("Thickness");
        scaleRow.Children.Add(_scale);
        Grid.SetColumn(scaleCaption, 1);
        scaleRow.Children.Add(scaleCaption);
        Grid.SetColumn(_thickness, 2);
        scaleRow.Children.Add(_thickness);
        Grid.SetColumn(thicknessCaption, 3);
        scaleRow.Children.Add(thicknessCaption);
        body.Children.Add(Theme.Row("Scale / depth", scaleRow));
        body.Children.Add(Theme.Hint("Scale multiplies the model size. Thickness is the depth of a quad panel and the extrusion depth of a triangular prism."));

        _center = Theme.Check("Center X/Z on the world origin", true);
        _drop = Theme.Check("Drop the lowest point onto y = 0", true);
        body.Children.Add(_center);
        body.Children.Add(_drop);

        body.Children.Add(Theme.SectionHeader("BLOCK MATERIAL"));
        _material = Theme.Combo(MaterialInfo.All.Select(m => m.Name).ToArray(),
            Math.Max(0, Array.FindIndex(MaterialInfo.All, m => m.Id == 8)));
        _material.SelectionChanged += (_, _) =>
        {
            if (_ready) UpdateMaterialNote();
        };
        body.Children.Add(Theme.Row("Material", _material));
        _modelColors = Theme.Check("Use the model's material / face colours", true);
        _modelTextures = Theme.Check("Sample embedded base-colour textures (TEXCOORD_0)", true);
        _vertexColors = Theme.Check("Use COLOR_0 vertex colours when the model provides them", true);
        body.Children.Add(_modelColors);
        body.Children.Add(_modelTextures);
        body.Children.Add(_vertexColors);
        _colorNote = Theme.Hint("");
        body.Children.Add(_colorNote);

        _stats = new TextBlock
        {
            Text = "Choose a model and press Preview.",
            Foreground = Theme.Dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        body.Children.Add(Theme.Card(_stats, new Thickness(9, 7, 9, 7)));

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
        var preview = Theme.Button("Preview");
        preview.Margin = new Thickness(0, 0, 8, 0);
        preview.Click += (_, _) => Preview();
        var import = Theme.Button("Import", accent: true);
        import.IsDefault = true;
        import.MinWidth = 110;
        import.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(preview);
        buttons.Children.Add(import);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        _ready = true;
        UpdateModeAvailability();
        UpdateMaterialNote();
        if (!string.IsNullOrWhiteSpace(_path.Text)) _stats.Text = "Press Preview to inspect this model.";
    }

    public string FilePath => _path.Text.Trim();
    public GlbImportSettings Settings { get; private set; } = new();

    private void Browse()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "glTF binary (*.glb)|*.glb|All files (*.*)|*.*",
            DefaultExt = ".glb",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Import a .glb model"
        };
        try
        {
            var dir = Path.GetDirectoryName(_path.Text);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }
        if (dlg.ShowDialog(this) != true) return;
        _path.Text = dlg.FileName;
        Preview();
    }

    private void UpdateModeAvailability()
    {
        bool faces = _mode.SelectedIndex != 1;
        _quads.IsEnabled = faces;
        _solid.IsEnabled = !faces;
        _thickness.IsEnabled = faces;
    }

    private void UpdateMaterialNote()
    {
        int index = Math.Max(0, _material.SelectedIndex);
        int id = MaterialInfo.All[Math.Min(index, MaterialInfo.All.Length - 1)].Id;
        bool colorable = MaterialInfo.IsColorable(id);
        _colorNote.Text = colorable
            ? "Default Colored uses Color1, so imported material and vertex colours are visible in game."
            : "This GRAB material uses its own fixed texture. Model colours are kept in the editor, but the game will show the material texture instead.";
        _colorNote.Foreground = colorable ? Theme.Dim : Theme.Warning;
        // Keep the colour options available for every material: the editor still
        // stores Color1 and the Importer can be changed without losing the model's
        // palette, even when the retail material ignores paint.
    }

    private void Collect()
    {
        int materialIndex = Math.Clamp(_material.SelectedIndex, 0, MaterialInfo.All.Length - 1);
        Settings = new GlbImportSettings
        {
            Mode = _mode.SelectedIndex == 1 ? GlbImportMode.Voxels : GlbImportMode.Faces,
            Detail = (int)Math.Round(_detail.Value),
            Scale = ParseDouble(_scale.Text, 1.0),
            Thickness = ParseDouble(_thickness.Text, 0.06),
            Material = MaterialInfo.All[materialIndex].Id,
            UseModelColors = _modelColors.IsChecked == true,
            UseModelTextures = _modelTextures.IsChecked == true,
            UseVertexColors = _vertexColors.IsChecked == true,
            DetectQuads = _quads.IsChecked == true,
            SolidVoxels = _solid.IsChecked == true,
            CenterOnOrigin = _center.IsChecked == true,
            DropToGround = _drop.IsChecked == true
        };
    }

    private static double ParseDouble(string text, double fallback)
        => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private void Preview()
    {
        if (!ValidatePath()) return;
        Collect();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = GlbImporter.ImportFile(FilePath, Settings);
            _stats.Text = result.Summary
                + $"\n{result.Nodes.Count} block object{(result.Nodes.Count == 1 ? "" : "s")} will be created"
                + (result.Truncated ? "\nThe model exceeded the safe editor cap; only the first objects will be imported." : "")
                + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
            _stats.Foreground = result.Truncated ? Theme.Warning : Theme.Dim;
        }
        catch (Exception ex)
        {
            _stats.Text = "Could not read this model:\n" + ex.Message;
            _stats.Foreground = Theme.Error;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private bool ValidatePath()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            MessageBox.Show(this, "Choose an existing .glb file first.", "Import model",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!FilePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "GRAB ENGINE imports binary glTF files (.glb).", "Import model",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private void Confirm()
    {
        if (!ValidatePath()) return;
        Collect();
        DialogResult = true;
    }
}
