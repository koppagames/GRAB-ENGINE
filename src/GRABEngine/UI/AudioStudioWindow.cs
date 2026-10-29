using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GrabEngine.Audio;
using GrabEngine.Core;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace GrabEngine.UI;

/// <summary>
/// Sound block authoring and the MP3 → sound-block importer. Sound blocks in GRAB are
/// tiny synths (waveform, pitch envelope, ADSR, filters, flanger, reverb), so this window
/// previews exactly what the game will generate and can turn an MP3 into a playable score.
/// </summary>
public sealed class AudioStudioWindow : Window
{
    private static readonly string[] TriggerModeNames =
    {
        "Timed (auto-plays on level load)",
        "One Start Trigger (plays when activated)"
    };
    private readonly GrabLevel _level;
    private readonly Func<Vec3> _origin;
    private readonly ListBox _soundList = new();
    private readonly TextBlock _soundInfo = Theme.Hint("");
    private readonly TextBlock _status = Theme.Hint("");
    private readonly Canvas _wave = new() { Height = 92, ClipToBounds = true };
    private readonly TextBox _name = Theme.TextBox();
    private readonly TextBox _volume = Theme.TextBox();
    private readonly TextBox _frequency = Theme.TextBox();
    private readonly TextBox _attack = Theme.TextBox();
    private readonly TextBox _sustain = Theme.TextBox();
    private readonly TextBox _release = Theme.TextBox();
    private readonly TextBox _punch = Theme.TextBox();
    private readonly TextBox _frequencyMin = Theme.TextBox();
    private readonly TextBox _frequencySlide = Theme.TextBox();
    private readonly TextBox _frequencyDeltaSlide = Theme.TextBox();
    private readonly TextBox _vibratoStrength = Theme.TextBox();
    private readonly TextBox _vibratoSpeed = Theme.TextBox();
    private readonly TextBox _pitchJumpMod = Theme.TextBox();
    private readonly TextBox _pitchJumpSpeed = Theme.TextBox();
    private readonly TextBox _dutyCycle = Theme.TextBox();
    private readonly TextBox _dutyCycleSweep = Theme.TextBox();
    private readonly TextBox _repeatSpeed = Theme.TextBox();
    private readonly TextBox _flangerFrequency = Theme.TextBox();
    private readonly TextBox _flangerDepth = Theme.TextBox();
    private readonly TextBox _lowPass = Theme.TextBox();
    private readonly TextBox _highPass = Theme.TextBox();
    private readonly TextBox _reverbDelay = Theme.TextBox();
    private readonly TextBox _reverbDecay = Theme.TextBox();
    private readonly TextBox _range = Theme.TextBox();
    private readonly ComboBox _waveType = Theme.Combo(SoundSynth.WaveNames);
    private readonly CheckBox _repeat = Theme.Check("Repeat");
    private readonly CheckBox _startActive = Theme.Check("Start Active", true);
    private readonly Button _addButton = Theme.Button("+ Add Sound", accent: true, small: true);
    private readonly ListBox _noteList = new();
    private readonly TextBox _mp3Path = Theme.TextBox();
    private readonly TextBlock _planSummary = Theme.Hint("Choose an MP3 or WAV, tune the analysis settings and press Analyze.");
    private readonly TextBox _detail = Theme.TextBox("55");
    private readonly TextBox _maxNotes = Theme.TextBox("160");
    private readonly ComboBox _polyphony = Theme.Combo(new[] { "1 (mono)", "2 (light chords)", "3", "4 (full chords)" }, 1);
    private readonly CheckBox _snap = Theme.Check("Snap to semitones", true);
    private readonly TextBox _minFreq = Theme.TextBox("65");
    private readonly TextBox _maxFreq = Theme.TextBox("1800");
    private readonly TextBox _sensitivity = Theme.TextBox("0.10");
    private readonly TextBox _gate = Theme.TextBox("0.006");
    private readonly ComboBox _waveMode = Theme.Combo(new[] { "Auto", "Square", "Sawtooth", "Sine", "Noise" });
    private readonly TextBox _noteLength = Theme.TextBox("1.0");
    private readonly TextBox _songVolume = Theme.TextBox("0.7");
    private readonly CheckBox _scheduled = Theme.Check("Schedule notes with triggers + Code Blocks", true);
    private readonly CheckBox _songStartActive = Theme.Check("Start Active", true);
    private readonly TextBox _maxSeconds = Theme.TextBox("30");
    private readonly ComboBox _songTriggerMode = Theme.Combo(TriggerModeNames);
    private readonly Button _importButton = Theme.Button("Import into Level", accent: true);
    private readonly Button _recreateButton = Theme.Button("Preview Recreation");
    private readonly TextBox _speechText = Theme.TextBox(SpeechSynthesizer.SampleText, multiline: true, height: 88);
    private readonly ComboBox _speechVoice = Theme.Combo(SpeechSynthesizer.VoiceNames, 1);
    private readonly TextBox _speechPitch = Theme.TextBox("120");
    private readonly TextBox _speechRate = Theme.TextBox("155");
    private readonly ComboBox _speechClarity = Theme.Combo(SpeechSynthesizer.ClarityNames, 2);
    private readonly ComboBox _speechPreset = Theme.Combo(SpeechSynthesizer.PresetNames, 1);
    private readonly TextBox _speechIntonation = Theme.TextBox("0.7");
    private readonly TextBox _speechPause = Theme.TextBox("1.0");
    private readonly TextBox _speechVolume = Theme.TextBox("0.9");
    private readonly TextBox _speechMaxSeconds = Theme.TextBox("20");
    private readonly TextBox _speechSharpness = Theme.TextBox("1.35");
    private readonly TextBox _speechContrast = Theme.TextBox("1.18");
    private readonly TextBox _speechConsonants = Theme.TextBox("1.6");
    private readonly TextBox _speechBrightness = Theme.TextBox("1.12");
    private readonly TextBox _speechWordGap = Theme.TextBox("0.04");
    private readonly TextBox _speechVowelLength = Theme.TextBox("1.12");
    private readonly CheckBox _speechAcronyms = Theme.Check("Spell out acronyms (API → A P I)", true);
    private readonly ComboBox _speechTriggerMode = Theme.Combo(TriggerModeNames);
    private bool _speechLoading;

    private readonly TextBlock _speechSummary = Theme.Hint("Type a sentence and press Speak to hear the formant voice, or GRAB Preview to hear the sound blocks it will export.");
    private readonly ListBox _speechPhones = new();
    private SpeechPlan? _speechPlan;
    private readonly MediaPlayer _sourcePlayer = new();
    private SongPlan? _plan;
    private LevelNode? _selected;
    private bool _draftSelected;
    private bool _loading;
    private bool _dirty;

    public bool Changed => _dirty;

    public AudioStudioWindow(Window owner, GrabLevel level, Func<Vec3> origin, int initialTab = 0)
    {
        _level = level;
        _origin = origin;
        Owner = owner;
        Title = "Audio Studio";
        Width = 1120;
        Height = 980;
        MinWidth = 840;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        var root = new DockPanel();
        Docked(root, Theme.AccentBar(new StackPanel
        {
            Children =
            {
                Theme.Heading("AUDIO STUDIO"),
                Theme.Hint("Author GRAB sound blocks, audition them with the real synth parameters, turn MP3s into playable note machines, and speak typed text with formant sound blocks.")
            }
        }), Dock.Top);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };
        footer.Child = _status;
        Docked(root, footer, Dock.Bottom);

