using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GrabEngine.UI;

/// <summary>Small Unity-style modal input box used for renaming objects.</summary>
public static class Prompt
{
    public static string? Show(Window owner, string title, string label, string value)
    {
        var win = new Window
        {
            Title = title,
            Width = 380,
            Height = 158,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)),
            ShowInTaskbar = false
        };
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 0, 0, 6) };
        var box = new TextBox { Text = value, Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(lbl, 0);
        Grid.SetRow(box, 1);
        Grid.SetRow(buttons, 2);
        root.Children.Add(lbl);
        root.Children.Add(box);
        root.Children.Add(buttons);
        win.Content = root;
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = box.Text; win.DialogResult = true; } };
        win.ShowDialog();
        return result;
    }
}
