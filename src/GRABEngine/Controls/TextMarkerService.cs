using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using GrabEngine.Core;

namespace GrabEngine.Controls;

public sealed class TextMarker : TextSegment
{
    public string Message { get; set; } = "";
    public DiagnosticSeverity Severity { get; set; }
}

public sealed class TextMarkerService : IBackgroundRenderer
{
    private readonly TextSegmentCollection<TextMarker> _markers;
    private readonly TextView _textView;
    private int _executionLine;
    private static readonly Brush ExecutionBrush = new SolidColorBrush(Color.FromArgb(42, 255, 218, 92));

    public TextMarkerService(TextEditor editor)
    {
        _textView = editor.TextArea.TextView;
        _markers = new TextSegmentCollection<TextMarker>(editor.Document);
        _textView.BackgroundRenderers.Add(this);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    static TextMarkerService()
    {
        ExecutionBrush.Freeze();
    }

    public void Clear() => _markers.Clear();

    public void SetExecutionLine(int line)
    {
        if (_executionLine == line) return;
        _executionLine = line;
        _textView.InvalidateVisual();
    }

    public void Create(int offset, int length, string message, DiagnosticSeverity severity)
    {
        if (offset < 0 || offset > _textView.Document.TextLength) return;
        length = Math.Clamp(length, 1, Math.Max(1, _textView.Document.TextLength - offset));
        _markers.Add(new TextMarker { StartOffset = offset, Length = length, Message = message, Severity = severity });
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_markers == null || !textView.VisualLinesValid) return;
        if (_executionLine > 0 && textView.Document != null && _executionLine <= textView.Document.LineCount)
        {
            var line = textView.Document.GetLineByNumber(_executionLine);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line))
                drawingContext.DrawRectangle(ExecutionBrush, null, new Rect(0, rect.Top, textView.ActualWidth, rect.Height));
        }
        foreach (var marker in _markers.FindOverlappingSegments(textView.VisualLines[0].FirstDocumentLine.Offset,
                     textView.VisualLines[^1].LastDocumentLine.EndOffset))
        {
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                var pen = new Pen(new SolidColorBrush(marker.Severity == DiagnosticSeverity.Error ? Color.FromRgb(244, 82, 82) : Color.FromRgb(226, 192, 68)), 1);
                pen.Freeze();
                double y = rect.Bottom - 2;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(rect.Left, y), false, false);
                    bool up = true;
                    for (double x = rect.Left + 2; x <= rect.Right; x += 2)
                    {
                        ctx.LineTo(new Point(x, y + (up ? -2 : 1)), true, false);
                        up = !up;
                    }
                }
                geo.Freeze();
                drawingContext.DrawGeometry(null, pen, geo);
            }
        }
    }
}
