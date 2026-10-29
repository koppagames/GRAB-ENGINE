using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace GrabEngine.Controls;

/// <summary>Unity/Visual Studio style breakpoint gutter with clickable red dots.</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private readonly HashSet<int> _lines = new();
    private static readonly Brush BreakpointBrush;
    private static readonly Brush HoverBrush;

    static BreakpointMargin()
    {
        BreakpointBrush = new SolidColorBrush(Color.FromRgb(228, 62, 62));
        HoverBrush = new SolidColorBrush(Color.FromArgb(90, 255, 90, 90));
        BreakpointBrush.Freeze();
        HoverBrush.Freeze();
    }

    public event Action<int>? LineClicked;

    public BreakpointMargin()
    {
        Width = 15;
        Cursor = Cursors.Hand;
        ToolTip = "Click to toggle a breakpoint (F9)";
        MouseLeftButtonDown += (_, e) =>
        {
            int line = LineAt(e.GetPosition(this).Y);
            if (line > 0)
            {
                LineClicked?.Invoke(line);
                e.Handled = true;
            }
        };
    }

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _lines.Clear();
        foreach (var line in lines) _lines.Add(line);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(15, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.ScrollOffsetChanged -= OnScrollOffsetChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.ScrollOffsetChanged += OnScrollOffsetChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void OnScrollOffsetChanged(object? sender, EventArgs e) => InvalidateVisual();

    private int LineAt(double y)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid) return 0;
        foreach (var line in textView.VisualLines)
        {
            double top = line.VisualTop - textView.VerticalOffset;
            if (y >= top && y < top + line.Height) return line.FirstDocumentLine.LineNumber;
        }
        return 0;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid) return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        pen.Freeze();
        foreach (var line in textView.VisualLines)
        {
            double top = line.VisualTop - textView.VerticalOffset;
            bool active = _lines.Contains(line.FirstDocumentLine.LineNumber);
            if (!active) continue;
            double cy = top + line.Height / 2;
            var center = new Point(ActualWidth / 2, cy);
            drawingContext.DrawEllipse(BreakpointBrush, null, center, 5.2, 5.2);
        }
    }
}
