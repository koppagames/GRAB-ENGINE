using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace GrabEngine.UI;

public enum InstallTarget { Steam, MetaQuest }

public sealed class InstallSettings
{
    public InstallTarget Target { get; set; } = InstallTarget.Steam;
    public string SteamFolder { get; set; } = "";
    public string AdbPath { get; set; } = "";
    public string DeviceSerial { get; set; } = "";
    public string RemoteFolder { get; set; } = "/sdcard/android/data/com.slindev.grab_demo/files/levels/user";
    public bool LaunchGame { get; set; } = true;
    public ExportProfile Profile { get; set; } = ExportProfile.Release;
}

/// <summary>
/// Chooses where "Install to GRAB" puts the level: the PC/Steam user levels folder
/// or a Meta Quest headset over adb.
/// </summary>
public sealed class InstallDialog : Window
{
    public const string QuestLevelFolder = "/sdcard/android/data/com.slindev.grab_demo/files/levels/user";
    public const string QuestPackage = "com.slindev.grab_demo";

    private readonly InstallSettings _settings;
    private readonly StackPanel _steamPanel;
    private readonly StackPanel _questPanel;
    private readonly Border _steamCard;
    private readonly Border _questCard;
    private readonly RadioButton _steamRadio;
    private readonly RadioButton _questRadio;
    private readonly TextBox _steamPath;
    private readonly TextBox _adbPath;
    private readonly ComboBox _devices;
    private readonly TextBox _remotePath;
    private readonly CheckBox _launch;
    private readonly ComboBox _profile;
    private readonly TextBlock _status;

