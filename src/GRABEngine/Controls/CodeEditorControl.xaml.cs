using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using GrabEngine.Code;
using GrabEngine.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Indentation;

namespace GrabEngine.Controls;

public partial class CodeEditorControl : UserControl
{
    private readonly TextMarkerService _markers;
    private readonly BreakpointMargin _breakpointMargin;
    private readonly HashSet<int> _breakpoints = new();
    private readonly DispatcherTimer _debounce;
    private bool _suppress;
    private CompletionWindow? _completion;
    private GrabLevel? _level;
    private System.Windows.Controls.ToolTip? _hoverTip;
    private readonly Popup _hintPopup = new() { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Relative };
    private readonly TextBlock _hintText = new() { Foreground = Brushes.White, FontSize = 11.5, FontFamily = new FontFamily("Cascadia Mono, Consolas") };

    public event Action<string>? ScriptChanged;
    public event Action? CompileRequested;
    public event Action? ProblemsRequested;
    public event Action<IReadOnlyList<int>>? BreakpointsChanged;

    public CodeEditorControl()
    {
        InitializeComponent();
        _markers = new TextMarkerService(Editor);
        _breakpointMargin = new BreakpointMargin();
        _breakpointMargin.LineClicked += ToggleBreakpoint;
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_suppress) ScriptChanged?.Invoke(Editor.Text); };
        Editor.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            _debounce.Stop();
            _debounce.Start();
            UpdateFooter();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateFooter();
        Editor.TextArea.TextEntered += TextEntered;
        Editor.TextArea.TextEntering += TextEntering;
        Editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        try { Editor.SyntaxHighlighting = LoadDarkCSharp(); } catch { }
        try { Editor.TextArea.IndentationStrategy = new DefaultIndentationStrategy(); } catch { }
        _hintPopup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 90)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 4, 8, 4), Child = _hintText
        };
        if (Content is Grid root) root.Children.Add(_hintPopup);
        Editor.TextArea.TextView.MouseHover += TextView_MouseHover;
        Editor.TextArea.TextView.MouseHoverStopped += (_, _) => { if (_hoverTip != null) _hoverTip.IsOpen = false; _hoverTip = null; };
        Editor.TextArea.PreviewMouseLeftButtonDown += TextArea_PreviewMouseLeftButtonDown;
        Editor.TextChanged += (_, _) => _hintPopup.IsOpen = false;
        Loaded += (_, _) => Editor.Focus();
    }

    public string ScriptText => Editor.Text;
    public TextEditor InnerEditor => Editor;

    /// <summary>Live GASM budget readout for the selected Code Block.</summary>
    public void SetGasCost(GasmCompileResult? result)
    {
        if (result == null)
        {
            GasCostText.Text = "";
            return;
        }
        int instructions = result.Program.Instructions.Count;
        int connections = result.Connections.Count;
        int registers = result.Program.WorkingRegisters.Count;
        if (result.AllowExtendedLimits)
        {
            // The retail editor's 150 / 10 / 480 numbers do not apply to the runtime, so a
            // single extended Code Block is reported as budget-free instead of "over".
            GasCostText.Text = $"GASM {instructions}  ·  conn {connections}  ·  reg {registers}  ·  extended block";
            GasCostText.Foreground = new SolidColorBrush(Color.FromRgb(127, 200, 160));
            return;
        }
        double worst = Math.Max(instructions / (double)GrabLimits.MaxInstructions, connections / (double)GrabLimits.MaxConnections);
        GasCostText.Text = $"GASM {instructions}/{GrabLimits.MaxInstructions}  ·  conn {connections}/{GrabLimits.MaxConnections}  ·  reg {registers}";
        GasCostText.Foreground = new SolidColorBrush(worst >= 1 ? Color.FromRgb(235, 110, 100)
            : worst > 0.8 ? Color.FromRgb(235, 190, 90) : Color.FromRgb(127, 200, 160));
    }
    public IReadOnlyList<int> Breakpoints => _breakpoints.OrderBy(l => l).ToList();
    public void SetLevel(GrabLevel level) => _level = level;

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _breakpoints.Clear();
        int max = Math.Max(1, Editor.Document.LineCount);
        foreach (var line in lines) if (line >= 1) _breakpoints.Add(Math.Min(line, max));
        _breakpointMargin.SetBreakpoints(_breakpoints);
    }

    public void SetExecutionLine(int line)
    {
        _markers.SetExecutionLine(line);
        if (line > 0)
        {
            try { Editor.ScrollToLine(line); } catch { }
        }
    }

    public void ClearExecutionLine() => _markers.SetExecutionLine(0);

    private void ToggleBreakpoint(int line)
    {
        if (line < 1) return;
        if (!_breakpoints.Remove(line)) _breakpoints.Add(line);
        _breakpointMargin.SetBreakpoints(_breakpoints);
        BreakpointsChanged?.Invoke(Breakpoints);
    }

    public void SetScript(string script)
    {
        _suppress = true;
        Editor.Text = script ?? "";
        _suppress = false;
        _markers.Clear();
        _markers.SetExecutionLine(0);
        _breakpoints.Clear();
        _breakpointMargin.SetBreakpoints(_breakpoints);
        Editor.Document.UndoStack.ClearAll();
        UpdateFooter();
    }

    public void SetDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _markers.Clear();
        var doc = Editor.Document;
        foreach (var d in diagnostics)
        {
            try
            {
                int line = Math.Clamp(d.Line, 1, doc.LineCount);
                var dl = doc.GetLineByNumber(line);
                int col = Math.Clamp(d.Column, 1, dl.Length + 1);
                int offset = dl.Offset + col - 1;
                _markers.Create(offset, Math.Max(1, Math.Min(2, dl.Length - (col - 1))), d.Message, d.Severity);
            }
            catch { }
        }
    }

    public void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(ok ? Color.FromRgb(120, 208, 140) : Color.FromRgb(235, 150, 90));
    }

    public void SetHeader(string text) => FooterText.Text = text;

    public void ScrollToLine(int line)
    {
        try
        {
            var doc = Editor.Document;
            line = Math.Clamp(line, 1, doc.LineCount);
            var dl = doc.GetLineByNumber(line);
            Editor.Select(dl.Offset, dl.Length);
            Editor.ScrollToLine(line);
            Editor.Focus();
        }
        catch { }
    }

    private void UpdateFooter()
    {
        int line = Editor.TextArea.Caret.Line;
        int col = Editor.TextArea.Caret.Column;
        FooterText.Text = $"Ln {line}, Col {col}    •    {ScriptText.Length} chars    •    C# → GASM";
    }

    private void Compile_Click(object sender, RoutedEventArgs e) => CompileRequested?.Invoke();
    private void Problems_Click(object sender, RoutedEventArgs e) => ProblemsRequested?.Invoke();

    private static readonly Dictionary<string, string> AutoCorrect = new(StringComparer.Ordinal)
    {
        ["flaot"] = "float", ["flase"] = "false", ["fasle"] = "false", ["ture"] = "true", ["treu"] = "true",
        ["strign"] = "string", ["stirng"] = "string", ["retrun"] = "return", ["viod"] = "void",
        ["pulbic"] = "public", ["prviate"] = "private", ["statc"] = "static", ["conts"] = "const",
        ["Gameobject"] = "GameObject", ["gameobject"] = "GameObject", ["GameObjet"] = "GameObject",
        ["Vecter3"] = "Vector3", ["vector3"] = "Vector3", ["mathf"] = "Mathf", ["MathF"] = "Mathf",
        ["Plyaer"] = "Player", ["Triger"] = "Trigger", ["PhyiscsBlock"] = "PhysicsBlock", ["Sgin"] = "Sign",
        ["postion"] = "Position", ["Postion"] = "Position", ["rotaton"] = "Rotation", ["scael"] = "Scale",
        ["brigthness"] = "Brightness", ["deltatime"] = "DeltaTime", ["Deltatime"] = "DeltaTime",
        ["cosnole"] = "Console", ["Consoel"] = "Console", ["consle"] = "Console",
        ["writeline"] = "WriteLine", ["Writeline"] = "WriteLine", ["writleline"] = "WriteLine",
        ["anmiation"] = "animation", ["Varaible"] = "Variable", ["varaible"] = "variable",
        ["transfrom"] = "transform", ["Transfrom"] = "Transform", ["transfom"] = "transform",
        ["Vectror3"] = "Vector3", ["Vectro3"] = "Vector3", ["Vecotr3"] = "Vector3",
        ["GameObj"] = "GameObject", ["GameObect"] = "GameObject", ["GamObject"] = "GameObject",
        ["Roatation"] = "Rotation", ["Rotatoin"] = "Rotation",
        ["Positon"] = "Position", ["scael"] = "scale",
        ["Triger"] = "Trigger", ["Lgiht"] = "Light", ["PhysicBlock"] = "PhysicsBlock",
        ["ParticleEmiter"] = "ParticleEmitter", ["ParticleEmitte"] = "ParticleEmitter",
        ["Animtor"] = "Animator", ["Animatior"] = "Animator",
        ["StartActiv"] = "StartActive", ["StartAcitve"] = "StartActive",
        ["MaxRnage"] = "MaxRange", ["MaxRang"] = "MaxRange", ["Wavetype"] = "WaveType",
        ["Frequncy"] = "Frequency", ["Frequancy"] = "Frequency", ["Atack"] = "Attack",
        ["Sustian"] = "Sustain", ["Releas"] = "Release"
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var doc = Editor.Document;
        int caret = Editor.CaretOffset;
        // Enter between braces: newline, indented blank line, closing brace line.
        if (e.Key == Key.Enter && _completion == null)
        {
            TryAutoCorrect();
            caret = Editor.CaretOffset;
            char previous = caret > 0 ? doc.GetCharAt(caret - 1) : '\0';
            char next = caret < doc.TextLength ? doc.GetCharAt(caret) : '\0';
            if (previous == '{' && next == '}')
            {
                var line = doc.GetLineByOffset(caret);
                string beforeCaret = doc.GetText(line.Offset, caret - line.Offset);
                string baseIndent = beforeCaret[..(beforeCaret.Length - beforeCaret.TrimStart().Length)];
                string indent = baseIndent + "    ";
                doc.Insert(caret, "\n" + indent + "\n" + baseIndent);
                Editor.CaretOffset = caret + 1 + indent.Length;
                e.Handled = true;
                return;
            }
        }
        // Backspace between a matching pair removes both characters.
        if (e.Key == Key.Back && _completion == null && caret > 0 && caret < doc.TextLength)
        {
            char previous = doc.GetCharAt(caret - 1), next = doc.GetCharAt(caret);
            bool pair = (previous == '{' && next == '}') || (previous == '(' && next == ')')
                     || (previous == '[' && next == ']') || (previous == '"' && next == '"')
                     || (previous == '\'' && next == '\'');
            if (pair) { doc.Remove(caret - 1, 2); e.Handled = true; return; }
        }
        if (e.Key == Key.F9)
        {
            ToggleBreakpoint(Editor.TextArea.Caret.Line);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowCompletion(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            CompileRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _completion != null)
        {
            _completion.Close();
            _completion = null;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _hintPopup.IsOpen = false;
        }
        else if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            ShowReferences();
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            GoToDefinition();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            RenameSymbol();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            FormatDocument();
            e.Handled = true;
        }
    }

    private void TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_completion != null) return;
        if (e.Text == ".")
        {
            ShowCompletion(false);
            return;
        }
        if (e.Text.Length == 1 && (char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_'))
        {
            int caret = Editor.CaretOffset;
            int start = caret;
            while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
            if (caret - start >= 2) ShowCompletion(false);
        }
        if (e.Text == "(") ShowSignatureHint();
    }

    private void TextEntering(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        char c = e.Text[0];
        if (" \t;(){}[],.=+-*/<>".IndexOf(c) >= 0) TryAutoCorrect();
        int caret = Editor.CaretOffset;
        char next = caret < Editor.Document.TextLength ? Editor.Document.GetCharAt(caret) : '\0';
        // Typing the closing half of a pair steps over the auto-inserted one.
        if ((c == '}' || c == ')' || c == ']' || c == '"' || c == '\'') && next == c)
        {
            Editor.CaretOffset++;
            e.Handled = true;
            return;
        }
        string close = c switch { '{' => "}", '(' => ")", '[' => "]", '"' => "\"", '\'' => "'", _ => "" };
        if (close.Length > 0 && !Editor.TextArea.Selection.Length.Equals(0)) return;
        if (c == '"' && IsInsideString()) return;
        if (close.Length > 0)
        {
            // Insert the whole pair ourselves and stop AvalonEdit's normal input.
            // Letting it insert the opening character after we insert the closing
            // one can leave the pair reversed ("}{" instead of "{}").
            Editor.Document.Insert(caret, c.ToString() + close);
            Editor.CaretOffset = caret + 1;
            e.Handled = true;
        }
    }

    /// <summary>Fixes the most common C# typos as soon as the word is finished.</summary>
    private bool TryAutoCorrect()
    {
        var doc = Editor.Document;
        int caret = Editor.CaretOffset;
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_')) start--;
        int length = caret - start;
        if (length < 3 || length > 24) return false;
        string word = doc.GetText(start, length);
        if (!AutoCorrect.TryGetValue(word, out var fixedWord) || fixedWord == word) return false;
        var line = doc.GetLineByOffset(caret);
        string prefix = doc.GetText(line.Offset, start - line.Offset);
        if (IsInCommentOrString(prefix)) return false;
        doc.Replace(start, length, fixedWord);
        return true;
    }

    private bool IsInsideString()
    {
        int caret = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(caret);
        return IsInCommentOrString(Editor.Document.GetText(line.Offset, caret - line.Offset));
    }

    private static bool IsInCommentOrString(string prefix)
    {
        if (prefix.Contains("//")) return true;
        int quotes = 0;
        for (int i = 0; i < prefix.Length; i++)
            if (prefix[i] == '"' && (i == 0 || prefix[i - 1] != '\\')) quotes++;
        return quotes % 2 == 1;
    }

    private void ShowCompletion(bool forced)
    {
        if (_completion != null) return;
        int caret = Editor.CaretOffset;
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
        string prefix = Editor.Document.GetText(start, caret - start);
        bool afterDot = start > 0 && Editor.Document.GetCharAt(start - 1) == '.';
        string before = Editor.Document.GetText(Math.Max(0, start - 80), Math.Min(80, start));
        bool inFind = Regex.IsMatch(before, "Find\\s*\\(\\s*(\"|@\")?$") || Regex.IsMatch(before, "Find\\s*<[^>]+>\\s*\\(\\s*(\"|@\")?$");
        bool inFindType = Regex.IsMatch(before, @"Find\s*<\s*\w*$");
        bool afterNew = Regex.IsMatch(before, @"\bnew\s+$");
        if (!forced && !afterDot && !inFind && !inFindType && !afterNew && prefix.Length < 2) return;

        bool quoteOpen = start > 0 && Editor.Document.GetCharAt(start - 1) == '"';
        var suggestions = BuildSuggestions(prefix, afterDot, inFind, inFindType, afterNew, quoteOpen);
        if (suggestions.Count == 0) return;
        _completion = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = start,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = true,
            Width = 430,
            MaxHeight = 330
        };
        ApplyCompletionTheme(_completion);
        foreach (var s in suggestions.Take(120)) _completion.CompletionList.CompletionData.Add(s);
        if (_completion.CompletionList.CompletionData.Count == 0) { _completion.Close(); _completion = null; return; }
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
        ApplyCompletionTheme(_completion);
    }

    /// <summary>Dark, VS Code-style completion popup with a coloured glyph per kind.</summary>
    private static void ApplyCompletionTheme(CompletionWindow window)
    {
        var background = new SolidColorBrush(Color.FromRgb(37, 37, 40));
        var foreground = new SolidColorBrush(Color.FromRgb(228, 228, 230));
        background.Freeze();
        foreground.Freeze();
        window.Background = background;
        window.Foreground = foreground;
        window.BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 78));
        window.BorderThickness = new Thickness(1);

        var list = window.CompletionList.ListBox;
        if (list == null) return;
        list.Background = background;
        list.Foreground = foreground;
        list.BorderThickness = new Thickness(0);
        list.ItemTemplate = BuildCompletionTemplate();

        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 3, 10, 3)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(9, 71, 113))));
        itemStyle.Triggers.Add(selected);
        list.ItemContainerStyle = itemStyle;
    }

    private static DataTemplate BuildCompletionTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(FrameworkElement.WidthProperty, 18.0);
        badge.SetValue(FrameworkElement.HeightProperty, 18.0);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Suggestion.Accent)));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Suggestion.Glyph)));
        glyph.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        glyph.SetValue(TextBlock.FontSizeProperty, 9.5);
        glyph.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.AppendChild(glyph);
        panel.AppendChild(badge);

        var texts = new FrameworkElementFactory(typeof(StackPanel));
        texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Suggestion.Text)));
        name.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        name.SetValue(TextBlock.FontSizeProperty, 12.5);
        name.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(232, 232, 235)));
        var description = new FrameworkElementFactory(typeof(TextBlock));
        description.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Suggestion.Description)));
        description.SetValue(TextBlock.FontSizeProperty, 10.0);
        description.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(148, 148, 154)));
        texts.AppendChild(name);
        texts.AppendChild(description);
        panel.AppendChild(texts);

        return new DataTemplate { VisualTree = panel };
    }

    private delegate void AddSuggestion(string text, string description, string group, string? insert = null, int caretBack = 0);

    private List<Suggestion> BuildSuggestions(string prefix, bool afterDot, bool inFind, bool inFindType, bool afterNew, bool quoteOpen)
    {
        var list = new List<Suggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string text, string description, string group, string? insert = null, int caretBack = 0)
        {
            if (MatchScore(text, prefix) < 0) return;
            if (!seen.Add(text)) return;
            list.Add(new Suggestion(text, description, group, insert, caretBack));
        }
        AddSuggestion add = Add;

        if (inFind && _level != null)
        {
            foreach (var n in _level.AllNodes().Take(400))
                Add(n.DisplayName, "Scene object · " + n.Kind, "object", quoteOpen ? n.DisplayName : "\"" + n.DisplayName + "\"");
            return Ordered(list, prefix);
        }
        if (inFindType)
        {
            foreach (var t in new[] { "Sign", "Light", "Trigger", "PhysicsBlock", "GameObject" })
                Add(t, "Scene object type", "type");
            return Ordered(list, prefix);
        }
        if (afterNew)
        {
            Add("Vector3", "Vector3(x, y, z)", "type", "Vector3()", 1);
            Add("Color", "Color(r, g, b)", "type", "Color()", 1);
            foreach (var t in new[] { "Sign", "Light", "Trigger", "PhysicsBlock", "ParticleEmitter", "Sound", "GravityBlock", "LobbyTerminal", "StartPoint", "FinishPoint", "Cube", "Sphere", "Cylinder", "Pyramid", "Prism", "Cone", "PyramidSquare", "CrumblingBlock" })
                Add(t, "Scene object · baked into the level on compile", "object", t);
            Add("List<float>", "List with a fixed capacity", "type", "List<float>(8)", 1);
            Add("List<int>", "List with a fixed capacity", "type", "List<int>(8)", 1);
            Add("Dictionary<string, int>", "Dictionary with a fixed capacity", "type", "Dictionary<string, int>(8)", 1);
            Add("float[]", "fixed-size array", "type", "float[4]", 0);
            Add("int[]", "fixed-size array", "type", "int[4]", 0);
            Add("bool[]", "fixed-size array", "type", "bool[4]", 0);
            return Ordered(list, prefix);
        }
        if (afterDot)
        {
            AddMembers(ReceiverType(), add, IsStaticReceiver());
            return Ordered(list, prefix);
        }

        foreach (var k in new[] { "public", "private", "protected", "static", "const", "readonly", "sealed", "abstract", "virtual", "override", "float", "int", "bool", "string", "void", "var", "if", "else", "while", "for", "foreach", "in", "do", "switch", "case", "default", "return", "break", "continue", "goto", "new", "true", "false", "null", "class", "struct", "interface", "base", "this", "try", "catch", "finally", "throw", "using", "lock", "yield", "async", "await" })
            Add(k, "Keyword", "keyword");
        Add("if-block", "if statement with braces", "snippet", "if (condition)\n{\n    \n}", 6);
        Add("for-loop", "indexed for loop", "snippet", "for (int i = 0; i < count; i++)\n{\n    \n}", 6);
        Add("foreach-loop", "foreach loop", "snippet", "foreach (var item in collection)\n{\n    \n}", 6);
        Add("while-loop", "while loop", "snippet", "while (condition)\n{\n    \n}", 6);
        Add("try-catch", "try / catch block", "snippet", "try\n{\n    \n}\ncatch (Exception ex)\n{\n    Console.WriteLine(ex.Message);\n}", 50);
        Add("Awake-method", "Awake() · runs before Start", "snippet", "void Awake()\n{\n    \n}", 6);
        Add("Start-method", "Code Block Start()", "snippet", "void Start()\n{\n    \n}", 6);
        Add("Update-method", "Code Block Update()", "snippet", "void Update()\n{\n    \n}", 6);
        Add("FixedUpdate-method", "FixedUpdate() · once per frame in GASM", "snippet", "void FixedUpdate()\n{\n    \n}", 6);
        Add("LateUpdate-method", "LateUpdate() · after Update", "snippet", "void LateUpdate()\n{\n    \n}", 6);
        Add("OnEnable-method", "OnEnable() · runs after Awake", "snippet", "void OnEnable()\n{\n    \n}", 6);
        Add("OnTriggerEnter-method", "trigger entered by the player", "snippet", "void OnTriggerEnter()\n{\n    \n}", 6);
        Add("OnTriggerExit-method", "player left the trigger", "snippet", "void OnTriggerExit()\n{\n    \n}", 6);
        Add("OnTriggerStay-method", "runs every frame while inside a trigger", "snippet", "void OnTriggerStay()\n{\n    \n}", 6);
        Add("OnSignal-method", "fires when a trigger targets this Code Block", "snippet", "void OnSignal()\n{\n    \n}", 6);
        foreach (var t in new[] { "Vector3", "Color", "GameObject", "Trigger", "Light", "Sign", "PhysicsBlock", "ParticleEmitter", "Sound", "Player", "Time", "Level", "Mathf", "Math", "Array", "Console", "Animator", "char", "Convert", "string", "float[]", "int[]", "bool[]", "List<float>", "List<int>", "Dictionary<string, float>", "Dictionary<string, int>", "KeyValuePair<string, float>", "IEnumerator", "Task", "WaitForSeconds", "Exception" })
            Add(t, "Type", "type");
        foreach (var t in new[] { "List<>", "Dictionary<,>", "struct", "interface" })
            Add(t, "Generic / object type", "type", t, t.StartsWith("List", StringComparison.Ordinal) ? 1 : t.StartsWith("Dictionary", StringComparison.Ordinal) ? 2 : 0);
        foreach (var m in new[] { "Start", "Update", "Sin", "Cos", "Atan2", "Sqrt", "Abs", "Floor", "FloorToInt", "CeilToInt", "RoundToInt", "Clamp", "Lerp", "Min", "Max", "Pow", "Distance", "Dot", "Cross", "Normalize", "SqrMagnitude", "ClampMagnitude", "Project", "ProjectOnPlane", "Angle", "Find", "Log", "Repeat", "ToUpper", "ToLower", "Trim", "Substring", "Contains", "StartsWith", "EndsWith", "CompareTo", "Compare", "Replace", "IndexOf", "Concat", "Join", "WriteLine", "Write", "StartCoroutine", "Task.Delay", "Add", "AddRange", "Insert", "Remove", "RemoveAt", "Clear", "ContainsKey", "ToInt32", "ToSingle", "ToBoolean" })
            Add(m, "Method", "method", m + "()", 1);
        foreach (var (variable, type) in DeclaredVariables())
            Add(variable, type, "field");
        if (_level != null && prefix.Length >= 1)
            foreach (var n in _level.AllNodes().Where(n => n.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(20))
                Add(n.DisplayName, "Scene object · " + n.Kind, "object", "\"" + n.DisplayName + "\"");
        return Ordered(list, prefix);
    }

    private static List<Suggestion> Ordered(List<Suggestion> list, string prefix) => list
        .OrderByDescending(x => MatchScore(x.Text, prefix))
        .ThenBy(x => x.Priority)
        .ThenBy(x => x.Text)
        .ToList();

    /// <summary>Prefix match first, then camel-case initials, then a loose subsequence.</summary>
    private static int MatchScore(string candidate, string prefix)
    {
        if (prefix.Length == 0) return 1;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 1000 - Math.Min(400, candidate.Length - prefix.Length);

        int p = 0, camel = 0;
        for (int i = 0; i < candidate.Length && p < prefix.Length; i++)
        {
            bool boundary = i == 0 || char.IsUpper(candidate[i]) || candidate[i - 1] == '_';
            if (boundary && char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(prefix[p])) { p++; camel += 10; }
        }
        if (p == prefix.Length) return 600 + camel;

        p = 0;
        for (int i = 0; i < candidate.Length && p < prefix.Length; i++)
            if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(prefix[p])) p++;
        return p == prefix.Length ? 300 : -1;
    }

    private IEnumerable<(string Name, string Type)> DeclaredVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(ScriptText, @"\b(GameObject|Trigger|Light|Sign|PhysicsBlock|ParticleEmitter|Sound|GravityBlock|LobbyTerminal|Vector3|Color|string|char|float|int|bool)\s+([A-Za-z_]\w*)\b"))
            if (seen.Add(m.Groups[2].Value)) yield return (m.Groups[2].Value, m.Groups[1].Value);
        foreach (Match m in Regex.Matches(ScriptText, @"\bvar\s+([A-Za-z_]\w*)\s*=\s*new\s+([A-Za-z_]\w*)"))
            if (seen.Add(m.Groups[1].Value)) yield return (m.Groups[1].Value, m.Groups[2].Value);
        foreach (Match m in Regex.Matches(ScriptText, @"\b(List\s*<[^>]+>|Dictionary\s*<[^,]+,[^>]+>)\s+([A-Za-z_]\w*)"))
            if (seen.Add(m.Groups[2].Value))
                yield return (m.Groups[2].Value, m.Groups[1].Value.StartsWith("List", StringComparison.Ordinal) ? "List" : "Dictionary");
    }

    /// <summary>Test hook: builds the completion list for a script whose caret is at the end.</summary>
    internal List<string> SuggestionsForTest(string script)
    {
        Editor.Text = script;
        Editor.CaretOffset = Editor.Document.TextLength;
        int caret = Editor.CaretOffset, start = caret;
        while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
        string prefix = Editor.Document.GetText(start, caret - start);
        bool afterDot = start > 0 && Editor.Document.GetCharAt(start - 1) == '.';
        string before = Editor.Document.GetText(Math.Max(0, start - 80), Math.Min(80, start));
        bool inFind = Regex.IsMatch(before, "Find\\s*\\(\\s*(\"|@\")?$") || Regex.IsMatch(before, "Find\\s*<[^>]+>\\s*\\(\\s*(\"|@\")?$");
        bool inFindType = Regex.IsMatch(before, @"Find\s*<\s*\w*$");
        bool afterNew = Regex.IsMatch(before, @"\bnew\s+$");
        bool quoteOpen = start > 0 && Editor.Document.GetCharAt(start - 1) == '"';
        return BuildSuggestions(prefix, afterDot, inFind, inFindType, afterNew, quoteOpen).Select(s => s.Text).Take(40).ToList();
    }

    /// <summary>Works out what is to the left of the dot, including Find&lt;T&gt; and scene kinds.</summary>
    private string ReceiverType()
    {
        string before = Editor.Document.GetText(0, Math.Min(Editor.CaretOffset, Editor.Document.TextLength));

        // Chained calls and properties: GameObject.Find<Sign>("Sign 1").Position.
        var findCall = Regex.Match(before, @"GameObject\.Find\s*<\s*(\w+)\s*>\s*\([^()]*\)\s*\.$");
        if (findCall.Success) return findCall.Groups[1].Value;
        var findPlainCall = Regex.Match(before, "GameObject\\.Find\\s*\\(\\s*\"([^\"]+)\"\\s*\\)\\s*\\.$");
        if (findPlainCall.Success && _level != null)
            return TypeOfNamedObject(findPlainCall.Groups[1].Value);
        if (Regex.IsMatch(before, @"new\s+Vector3\s*\([^()]*\)\s*\.$")) return "Vector3";
        if (Regex.IsMatch(before, @"new\s+Color\s*\([^()]*\)\s*\.$")) return "Color";
        var chain = Regex.Match(before, @"([A-Za-z_]\w*)\.(Position|Rotation|Scale|Velocity|VelocityMin|VelocityMax|AccelerationMin|AccelerationMax|GravityDirection|GradientDirection|StartColor|EndColor|LightColor|SignColor|Color1|Color2)\s*\.$");
        if (chain.Success)
        {
            string result = chain.Groups[2].Value switch
            {
                "Position" or "Rotation" or "Scale" or "Velocity" or "VelocityMin" or "VelocityMax" or
                "AccelerationMin" or "AccelerationMax" or "GravityDirection" or "GradientDirection" => "Vector3",
                _ => "Color"
            };
            if (ResolveIdentifierType(chain.Groups[1].Value) is { Length: > 0 })
                return result;
        }

        var m = Regex.Match(before, @"([A-Za-z_]\w*)\.(?:[A-Za-z_]\w*)?$");
        if (!m.Success) return "";
        string receiver = m.Groups[1].Value;
        if (receiver is "Time" or "Mathf" or "Math" or "MathF" or "Player" or "Level" or "string" or "String" or "Vector3" or "Color" or "Console" or "Array" or "char" or "Char" or "Convert" or "GameObject" or "Animator" or "List" or "Dictionary")
            return receiver;

        return ResolveIdentifierType(receiver);
    }

    private string ResolveIdentifierType(string receiver)
    {
        var find = Regex.Match(ScriptText, @"\b" + Regex.Escape(receiver) + @"\s*=\s*GameObject\.Find\s*<\s*(\w+)\s*>");
        if (find.Success) return find.Groups[1].Value;

        var findPlain = Regex.Match(ScriptText, "\\b" + Regex.Escape(receiver) + "\\s*=\\s*GameObject\\.Find\\s*\\(\\s*\"([^\"]+)\"");
        if (findPlain.Success && _level != null)
            return TypeOfNamedObject(findPlain.Groups[1].Value);
        foreach (var (name, type) in DeclaredVariables())
            if (name == receiver) return type;
        return "";
    }

    private string TypeOfNamedObject(string name)
    {
        var node = _level?.AllNodes().FirstOrDefault(n =>
            string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        return node?.Kind switch
        {
            NodeKind.Sign => "Sign",
            NodeKind.Light => "Light",
            NodeKind.Trigger => "Trigger",
            NodeKind.Static or NodeKind.Crumbling => "PhysicsBlock",
            NodeKind.ParticleEmitter => "ParticleEmitter",
            NodeKind.Sound => "Sound",
            NodeKind.Gravity => "GravityBlock",
            _ => "GameObject"
        };
    }

    private bool IsStaticReceiver()
    {
        string before = Editor.Document.GetText(0, Editor.CaretOffset);
        var m = Regex.Match(before, @"([A-Za-z_]\w*)\.(?:[A-Za-z_]\w*)?$");
        if (!m.Success) return false;
        string receiver = m.Groups[1].Value;
        return receiver is "GameObject" or "Array" or "Convert" or "Mathf" or "Math" or "MathF" or "char" or "Char" or "Animator" or "string" or "String";
    }

    private static void AddMembers(string type, AddSuggestion add, bool staticOnly = false)
    {
        void Transform()
        {
            add("Position", "Vector3 · world position", "property");
            add("Rotation", "Vector3 · euler rotation", "property");
            add("Name", "string · name in the Hierarchy", "property");
        }
        void ObjectMethods()
        {
            add("SetPosition", "void · SetPosition(Vector3)", "method", "SetPosition()", 1);
            add("SetRotation", "void · SetRotation(euler Vector3)", "method", "SetRotation()", 1);
            add("Translate", "void · add a Vector3 offset", "method", "Translate()", 1);
            add("Rotate", "void · add euler degrees", "method", "Rotate()", 1);
            add("SetColor", "void · SetColor(Color)", "method", "SetColor()", 1);
        }
        void StringMembers()
        {
            add("Concat", "static string · join values", "method", "Concat()", 1);
            add("Join", "static string · join with a separator", "method", "Join()", 1);
            add("Format", "static string · formatted text", "method", "Format()", 1);
            add("IsNullOrEmpty", "static bool · empty/null check", "method", "IsNullOrEmpty()", 1);
            add("Length", "int · character count", "string");
            add("ToUpper", "string · uppercase copy", "string", "ToUpper()", 1);
            add("ToLower", "string · lowercase copy", "string", "ToLower()", 1);
            add("Trim", "string · remove surrounding spaces", "string", "Trim()", 1);
            add("TrimStart", "string · trim the start", "string", "TrimStart()", 1);
            add("TrimEnd", "string · trim the end", "string", "TrimEnd()", 1);
            add("Substring", "string · Substring(start, length)", "string", "Substring()", 1);
            add("Contains", "bool · Contains(text)", "string", "Contains()", 1);
            add("StartsWith", "bool · StartsWith(text)", "string", "StartsWith()", 1);
            add("EndsWith", "bool · EndsWith(text)", "string", "EndsWith()", 1);
            add("IndexOf", "int · IndexOf(text[, start])", "string", "IndexOf()", 1);
            add("LastIndexOf", "int · LastIndexOf(text[, start])", "string", "LastIndexOf()", 1);
            add("CompareTo", "int · string comparison", "string", "CompareTo()", 1);
            add("Replace", "string · Replace(old, new)", "string", "Replace()", 1);
            add("Remove", "string · Remove(start[, count])", "string", "Remove()", 1);
            add("Insert", "string · Insert(index, text)", "string", "Insert()", 1);
            add("PadLeft", "string · PadLeft(width)", "string", "PadLeft()", 1);
            add("PadRight", "string · PadRight(width)", "string", "PadRight()", 1);
            add("Equals", "bool · text comparison", "string", "Equals()", 1);
            add("ToString", "string · text copy", "string", "ToString()", 1);
        }

        switch (type)
        {
            case "Sign":
                add("Write", "string or ASCII code · append text", "property");
                add("Text", "string · the sign's text", "property");
                add("Select", "0 ASCII · 1 number mode", "property");
                add("Control", "1 clear · 2 delete last character", "property");
                add("Number", "number · clear and print", "property");
                Transform();
                ObjectMethods();
                break;
            case "Light":
                add("Brightness", "0–10 · light intensity", "property");
                add("Range", "0–100 · light radius", "property");
                add("Color", "Color · light colour", "property");
                Transform();
                ObjectMethods();
                break;
            case "Trigger":
                add("Active", "0/1 · something is inside", "property");
                add("PlayerId", "0–255 · who is inside", "property");
                add("ForceActive", "0/1 · fire the trigger", "property");
                add("Part", "0–255 · trigger part filter", "property");
                Transform();
                ObjectMethods();
                break;
            case "ParticleEmitter":
                foreach (var m in new[] { "Position", "Scale", "Rate", "LifeSpanMin", "LifeSpanMax", "StartColor", "EndColor", "StartSizeMin", "StartSizeMax", "EndSizeMin", "EndSizeMax", "Velocity", "VelocityMin", "VelocityMax", "AccelerationMin", "AccelerationMax", "HideParticles" })
                    add(m, "emitter property", "property");
                ObjectMethods();
                break;
            case "Sound" or "SoundEmitter":
                foreach (var m in new[] { "Position", "SoundName", "Volume", "Repeat", "StartActive", "MaxRange", "Frequency", "WaveType", "Attack", "Sustain", "Release" })
                    add(m, "sound property", "property");
                ObjectMethods();
                break;
            case "GravityBlock":
                foreach (var m in new[] { "Position", "Scale", "GravityMode", "GravityDirection" }) add(m, "gravity property", "property");
                ObjectMethods();
                break;
            case "string" or "String":
                StringMembers();
                break;
            case "Vector3":
                foreach (var m in new[] { "zero", "one", "up", "down", "left", "right", "forward", "back" }) add(m, "Vector3 constant", "property");
                add("X", "float · X component", "property");
                add("Y", "float · Y component", "property");
                add("Z", "float · Z component", "property");
                add("Length", "float · magnitude", "property");
                add("Normalized", "Vector3 · unit length", "property");
                add("Distance", "float · Distance(a, b)", "method", "Distance()", 1);
                add("Dot", "float · Dot(a, b)", "method", "Dot()", 1);
                add("Cross", "Vector3 · Cross(a, b)", "method", "Cross()", 1);
                add("Normalize", "Vector3 · Normalize(v)", "method", "Normalize()", 1);
                add("Lerp", "Vector3 · Lerp(a, b, t)", "method", "Lerp()", 1);
                add("LerpUnclamped", "Vector3 · LerpUnclamped(a, b, t)", "method", "LerpUnclamped()", 1);
                add("SqrMagnitude", "float · SqrMagnitude(v)", "method", "SqrMagnitude()", 1);
                add("ClampMagnitude", "Vector3 · ClampMagnitude(v, max)", "method", "ClampMagnitude()", 1);
                add("Project", "Vector3 · Project(v, onNormal)", "method", "Project()", 1);
                add("ProjectOnPlane", "Vector3 · ProjectOnPlane(v, normal)", "method", "ProjectOnPlane()", 1);
                add("Angle", "float · Angle(a, b) in degrees", "method", "Angle()", 1);
                break;
            case "Color":
                foreach (var m in new[] { "red", "green", "blue", "white", "black", "yellow", "cyan", "magenta", "gray", "clear" }) add(m, "Color constant", "property");
                foreach (var m in new[] { "R", "G", "B", "A" }) add(m, "float · 0–1 channel", "property");
                add("Lerp", "Color · Lerp(a, b, t)", "method", "Lerp()", 1);
                add("LerpUnclamped", "Color · LerpUnclamped(a, b, t)", "method", "LerpUnclamped()", 1);
                break;
            case "Time":
                add("DeltaTime", "float · seconds since last frame", "property");
                add("Time", "float · seconds since start", "property");
                add("FrameCount", "float · frames since start", "property");
                break;
            case "Player":
                foreach (var m in new[] { "SelectIndex", "SelectId", "SelectPart", "Control", "Count", "Valid", "Id", "LocalId", "Pos", "Rot", "Vel", "Col", "Tel", "Respawn" })
                    add(m, "Player field", "property");
                break;
            case "Level":
                foreach (var m in new[] { "Var1", "Var2", "Var3", "Var4", "Var5" }) add(m, "float · lobby variable", "property");
                break;
            case "Mathf" or "Math" or "MathF":
                foreach (var m in new[] { "Sin", "Cos", "Tan", "Atan2", "Atan", "Asin", "Acos", "Sqrt", "Abs", "Floor", "FloorToInt", "Ceil", "CeilToInt", "Round", "RoundToInt", "Pow", "Min", "Max", "Clamp", "Clamp01", "Lerp", "Sign", "Repeat", "PingPong", "Approximately", "InverseLerp", "SmoothStep", "MoveTowards", "LerpAngle", "MoveTowardsAngle", "DeltaAngle", "Rad2Deg", "Deg2Rad", "IsPowerOfTwo", "NextPowerOfTwo" })
                    add(m, "Mathf function (radians for trig, degrees for angles)", "method", m + "()", 1);
                add("PI", "float · 3.14159…", "property");
                add("E", "float · 2.71828…", "property");
                add("Infinity", "float · positive infinity", "property");
                break;
            case "List":
                foreach (var m in new[] { "Add", "AddRange", "Insert", "RemoveAt", "Remove", "Clear", "Contains", "IndexOf" })
                    add(m, "List method", "method", m + "()", 1);
                add("Count", "int · number of items", "property");
                add("Capacity", "int · fixed register capacity", "property");
                break;
            case "Dictionary":
                foreach (var m in new[] { "Add", "ContainsKey", "TryGetValue", "Remove", "Clear" })
                    add(m, "Dictionary method", "method", m + "()", 1);
                add("Count", "int · number of entries", "property");
                break;
            case "Array":
                add("Reverse", "void · Reverse(array)", "method", "Reverse()", 1);
                add("IndexOf", "int · IndexOf(array, value)", "method", "IndexOf()", 1);
                add("Copy", "void · Copy(source, destination[, length])", "method", "Copy()", 1);
                add("Fill", "void · Fill(array, value)", "method", "Fill()", 1);
                add("Clear", "void · zero every element", "method", "Clear()", 1);
                break;
            case "char" or "Char":
                foreach (var m in new[] { "IsDigit", "IsLetter", "IsLetterOrDigit", "IsNumber", "IsWhiteSpace", "IsUpper", "IsLower", "IsPunctuation", "IsSymbol", "IsControl", "ToUpper", "ToLower", "ToString", "Parse" })
                    add(m, "char helper (compile-time)", "method", m + "()", 1);
                break;
            case "Convert":
                foreach (var m in new[] { "ToInt32", "ToInt64", "ToUInt32", "ToSingle", "ToDouble", "ToDecimal", "ToBoolean", "ToChar", "ToString" })
                    add(m, "value conversion", "method", m + "()", 1);
                break;
            case "Animator":
                add("Play", "void · Play(target, clip[, loop[, reverse]])", "method", "Play()", 1);
                add("Stop", "void · stop the clip on target", "method", "Stop()", 1);
                add("Pause", "void · pause the clip on target", "method", "Pause()", 1);
                add("Resume", "void · resume a paused clip", "method", "Resume()", 1);
                add("Restart", "void · restart the clip", "method", "Restart()", 1);
                add("Reverse", "void · Reverse(target, clip)", "method", "Reverse()", 1);
                add("IsPlaying", "bool · whether the clip is playing", "method", "IsPlaying()", 1);
                break;
            case "Console":
                add("WriteLine", "void · print a line to the Console and a generated sign", "method", "WriteLine()", 1);
                add("Write", "void · print without a newline", "method", "Write()", 1);
                add("Clear", "void · clear the generated console sign", "method", "Clear()", 1);
                break;
            case "GameObject":
                if (staticOnly)
                {
                    add("Find", "GameObject · Find(name)", "method", "Find(\"\")", 2);
                    foreach (var kindName in new[] { "Sign", "Light", "Trigger", "PhysicsBlock", "ParticleEmitter", "Sound", "GravityBlock" })
                        add("Find<" + kindName + ">", kindName + " · strongly typed lookup", "method", "Find<" + kindName + ">(\"\")", 2);
                }
                else
                {
                    Transform();
                    ObjectMethods();
                    add("Write", "string or ASCII code · text", "property");
                }
                break;
            default:
                Transform();
                ObjectMethods();
                add("Scale", "Vector3 · size", "property");
                add("Color", "Color · block colour", "property");
                add("Enabled", "0/1 · physics body", "property");
                add("State", "number · physics state", "property");
                add("Write", "string or ASCII code · text", "property");
                add("Text", "string · editor label", "property");
                break;
        }
    }

    // ── Editor intelligence: definitions, references, hover, rename, format ─────

    private static readonly Dictionary<string, string> ApiDocs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Find"] = "GameObject Find(string name) · binds a scene object at compile time. Names are limited to 4 characters in GRAB.",
        ["Find<T>"] = "T Find<T>(string name) · typed lookup. T can be Sign, Light, Trigger, PhysicsBlock, ParticleEmitter, Sound, GravityBlock.",
        ["Position"] = "Vector3 · world position. Assign X/Y/Z separately or a whole Vector3.",
        ["Rotation"] = "Vector3 · euler rotation in degrees.",
        ["Scale"] = "Vector3 · object scale.",
        ["Color"] = "Color · block colour (only colour-capable materials accept it).",
        ["Write"] = "string · sign text. Works on Sign blocks, or any object as ASCII codes.",
        ["Control"] = "float · 1 makes a sign scroll / animate its text, 0 holds it still.",
        ["DeltaTime"] = "float · seconds since the previous frame. Multiply speeds by this.",
        ["Time"] = "float · level time in seconds (Time.Time).",
        ["Sin"] = "Mathf.Sin(float radians) → float.",
        ["Cos"] = "Mathf.Cos(float radians) → float.",
        ["Clamp"] = "Mathf.Clamp(value, min, max) → float.",
        ["Lerp"] = "Mathf.Lerp(a, b, t) → float. t=0 is a, t=1 is b.",
        ["MoveTowards"] = "Mathf.MoveTowards(current, target, maxDelta) → float.",
        ["Sqrt"] = "Mathf.Sqrt(float) → float.",
        ["Abs"] = "Mathf.Abs(float) → float.",
        ["Play"] = "Animator.Play(target, clip[, loop[, reverse]]) · play an animation clip via its trigger.",
        ["Stop"] = "Animator.Stop(target, clip) · stop a playing clip.",
        ["Reverse"] = "Animator.Reverse(target, clip) · play a clip backwards.",
        ["SetTrigger"] = "Trigger.Set(id) / SetTrigger · activate a trigger connection.",
        ["WriteLine"] = "Console.WriteLine(value) · prints to the engine console and a generated sign in game.",
        ["Instantiate"] = "Instantiate(block, position) · creates a new object at run time (and on export).",
        ["Destroy"] = "Destroy(target) · removes a spawned object at run time.",
        ["Sleep"] = "GASM Sleep · pause the code block for N seconds (yield return new WaitForSeconds).",
        ["activeSelf"] = "bool · whether this object starts active (the inspector's Start Active checkbox)."
    };

    private static readonly Dictionary<string, string> ApiSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Find"] = "GameObject Find(string name)",
        ["WriteLine"] = "void WriteLine(object value)",
        ["Write"] = "void Write(object value)",
        ["Sin"] = "float Sin(float radians)",
        ["Cos"] = "float Cos(float radians)",
        ["Clamp"] = "float Clamp(float value, float min, float max)",
        ["Lerp"] = "float Lerp(float a, float b, float t)",
        ["MoveTowards"] = "float MoveTowards(float current, float target, float maxDelta)",
        ["Play"] = "void Play(GameObject target, string clip, bool loop = true, bool reverse = false)",
        ["Stop"] = "void Stop(GameObject target, string clip)",
        ["WaitForSeconds"] = "WaitForSeconds(float seconds)",
        ["Concat"] = "string Concat(params object[] values)"
    };

    private void TextArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        try { if (GoToDefinition()) e.Handled = true; } catch { }
    }

    private void TextView_MouseHover(object sender, MouseEventArgs e)
    {
        try
        {
            var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (pos == null) return;
            int offset = Editor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
            int start = offset, end = offset;
            while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
            while (end < Editor.Document.TextLength && (char.IsLetterOrDigit(Editor.Document.GetCharAt(end)) || Editor.Document.GetCharAt(end) == '_')) end++;
            if (end <= start) return;
            string word = Editor.Document.GetText(start, end - start);
            string? doc = null;
            if (ApiDocs.TryGetValue(word, out var api)) doc = api;
            var field = ScriptIntrospection.Inspect(Editor.Text).Fields.FirstOrDefault(f => f.Name == word);
            if (field != null) doc = $"{field.TypeName} {field.Name}" + (string.IsNullOrWhiteSpace(field.Tooltip) ? " · inspector-serialized field" : "\n" + field.Tooltip);
            if (doc == null) return;
            if (_hoverTip != null) _hoverTip.IsOpen = false;
            _hoverTip = new System.Windows.Controls.ToolTip
            {
                Content = new TextBlock { Text = doc, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                PlacementTarget = Editor,
                Placement = PlacementMode.Mouse,
                StaysOpen = false
            };
            Editor.ToolTip = _hoverTip;
            _hoverTip.IsOpen = true;
        }
        catch { }
    }

    private void ShowSignatureHint()
    {
        try
        {
            int caret = Editor.CaretOffset;
            string before = Editor.Document.GetText(0, caret);
            var m = Regex.Match(before, @"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(([^()]*)$");
            if (!m.Success) { _hintPopup.IsOpen = false; return; }
            string full = m.Groups[1].Value;
            string name = full.Split('.').Last();
            string? signature = null;
            // Prefer the script's own declaration so user methods show their real parameters.
            var decl = Regex.Match(Editor.Text, @"(?m)^\s*(?:public|private|protected|internal|static|virtual|override|async|\s)*[A-Za-z_][\w<>\[\],.?]*\s+" + Regex.Escape(name) + @"\s*\(([^)]*)\)");
            if (decl.Success) signature = $"{name}({decl.Groups[1].Value.Trim()})";
            else if (ApiSignatures.TryGetValue(name, out var api)) signature = api;
            else if (ApiDocs.TryGetValue(name, out var apiDoc)) signature = apiDoc;
            if (signature == null) { _hintPopup.IsOpen = false; return; }
            int arg = 1 + m.Groups[2].Value.Count(c => c == ',');
            _hintText.Text = $"{signature}    · argument {arg}";
            _hintPopup.PlacementTarget = Editor;
            var rect = Editor.TextArea.Caret.CalculateCaretRectangle();
            var p = Editor.TextArea.TextView.TranslatePoint(new Point(rect.Left, rect.Bottom), Editor);
            _hintPopup.HorizontalOffset = Math.Max(0, p.X);
            _hintPopup.VerticalOffset = Math.Max(0, p.Y + 4);
            _hintPopup.IsOpen = true;
        }
        catch { _hintPopup.IsOpen = false; }
    }

    private bool GoToDefinition()
    {
        string? symbol = SymbolAtCaret();
        if (symbol == null) return false;
        int line = FindDeclarationLine(symbol);
        if (line <= 0) return false;
        ScrollToLine(line);
        StatusText.Text = $"{symbol} → line {line}";
        return true;
    }

    private void ShowDefinition() => GoToDefinition();

    private void ShowReferences()
    {
        string? symbol = SymbolAtCaret();
        if (symbol == null) return;
        var hits = new List<(int Line, string Text)>();
        foreach (var line in Editor.Document.Lines)
        {
            string text = Editor.Document.GetText(line);
            if (Regex.IsMatch(text, @"\b" + Regex.Escape(symbol) + @"\b"))
                hits.Add((line.LineNumber, text.Trim()));
        }
        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Title = $"References to '{symbol}' ({hits.Count})", Width = 620, Height = 440,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)), ShowInTaskbar = false
        };
        var list = new ListBox { Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        foreach (var hit in hits) list.Items.Add(new ReferenceItem(hit.Line, $"{hit.Line,4}:  {hit.Text}"));
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ReferenceItem r) { ScrollToLine(r.Line); win.Close(); } };
        var header = new TextBlock
        {
            Text = $"  {hits.Count} reference(s) to {symbol}   ·   double-click to jump",
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)), Margin = new Thickness(6, 6, 6, 6)
        };
        var host = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        host.Children.Add(header);
        host.Children.Add(list);
        win.Content = host;
        win.Show();
    }

    private void RenameSymbol()
    {
        string? symbol = SymbolAtCaret();
        if (symbol == null) return;
        var owner = Window.GetWindow(this);
        string? replacement = UI.Prompt.Show(owner, "Rename Symbol", $"Rename '{symbol}' everywhere in this script:", symbol);
        if (string.IsNullOrWhiteSpace(replacement) || replacement == symbol) return;
        if (!Regex.IsMatch(replacement, @"^[A-Za-z_]\w*$"))
        {
            MessageBox.Show(owner, "A C# identifier must start with a letter or underscore and contain only letters, digits and underscores.", "GRAB ENGINE");
            return;
        }
        string updated = Regex.Replace(Editor.Text, @"\b" + Regex.Escape(symbol) + @"\b", replacement);
        Editor.BeginChange();
        Editor.Document.Text = updated;
        Editor.EndChange();
        StatusText.Text = $"Renamed {symbol} → {replacement}";
    }

    private void FormatDocument()
    {
        string[] lines = Editor.Text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int depth = 0;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0) { sb.Append('\n'); continue; }
            if (trimmed.StartsWith("#")) { sb.Append(trimmed).Append('\n'); continue; }
            int leadingClosers = 0;
            foreach (char c in trimmed)
            {
                if (c is '}' or ')' or ']') leadingClosers++;
                else if (!char.IsWhiteSpace(c)) break;
            }
            int indentDepth = Math.Max(0, depth - leadingClosers);
            if (trimmed.StartsWith("case ") || trimmed.StartsWith("default:")) indentDepth = Math.Max(0, indentDepth - 1);
            sb.Append(new string(' ', indentDepth * 4)).Append(trimmed).Append('\n');
            int opens = 0, closes = 0;
            bool inString = false; char quote = '\0'; bool inLineComment = false; bool inBlockComment = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i], n = i + 1 < trimmed.Length ? trimmed[i + 1] : '\0';
                if (inLineComment) break;
                if (inBlockComment) { if (c == '*' && n == '/') { inBlockComment = false; i++; } continue; }
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) inString = false;
                    continue;
                }
                if (c == '/' && n == '/') { inLineComment = true; continue; }
                if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
                if (c is '"' or '\'') { inString = true; quote = c; continue; }
                if (c == '{') opens++; else if (c == '}') closes++;
            }
            depth = Math.Max(0, depth + opens - closes);
        }
        string result = sb.ToString().TrimEnd('\n') + "\n";
        int caretLine = Editor.TextArea.Caret.Line;
        Editor.BeginChange();
        Editor.Document.Text = result;
        Editor.EndChange();
        ScrollToLine(Math.Min(caretLine, Editor.Document.LineCount));
        StatusText.Text = "Formatted document";
    }

    private string? SymbolAtCaret()
    {
        int caret = Editor.CaretOffset;
        int start = caret, end = caret;
        while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
        while (end < Editor.Document.TextLength && (char.IsLetterOrDigit(Editor.Document.GetCharAt(end)) || Editor.Document.GetCharAt(end) == '_')) end++;
        if (end <= start) return null;
        string word = Editor.Document.GetText(start, end - start);
        Editor.Select(start, end - start);
        Editor.CaretOffset = end;
        return word;
    }

    private int FindDeclarationLine(string symbol)
    {
        string escaped = Regex.Escape(symbol);
        string[] patterns =
        {
            $@"\b(?:class|struct|interface|enum|record)\s+{escaped}\b",
            $@"\b[A-Za-z_][\w<>\[\],.?]*\s+{escaped}\s*(?:=|;|\(|\{{)",
            $@"\b(?:const|static|readonly|public|private|protected|internal)\s+[A-Za-z_][\w<>\[\],.?]*\s+{escaped}\b"
        };
        foreach (string pattern in patterns)
        {
            var m = Regex.Match(Editor.Text, pattern);
            if (m.Success) return Editor.Document.GetLineByOffset(m.Index).LineNumber;
        }
        return -1;
    }

    private sealed record ReferenceItem(int Line, string Text);

    private static IHighlightingDefinition LoadDarkCSharp()
    {
        const string xshd = """
<SyntaxDefinition name="C# Dark" extensions=".cs" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#6A9955" fontStyle="italic" />
  <Color name="String" foreground="#CE9178" />
  <Color name="Char" foreground="#CE9178" />
  <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
  <Color name="Type" foreground="#4EC9B0" />
  <Color name="Number" foreground="#B5CEA8" />
  <Color name="Preprocessor" foreground="#C586C0" />
  <Color name="Method" foreground="#DCDCAA" />
  <RuleSet>
    <Span color="Comment" begin="//" />
    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
    <Span color="String" begin="&quot;" end="&quot;" />
    <Span color="String" begin="@&quot;" end="&quot;" />
    <Span color="Char" begin="'" end="'" />
    <Rule color="Preprocessor">^\s*\#.*$</Rule>
    <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+(\.\d+)?[fFdD]?\b</Rule>
    <Keywords color="Keyword">
      <Word>abstract</Word><Word>as</Word><Word>base</Word><Word>break</Word><Word>case</Word><Word>catch</Word>
      <Word>class</Word><Word>const</Word><Word>continue</Word><Word>default</Word><Word>do</Word><Word>else</Word>
      <Word>enum</Word><Word>false</Word><Word>finally</Word><Word>for</Word><Word>foreach</Word><Word>goto</Word>
      <Word>if</Word><Word>in</Word><Word>interface</Word><Word>is</Word><Word>lock</Word><Word>namespace</Word>
      <Word>new</Word><Word>null</Word><Word>operator</Word><Word>out</Word><Word>override</Word><Word>params</Word>
      <Word>private</Word><Word>protected</Word><Word>public</Word><Word>readonly</Word><Word>ref</Word><Word>return</Word>
      <Word>sealed</Word><Word>static</Word><Word>struct</Word><Word>switch</Word><Word>this</Word><Word>throw</Word>
      <Word>true</Word><Word>try</Word><Word>using</Word><Word>var</Word><Word>virtual</Word><Word>void</Word><Word>while</Word>
      <Word>async</Word><Word>await</Word><Word>yield</Word><Word>readonly</Word><Word>partial</Word>
    </Keywords>
    <Keywords color="Type">
      <Word>float</Word><Word>double</Word><Word>int</Word><Word>uint</Word><Word>long</Word><Word>short</Word>
      <Word>byte</Word><Word>bool</Word><Word>string</Word><Word>char</Word><Word>object</Word><Word>Vector3</Word>
      <Word>Color</Word><Word>GameObject</Word><Word>Trigger</Word><Word>Light</Word><Word>Sign</Word>
      <Word>PhysicsBlock</Word><Word>ParticleEmitter</Word><Word>Sound</Word><Word>GravityBlock</Word><Word>Player</Word><Word>Time</Word><Word>Level</Word>      <Word>Mathf</Word><Word>Math</Word><Word>Array</Word><Word>Convert</Word><Word>Console</Word><Word>Animator</Word>
      <Word>List</Word><Word>Dictionary</Word><Word>KeyValuePair</Word><Word>IEnumerator</Word><Word>Task</Word><Word>WaitForSeconds</Word><Word>Exception</Word>
    </Keywords>
    <Rule color="Method">\b[A-Za-z_]\w*(?=\s*\()</Rule>
  </RuleSet>
</SyntaxDefinition>
""";
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}

