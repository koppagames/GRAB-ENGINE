using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace GrabEngine.UI;

/// <summary>
/// In-editor documentation browser. Help → C# Scripting Reference / GASM Reference
/// opens this window instead of launching Notepad, so the keyboard shortcuts, search
/// and heading navigation keep working while the level stays open behind it.
/// </summary>
public sealed class DocsWindow : Window
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CSharpReference.md"] = "C# Scripting Reference",
        ["GASMReference.md"] = "GASM Reference",
        ["README.md"] = "GRAB ENGINE Guide"
    };

    private readonly ListBox _tabs;
    private readonly ListBox _headings;
    private readonly RichTextBox _content;
    private readonly TextBlock _status;
    private readonly TextBox _search;
    private readonly List<(string Title, string File)> _documents = new();
    private readonly Dictionary<string, List<(int Level, string Title)>> _headingCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Paragraph> _anchors = new();
    private string? _currentFile;

    public DocsWindow(Window owner)
    {
        Title = "GRAB ENGINE Documentation";
        Width = 1080;
        Height = 740;
        MinWidth = 640;
        MinHeight = 420;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Panel;
        Foreground = Theme.Text;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;

        foreach (var name in new[] { "CSharpReference.md", "GASMReference.md", "README.md" })
            if (FindDocument(name) != null) _documents.Add((Titles[name], name));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(14, 9, 14, 8) };
        var close = Theme.Button("Close", small: true);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var openExternal = Theme.Button("Open in Notepad", small: true);
        openExternal.Margin = new Thickness(0, 0, 6, 0);
        openExternal.Click += (_, _) => OpenExternal();
        DockPanel.SetDock(openExternal, Dock.Right);
        header.Children.Add(openExternal);
        header.Children.Add(new TextBlock { Text = "DOCUMENTATION", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(Theme.AccentBar(header));

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 180 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)) };
        var searchHost = new Border { Padding = new Thickness(8, 8, 8, 4) };
        _search = Theme.TextBox();
        _search.ToolTip = "Filter headings";
        _search.TextChanged += (_, _) => RefreshHeadingList();
        searchHost.Child = _search;
        DockPanel.SetDock(searchHost, Dock.Top);
        left.Children.Add(searchHost);

        _tabs = new ListBox { BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 8) };
        foreach (var doc in _documents) _tabs.Items.Add(doc.Title);
        _tabs.SelectionChanged += (_, _) => { if (_tabs.SelectedIndex >= 0) LoadDocument(_documents[_tabs.SelectedIndex].File); };
        DockPanel.SetDock(_tabs, Dock.Top);
        left.Children.Add(_tabs);

        var headingBorder = new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 4, 4, 6) };
        var headingPanel = new DockPanel();
        var headingLabel = new TextBlock { Text = "ON THIS PAGE", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Dim, Margin = new Thickness(6, 4, 0, 4) };
        DockPanel.SetDock(headingLabel, Dock.Top);
        headingPanel.Children.Add(headingLabel);
        _headings = new ListBox { BorderThickness = new Thickness(0), FontSize = 11 };
        _headings.SelectionChanged += (_, _) => ScrollToHeading(_headings.SelectedIndex);
        headingPanel.Children.Add(_headings);
        headingBorder.Child = headingPanel;
        left.Children.Add(headingBorder);

        split.Children.Add(left);

        _content = new RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 16, 22, 30),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            IsDocumentEnabled = true
        };
        _content.Document = new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = 900, FontFamily = new FontFamily("Segoe UI") };
        _content.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) =>
        {
            if (e.Uri != null) OpenLink(e.Uri);
        }));
        Grid.SetColumn(_content, 1);
        split.Children.Add(_content);
        Grid.SetRow(split, 1);
        root.Children.Add(split);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 37)),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 5, 12, 5)
        };
        _status = new TextBlock { FontSize = 10.5, Foreground = Theme.Dim };
        footer.Child = _status;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;

        if (_documents.Count > 0)
        {
            _tabs.SelectedIndex = 0;
        }
        else
        {
            _status.Text = "Documentation files were not found next to GRAB ENGINE.";
            ShowMissing();
        }
    }

    public void ShowDocument(string file)
    {
        int index = _documents.FindIndex(d => string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _tabs.SelectedIndex = index;
        else LoadDocument(file);
    }

    // ---------------- file lookup ----------------

    public static string? FindDocument(string file)
    {
        var candidates = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
                {
                    candidates.Add(Path.Combine(dir.FullName, file));
                    candidates.Add(Path.Combine(dir.FullName, "docs", file));
                }
            }
            catch { }
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                var dir = Path.GetDirectoryName(candidate);
                if (dir == null || !Directory.Exists(dir)) continue;
                var match = Directory.EnumerateFiles(dir, "*.md")
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                if (match != null) return Path.GetFullPath(match);
            }
            catch { }
        }
        return null;
    }

    private void OpenExternal()
    {
        if (_currentFile == null) return;
        try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _currentFile + "\"") { UseShellExecute = false }); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open Notepad:\n\n" + ex.Message, "Documentation", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenLink(Uri uri)
    {
        if (uri.IsAbsoluteUri && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
            return;
        }
        // Links between markdown files open inside this window.
        string name = Path.GetFileName(uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('#')[0]);
        if (!string.IsNullOrWhiteSpace(name) && FindDocument(name) != null)
        {
            int index = _documents.FindIndex(d => string.Equals(d.File, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) _documents.Add((Titles.TryGetValue(name, out var t) ? t : name, name));
            _tabs.Items.Refresh();
            _tabs.SelectedIndex = _documents.FindIndex(d => string.Equals(d.File, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ShowMissing()
    {
        _content.Document = new FlowDocument();
        _content.Document.Blocks.Add(new Paragraph(new Run("Documentation not found.\n\nLook for CSharpReference.md, GASMReference.md and README.md next to GRAB ENGINE.exe, or in the project folder."))
        {
            Foreground = Theme.Warning,
            FontSize = 13
        });
    }

    // ---------------- loading ----------------

    private void LoadDocument(string file)
    {
        if (string.Equals(file, _currentFile, StringComparison.OrdinalIgnoreCase)) return;
        var path = FindDocument(file);
        if (path == null) { ShowMissing(); return; }
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            _status.Text = "Could not read " + file + ": " + ex.Message;
            ShowMissing();
            return;
        }
        _currentFile = path;
        _status.Text = path;
        _content.Document = RenderMarkdown(text);
        _content.ScrollToHome();
        RefreshHeadingList();
    }

    private void RefreshHeadingList()
    {
        if (_currentFile == null) return;
        string key = _currentFile;
        if (!_headingCache.TryGetValue(key, out var headings))
        {
            headings = new List<(int, string)>();
            _headingCache[key] = headings;
        }
        string filter = _search.Text.Trim();
        _headings.Items.Clear();
        _headingIndices.Clear();
        for (int i = 0; i < _anchors.Count; i++)
        {
            var p = _anchors[i];
            string title = AnchorTitle(p);
            int level = p.Tag is int l ? l : 0;
            if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _headings.Items.Add(new TextBlock
            {
                Text = (level >= 3 ? "   " : "") + title,
                Foreground = level <= 1 ? Theme.Text : new SolidColorBrush(Color.FromRgb(178, 190, 202)),
                FontWeight = level <= 1 ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = title
            });
            _headingIndices.Add(i);
        }
    }

    private readonly List<int> _headingIndices = new();

    private static string AnchorTitle(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is Run run) sb.Append(run.Text);
            else if (inline is Hyperlink link) sb.Append(link.Inlines.OfType<Run>().FirstOrDefault()?.Text);
        }
        return sb.ToString().Trim();
    }

    private void ScrollToHeading(int listIndex)
    {
        if (listIndex < 0 || listIndex >= _headingIndices.Count) return;
        int index = _headingIndices[listIndex];
        if (index < 0 || index >= _anchors.Count) return;
        var p = _anchors[index];
        try
        {
            var rect = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            double target = _content.VerticalOffset + rect.Top - 8;
            _content.ScrollToVerticalOffset(Math.Max(0, target));
        }
        catch { }
    }

    // ---------------- markdown ----------------

    private FlowDocument RenderMarkdown(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = 900,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
            LineHeight = 19
        };
        _anchors.Clear();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        var quote = new StringBuilder();
        var list = new List();
        bool inCode = false;
        var code = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            var p = BodyParagraph();
            AddInline(p, paragraph.ToString().Trim());
            doc.Blocks.Add(p);
            paragraph.Clear();
        }
        void FlushList()
        {
            if (list.ListItems.Count > 0) { doc.Blocks.Add(list); list = new List { Margin = new Thickness(20, 2, 0, 6) }; }
        }
        void FlushQuote()
        {
            if (quote.Length == 0) return;
            var p = BodyParagraph();
            p.BorderBrush = Theme.Accent;
            p.BorderThickness = new Thickness(3, 0, 0, 0);
            p.Padding = new Thickness(10, 3, 0, 3);
            p.Margin = new Thickness(0, 5, 0, 9);
            AddInline(p, quote.ToString());
            doc.Blocks.Add(p);
            quote.Clear();
        }
        void FlushCode()
        {
            if (code.Length == 0) return;
            var p = new Paragraph
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11.5,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 4, 0, 9),
                LineHeight = 17
            };
            p.Inlines.Add(new Run(code.ToString().TrimEnd('\n')) { Foreground = new SolidColorBrush(Color.FromRgb(206, 206, 206)) });
            doc.Blocks.Add(p);
            code.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode) { FlushCode(); inCode = false; }
                else { FlushParagraph(); FlushList(); FlushQuote(); inCode = true; }
                continue;
            }
            if (inCode) { code.Append(line).Append('\n'); continue; }

            string trimmed = line.Trim();
            if (trimmed.Length == 0) { FlushParagraph(); FlushList(); FlushQuote(); continue; }
            if (!trimmed.StartsWith(">", StringComparison.Ordinal)) FlushQuote();

            if (trimmed.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph(); FlushList();
                int end = i;
                while (end + 1 < lines.Length && lines[end + 1].TrimStart().StartsWith("|", StringComparison.Ordinal)) end++;
                var rows = new List<string[]>();
                for (int r = i; r <= end; r++)
                {
                    if (IsTableSeparator(lines[r])) continue;
                    rows.Add(SplitTableRow(lines[r]));
                }
                doc.Blocks.Add(BuildTable(rows));
                i = end;
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                FlushParagraph(); FlushList();
                int level = trimmed.TakeWhile(c => c == '#').Count();
                string title = trimmed[level..].Trim();
                var p = new Paragraph
                {
                    FontSize = level switch { 1 => 22, 2 => 17, 3 => 14, _ => 12.5 },
                    FontWeight = level <= 2 ? FontWeights.SemiBold : FontWeights.Bold,
                    Foreground = level == 1 ? Brushes.White : level == 2 ? new SolidColorBrush(Color.FromRgb(126, 184, 232)) : new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                    Margin = new Thickness(0, level == 1 ? 2 : 16, 0, level == 1 ? 4 : 6),
                    Tag = level
                };
                p.Inlines.Add(new Run(title));
                if (level == 1)
                {
                    p.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58));
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.Padding = new Thickness(0, 0, 0, 8);
                }
                _anchors.Add(p);
                doc.Blocks.Add(p);
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph(); FlushList();
                doc.Blocks.Add(new Paragraph { BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) });
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph(); FlushList();
                if (quote.Length > 0) quote.Append(' ');
                quote.Append(trimmed.TrimStart('>').Trim());
                continue;
            }

            var bullet = Regex.Match(trimmed, @"^([-*+])\s+(.*)$");
            if (bullet.Success)
            {
                FlushParagraph();
                var item = new ListItem(BodyParagraph());
                AddInline((Paragraph)item.Blocks.FirstBlock, bullet.Groups[2].Value);
                list.ListItems.Add(item);
                continue;
            }

            var numbered = Regex.Match(trimmed, @"^(\d+)[.)]\s+(.*)$");
            if (numbered.Success)
            {
                FlushParagraph();
                if (list.MarkerStyle != TextMarkerStyle.Decimal) FlushList();
                list.MarkerStyle = TextMarkerStyle.Decimal;
                var item = new ListItem(BodyParagraph());
                AddInline((Paragraph)item.Blocks.FirstBlock, numbered.Groups[2].Value);
                list.ListItems.Add(item);
                continue;
            }

            FlushList();
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        FlushParagraph();
        FlushList();
        FlushQuote();
        if (inCode) FlushCode();
        return doc;
    }

    private static Paragraph BodyParagraph() => new()
    {
        Margin = new Thickness(0, 2, 0, 7),
        LineHeight = 19
    };

    private static bool IsTableSeparator(string line)
    {
        string t = line.Trim();
        return t.StartsWith("|", StringComparison.Ordinal) && t.Contains('-') && t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0;
    }

    private static string[] SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(s => s.Trim()).ToArray();

    private static Table BuildTable(List<string[]> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        int columns = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            row.Background = r == 0 ? new SolidColorBrush(Color.FromRgb(52, 52, 52)) : r % 2 == 0 ? new SolidColorBrush(Color.FromRgb(38, 38, 38)) : Brushes.Transparent;
            for (int c = 0; c < columns; c++)
            {
                var cell = new TableCell { Padding = new Thickness(7, 3, 7, 3), BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 62)), BorderThickness = new Thickness(0, 0, 0, 1) };
                var p = new Paragraph { Margin = new Thickness(0), FontSize = 11.5, LineHeight = 16 };
                if (r == 0) p.FontWeight = FontWeights.SemiBold;
                if (c < rows[r].Length) AddInline(p, rows[r][c]);
                cell.Blocks.Add(p);
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddInline(Paragraph p, string text)
    {
        var pattern = new Regex(@"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|\*[^*]+\*)");
        int last = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index > last) p.Inlines.Add(new Run(text[last..m.Index]));
            string token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
                p.Inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith("`", StringComparison.Ordinal))
                p.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(47, 47, 47)),
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 170)),
                    FontSize = 11.5
                });
            else if (token.StartsWith("[", StringComparison.Ordinal))
            {
                int split = token.IndexOf("](", StringComparison.Ordinal);
                string label = token[1..split];
                string url = token[(split + 2)..^1];
                var link = new Hyperlink(new Run(label))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(112, 176, 232)),
                    TextDecorations = null,
                    ToolTip = url
                };
                try { link.NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute); } catch { }
                p.Inlines.Add(link);
            }
            else if (token.StartsWith("*", StringComparison.Ordinal))
                p.Inlines.Add(new Italic(new Run(token[1..^1])));
            last = m.Index + m.Length;
        }
        if (last < text.Length) p.Inlines.Add(new Run(text[last..]));
    }
}
