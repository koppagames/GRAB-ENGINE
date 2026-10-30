using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// Explains and toggles what GRAB's GASM limits really are, because the retail editor
/// shows numbers the runtime does not enforce.
/// </summary>
public static class CompilerSettingsWindow
{
    public static bool Show(Window owner)
    {
        var win = new Window
        {
            Title = "Compiler & Limits",
            Width = 660,
            Height = 610,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Theme.Panel,
            Foreground = Theme.Text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            UseLayoutRounding = true
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("GRAB's real GASM limits"),
                Theme.Hint("The retail editor enforces numbers the runtime itself does not.")
            }
        }));

        var body = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
        body.Children.Add(Theme.SectionHeader("WHAT THE GAME ACTUALLY ENFORCES"));
        body.Children.Add(Paragraph(
            "The GASM screen in GRAB refuses to store more than 150 instructions, 10 object connections or " +
            "480 registers in one Code Block. That check lives in the editor UI: it also truncates a program " +
            "pasted into the screen."));
        body.Children.Add(Paragraph(
            "The interpreter that runs a level keeps instructions, connections and registers in dynamic " +
            "arrays, and a .level file is loaded straight into them. A Code Block that already contains " +
            "600 instructions, 20 connections or 300 registers still loads and runs in the retail game — " +
            "the same way custom register names survive a level that was written by hand."));
        body.Children.Add(Paragraph(
            "GRAB ENGINE used to split a long script into continuation Code Blocks, each under 150 " +
            "instructions, linked through a hidden bus object. That works, but it costs objects, " +
            "connections and a page hand-off on every frame, and it cannot express a script whose single " +
            "statement needs the whole budget."));

        body.Children.Add(Theme.SectionHeader("COMPILER MODE"));
        var extended = Theme.Check(
            "Extended GASM: compile each script as one Code Block (bypass the editor's 150 / 10 / 480 caps)",
            EngineSettings.ExtendedGasm);
        body.Children.Add(extended);
        var note = Theme.Hint(EngineSettings.ExtendedGasm
            ? "ON — scripts are exported exactly as compiled. Nothing is split, so there is no per-frame page hand-off and no bus object."
            : "OFF — scripts above the editor caps are split across continuation Code Blocks, which the retail GASM screen displays without errors.");
        body.Children.Add(note);
        extended.Checked += (_, _) =>
        {
            note.Text = "ON — scripts are exported exactly as compiled. Nothing is split, so there is no per-frame page hand-off and no bus object.";
        };
        extended.Unchecked += (_, _) =>
        {
            note.Text = "OFF — scripts above the editor caps are split across continuation Code Blocks, which the retail GASM screen displays without errors.";
        };

        body.Children.Add(Theme.SectionHeader("TRIGGER CONNECTIONS"));
        var triggers = Theme.Check(
            "Extended trigger targets: connect one trigger to more than 17 objects",
            EngineSettings.ExtendedTriggerTargets);
        body.Children.Add(triggers);
        var triggerNote = Theme.Hint(EngineSettings.ExtendedTriggerTargets
            ? "ON — a trigger's target list is written in full. The retail editor's trigger screen prints \"Connections: n / 17\" and refuses to add more, but the file and the runtime keep every entry."
            : "OFF — audio imports and other generators stop adding targets at 17.");
        body.Children.Add(triggerNote);
        triggers.Checked += (_, _) =>
        {
            triggerNote.Text = "ON — a trigger's target list is written in full. The retail editor's trigger screen prints \"Connections: n / 17\" and refuses to add more, but the file and the runtime keep every entry.";
        };
        triggers.Unchecked += (_, _) =>
        {
            triggerNote.Text = "OFF — audio imports and other generators stop adding targets at 17.";
        };

        body.Children.Add(Theme.SectionHeader("STILL A HARD LIMIT"));
        body.Children.Add(Paragraph(
            "These are enforced by the game and are not bypassed: 4-character connection names, 20 transparent " +
            "blocks, 100 physics groups, 10 spawn points and 1000 animation frames per object. Build Validation " +
            "reports those."));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

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
        var ok = Theme.Button("Apply & recompile", accent: true);
        ok.IsDefault = true;
        ok.MinWidth = 150;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        bool apply = false;
        ok.Click += (_, _) => { apply = true; win.DialogResult = true; };
        CancelApply(win, cancel, note, extended);

        win.Content = root;
        win.ShowDialog();
        if (!apply) return false;

        bool changed = EngineSettings.ExtendedGasm != (extended.IsChecked == true)
            || EngineSettings.ExtendedTriggerTargets != (triggers.IsChecked == true);
        EngineSettings.ExtendedGasm = extended.IsChecked == true;
        EngineSettings.ExtendedTriggerTargets = triggers.IsChecked == true;
        return changed;
    }

    private static void CancelApply(Window win, Button cancel, TextBlock note, CheckBox extended)
    {
        // Cancel keeps the previous value; the window only writes on Apply.
        cancel.Click += (_, _) =>
        {
            extended.IsChecked = EngineSettings.ExtendedGasm;
            note.Text = EngineSettings.ExtendedGasm
                ? "ON — scripts are exported exactly as compiled."
                : "OFF — long scripts are split across continuation Code Blocks.";
        };
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        Foreground = Theme.Dim,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11.5,
        Margin = new Thickness(0, 0, 0, 8)
    };
}