        var tabs = new TabControl { Background = Theme.Panel, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "  Sound Blocks  ", Content = BuildSoundTab() });
        tabs.Items.Add(new TabItem { Header = "  MP3 → Sound Blocks  ", Content = BuildMp3Tab() });
        tabs.Items.Add(new TabItem { Header = "  Text → Speech  ", Content = BuildSpeechTab() });
        tabs.Items.Add(new TabItem { Header = "  How GRAB Audio Works  ", Content = BuildGuideTab() });
        tabs.SelectedIndex = Math.Clamp(initialTab, 0, tabs.Items.Count - 1);
        root.Children.Add(tabs);
        Content = root;

        LoadSounds();
        BuildSpeech();
        Closed += (_, _) => { _sourcePlayer.Close(); AudioEngine.Shared.StopAll(); };
    }

    private static void Docked(DockPanel panel, UIElement child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        panel.Children.Add(child);
    }

    private UIElement BuildSoundTab()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(12) };
        var leftHead = new StackPanel();
        leftHead.Children.Add(Theme.Heading("SOUND BLOCKS"));
        leftHead.Children.Add(Theme.Hint("Every sound in the level. Select one to edit."));
        Docked(left, leftHead, Dock.Top);

        var addRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        _addButton.Click += (_, _) => AddSound();
        addRow.Children.Add(_addButton);
        var duplicate = Theme.Button("Duplicate", small: true);
        duplicate.Margin = new Thickness(5, 0, 0, 0);
        duplicate.Click += (_, _) => DuplicateSound();
        addRow.Children.Add(duplicate);
        Docked(left, addRow, Dock.Top);

        _soundList.Background = Theme.Field;
        _soundList.Foreground = Theme.Text;
        _soundList.BorderBrush = Theme.Border;
        _soundList.DisplayMemberPath = nameof(SoundRow.Label);
        _soundList.SelectionChanged += (_, _) =>
        {
            if (_soundList.SelectedItem is not SoundRow row) return;
            _draftSelected = false;
            SelectSound(row.Node);
        };
        left.Children.Add(_soundList);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var rightScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 12, 18, 12)
        };
        var right = new StackPanel();
        right.Children.Add(Theme.Heading("SOUND SYNTH"));
        right.Children.Add(_soundInfo);
        right.Children.Add(Theme.SectionHeader("WAVE"));
        var waveBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 26, 30)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Height = 106,
            Margin = new Thickness(0, 2, 0, 8)
        };
        waveBorder.Child = _wave;
        right.Children.Add(waveBorder);
        right.Children.Add(Row("Name", _name));
        right.Children.Add(Row("Waveform", _waveType));
        right.Children.Add(Row("Frequency (Hz)", _frequency));
        right.Children.Add(Row("Volume", _volume));
        right.Children.Add(Row("Max Range", _range));
        right.Children.Add(Row("Attack (s)", _attack));
        right.Children.Add(Row("Sustain (s)", _sustain));
        right.Children.Add(Row("Release (s)", _release));

        right.Children.Add(Theme.SectionHeader("ADVANCED SYNTH  ·  SAME FIELDS AS GRAB"));
        var advanced = new Grid();
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var advLeft = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        advLeft.Children.Add(Row("Envelope punch", _punch));
        advLeft.Children.Add(Row("Frequency min (Hz)", _frequencyMin));
        advLeft.Children.Add(Row("Frequency slide", _frequencySlide));
        advLeft.Children.Add(Row("Freq. delta slide", _frequencyDeltaSlide));
        advLeft.Children.Add(Row("Vibrato strength", _vibratoStrength));
        advLeft.Children.Add(Row("Vibrato speed", _vibratoSpeed));
        var advRight = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        advRight.Children.Add(Row("Pitch jump mod", _pitchJumpMod));
        advRight.Children.Add(Row("Pitch jump speed", _pitchJumpSpeed));
        advRight.Children.Add(Row("Square duty (0-1)", _dutyCycle));
        advRight.Children.Add(Row("Square duty sweep", _dutyCycleSweep));
        advRight.Children.Add(Row("Repeat speed", _repeatSpeed));
        advRight.Children.Add(Row("Flanger frequency", _flangerFrequency));
        advRight.Children.Add(Row("Flanger depth", _flangerDepth));
        Grid.SetColumn(advLeft, 0);
        Grid.SetColumn(advRight, 1);
        advanced.Children.Add(advLeft);
        advanced.Children.Add(advRight);
        right.Children.Add(advanced);

        _punch.ToolTip = "Adds a short attack spike. GRAB's Envelope Punch.";
        _frequencyMin.ToolTip = "Lowest pitch the slide may reach. GRAB's retail default is 35 Hz.";
        _frequencySlide.ToolTip = "Pitch slide in kHz per second (GRAB: Frequency Slide).";
        _frequencyDeltaSlide.ToolTip = "How fast the slide accelerates (GRAB: Frequency Delta Slide).";
        _vibratoStrength.ToolTip = "Pitch wobble depth, as a fraction of the frequency (0 = off).";
        _vibratoSpeed.ToolTip = "Pitch wobble rate in Hz (0 = off).";
        _pitchJumpMod.ToolTip = "Pitch multiplier for the jump. GRAB's retail default is 1 (no jump).";
        _pitchJumpSpeed.ToolTip = "How many times per second the pitch jumps (0 = off).";
        _dutyCycle.ToolTip = "Square-wave pulse width. 0.5 is an even square.";
        _dutyCycleSweep.ToolTip = "Changes the pulse width over time.";
        _repeatSpeed.ToolTip = "Retrigger rate for repeating sounds (0 = one shot).";
        _flangerFrequency.ToolTip = "Sweep rate of the flanger (0 = off).";
        _flangerDepth.ToolTip = "Flanger mix depth.";

        right.Children.Add(Theme.SectionHeader("FILTERS & SPACE"));
        var filters = new Grid();
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var filterLeft = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        filterLeft.Children.Add(Row("Low-pass (Hz)", _lowPass));
        filterLeft.Children.Add(Row("High-pass (Hz)", _highPass));
        filterLeft.Children.Add(Row("Reverb delay (ms)", _reverbDelay));
        var filterRight = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        filterRight.Children.Add(Row("Reverb decay", _reverbDecay));
        var safeDefaults = Theme.Button("Safe filter defaults", small: true);
        safeDefaults.Margin = new Thickness(0, 5, 0, 2);
        safeDefaults.ToolTip = "Low-pass 25000 (open), high-pass 0 (off), duty 0.5, pitch jump 1.";
        safeDefaults.Click += (_, _) => RestoreSafeSoundDefaults();
        filterRight.Children.Add(safeDefaults);
        Grid.SetColumn(filterLeft, 0);
        Grid.SetColumn(filterRight, 1);
        filters.Children.Add(filterLeft);
        filters.Children.Add(filterRight);
        right.Children.Add(filters);
        _lowPass.ToolTip = "GRAB's Low-Pass Filter Frequency. 25000 Hz is fully open. 0 Hz is a closed filter and makes the block silent in game.";
        _highPass.ToolTip = "GRAB's High-Pass Filter Frequency. 0 Hz is off.";
        _reverbDelay.ToolTip = "Reverb delay in milliseconds (retail editor: 0 = off).";
        _reverbDecay.ToolTip = "Reverb feedback (0 = off, below 1).";

        var flags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        flags.Children.Add(_repeat);
        _startActive.Margin = new Thickness(14, 0, 0, 0);
        flags.Children.Add(_startActive);
        right.Children.Add(flags);

        _name.LostFocus += (_, _) => ApplyFields();
        _volume.LostFocus += (_, _) => ApplyFields();
        _frequency.LostFocus += (_, _) => ApplyFields();
        _attack.LostFocus += (_, _) => ApplyFields();
        _sustain.LostFocus += (_, _) => ApplyFields();
        _release.LostFocus += (_, _) => ApplyFields();
        _punch.LostFocus += (_, _) => ApplyFields();
        _frequencyMin.LostFocus += (_, _) => ApplyFields();
        _frequencySlide.LostFocus += (_, _) => ApplyFields();
        _frequencyDeltaSlide.LostFocus += (_, _) => ApplyFields();
        _vibratoStrength.LostFocus += (_, _) => ApplyFields();
        _vibratoSpeed.LostFocus += (_, _) => ApplyFields();
        _pitchJumpMod.LostFocus += (_, _) => ApplyFields();
        _pitchJumpSpeed.LostFocus += (_, _) => ApplyFields();
        _dutyCycle.LostFocus += (_, _) => ApplyFields();
        _dutyCycleSweep.LostFocus += (_, _) => ApplyFields();
        _repeatSpeed.LostFocus += (_, _) => ApplyFields();
        _flangerFrequency.LostFocus += (_, _) => ApplyFields();
        _flangerDepth.LostFocus += (_, _) => ApplyFields();
        _lowPass.LostFocus += (_, _) => ApplyFields();
        _highPass.LostFocus += (_, _) => ApplyFields();
        _reverbDelay.LostFocus += (_, _) => ApplyFields();
        _reverbDecay.LostFocus += (_, _) => ApplyFields();
        _range.LostFocus += (_, _) => ApplyFields();
        _waveType.SelectionChanged += (_, _) => ApplyFields();
        _repeat.Checked += (_, _) => ApplyFields();
        _repeat.Unchecked += (_, _) => ApplyFields();
        _startActive.Checked += (_, _) => ApplyFields();
        _startActive.Unchecked += (_, _) => ApplyFields();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        var preview = Theme.Button("▶ Preview", accent: true);
        preview.Click += (_, _) => PreviewSelected();
        buttons.Children.Add(preview);
        var stop = Theme.Button("■ Stop");
        stop.Margin = new Thickness(6, 0, 0, 0);
        stop.Click += (_, _) => AudioEngine.Shared.StopAll();
        buttons.Children.Add(stop);
        right.Children.Add(buttons);

        right.Children.Add(Theme.SectionHeader("PRESETS"));
        var presets = new WrapPanel();
        foreach (var preset in Presets)
        {
            var b = Theme.Button(preset.Name, small: true);
            b.Margin = new Thickness(0, 0, 5, 5);
            b.Click += (_, _) => ApplyPreset(preset);
            presets.Children.Add(b);
        }
        right.Children.Add(presets);
        right.Children.Add(Theme.Hint("Preview uses the same oscillator, pitch envelope, filters and reverb the exported Sound block runs in GRAB. Low-pass 25000 Hz is fully open; 0 Hz closes the filter and the block is silent in game."));
        rightScroll.Content = right;
        Grid.SetColumn(rightScroll, 1);
        root.Children.Add(rightScroll);

        return root;
    }

    private UIElement BuildMp3Tab()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 12, 18, 16)
        };
        var root = new StackPanel();
        root.Children.Add(Theme.Heading("MP3 → SOUND BLOCKS"));
        root.Children.Add(Theme.Hint("Retail .level files cannot contain audio. This transcriber listens to the track and rebuilds it as GRAB's oscillator Sound blocks, then schedules each slice with real triggers and Code Blocks."));

        var fileRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        var browse = Theme.Button("Browse...", small: true);
        browse.Click += (_, _) => BrowseMp3();
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(browse);
        _mp3Path.IsReadOnly = true;
        _mp3Path.Margin = new Thickness(0, 0, 6, 0);
        fileRow.Children.Add(_mp3Path);
        root.Children.Add(fileRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var playSource = Theme.Button("▶ Source");
        playSource.ToolTip = "Play the original file for comparison";
        playSource.Click += (_, _) => PlaySource();
        actions.Children.Add(playSource);
        var stopSource = Theme.Button("■ Stop");
        stopSource.Margin = new Thickness(6, 0, 0, 0);
        stopSource.Click += (_, _) => _sourcePlayer.Stop();
        actions.Children.Add(stopSource);
        var analyze = Theme.Button("Analyze", accent: true);
        analyze.Margin = new Thickness(6, 0, 0, 0);
        analyze.Click += (_, _) => AnalyzeMp3();
        actions.Children.Add(analyze);
        root.Children.Add(actions);

        root.Children.Add(Theme.SectionHeader("ANALYSIS SETTINGS"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(Row("Detail (0-100)", _detail));
        left.Children.Add(Row("Max notes", _maxNotes));
        left.Children.Add(Row("Polyphony", _polyphony));
        left.Children.Add(Row("Min frequency", _minFreq));
        left.Children.Add(Row("Max frequency", _maxFreq));
        left.Children.Add(Row("Sensitivity", _sensitivity));
        left.Children.Add(Row("Noise gate", _gate));
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(Row("Waveform", _waveMode));
        right.Children.Add(Row("Note length", _noteLength));
        right.Children.Add(Row("Volume", _songVolume));
        right.Children.Add(Row("Analyze seconds", _maxSeconds));
        right.Children.Add(_snap);
        _scheduled.Checked += (_, _) => _songStartActive.IsEnabled = _scheduled.IsChecked != true;
        _scheduled.Unchecked += (_, _) => _songStartActive.IsEnabled = _scheduled.IsChecked != true;
        right.Children.Add(_scheduled);
        _songStartActive.IsEnabled = _scheduled.IsChecked != true;
        right.Children.Add(_songStartActive);
        right.Children.Add(Row("Trigger layout", _songTriggerMode));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        root.Children.Add(grid);
        root.Children.Add(Theme.Hint("More detail = faster slices and tighter timing, but many more objects. Fewer notes sound cleaner in GRAB and keep the level inside the game's object budget. Timed mode starts playing as soon as the level loads. One Start Trigger keeps the exact same timed score, but waits for a Music Start trigger: touch it in game (or set its ForceActive from code) to begin, and activate it again after the score finishes to replay."));

        root.Children.Add(Theme.SectionHeader("RESULT"));
        root.Children.Add(_planSummary);
        var resultButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 7) };
        _recreateButton.IsEnabled = false;
        _recreateButton.Click += (_, _) => PreviewRecreation();
        resultButtons.Children.Add(_recreateButton);
        var stopRecreate = Theme.Button("■ Stop");
        stopRecreate.Margin = new Thickness(6, 0, 0, 0);
        stopRecreate.Click += (_, _) => AudioEngine.Shared.StopAll();
        resultButtons.Children.Add(stopRecreate);
        _importButton.IsEnabled = false;
        _importButton.Margin = new Thickness(6, 0, 0, 0);
        _importButton.Click += (_, _) => ImportSong();
        resultButtons.Children.Add(_importButton);
        root.Children.Add(resultButtons);
        _noteList.Height = 235;
        _noteList.Background = Theme.Field;
        _noteList.Foreground = Theme.Text;
        _noteList.BorderBrush = Theme.Border;
        _noteList.FontFamily = new FontFamily("Consolas");
        _noteList.FontSize = 11;
        root.Children.Add(new TextBlock
        {
            Text = "START      PITCH   FREQUENCY   LENGTH   WAVE      AMPLITUDE",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9.5,
            Foreground = Theme.Dim,
            Margin = new Thickness(2, 0, 0, 3)
        });
        root.Children.Add(_noteList);
        scroll.Content = root;
        return scroll;
    }

    private UIElement BuildSpeechTab()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 12, 18, 16)
        };
        _speechPreset.SelectionChanged += (_, _) => ApplySpeechPreset();
        var root = new StackPanel();
        root.Children.Add(Theme.Heading("TEXT → SPEECH"));
        root.Children.Add(Theme.Hint("This speaks through GRAB's oscillator sound blocks. The studio preview is a formant voice; the GRAB preview is the exact block score that gets exported."));

        root.Children.Add(Theme.SectionHeader("SCRIPT"));
        _speechText.Height = 88;
        _speechText.MinHeight = 88;
        root.Children.Add(_speechText);
        var samples = new WrapPanel { Margin = new Thickness(0, 5, 0, 4) };
        foreach (var sample in new[]
        {
            "Hello! This is GRAB ENGINE speaking with sound blocks.",
            "The API and GPU are ready.",
            "Warning: the floor is lava. Please jump.",
            "You found the secret room. Well done!"
        })
        {
            var b = Theme.Button(sample.Length > 30 ? sample[..30] + "…" : sample, small: true);
            b.Margin = new Thickness(0, 0, 5, 4);
            b.ToolTip = sample;
            b.Click += (_, _) => { _speechText.Text = sample; _speechSummary.Text = "Sample loaded. Press Speak."; };
            samples.Children.Add(b);
        }
        root.Children.Add(samples);

        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        var speak = Theme.Button("▶ Speak", accent: true);
        speak.ToolTip = "Render the studio formant voice for this text";
        speak.Click += (_, _) => SpeakText();
        actions.Children.Add(speak);
        var grabPreview = Theme.Button("▶ GRAB Preview");
        grabPreview.Margin = new Thickness(6, 0, 0, 0);
        grabPreview.ToolTip = "Hear the same score played by the oscillator sound blocks that will be exported";
        grabPreview.Click += (_, _) => PreviewSpeechBlocks();
        actions.Children.Add(grabPreview);
        var stop = Theme.Button("■ Stop");
        stop.Margin = new Thickness(6, 0, 0, 0);
        stop.Click += (_, _) => AudioEngine.Shared.StopAll();
        actions.Children.Add(stop);
        var build = Theme.Button("Build Score");
        build.Margin = new Thickness(6, 0, 0, 0);
        build.Click += (_, _) => BuildSpeech();
        actions.Children.Add(build);
        var import = _speechImport;
        import.Margin = new Thickness(6, 0, 0, 0);
        import.Click += (_, _) => ImportSpeech();
        actions.Children.Add(import);
        root.Children.Add(actions);

        root.Children.Add(Theme.SectionHeader("VOICE SETTINGS"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(Row("Voice", _speechVoice));
        left.Children.Add(Row("Preset", _speechPreset));
        left.Children.Add(Row("Pitch (Hz)", _speechPitch));
        left.Children.Add(Row("Rate (words/min)", _speechRate));
        left.Children.Add(Row("Formant blocks", _speechClarity));
        left.Children.Add(_speechAcronyms);
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(Row("Intonation (0-1)", _speechIntonation));
        right.Children.Add(Row("Pause scale", _speechPause));
        right.Children.Add(Row("Volume", _speechVolume));
        right.Children.Add(Row("Max seconds", _speechMaxSeconds));
        right.Children.Add(Row("Vowel length", _speechVowelLength));
        right.Children.Add(Row("Word gap (s)", _speechWordGap));
        right.Children.Add(Row("Trigger layout", _speechTriggerMode));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        root.Children.Add(grid);

        root.Children.Add(Theme.SectionHeader("CLARITY TUNING"));
        var tuning = new Grid();
        tuning.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tuning.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tleft = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        tleft.Children.Add(Row("Formant sharpness", _speechSharpness));
        tleft.Children.Add(Row("Vowel contrast", _speechContrast));
        var tright = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        tright.Children.Add(Row("Consonant boost", _speechConsonants));
        tright.Children.Add(Row("Brightness", _speechBrightness));
        Grid.SetColumn(tleft, 0);
        Grid.SetColumn(tright, 1);
        tuning.Children.Add(tleft);
        tuning.Children.Add(tright);
        root.Children.Add(tuning);
        root.Children.Add(Theme.Hint("Presets set every clarity field at once. Sharpness narrows the formants, contrast pushes vowels apart, consonant boost lifts fricatives and plosives, and brightness adds crisp high end. Deep lowers pitch and formants, Bright raises both."));

        root.Children.Add(Theme.SectionHeader("SCORE"));
        root.Children.Add(_speechSummary);
        _speechPhones.Height = 210;
        _speechPhones.Background = Theme.Field;
        _speechPhones.Foreground = Theme.Text;
        _speechPhones.BorderBrush = Theme.Border;
        _speechPhones.FontFamily = new FontFamily("Consolas");
        _speechPhones.FontSize = 11;
        root.Children.Add(new TextBlock
        {
            Text = $"{"START",6}  {"PHONEME",-7} {"LENGTH",6}  {"F1 / F2 / F3",17}  {"PITCH",5}  TYPE",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = Theme.Dim,
            Margin = new Thickness(2, 0, 0, 3)
        });
        root.Children.Add(_speechPhones);
        root.Children.Add(Theme.Hint("Each voiced phoneme becomes one sine sound block per formant, plus a trigger and a Code Block. A ten word sentence is roughly 30 phonemes, so keep Max seconds short while you tune the voice. One Start Trigger keeps the same spoken timing but waits for a Speech Start trigger: activate it to begin, and activate it again after the speech ends to replay."));
        scroll.Content = root;
        return scroll;
    }

    private UIElement BuildGuideTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) };
        var panel = new StackPanel();
        panel.Children.Add(Theme.Heading("HOW GRAB AUDIO WORKS"));
        panel.Children.Add(Theme.Hint("The retail level format stores no audio blob. A Sound block is a tiny monophonic synth, and this is exactly what the game plays."));
        panel.Children.Add(Section("Sound block fields"));
        panel.Children.Add(Bullet("Waveform: Square, Sawtooth, Sine or Noise."));
        panel.Children.Add(Bullet("Frequency: the oscillator pitch in Hz (440 = A4)."));
        panel.Children.Add(Bullet("Attack / Sustain / Release: the envelope times in seconds."));
        panel.Children.Add(Bullet("Envelope Punch: extra attack spike; Frequency Min / Slide / Delta Slide shape the pitch over time."));
        panel.Children.Add(Bullet("Vibrato Strength / Speed: pitch wobble. Pitch Jump Mod / Speed: stepped pitch changes."));
        panel.Children.Add(Bullet("Square Duty / Sweep: pulse width for square waves. Repeat Speed retriggers the envelope."));
        panel.Children.Add(Bullet("Flanger Frequency / Depth, Low-Pass, High-Pass, Reverb Delay / Decay: the effect chain."));
        panel.Children.Add(Bullet("Low-pass 25000 Hz is fully open; 0 Hz is a closed filter and plays silence in GRAB. High-pass 0 is off. The exporter writes all of these fields and repairs a 0 Hz low-pass automatically."));
        panel.Children.Add(Bullet("Volume: mixer gain for this block."));
        panel.Children.Add(Bullet("Max Range: distance at which the sound is no longer audible."));
        panel.Children.Add(Bullet("Repeat: retrigger the envelope continuously while active."));
        panel.Children.Add(Bullet("Start Active: the block starts playing when the level loads; otherwise a trigger or Code Block must activate it."));
        panel.Children.Add(Section("What the MP3 importer does"));
        panel.Children.Add(Bullet("Decodes the MP3, runs an FFT over short slices and finds the strongest pitches."));
        panel.Children.Add(Bullet("Merges adjacent frames of the same pitch into one held note."));
        panel.Children.Add(Bullet("Creates one Sound block per note, then one trigger per time slice."));
        panel.Children.Add(Bullet("Creates a generated Code Block per slice. Its C# is compiled to GASM that waits, sets the trigger's ForceActive to 1, waits for the note, then releases it."));
        panel.Children.Add(Bullet("Because these are normal triggers and Code Blocks, the timing still works in the retail game after export."));
        panel.Children.Add(Section("Limits and quality"));
        panel.Children.Add(Bullet("An MP3 is polyphonic and noisy; a GRAB level can only hold discrete oscillator notes. Fewer notes and polyphony 1-2 sound cleaner."));
        panel.Children.Add(Bullet("The importer caps the score at Max notes and keeps the strongest events."));
        panel.Children.Add(Bullet("Very long tracks create many Code Blocks and triggers. Import 15-30 seconds at a time for music with tight timing."));
        panel.Children.Add(Bullet("Code Blocks use real GASM on export, not a custom runtime, so the result plays in GRAB."));
        panel.Children.Add(Section("Trigger layout"));
        panel.Children.Add(Bullet("Timed: one trigger per note/phoneme, started by a generated Code Block, so the piece plays in order from level load."));
        panel.Children.Add(Bullet("One Start Trigger: the same timed score, but every scheduler waits for one Music Start / Speech Start trigger. Activate it whenever you want the piece to begin."));
        panel.Children.Add(Bullet("After the score ends the start trigger can be activated again to replay it. Touch it in game, or pulse ForceActive = 1 from your own Code Block. It never fires the whole bank at once."));
        panel.Children.Add(Bullet("The start trigger is connected to its scheduler Code Blocks with native START targets (up to GRAB's trigger target limit), so the connection is visible in the trigger settings. Every scheduler also polls the trigger, so the whole score still runs even when the list is longer than GRAB's limit."));
        panel.Children.Add(Section("In-engine preview"));
        panel.Children.Add(Bullet("Sound blocks play through the same synth in the editor and in Play mode."));
        panel.Children.Add(Bullet("Triggered sounds are audible when their trigger fires; start-active sounds play at level start."));
        panel.Children.Add(Bullet("Spatial volume uses the block's Max Range and the player position in Play mode."));
        panel.Children.Add(Section("Text to speech"));
        panel.Children.Add(Bullet("Text is converted to English phonemes, then each phoneme gets sine sound blocks at its F1/F2/F3 formant frequencies."));
        panel.Children.Add(Bullet("The studio preview filters a glottal pulse through three resonators, so the words are understandable before export."));
        panel.Children.Add(Bullet("GRAB Preview renders the same score with the real oscillator blocks, which is what the level actually plays."));
        panel.Children.Add(Bullet("Clarity presets set everything at once: Natural, Clear, Ultra clear, Max clarity and Robot. Max clarity uses four formants per vowel."));
        panel.Children.Add(Bullet("Sharpness narrows the formants, vowel contrast pushes F2 away from F1, consonant boost lifts fricatives and plosives, brightness adds high end, and vowel length / word gap slow the delivery down."));
        panel.Children.Add(Bullet("Initialisms are spelled letter by letter (API, CPU, U.S.A.), while ordinary upper-case words stay words. By-products: number and abbreviation expansion, question intonation and pauses."));
        panel.Children.Add(Bullet("Pitch, rate, intonation and pauses are baked into each phoneme's schedule and envelope."));
        scroll.Content = panel;
        return scroll;
    }

    private static TextBlock Bullet(string text) => new()
    {
        Text = "•  " + text,
        Foreground = Theme.Text,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 2, 0, 2)
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.Text,
        Margin = new Thickness(0, 13, 0, 3)
    };

    private static Grid Row(string label, UIElement content)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 25 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Theme.FieldLabel(label);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private sealed class SoundRow
    {
        public LevelNode Node { get; init; } = null!;
        public string Label => $"{Node.DisplayName}   ·   {SoundSynth.Describe(Node)}";
    }

    private void LoadSounds()
    {
        var rows = _level.AllNodes().Where(n => n.Kind == NodeKind.Sound)
            .Select(n => new SoundRow { Node = n }).ToList();
        _soundList.ItemsSource = rows;
        if (_selected != null && !_draftSelected)
            _soundList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Node, _selected));
        else if (_selected == null && rows.Count > 0)
            _soundList.SelectedIndex = 0;

        // An empty level still needs a usable synth: show a real default patch so every
        // field is populated and presets work. It only enters the level when the user
        // presses Add Sound.
        if (_selected == null)
        {
            _draftSelected = true;
            SelectSound(NewDefaultSound());
        }
        _addButton.Content = _draftSelected ? "+ Add Sound to Level" : "+ Add Sound";
        if (_draftSelected)
            _status.Text = "No sound blocks yet. This is a preview patch — press + Add Sound to Level to add it.";
        else
            _status.Text = rows.Count == 0 ? "Ready. This level has no sound blocks yet." : $"{rows.Count} sound block(s).";
    }

    private LevelNode NewDefaultSound() => new()
    {
        Kind = NodeKind.Sound,
        Name = "New Sound",
        Position = _origin(),
        Scale = new Vec3(1000, 1000, 1000),
        SoundName = "sound",
        SoundFrequency = 440,
        SoundVolume = 0.7f,
        SoundWaveType = 2,
        SoundAttack = 0.01f,
        SoundSustain = 0.3f,
        SoundRelease = 0.25f,
        SoundMaxRange = 1000,
        SoundStartActive = true,
        LayerId = _level.ActiveLayerId
    };

    private void SelectSound(LevelNode node)
    {
        _selected = node;
        _loading = true;
        _name.Text = node.SoundName;
        _frequency.Text = node.SoundFrequency.ToString("0.###", CultureInfo.InvariantCulture);
        _volume.Text = node.SoundVolume.ToString("0.###", CultureInfo.InvariantCulture);
        _range.Text = node.SoundMaxRange.ToString("0.###", CultureInfo.InvariantCulture);
        _attack.Text = node.SoundAttack.ToString("0.###", CultureInfo.InvariantCulture);
        _sustain.Text = node.SoundSustain.ToString("0.###", CultureInfo.InvariantCulture);
        _release.Text = node.SoundRelease.ToString("0.###", CultureInfo.InvariantCulture);
        _punch.Text = node.SoundPunch.ToString("0.###", CultureInfo.InvariantCulture);
        _frequencyMin.Text = node.SoundFrequencyMin.ToString("0.###", CultureInfo.InvariantCulture);
        _frequencySlide.Text = node.SoundFrequencySlide.ToString("0.###", CultureInfo.InvariantCulture);
        _frequencyDeltaSlide.Text = node.SoundFrequencyDeltaSlide.ToString("0.###", CultureInfo.InvariantCulture);
        _vibratoStrength.Text = node.SoundVibratoStrength.ToString("0.###", CultureInfo.InvariantCulture);
        _vibratoSpeed.Text = node.SoundVibratoSpeed.ToString("0.###", CultureInfo.InvariantCulture);
        _pitchJumpMod.Text = node.SoundPitchJumpMod.ToString("0.###", CultureInfo.InvariantCulture);
        _pitchJumpSpeed.Text = node.SoundPitchJumpSpeed.ToString("0.###", CultureInfo.InvariantCulture);
        _dutyCycle.Text = node.SoundDutyCycle.ToString("0.###", CultureInfo.InvariantCulture);
        _dutyCycleSweep.Text = node.SoundDutyCycleSweep.ToString("0.###", CultureInfo.InvariantCulture);
        _repeatSpeed.Text = node.SoundRepeatSpeed.ToString("0.###", CultureInfo.InvariantCulture);
        _flangerFrequency.Text = node.SoundFlangerFrequency.ToString("0.###", CultureInfo.InvariantCulture);
        _flangerDepth.Text = node.SoundFlangerDepth.ToString("0.###", CultureInfo.InvariantCulture);
        _lowPass.Text = node.SoundLowPass.ToString("0.###", CultureInfo.InvariantCulture);
        _highPass.Text = node.SoundHighPass.ToString("0.###", CultureInfo.InvariantCulture);
        _reverbDelay.Text = node.SoundReverbDelay.ToString("0.###", CultureInfo.InvariantCulture);
        _reverbDecay.Text = node.SoundReverbDecay.ToString("0.###", CultureInfo.InvariantCulture);
        _waveType.SelectedIndex = Math.Clamp((int)node.SoundWaveType, 0, 3);
        _repeat.IsChecked = node.SoundRepeat;
        _startActive.IsChecked = node.SoundStartActive;
        _soundInfo.Text = _draftSelected
            ? "PREVIEW PATCH — not in the level yet. Press + Add Sound to Level to add it."
            : $"{node.DisplayName} in '{_level.Title}'   ·   {SoundSynth.Describe(node)}";
        _loading = false;
        DrawWave();
    }

    private void ApplyFields()
    {
        if (_loading || _selected == null) return;
        var n = _selected;
        string? warning = null;
        n.SoundName = string.IsNullOrWhiteSpace(_name.Text) ? "sound" : _name.Text.Trim();
        n.SoundFrequency = ParseFloat(_frequency.Text, n.SoundFrequency, 20, 12000);
        n.SoundVolume = ParseFloat(_volume.Text, n.SoundVolume, 0, 10);
        n.SoundMaxRange = ParseFloat(_range.Text, n.SoundMaxRange, 0, 2000);
        n.SoundAttack = ParseFloat(_attack.Text, n.SoundAttack, 0, 2);
        n.SoundSustain = ParseFloat(_sustain.Text, n.SoundSustain, 0.01f, 4);
        n.SoundRelease = ParseFloat(_release.Text, n.SoundRelease, 0, 2);
        n.SoundPunch = ParseFloat(_punch.Text, n.SoundPunch, 0, 2);
        n.SoundFrequencyMin = ParseFloat(_frequencyMin.Text, n.SoundFrequencyMin, 20, 18000);
        n.SoundFrequencySlide = ParseFloat(_frequencySlide.Text, n.SoundFrequencySlide, -200, 200);
        n.SoundFrequencyDeltaSlide = ParseFloat(_frequencyDeltaSlide.Text, n.SoundFrequencyDeltaSlide, -200, 200);
        n.SoundVibratoStrength = ParseFloat(_vibratoStrength.Text, n.SoundVibratoStrength, 0, 4);
        n.SoundVibratoSpeed = ParseFloat(_vibratoSpeed.Text, n.SoundVibratoSpeed, 0, 1000);
        n.SoundPitchJumpMod = ParseFloat(_pitchJumpMod.Text, n.SoundPitchJumpMod, 0, 8);
        n.SoundPitchJumpSpeed = ParseFloat(_pitchJumpSpeed.Text, n.SoundPitchJumpSpeed, 0, 20);
        n.SoundDutyCycle = ParseFloat(_dutyCycle.Text, n.SoundDutyCycle, 0.01f, 0.99f);
        n.SoundDutyCycleSweep = ParseFloat(_dutyCycleSweep.Text, n.SoundDutyCycleSweep, -20, 20);
        n.SoundRepeatSpeed = ParseFloat(_repeatSpeed.Text, n.SoundRepeatSpeed, 0, 20);
        n.SoundFlangerFrequency = ParseFloat(_flangerFrequency.Text, n.SoundFlangerFrequency, 0, 40);
        n.SoundFlangerDepth = ParseFloat(_flangerDepth.Text, n.SoundFlangerDepth, 0, 1);
        n.SoundLowPass = ParseFloat(_lowPass.Text, n.SoundLowPass, 0, 26000);
        n.SoundHighPass = ParseFloat(_highPass.Text, n.SoundHighPass, 0, 20000);
        n.SoundReverbDelay = ParseFloat(_reverbDelay.Text, n.SoundReverbDelay, 0, 2000);
        n.SoundReverbDecay = ParseFloat(_reverbDecay.Text, n.SoundReverbDecay, 0, 1);
        // A low-pass between 0 and 20 Hz is a closed filter in GRAB, not "off".
        // Treat it as the retail editor's open value so a typo cannot mute a level.
        if (n.SoundLowPass > 0 && n.SoundLowPass < 20)
        {
            n.SoundLowPass = 25000;
            _lowPass.Text = "25000";
            warning = "Low-pass below 20 Hz would silence the block in GRAB — restored to 25000 Hz (open).";
        }
        n.SoundWaveType = Math.Clamp(_waveType.SelectedIndex, 0, 3);
        n.SoundRepeat = _repeat.IsChecked == true;
        n.SoundStartActive = _startActive.IsChecked == true;
        n.RawSoundParameters = null;
        _soundInfo.Text = _draftSelected
            ? "PREVIEW PATCH — not in the level yet. Press + Add Sound to Level to add it."
            : $"{n.DisplayName}   ·   {SoundSynth.Describe(n)}";
        DrawWave();
        if (_draftSelected)
        {
            _status.Text = warning ?? "Preview patch updated. Press + Add Sound to Level to add it.";
            return;
        }
        _dirty = true;
        LoadSounds();
        if (warning != null) _status.Text = warning;
    }

    private void DrawWave()
    {
        _wave.Children.Clear();
        if (_selected == null) return;
        double w = Math.Max(120, _wave.ActualWidth > 10 ? _wave.ActualWidth : 620);
        double h = 92;
        var samples = SoundSynth.RenderNode(_selected);
        var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(84, 196, 255)), StrokeThickness = 1.3 };
        int count = Math.Min(1400, Math.Max(2, samples.Length));
        int step = Math.Max(1, samples.Length / count);
        for (int i = 0; i < count; i++)
        {
            float sample = samples[Math.Min(samples.Length - 1, i * step)];
            line.Points.Add(new Point(i * w / (count - 1), h * 0.5 - sample * h * 0.43));
        }
        _wave.Children.Add(line);
        var mid = new Line
        {
            X1 = 0, X2 = w, Y1 = h / 2, Y2 = h / 2,
            Stroke = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), StrokeThickness = 1
        };
        _wave.Children.Add(mid);
        var label = new TextBlock
        {
            Text = SoundSynth.Describe(_selected),
            Foreground = Theme.Dim,
            FontSize = 10,
            Margin = new Thickness(7, 5, 0, 0)
        };
        _wave.Children.Add(label);
    }

    private void PreviewSelected()
    {
        if (_selected == null) return;
        AudioEngine.Shared.PlayNode(_selected, 1f, _selected.SoundRepeat, "audio-studio:" + _selected.EditorId, _selected.DisplayName);
        _status.Text = "Previewing " + _selected.DisplayName + " — " + SoundSynth.Describe(_selected);
    }

    private void RestoreSafeSoundDefaults()
    {
        if (_selected == null) return;
        var n = _selected;
        n.SoundFrequencyMin = 35;
        n.SoundPitchJumpMod = 1;
        n.SoundDutyCycle = 0.5f;
        n.SoundLowPass = 25000;
        n.SoundHighPass = 0;
        n.SoundReverbDelay = 0;
        n.SoundReverbDecay = 0;
        n.RawSoundParameters = null;
        SelectSound(n);
        if (!_draftSelected)
        {
            _dirty = true;
            LoadSounds();
        }
        _status.Text = "Restored GRAB's safe defaults: low-pass 25000 Hz (open), high-pass 0 (off), duty 0.5, pitch jump 1.";
    }

    private void AddSound()
    {
        LevelNode n;
        if (_draftSelected && _selected != null)
        {
            // Commit the preview patch exactly as auditioned, at the current editor point.
            n = _selected;
            n.Name = UniqueName("Sound");
            n.Position = _origin();
            n.LayerId = _level.ActiveLayerId;
            _draftSelected = false;
        }
        else
        {
            n = NewDefaultSound();
            n.Name = UniqueName("Sound");
        }
        _level.AddNode(n);
        _dirty = true;
        _selected = n;
        LoadSounds();
        _status.Text = "Added " + n.DisplayName + ". Position it in the Scene, then export.";
    }

    private void DuplicateSound()
    {
        if (_selected == null) return;
        if (_draftSelected) { AddSound(); return; }
        var n = _selected.Clone();
        n.Name = UniqueName(_selected.DisplayName + " Copy");
        n.Position = _origin();
        _level.AddNode(n);
        _dirty = true;
        _selected = n;
        LoadSounds();
        _status.Text = "Duplicated " + n.DisplayName + ".";
    }

    private string UniqueName(string baseName)
    {
        var used = new HashSet<string>(_level.AllNodes().Select(n => n.DisplayName), StringComparer.OrdinalIgnoreCase);
        string name = baseName;
        int i = 2;
        while (!used.Add(name)) name = baseName + " " + i++;
        return name;
    }

    private void ApplyPreset(SoundPreset preset)
    {
        if (_selected == null) { _status.Text = "Select a sound block first."; return; }
        _selected.SoundWaveType = preset.Wave;
        _selected.SoundFrequency = preset.Frequency;
        _selected.SoundAttack = preset.Attack;
        _selected.SoundSustain = preset.Sustain;
        _selected.SoundRelease = preset.Release;
        _selected.SoundVolume = preset.Volume;
        // A preset is a complete sound: reset the advanced synth so a retail filter
        // (or an old silent import) cannot leak into the new patch.
        _selected.SoundPunch = 0;
        _selected.SoundFrequencyMin = 35;
        _selected.SoundFrequencySlide = 0;
        _selected.SoundFrequencyDeltaSlide = 0;
        _selected.SoundVibratoStrength = 0;
        _selected.SoundVibratoSpeed = 0;
        _selected.SoundPitchJumpMod = 1;
        _selected.SoundPitchJumpSpeed = 0;
        _selected.SoundDutyCycle = 0.5f;
        _selected.SoundDutyCycleSweep = 0;
        _selected.SoundRepeatSpeed = 0;
        _selected.SoundFlangerFrequency = 0;
        _selected.SoundFlangerDepth = 0;
        _selected.SoundLowPass = 25000;
        _selected.SoundHighPass = 0;
        _selected.SoundReverbDelay = 0;
        _selected.SoundReverbDecay = 0;
        preset.Extra?.Invoke(_selected);
        _selected.RawSoundParameters = null;
        if (!_draftSelected) _dirty = true;
        SelectSound(_selected);
        _status.Text = _draftSelected
            ? "Preset " + preset.Name + " loaded in the preview. Press + Add Sound to Level to add it."
            : "Applied preset " + preset.Name + ".";
    }

    // ---------- MP3 ----------

    private void BrowseMp3()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose audio to transcribe",
            Filter = "Audio files|*.mp3;*.wav;*.ogg;*.m4a;*.aiff;*.flac|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _mp3Path.Text = dialog.FileName;
        _plan = null;
        _noteList.ItemsSource = null;
        _planSummary.Text = "Ready to analyze " + Path.GetFileName(dialog.FileName) + ".";
        _recreateButton.IsEnabled = false;
        _importButton.IsEnabled = false;
        _status.Text = Path.GetFileName(dialog.FileName) + " loaded.";
    }

    private void PlaySource()
    {
        if (!File.Exists(_mp3Path.Text)) { _status.Text = "Choose an audio file first."; return; }
        try
        {
            _sourcePlayer.Open(new Uri(_mp3Path.Text));
            _sourcePlayer.Volume = 0.8;
            _sourcePlayer.Play();
            _status.Text = "Playing source: " + Path.GetFileName(_mp3Path.Text);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not play the source: " + ex.Message;
        }
    }

    private Mp3ImportSettings ReadSettings()
    {
        return new Mp3ImportSettings
        {
            Detail = (int)ParseFloat(_detail.Text, 55, 0, 100),
            MaxNotes = (int)ParseFloat(_maxNotes.Text, 160, 8, 600),
            Polyphony = Math.Clamp(_polyphony.SelectedIndex + 1, 1, 4),
            SnapSemitones = _snap.IsChecked == true,
            MinFrequency = ParseFloat(_minFreq.Text, 65, 20, 2000),
            MaxFrequency = ParseFloat(_maxFreq.Text, 1800, 100, 8000),
            Sensitivity = ParseFloat(_sensitivity.Text, 0.10f, 0.01f, 0.5f),
            NoiseGate = ParseFloat(_gate.Text, 0.006f, 0.0001f, 0.2f),
            WaveMode = _waveMode.SelectedIndex switch
            {
                1 => "Square", 2 => "Sawtooth", 3 => "Sine", 4 => "Noise", _ => "Auto"
            },
            NoteLengthScale = ParseFloat(_noteLength.Text, 1f, 0.3f, 3f),
            Volume = ParseFloat(_songVolume.Text, 0.7f, 0.05f, 1.5f),
            Scheduled = _scheduled.IsChecked == true,
            StartActive = _songStartActive.IsChecked == true,
            MaxSeconds = ParseFloat(_maxSeconds.Text, 30, 2, 180),
            SingleTrigger = _songTriggerMode.SelectedIndex == 1
        };
    }

    private async void AnalyzeMp3()
    {
        if (!File.Exists(_mp3Path.Text)) { _status.Text = "Choose an MP3/WAV first."; return; }
        // Read the UI into plain values on the dispatcher thread: the worker below must
        // never touch WPF objects, or WPF throws a cross-thread access exception.
        var audioPath = _mp3Path.Text;
        var settings = ReadSettings();
        var ui = Dispatcher;
        _recreateButton.IsEnabled = false;
        _importButton.IsEnabled = false;
        _planSummary.Text = "Analyzing...";
        void OnUi(Action action)
        {
            try
            {
                if (!ui.HasShutdownStarted && !ui.HasShutdownFinished) ui.BeginInvoke(action);
            }
            catch { }
        }
        try
        {
            var plan = await Task.Run(() => Mp3Transcriber.Analyze(audioPath, settings,
                // Progress may be raised from the analyser thread; queue a UI update
                // instead of touching the TextBlock from that thread.
                msg => OnUi(() => _planSummary.Text = msg)));
            if (ui.HasShutdownStarted || ui.HasShutdownFinished) return;
            // Marshal the result explicitly. This still works if the await continuation
            // does not resume on the WPF synchronization context.
            ui.Invoke(() =>
            {
                _plan = plan;
                _planSummary.Text = plan.Summary;
                _noteList.ItemsSource = plan.Notes.Take(500).Select(n =>
                    $"{n.Start,7:0.00}s   {n.PitchName,-5}  {n.Frequency,7:0.0} Hz   {n.Duration,5:0.00}s   {SoundSynth.WaveNames[Math.Clamp(n.Wave, 0, 3)],-8}  amp {n.Amplitude:0.00}").ToList();
                _recreateButton.IsEnabled = plan.Notes.Count > 0;
                _importButton.IsEnabled = plan.Notes.Count > 0;
                _status.Text = plan.Summary;
            });
        }
        catch (Exception ex)
        {
            LogAudioFailure("analyze", ex);
            OnUi(() =>
            {
                _planSummary.Text = "Analysis failed: " + ex.Message;
                _status.Text = "Analysis failed: " + ex.Message;
            });
        }
    }

    private static void LogAudioFailure(string stage, Exception ex)
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "grabengine-audio.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {ex}\n\n");
        }
        catch { }
    }

    private void PreviewRecreation()
    {
        if (_plan == null || _plan.Notes.Count == 0) return;
        var settings = ReadSettings();
        AudioEngine.Shared.StopAll();
        var mixed = MixPlan(_plan, settings);
        AudioEngine.Shared.PlaySamples(mixed, 0.9f, false, "audio-studio:song", "MP3 recreation");
        _status.Text = $"Previewing the recreated score ({_plan.Notes.Count} notes, {mixed.Length / (double)SoundSynth.SampleRate:0.0}s).";
    }

    private static float[] MixPlan(SongPlan plan, Mp3ImportSettings settings)
    {
        float length = Math.Min(settings.MaxSeconds, plan.Notes.Max(n => n.Start + n.Duration) + 0.3f);
        var buffer = new float[Math.Max(1, (int)(length * SoundSynth.SampleRate))];
        foreach (var note in plan.Notes)
        {
            float duration = Math.Clamp(note.Duration * Math.Max(0.4f, settings.NoteLengthScale), 0.06f, 3f);
            var samples = SoundSynth.Render(note.Frequency, note.Wave,
                note.Wave == 3 ? 0.003f : 0.012f,
                Math.Max(0.03f, duration - 0.012f - Math.Min(0.6f, duration * 0.35f)),
                Math.Min(0.6f, duration * 0.35f),
                note.Amplitude * settings.Volume, duration, note.Midi + 17);
            int offset = (int)(note.Start * SoundSynth.SampleRate);
            for (int i = 0; i < samples.Length && offset + i < buffer.Length; i++)
                buffer[offset + i] += samples[i];
        }
        float peak = 0;
        foreach (var s in buffer) peak = Math.Max(peak, Math.Abs(s));
        if (peak > 0.98f)
        {
            float gain = 0.98f / peak;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        }
        return buffer;
    }

    private void ImportSong()
    {
        if (_plan == null || _plan.Notes.Count == 0) return;
        var settings = ReadSettings();
        _status.Text = "Creating sound blocks and compiling schedulers...";
        try
        {
            var result = SongImporter.Import(_level, _plan, settings, _origin(), msg => _status.Text = msg);
            _dirty = true;
            LoadSounds();
            _status.Text = "Imported: " + result.Summary +
                (result.Warnings.Count > 0 ? "  ·  " + result.Warnings[0] : "");
            _planSummary.Text = "Imported " + result.Summary + " into the level.";
            MessageBox.Show(this,
                "Music imported into the level.\n\n" + result.Summary + "\n\n" +
                "Sound blocks are oscillators, so this is a chiptune-style recreation rather than the original recording.\n" +
                "The generated Code Blocks are normal GASM and are included when you export the .level.",
                "Audio Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Import failed: " + ex.Message;
            MessageBox.Show(this, "Import failed:\n\n" + ex.Message, "Audio Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- text to speech ----------

    private readonly Button _speechImport = Theme.Button("Import into Level", accent: true);

    private SpeechSettings ReadSpeechSettings()
    {
        return new SpeechSettings
        {
            Voice = _speechVoice.SelectedItem as string ?? "Neutral",
            PitchHz = ParseFloat(_speechPitch.Text, 120f, 70f, 320f),
            RateWpm = (int)ParseFloat(_speechRate.Text, 155f, 70f, 300f),
            Clarity = Math.Clamp(_speechClarity.SelectedIndex + 1, 1, 4),
            Intonation = ParseFloat(_speechIntonation.Text, 0.7f, 0f, 1f),
            PauseScale = ParseFloat(_speechPause.Text, 1f, 0.2f, 3f),
            Volume = ParseFloat(_speechVolume.Text, 0.9f, 0.05f, 1.5f),
            MaxSeconds = ParseFloat(_speechMaxSeconds.Text, 20f, 2f, 60f),
            ClarityPreset = _speechPreset.SelectedItem as string ?? "Clear",
            FormantSharpness = ParseFloat(_speechSharpness.Text, 1.35f, 0.4f, 2.2f),
            FormantContrast = ParseFloat(_speechContrast.Text, 1.18f, 0.7f, 1.8f),
            ConsonantBoost = ParseFloat(_speechConsonants.Text, 1.6f, 0.3f, 3f),
            Brightness = ParseFloat(_speechBrightness.Text, 1.12f, 0.5f, 2f),
            WordGap = ParseFloat(_speechWordGap.Text, 0.04f, 0f, 0.2f),
            VowelLength = ParseFloat(_speechVowelLength.Text, 1.12f, 0.5f, 2.5f),
            SpellAcronyms = _speechAcronyms.IsChecked == true,
            SingleTrigger = _speechTriggerMode.SelectedIndex == 1
        };
    }

    private void ApplySpeechPreset()
    {
        if (_speechLoading) return;
        var settings = ReadSpeechSettings();
        SpeechSynthesizer.ApplyPreset(settings, _speechPreset.SelectedItem as string ?? "Clear");
        _speechLoading = true;
        _speechRate.Text = settings.RateWpm.ToString(CultureInfo.InvariantCulture);
        _speechClarity.SelectedIndex = Math.Clamp(settings.Clarity - 1, 0, 3);
        _speechIntonation.Text = settings.Intonation.ToString("0.##", CultureInfo.InvariantCulture);
        _speechSharpness.Text = settings.FormantSharpness.ToString("0.##", CultureInfo.InvariantCulture);
        _speechContrast.Text = settings.FormantContrast.ToString("0.##", CultureInfo.InvariantCulture);
        _speechConsonants.Text = settings.ConsonantBoost.ToString("0.##", CultureInfo.InvariantCulture);
        _speechBrightness.Text = settings.Brightness.ToString("0.##", CultureInfo.InvariantCulture);
        _speechWordGap.Text = settings.WordGap.ToString("0.###", CultureInfo.InvariantCulture);
        _speechVowelLength.Text = settings.VowelLength.ToString("0.##", CultureInfo.InvariantCulture);
        _speechLoading = false;
        BuildSpeech();
        _status.Text = $"Clarity preset '{_speechPreset.SelectedItem}' applied.";
    }

    private SpeechPlan BuildSpeech()
    {
        var settings = ReadSpeechSettings();
        var plan = SpeechSynthesizer.Build(_speechText.Text, settings);
        _speechPlan = plan;
        _speechSummary.Text = plan.Summary + (plan.Warnings.Count > 0 ? " · " + plan.Warnings[0] : "");
        _speechPhones.ItemsSource = plan.Phones.Take(600).Select(p =>
            $"{p.Start,6:0.00}  {p.Symbol,-7} {p.Duration,6:0.000}  {p.F1,5:0}/{p.F2,5:0}/{p.F3,5:0}  {p.PitchHz,5:0}  {(p.Voiced ? "voiced" : "noise")}{(p.Stressed ? " *" : "")}").ToList();
        _speechImport.IsEnabled = plan.Phones.Count > 0;
        return plan;
    }

    private void SpeakText()
    {
        try
        {
            var settings = ReadSpeechSettings();
            var plan = BuildSpeech();
            if (plan.Phones.Count == 0)
            {
                _status.Text = "That text has nothing pronounceable in it.";
                return;
            }
            AudioEngine.Shared.StopAll();
            var samples = SpeechSynthesizer.RenderVoice(plan, settings);
            AudioEngine.Shared.PlaySamples(samples, 0.95f, false, "audio-studio:speech", "Speech preview");
            _status.Text = $"Speaking {plan.Phones.Count} phoneme(s) with the formant voice ({plan.Duration:0.0}s).";
        }
        catch (Exception ex)
        {
            LogAudioFailure("speech voice", ex);
            _status.Text = "Speech preview failed: " + ex.Message;
        }
    }

    private void PreviewSpeechBlocks()
    {
        try
        {
            var settings = ReadSpeechSettings();
            var plan = BuildSpeech();
            if (plan.Phones.Count == 0)
            {
                _status.Text = "That text has nothing pronounceable in it.";
                return;
            }
            AudioEngine.Shared.StopAll();
            var samples = SpeechSynthesizer.RenderSoundBlocks(plan, settings);
            AudioEngine.Shared.PlaySamples(samples, 0.95f, false, "audio-studio:speech-blocks", "Speech blocks preview");
            _status.Text = $"Previewing the exported sound blocks ({settings.Clarity} formant(s) per phoneme).";
        }
        catch (Exception ex)
        {
            LogAudioFailure("speech blocks", ex);
            _status.Text = "GRAB preview failed: " + ex.Message;
        }
    }

    private void ImportSpeech()
    {
        try
        {
            var settings = ReadSpeechSettings();
            var plan = BuildSpeech(); // always rebuild so edits to the text are never ignored
            if (plan.Phones.Count == 0)
            {
                _status.Text = "Nothing to import: the text has no pronounceable words.";
                return;
            }
            _status.Text = "Creating speech sound blocks and compiling schedulers...";
            var result = SpeechImporter.Import(_level, plan, settings, _origin(), msg => _status.Text = msg);
            _dirty = true;
            LoadSounds();
            _speechSummary.Text = "Imported " + result.Summary + " into the level.";
            _status.Text = "Imported: " + result.Summary +
                (result.Warnings.Count > 0 ? "  ·  " + result.Warnings[0] : "");
            MessageBox.Show(this,
                "Speech imported into the level.\n\n" + result.Summary + "\n\n" +
                "Each phoneme is a set of oscillator sound blocks scheduled by a trigger and a real GASM Code Block, so the words play in the exported .level.\n" +
                "The retail level format holds no audio, so this is a formant robot voice rather than a recording.",
                "Text to Speech", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogAudioFailure("speech import", ex);
            _status.Text = "Speech import failed: " + ex.Message;
            MessageBox.Show(this, "Speech import failed:\n\n" + ex.Message, "Text to Speech", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static float ParseFloat(string text, float fallback, float min, float max)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    private sealed record SoundPreset(string Name, int Wave, float Frequency, float Attack, float Sustain,
        float Release, float Volume, Action<LevelNode>? Extra = null);

    private static readonly SoundPreset[] Presets =
    {
        // The fresh-block patch from the retail editor: open low-pass, no effects.
        new("Default", 2, 440, 0.01f, 0.3f, 0.25f, 0.7f),
        new("Blip", 2, 880, 0.005f, 0.08f, 0.08f, 0.6f),
        new("Beep", 0, 440, 0.008f, 0.14f, 0.06f, 0.55f),
        new("Bass", 1, 110, 0.01f, 0.4f, 0.18f, 0.8f, n =>
        {
            n.SoundPunch = 0.25f;
            n.SoundLowPass = 1800;
        }),
        new("Laser", 1, 1200, 0.002f, 0.05f, 0.18f, 0.55f, n =>
        {
            n.SoundFrequencySlide = -4f;
            n.SoundFrequencyDeltaSlide = -1.5f;
        }),
        new("Bell", 2, 660, 0.005f, 0.65f, 0.4f, 0.55f, n =>
        {
            n.SoundReverbDelay = 90;
            n.SoundReverbDecay = 0.45f;
        }),
        new("Wind", 3, 300, 0.2f, 0.8f, 0.5f, 0.35f, n =>
        {
            n.SoundLowPass = 1200;
            n.SoundHighPass = 200;
            n.SoundVibratoStrength = 0.08f;
            n.SoundVibratoSpeed = 2.5f;
        }),
        new("Alarm", 0, 720, 0.01f, 0.22f, 0.04f, 0.5f, n =>
        {
            n.SoundDutyCycle = 0.35f;
            n.SoundVibratoStrength = 0.12f;
            n.SoundVibratoSpeed = 6f;
        }),
        new("Sub Drop", 2, 100, 0.02f, 0.3f, 0.5f, 1f, n =>
        {
            n.SoundFrequencySlide = -2f;
            n.SoundFrequencyMin = 25;
        })
    };
}