public sealed class Suggestion : ICompletionData
{
    private static readonly Brush KeywordBrush = Freeze(Color.FromRgb(86, 156, 214));
    private static readonly Brush TypeBrush = Freeze(Color.FromRgb(78, 201, 176));
    private static readonly Brush MethodBrush = Freeze(Color.FromRgb(200, 180, 110));
    private static readonly Brush FieldBrush = Freeze(Color.FromRgb(130, 190, 240));
    private static readonly Brush ObjectBrush = Freeze(Color.FromRgb(190, 130, 200));
    private static readonly Brush PropertyBrush = Freeze(Color.FromRgb(120, 160, 200));
    private static readonly Brush StringBrush = Freeze(Color.FromRgb(200, 130, 110));
    private static readonly Brush SnippetBrush = Freeze(Color.FromRgb(150, 110, 200));

    public Suggestion(string text, string description, string group, string? insert = null, int caretBack = 0)
    {
        Text = text;
        Description = description;
        Group = group;
        InsertText = insert ?? text;
        CaretBack = caretBack;
    }

    public string Text { get; }
    public string Description { get; }
    object ICompletionData.Description => Description;
    public string Group { get; }
    public string InsertText { get; }
    public int CaretBack { get; }
    public System.Windows.Media.ImageSource? Image => null;
    public object Content => Text;

    public string Glyph => Group switch
    {
        "keyword" => "K", "type" => "T", "method" => "M", "field" => "F",
        "object" => "O", "property" => "P", "string" => "S", "snippet" => "{}", _ => "•"
    };

    public Brush Accent => Group switch
    {
        "keyword" => KeywordBrush, "type" => TypeBrush, "method" => MethodBrush,
        "field" => FieldBrush, "object" => ObjectBrush, "string" => StringBrush,
        "property" => PropertyBrush, "snippet" => SnippetBrush, _ => PropertyBrush
    };

    public double Priority => Group switch
    {
        "field" => 0, "type" => 1, "object" => 2, "property" => 3,
        "method" => 4, "string" => 5, "snippet" => 7, _ => 6
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, InsertText);
        if (CaretBack > 0)
            textArea.Caret.Offset = Math.Max(0, textArea.Caret.Offset - CaretBack);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
