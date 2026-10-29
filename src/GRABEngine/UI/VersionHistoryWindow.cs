using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>Lists autosave-created and manual project snapshots and restores one.</summary>
public sealed class VersionHistoryWindow : Window
{
    private readonly string? _projectPath;
    private readonly string _title;
    private readonly ListView _list = new();
    private readonly TextBlock _details = new();
    private readonly TextBlock _empty = new();
    private List<SnapshotInfo> _snapshots = new();

    public event Action<string>? RestoreRequested;

    public VersionHistoryWindow(Window owner, string? projectPath, string title)
    {
        _projectPath = projectPath;
        _title = title;
        Title = "Version History — " + title;
        Width = 760;
        Height = 540;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;
        Content = BuildUi();
        RefreshList();
    }

    private UIElement BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("VERSION HISTORY"),
                Theme.Hint("Every manual save and periodic autosave is kept here. Restore a snapshot to undo a bad edit session.")
            }
        });
        root.Children.Add(header);
        Grid.SetRow(header, 0);

        var body = new Grid { Margin = new Thickness(14) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _list.Background = Theme.Field;
        _list.Foreground = Theme.Text;
        _list.BorderBrush = Theme.Border;
        _list.SelectionChanged += (_, _) => ShowDetails();
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "When", Width = 190, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SnapshotInfo.DisplayTime)) });
        view.Columns.Add(new GridViewColumn { Header = "Snapshot", Width = 360, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SnapshotInfo.Label)) });
        view.Columns.Add(new GridViewColumn { Header = "Size", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SnapshotInfo.DisplaySize)) });
        _list.View = view;
        body.Children.Add(_list);
        Grid.SetRow(_list, 0);

        _details.Foreground = Theme.Dim;
        _details.Margin = new Thickness(2, 8, 2, 0);
        _details.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_details);
        Grid.SetRow(_details, 1);

        _empty.Text = "No snapshots yet. Save the project, or use File → Create Snapshot.";
        _empty.Foreground = Theme.Dim;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.IsHitTestVisible = false;
        var overlay = new Grid();
        // Detach first: a WPF element cannot be added to a second visual parent.
        // Adding the list to the overlay while it was still in the body grid threw
        // "Specified element is already the logical child of another element" and
        // crashed the editor as soon as Version History opened.
        body.Children.Remove(_list);
        overlay.Children.Add(_list);
        overlay.Children.Add(_empty);
        body.Children.Add(overlay);
        Grid.SetRow(overlay, 0);

        root.Children.Add(body);
        Grid.SetRow(body, 1);

        var actions = new DockPanel { Margin = new Thickness(14, 0, 14, 12) };
        var restore = Theme.Button("Restore Snapshot", accent: true);
        restore.Click += (_, _) => RestoreSelected();
        var delete = Theme.Button("Delete");
        delete.Click += (_, _) => DeleteSelected();
        var open = Theme.Button("Open History Folder");
        open.Click += (_, _) => OpenHistoryFolder();
        var close = Theme.Button("Close");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        actions.Children.Add(close);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(restore);
        left.Children.Add(delete);
        left.Children.Add(open);
        actions.Children.Add(left);
        root.Children.Add(actions);
        Grid.SetRow(actions, 2);
        return root;
    }

    private void RefreshList(int? selectIndex = null)
    {
        _snapshots = WorkspaceStore.ListSnapshots(_projectPath, _title);
        _list.ItemsSource = null;
        _list.ItemsSource = _snapshots;
        bool any = _snapshots.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any) _list.SelectedIndex = selectIndex ?? 0;
        ShowDetails();
    }

    private SnapshotInfo? Current => _list.SelectedItem as SnapshotInfo;

    private void ShowDetails()
    {
        var item = Current;
        if (item == null)
        {
            _details.Text = _snapshots.Count == 0 ? "" : "Select a snapshot to restore it.";
            return;
        }
        _details.Text = $"{item.DisplayTime}  •  {item.DisplaySize}\n{item.FilePath}";
    }

    private void RestoreSelected()
    {
        var item = Current;
        if (item == null) return;
        var answer = MessageBox.Show(this,
            "Restore this snapshot?\n\n" + item.DisplayTime + "  —  " + item.Label +
            "\n\nThe current project state will be saved as a snapshot first, so this is reversible.",
            "Restore Snapshot", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            RestoreRequested?.Invoke(item.FilePath);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not restore the snapshot:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteSelected()
    {
        var item = Current;
        if (item == null) return;
        if (MessageBox.Show(this, "Delete this snapshot permanently?\n\n" + item.DisplayTime + "  —  " + item.Label,
                "Delete Snapshot", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        WorkspaceStore.DeleteSnapshot(item.FilePath);
        RefreshList();
    }

    private void OpenHistoryFolder()
    {
        try
        {
            var directory = WorkspaceStore.HistoryRoot;
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the history folder:\n\n" + ex.Message,
                "GRAB ENGINE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
