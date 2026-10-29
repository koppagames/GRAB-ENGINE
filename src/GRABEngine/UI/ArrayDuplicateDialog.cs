using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrabEngine.UI;

public sealed class ArrayDuplicateSettings
{
    public int Axis { get; set; }
    public int Count { get; set; } = 3;
    public double Spacing { get; set; } = 1.5;
}

/// <summary>Unity-style linear array duplicate used by Edit → Transform → Duplicate Array.</summary>
public sealed class ArrayDuplicateDialog : Window
{
    private readonly ComboBox _axis;
    private readonly TextBox _count;
    private readonly TextBox _spacing;

    public ArrayDuplicateDialog(Window owner)
    {
        Title = "Duplicate Array";
        Width = 430;
        Height = 286;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
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
                Theme.Heading("Duplicate in a line"),
                Theme.Hint("Creates evenly spaced copies of every selected root object along one world axis.")
            }
        }));

        var body = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _axis = Theme.Combo(new[] { "X — right", "Y — up", "Z — forward" }, 0);
        _count = Theme.TextBox("3");
        _spacing = Theme.TextBox("1.5");
        body.Children.Add(Theme.Row("Axis", _axis));
        body.Children.Add(Theme.Row("Copies", _count));
        body.Children.Add(Theme.Row("Spacing", _spacing));
        body.Children.Add(Theme.Hint("Copies includes the original. Spacing is in world units; negative values duplicate in the opposite direction."));

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Theme.Button("Cancel");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.IsCancel = true;
        var create = Theme.Button("Duplicate", accent: true);
        create.IsDefault = true;
        create.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    public ArrayDuplicateSettings Settings { get; } = new();

    private void Confirm()
    {
        int count = int.TryParse(_count.Text, out int parsedCount) ? parsedCount : 0;
        double spacing = double.TryParse(_spacing.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsedSpacing) ? parsedSpacing : 0;
        if (count < 2 || count > 64)
        {
            MessageBox.Show(this, "Copies must be between 2 and 64.", "Duplicate Array",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Math.Abs(spacing) < 1e-5)
        {
            MessageBox.Show(this, "Spacing cannot be zero.", "Duplicate Array",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Settings.Axis = Math.Clamp(_axis.SelectedIndex, 0, 2);
        Settings.Count = count;
        Settings.Spacing = spacing;
        DialogResult = true;
    }
}