    public InstallDialog(Window owner, string levelTitle)
    {
        _settings = new InstallSettings
        {
            SteamFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GRAB", "levels", "user"),
            AdbPath = FindAdb(),
            RemoteFolder = QuestLevelFolder,
            Profile = ExportProfile.Release
        };

        Title = "Install to GRAB";
        Width = 640;
        Height = 600;
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
                Theme.Heading("Install \"" + levelTitle + "\" into GRAB"),
                Theme.Hint("The level keeps the same .level format on PC and Quest. Pick where GRAB should see it.")
            }
        }));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 2, 16, 12) };
        var body = new StackPanel();
        scroll.Content = body;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        body.Children.Add(Theme.SectionHeader("DESTINATION"));
        var targets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        _steamRadio = new RadioButton { Content = "Steam / PC", IsChecked = true, GroupName = "installTarget", Foreground = Theme.Text, Margin = new Thickness(0, 0, 22, 0) };
        _questRadio = new RadioButton { Content = "Meta Quest (adb)", GroupName = "installTarget", Foreground = Theme.Text };
        targets.Children.Add(_steamRadio);
        targets.Children.Add(_questRadio);
        body.Children.Add(targets);

        // ---- Steam ----
        // A Grid with no row definitions stacks every child in row 0. These panels
        // need a vertical StackPanel so labels, paths and hints get their own rows.
        _steamPanel = new StackPanel();
        _steamPath = Theme.TextBox(_settings.SteamFolder);
        var browse = Theme.Button("Browse…", small: true);
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += (_, _) => BrowseSteam();
        var steamFile = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        steamFile.Children.Add(browse);
        steamFile.Children.Add(_steamPath);
        _steamPanel.Children.Add(Theme.Row("Levels folder", steamFile));
        _steamPanel.Children.Add(Theme.Hint("Default: Documents\\GRAB\\levels\\user. The level appears in GRAB's level browser the next time the game refreshes its list."));
        _steamCard = Theme.Card(_steamPanel);
        body.Children.Add(_steamCard);

        // ---- Quest ----
        _questPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var adbRow = new DockPanel();
        _adbPath = Theme.TextBox(_settings.AdbPath);
        var findAdb = Theme.Button("Locate…", small: true);
        findAdb.Margin = new Thickness(6, 0, 0, 0);
        findAdb.Click += (_, _) => BrowseAdb();
        DockPanel.SetDock(findAdb, Dock.Right);
        adbRow.Children.Add(findAdb);
        adbRow.Children.Add(_adbPath);
        _questPanel.Children.Add(Theme.Row("adb.exe", adbRow));

        var deviceRow = new DockPanel();
        _devices = Theme.Combo(new[] { "(no device found)" }, 0);
        _devices.MinWidth = 220;
        var refresh = Theme.Button("Refresh", small: true);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.Click += (_, _) => RefreshDevices();
        DockPanel.SetDock(refresh, Dock.Right);
        deviceRow.Children.Add(refresh);
        deviceRow.Children.Add(_devices);
        _questPanel.Children.Add(Theme.Row("Headset", deviceRow));

        _remotePath = Theme.TextBox(_settings.RemoteFolder);
        _questPanel.Children.Add(Theme.Row("Remote folder", _remotePath));
        _launch = Theme.Check("Launch GRAB on the headset after installing", true);
        _questPanel.Children.Add(_launch);
        _questPanel.Children.Add(Theme.Hint("Install pushes the .level with `adb push` into " + QuestLevelFolder + ". Enable Developer Mode and USB debugging on the headset, then accept the USB debugging prompt."));
        _questCard = Theme.Card(_questPanel);
        body.Children.Add(_questCard);

        body.Children.Add(Theme.SectionHeader("BUILD"));
        _profile = Theme.Combo(new[] { "Debug", "Release (optimised)" }, 1);
        _profile.Width = 190;
        body.Children.Add(Theme.Row("Export profile", _profile));
        body.Children.Add(Theme.Hint("Install builds the same file as Export .level. Release uses the safe optimiser and is recommended for playing and sharing."));

        _status = new TextBlock { Foreground = Theme.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(_status);

        _steamRadio.Checked += (_, _) => SelectTarget(InstallTarget.Steam);
        _questRadio.Checked += (_, _) => SelectTarget(InstallTarget.MetaQuest);

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
        var install = Theme.Button("Install", accent: true);
        install.IsDefault = true;
        install.MinWidth = 120;
        install.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(install);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        SelectTarget(InstallTarget.Steam);
    }

    public InstallSettings Settings => _settings;

    public static string FindAdb()
    {
        try
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
            if (File.Exists(local)) return local;
            foreach (var candidate in new[]
                     {
                         @"C:\Platform-Tools\adb.exe", @"C:\adb\adb.exe",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "platform-tools", "adb.exe")
                     })
                if (File.Exists(candidate)) return candidate;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string exe = Path.Combine(dir.Trim(), "adb.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
        }
        catch { }
        return "adb";
    }

    private void SelectTarget(InstallTarget target)
    {
        _settings.Target = target;
        if (_steamCard == null || _questCard == null) return;
        // Hide the whole card, not just its contents, so the unselected destination
        // never leaves an empty bordered strip behind.
        _steamCard.Visibility = target == InstallTarget.Steam ? Visibility.Visible : Visibility.Collapsed;
        _questCard.Visibility = target == InstallTarget.MetaQuest ? Visibility.Visible : Visibility.Collapsed;
        if (target == InstallTarget.MetaQuest)
        {
            if (!_settings.AdbPath.Contains(Path.DirectorySeparatorChar)) _settings.AdbPath = FindAdb();
            // Only talk to adb when the user actually chooses a headset; opening the
            // dialog for a Steam install should never wait for a USB scan.
            RefreshDevices(silent: true);
        }
    }

    private void BrowseSteam()
    {
        var dlg = new OpenFolderDialog { Title = "Choose GRAB's levels/user folder" };
        if (Directory.Exists(_steamPath.Text)) dlg.InitialDirectory = _steamPath.Text;
        if (dlg.ShowDialog(this) == true) _steamPath.Text = dlg.FolderName;
    }

    private void BrowseAdb()
    {
        var dlg = new OpenFileDialog { Filter = "adb.exe|adb.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*", Title = "Locate adb.exe" };
        if (dlg.ShowDialog(this) != true) return;
        _adbPath.Text = dlg.FileName;
        RefreshDevices();
    }

    private void RefreshDevices(bool silent = false)
    {
        string adb = _adbPath.Text.Trim();
        var found = new List<string>();
        try
        {
            var output = RunAdb(adb, "devices", 4000);
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = line.Split('\t', ' ');
                if (parts.Length >= 2 && parts[1].Trim() is "device" or "unauthorized" or "offline")
                    found.Add(parts[0].Trim() + (parts[1].Trim() == "device" ? "" : " (" + parts[1].Trim() + ")"));
            }
        }
        catch (Exception ex)
        {
            if (!silent) _status.Text = "Could not run adb: " + ex.Message;
        }
        if (found.Count == 0) found.Add("(no device found)");
        var previous = _devices.SelectedItem as string;
        _devices.ItemsSource = found;
        _devices.SelectedIndex = previous != null && found.Contains(previous) ? found.IndexOf(previous) : 0;
        if (!silent && found[0].StartsWith("("))
            _status.Text = "No headset detected. Connect it by USB, enable Developer Mode and accept the debugging prompt.";
        else if (!silent)
            _status.Text = found.Count + " device(s) ready.";
    }

    private static string RunAdb(string adb, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(adb, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("adb did not respond. Check that the headset is connected and the adb path is correct.");
        }
        return output + error;
    }

    private void Confirm()
    {
        _settings.SteamFolder = _steamPath.Text.Trim();
        _settings.AdbPath = _adbPath.Text.Trim();
        _settings.RemoteFolder = _remotePath.Text.Trim();
        _settings.LaunchGame = _launch.IsChecked == true;
        _settings.Profile = _profile.SelectedIndex == 0 ? ExportProfile.Debug : ExportProfile.Release;
        var device = _devices.SelectedItem as string;
        _settings.DeviceSerial = device != null && !device.StartsWith("(") ? device.Split(' ')[0] : "";

        if (_settings.Target == InstallTarget.Steam)
        {
            if (string.IsNullOrWhiteSpace(_settings.SteamFolder))
            {
                MessageBox.Show(this, "Choose GRAB's levels/user folder.", "Install", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.RemoteFolder))
            {
                MessageBox.Show(this, "Set the levels folder on the headset.", "Install", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.DeviceSerial))
            {
                MessageBox.Show(this,
                    "No Meta Quest is connected.\n\nConnect the headset by USB, enable Developer Mode, accept the USB debugging prompt, then press Refresh.",
                    "Install", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
    }
}
