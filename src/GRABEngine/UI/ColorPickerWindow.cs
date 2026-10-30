using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// A small HSV colour picker used by every colour field in the editor: drag inside the
/// square for saturation/brightness, the strip for hue, and the second strip for alpha.
/// Hex and R/G/B/A stay editable, so a value copied from GRAB can still be typed.
/// </summary>
public sealed class ColorPickerWindow : Window
{
    private readonly SvSquare _square = new();
    private readonly HueStrip _hueStrip = new();
    private readonly AlphaStrip _alphaStrip = new();
    private readonly Border _preview = new();
    private readonly TextBox _hex = Theme.TextBox();
    private readonly TextBox _rBox = Theme.TextBox();
    private readonly TextBox _gBox = Theme.TextBox();
    private readonly TextBox _bBox = Theme.TextBox();
    private readonly TextBox _aBox = Theme.TextBox();
    private readonly TextBlock _footerNote;
    private readonly CheckBox? _noTextureCheck;

    private double _hue;
    private double _sat = 1;
    private double _val = 1;
    private int _alpha = 255;
    private bool _syncing;

    public Col Selected { get; private set; } = Col.White;

    /// <summary>True when the caller allows (and the user chose) an untextured surface.</summary>
    public bool NoTexture { get; private set; }

    public ColorPickerWindow(Col initial, bool allowNoTexture = false, bool noTexture = false)
    {
        NoTexture = allowNoTexture && noTexture;
        Title = "Colour";
        Width = 434;
        Height = allowNoTexture ? 700 : 604;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
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
                Theme.Heading("COLOUR PICKER"),
                Theme.Hint("Click the square for saturation and brightness, the first strip for hue and the second for alpha. Hex and RGB are editable.")
            }
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new Border
        {
            Background = Theme.PanelAlt,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8, 14, 8)
        };
        var footerRow = new DockPanel();
        _footerNote = Theme.Hint("The picked colour is written back as #RRGGBB (or #RRGGBBAA with transparency).");
        footerRow.Children.Add(_footerNote);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Theme.Button("Cancel");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(cancel);
        var ok = Theme.Button("OK", accent: true);
        ok.IsDefault = true;
        ok.Margin = new Thickness(6, 0, 0, 0);
        ok.Click += (_, _) =>
        {
            NoTexture = _noTextureCheck?.IsChecked == true;
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Insert(0, buttons);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(14, 10, 14, 8) };

        _square.Height = 210;
        _square.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(_square);

        _hueStrip.Height = 18;
        body.Children.Add(_hueStrip);

        _alphaStrip.Height = 18;
        _alphaStrip.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_alphaStrip);

        if (allowNoTexture)
        {
            body.Children.Add(Theme.SectionHeader("SURFACE"));
            _noTextureCheck = Theme.Check("No texture (flat untextured surface)", noTexture);
            _noTextureCheck.ToolTip = "Exports Color 1 as 99999999, GRAB's sentinel for a block with no texture.";
            _noTextureCheck.Checked += (_, _) => UpdateSurfaceNote();
            _noTextureCheck.Unchecked += (_, _) => UpdateSurfaceNote();
            body.Children.Add(_noTextureCheck);
            body.Children.Add(Theme.Hint("GRAB draws a plain white surface when Color 1 is 99999999. The picked colour is still stored in the project, so turning this off restores it."));
        }

        var readout = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _preview.Height = 74;
        _preview.Margin = new Thickness(0, 0, 14, 0);
        _preview.BorderBrush = Theme.Border;
        _preview.BorderThickness = new Thickness(1);
        _preview.CornerRadius = new CornerRadius(3);
        readout.Children.Add(_preview);

        var fields = new StackPanel();
        Grid.SetColumn(fields, 1);
        _hex.MaxLength = 9;
        _hex.ToolTip = "#RRGGBB or #RRGGBBAA";
        fields.Children.Add(Theme.Row("Hex", _hex, 34));
        var rgb = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 2, 0, 0) };
        rgb.Children.Add(SmallField("R", _rBox));
        rgb.Children.Add(SmallField("G", _gBox));
        rgb.Children.Add(SmallField("B", _bBox));
        rgb.Children.Add(SmallField("A", _aBox));
        fields.Children.Add(rgb);
        readout.Children.Add(fields);
        body.Children.Add(readout);

        body.Children.Add(Theme.SectionHeader("QUICK COLOURS"));
        var presets = new WrapPanel();
        foreach (string hex in new[]
                 {
                     "#FFFFFF", "#B8B8B8", "#5A5A5A", "#101010", "#D94A4A", "#E07B33",
                     "#E3C04A", "#6FBF4A", "#3FA9A0", "#4A8FD9", "#7B5AD9", "#C95AA8",
                     "#946B47", "#E0EBF2"
                 })
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(2),
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(ParseHex(hex)),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            string captured = hex;
            swatch.MouseLeftButtonUp += (_, _) => ApplyCol(Col.FromHex(captured, Col.White));
            presets.Children.Add(swatch);
        }
        body.Children.Add(presets);

        root.Children.Add(body);
        Content = root;

        _square.Changed += () => { _hue = _square.Hue; _sat = _square.Sat; _val = _square.Val; SyncUi(); };
        _hueStrip.Changed += value => { _hue = value; SyncUi(); };
        _alphaStrip.Changed += value => { _alpha = (int)Math.Round(Math.Clamp(value, 0, 1) * 255); SyncUi(); };

        HookHex();
        HookRgb(_rBox, 0);
        HookRgb(_gBox, 1);
        HookRgb(_bBox, 2);
        HookRgb(_aBox, 3);

        ApplyCol(initial);
        UpdateSurfaceNote();
    }

    private void UpdateSurfaceNote()
    {
        bool flat = _noTextureCheck?.IsChecked == true;
        _footerNote.Text = flat
            ? "No texture: Color 1 exports as 99999999."
            : "The picked colour is written back as #RRGGBB (or #RRGGBBAA with transparency).";
    }

    /// <summary>Opens the picker and returns the chosen colour, or null when cancelled.</summary>
    public static Col? Pick(Window? owner, Col initial)
    {
        var dialog = new ColorPickerWindow(initial);
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    /// <summary>A colour plus the optional GRAB "no texture" flag.</summary>
    public readonly record struct ColorPickResult(Col Color, bool NoTexture);

    /// <summary>Opens the picker with the no-texture option available.</summary>
    public static ColorPickResult? PickEx(Window? owner, Col initial, bool allowNoTexture, bool noTexture)
    {
        var dialog = new ColorPickerWindow(initial, allowNoTexture, noTexture);
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dialog.ShowDialog() != true) return null;
        return new ColorPickResult(dialog.Selected, dialog.NoTexture);
    }

    private static UIElement SmallField(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(3, 0, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10, Foreground = Theme.Dim, Margin = new Thickness(2, 0, 0, 2)
        });
        box.MaxLength = 3;
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        panel.Children.Add(box);
        return panel;
    }

    private void HookHex()
    {
        void Commit()
        {
            if (_syncing) return;
            var parsed = Col.FromHex(_hex.Text, Selected);
            if (parsed.Hex == Selected.Hex && parsed.Ai == Selected.Ai) return;
            ApplyCol(parsed);
        }
        _hex.LostFocus += (_, _) => Commit();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            Keyboard.ClearFocus();
            e.Handled = true;
        };
    }

    private void HookRgb(TextBox box, int channel)
    {
        void Commit()
        {
            if (_syncing) return;
            var parts = new[] { _rBox, _gBox, _bBox, _aBox };
            var values = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    v = i == 3 ? _alpha : i == 0 ? Selected.Ri : i == 1 ? Selected.Gi : Selected.Bi;
                values[i] = Math.Clamp(v, 0, 255);
            }
            ApplyCol(Col.FromBytes(values[0], values[1], values[2], values[3]));
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            Keyboard.ClearFocus();
            e.Handled = true;
        };
    }

    private void ApplyCol(Col color)
    {
        Selected = color;
        _alpha = color.Ai;
        ToHsv(color, out _hue, out _sat, out _val);
        SyncUi();
    }

    /// <summary>Pushes the current HSV/alpha state into every control and the preview.</summary>
    private void SyncUi()
    {
        var color = FromHsv(_hue, _sat, _val, _alpha);
        Selected = color;
        _syncing = true;
        try
        {
            _square.Hue = _hue;
            _square.Sat = _sat;
            _square.Val = _val;
            _square.InvalidateVisual();

            _hueStrip.Hue = _hue;
            _hueStrip.InvalidateVisual();

            _alphaStrip.BaseColor = ToMedia(color);
            _alphaStrip.Value = _alpha / 255.0;
            _alphaStrip.InvalidateVisual();

            _preview.Background = new SolidColorBrush(ToMedia(color));
            _hex.Text = color.HexAlpha;
            _rBox.Text = color.Ri.ToString(CultureInfo.InvariantCulture);
            _gBox.Text = color.Gi.ToString(CultureInfo.InvariantCulture);
            _bBox.Text = color.Bi.ToString(CultureInfo.InvariantCulture);
            _aBox.Text = color.Ai.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static Color ToMedia(Col c) =>
        Color.FromArgb((byte)Math.Clamp(c.Ai, 0, 255), (byte)Math.Clamp(c.Ri, 0, 255),
            (byte)Math.Clamp(c.Gi, 0, 255), (byte)Math.Clamp(c.Bi, 0, 255));

    private static Color ParseHex(string hex)
    {
        var c = Col.FromHex(hex, Col.White);
        return ToMedia(c);
    }

    /// <summary>HSV (h 0-360, s/v 0-1) to a GRAB colour with the given alpha.</summary>
    public static Col FromHsv(double h, double s, double v, int alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new Col((float)(r + m), (float)(g + m), (float)(b + m), Math.Clamp(alpha, 0, 255) / 255f);
    }

    /// <summary>GRAB colour to HSV.</summary>
    public static void ToHsv(Col color, out double h, out double s, out double v)
    {
        double r = Math.Clamp(color.R, 0, 1);
        double g = Math.Clamp(color.G, 0, 1);
        double b = Math.Clamp(color.B, 0, 1);
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        h = 0;
        if (delta > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);
        }
        if (h < 0) h += 360;
        s = max <= 1e-6 ? 0 : delta / max;
        v = max;
    }

    // ------------------------------------------------------------------ picker pieces

    private sealed class SvSquare : FrameworkElement
    {
        public double Hue { get; set; }
        public double Sat { get; set; } = 1;
        public double Val { get; set; } = 1;
        public event Action? Changed;

        private bool _drag;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var rect = new Rect(0, 0, w, h);
            var pure = new SolidColorBrush(ToMedia(FromHsv(Hue, 1, 1)));
            dc.DrawRectangle(pure, null, rect);
            dc.DrawRectangle(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0), null, rect);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90), null, rect);

            var point = new Point(Sat * w, (1 - Val) * h);
            dc.DrawEllipse(null, new Pen(Brushes.Black, 2), point, 7, 7);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1), point, 7, 7);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _drag = true;
            CaptureMouse();
            SetFrom(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag) return;
            SetFrom(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SetFrom(Point p)
        {
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            Sat = Math.Clamp(p.X / w, 0, 1);
            Val = Math.Clamp(1 - p.Y / h, 0, 1);
            InvalidateVisual();
            Changed?.Invoke();
        }
    }

    private sealed class HueStrip : FrameworkElement
    {
        public double Hue { get; set; }
        public event Action<double>? Changed;
        private bool _drag;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++)
                brush.GradientStops.Add(new GradientStop(ToMedia(FromHsv(i * 60, 1, 1)), i / 6.0));
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            double x = Math.Clamp(Hue / 360.0, 0, 1) * w;
            dc.DrawRectangle(null, new Pen(Brushes.Black, 2), new Rect(x - 2, 0, 4, h));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(x - 1, 0, 2, h));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _drag = true;
            CaptureMouse();
            SetFrom(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag) SetFrom(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SetFrom(Point p)
        {
            Hue = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1) * 360;
            InvalidateVisual();
            Changed?.Invoke(Hue);
        }
    }

    private sealed class AlphaStrip : FrameworkElement
    {
        public Color BaseColor { get; set; } = Colors.White;
        public double Value { get; set; } = 1;
        public event Action<double>? Changed;
        private bool _drag;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            const double square = 7;
            for (int y = 0; y * square < h; y++)
                for (int x = 0; x * square < w; x++)
                {
                    var brush = (x + y) % 2 == 0 ? Brushes.LightGray : Brushes.DimGray;
                    dc.DrawRectangle(brush, null, new Rect(x * square, y * square,
                        Math.Min(square, w - x * square), Math.Min(square, h - y * square)));
                }
            var opaque = Color.FromArgb(255, BaseColor.R, BaseColor.G, BaseColor.B);
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, BaseColor.R, BaseColor.G, BaseColor.B), 0));
            gradient.GradientStops.Add(new GradientStop(opaque, 1));
            dc.DrawRectangle(gradient, null, new Rect(0, 0, w, h));

            double xpos = Math.Clamp(Value, 0, 1) * w;
            dc.DrawRectangle(null, new Pen(Brushes.Black, 2), new Rect(xpos - 2, 0, 4, h));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(xpos - 1, 0, 2, h));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _drag = true;
            CaptureMouse();
            SetFrom(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag) SetFrom(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SetFrom(Point p)
        {
            Value = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
            InvalidateVisual();
            Changed?.Invoke(Value);
        }
    }
}

