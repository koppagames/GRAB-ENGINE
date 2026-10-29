using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrabEngine.UI;

/// <summary>Shared look for GRAB ENGINE's tool windows and dialogs.</summary>
public static class Theme
{
    public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(43, 43, 43));
    public static readonly Brush PanelAlt = new SolidColorBrush(Color.FromRgb(37, 37, 37));
    public static readonly Brush Field = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    public static readonly Brush Border = new SolidColorBrush(Color.FromRgb(64, 64, 64));
    public static readonly Brush Text = new SolidColorBrush(Color.FromRgb(216, 216, 216));
    public static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0, 122, 204));
    public static readonly Brush Section = new SolidColorBrush(Color.FromRgb(56, 56, 56));
    public static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(226, 181, 78));
    public static readonly Brush Error = new SolidColorBrush(Color.FromRgb(234, 112, 96));
    public static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(112, 196, 128));

    static Theme()
    {
        foreach (var brush in new[] { Panel, PanelAlt, Field, Border, Text, Dim, Accent, Section, Warning, Error, Ok })
            brush.Freeze();
    }

    public static Window Dialog(Window owner, string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Panel,
            Foreground = Text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            UseLayoutRounding = true
        };
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = Text,
        Margin = new Thickness(0, 0, 0, 2)
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Dim,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 6)
    };

    public static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };

    public static Border SectionHeader(string text)
    {
        return new Border
        {
            Background = Section,
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 10, 0, 5),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(218, 218, 218))
            }
        };
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(47, 47, 47)),
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = padding ?? new Thickness(8),
            Child = child
        };
    }

    public static Grid Row(string label, UIElement content, double labelWidth = 118)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = FieldLabel(label);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    public static Button Button(string text, bool accent = false, bool small = false)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = small ? 0 : 84,
            Padding = small ? new Thickness(8, 2, 8, 2) : new Thickness(12, 4, 12, 4),
            FontSize = small ? 11 : 12,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        return b;
    }

    public static TextBox TextBox(string value = "", bool multiline = false, double height = 24)
    {
        return new TextBox
        {
            Text = value,
            Height = multiline ? double.NaN : height,
            MinHeight = height,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
    }

    public static CheckBox Check(string text, bool value = false)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = value,
            Foreground = Text,
            Margin = new Thickness(0, 3, 0, 3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    public static ComboBox Combo(string[] items, int index = 0)
    {
        var cb = new ComboBox { ItemsSource = items, Height = 24, MinWidth = 130 };
        cb.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, items.Length - 1));
        return cb;
    }

    public static Border AccentBar(UIElement content) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
        BorderBrush = Border,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(14, 10, 14, 10),
        Child = content
    };
}