/// <summary>
/// Helpers that add a clickable colour swatch to an existing hex text box, so every
/// colour field in the editor opens the picker from its preview.
/// </summary>
public static class ColorInputs
{
    private static readonly Brush NoTextureBrush = BuildNoTextureBrush();

    /// <summary>A swatch that mirrors the text box and opens the picker when clicked.</summary>
    public static Border CreateSwatch(TextBox box, Action<Col>? picked = null,
        Func<bool>? getNoTexture = null, Action<bool>? setNoTexture = null) =>
        new Swatch(box, picked, null, getNoTexture, setNoTexture);

    /// <summary>Adds picker behaviour to an existing swatch border and text box.</summary>
    public static void Attach(Border swatch, TextBox box, Action<Col>? picked = null,
        Func<bool>? getNoTexture = null, Action<bool>? setNoTexture = null)
    {
        var driver = new Swatch(box, picked, swatch, getNoTexture, setNoTexture);
        _ = driver;
    }

    /// <summary>The grey checkerboard used by a swatch whose block has no texture.</summary>
    public static Brush NoTexturePreview => NoTextureBrush;

    private static Brush BuildNoTextureBrush()
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            var light = new SolidColorBrush(Color.FromRgb(224, 224, 224));
            var dark = new SolidColorBrush(Color.FromRgb(166, 166, 166));
            dc.DrawRectangle(light, null, new Rect(0, 0, 16, 16));
            dc.DrawRectangle(dark, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(dark, null, new Rect(8, 8, 8, 8));
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>A text box with its swatch docked to the right, for a settings row.</summary>
    public static FrameworkElement Field(TextBox box, Action<Col>? picked = null)
    {
        var panel = new DockPanel { LastChildFill = true };
        var swatch = CreateSwatch(box, picked);
        swatch.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(swatch, Dock.Right);
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return panel;
    }

    private sealed class Swatch : Border
    {
        private readonly TextBox _box;
        private readonly Action<Col>? _picked;
        private readonly Func<bool>? _getNoTexture;
        private readonly Action<bool>? _setNoTexture;
        private Border? _target;

        public Swatch(TextBox box, Action<Col>? picked, Border? existing = null,
            Func<bool>? getNoTexture = null, Action<bool>? setNoTexture = null)
        {
            _box = box;
            _picked = picked;
            _getNoTexture = getNoTexture;
            _setNoTexture = setNoTexture;

            if (existing != null)
            {
                _target = existing;
                // Reuse the border already laid out in XAML.
                existing.Cursor = Cursors.Hand;
                existing.ToolTip = _setNoTexture != null
                    ? "Click to open the colour picker and texture options"
                    : "Click to open the colour picker";
                existing.MouseLeftButtonUp += OnClick;
                box.TextChanged += (_, _) => Refresh(existing);
                Refresh(existing);
                return;
            }

            Width = 26;
            Height = 22;
            CornerRadius = new CornerRadius(2);
            BorderBrush = Theme.Border;
            BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand;
            ToolTip = _setNoTexture != null
                ? "Click to open the colour picker and texture options"
                : "Click to open the colour picker";
            MouseLeftButtonUp += OnClick;
            box.TextChanged += (_, _) => Refresh(this);
            Refresh(this);
        }

        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var current = Col.FromHex(_box.Text, Col.White);
            var owner = Window.GetWindow(_target ?? this);

            if (_setNoTexture != null)
            {
                bool initial = _getNoTexture?.Invoke() == true;
                var choice = ColorPickerWindow.PickEx(owner, current, true, initial);
                if (choice == null) return;
                _box.Text = choice.Value.Color.HexAlpha;
                if (choice.Value.NoTexture != initial)
                    _setNoTexture(choice.Value.NoTexture);
                Refresh((Border)sender);
                _picked?.Invoke(choice.Value.Color);
                return;
            }

            var result = ColorPickerWindow.Pick(owner, current);
            if (result == null) return;
            _box.Text = result.Value.HexAlpha;
            Refresh((Border)sender);
            _picked?.Invoke(result.Value);
        }

        private void Refresh(Border target)
        {
            if (_getNoTexture?.Invoke() == true)
            {
                target.Background = NoTextureBrush;
                target.ToolTip = "No texture (Color 1 = 99999999). Click for colour and texture options.";
                target.Opacity = _box.IsEnabled ? 1 : 0.35;
                return;
            }

            var c = Col.FromHex(_box.Text, Col.White);
            target.Background = new SolidColorBrush(Color.FromArgb(255, (byte)c.Ri, (byte)c.Gi, (byte)c.Bi));
            target.ToolTip = _setNoTexture != null
                ? "Click to open the colour picker and texture options"
                : "Click to open the colour picker";
            target.Opacity = _box.IsEnabled ? 1 : 0.35;
        }
    }
}
