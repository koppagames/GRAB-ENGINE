using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GrabEngine.Core;

namespace GrabEngine.UI;

/// <summary>
/// Unity-style animation timeline: ruler, keyframe diamonds, playhead and direct
/// dragging. Drawing is done in OnRender so scrubbing stays smooth even with long clips.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double PadX = 16;
    private const double TrackY = 52;
    private const double Diamond = 5.5;

    private GrabAnimation? _clip;
    private float _time;
    private int _selectedFrame = -1;
    private bool _scrubbing;
    private bool _dragging;
    private int _dragFrame = -1;

    public GrabAnimation? Clip
    {
        get => _clip;
        set { _clip = value; InvalidateVisual(); }
    }

    public float Time
    {
        get => _time;
        set { _time = MathF.Max(0, value); InvalidateVisual(); }
    }

    public int SelectedFrame
    {
        get => _selectedFrame;
        set { _selectedFrame = value; InvalidateVisual(); }
    }

    public event Action<float>? Scrub;
    public event Action<int>? FrameSelected;
    public event Action<int, float>? FrameMoved;
    public event Action<int>? DeleteFrame;

    public TimelineControl()
    {
        Height = 104;
        MinHeight = 86;
        Focusable = true;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    private float Duration
    {
        get
        {
            if (_clip == null || _clip.Frames.Count == 0) return 1f;
            return MathF.Max(1f, _clip.Frames.Max(f => f.Time) + 0.5f);
        }
    }

    private double X(float time)
    {
        double width = Math.Max(1, ActualWidth - PadX * 2);
        return PadX + Math.Clamp(time / Duration, 0, 1) * width;
    }

    private float T(double x)
    {
        double width = Math.Max(1, ActualWidth - PadX * 2);
        return (float)Math.Clamp((x - PadX) / width, 0, 1) * Duration;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(26, 26, 26)), null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
        if (_clip == null)
        {
            DrawText(dc, "Select or create an animation clip", new Point(PadX, 36), 11.5, Color.FromRgb(130, 130, 130));
            return;
        }

        double width = Math.Max(1, ActualWidth - PadX * 2);
        var trackBrush = new SolidColorBrush(Color.FromRgb(52, 52, 52));
        dc.DrawRectangle(trackBrush, null, new Rect(PadX, TrackY - 1, width, 2));

        // Ruler ticks. Keep labels readable at any zoom.
        float duration = Duration;
        float step = duration <= 2f ? 0.25f : duration <= 5f ? 0.5f : duration <= 12f ? 1f : 2f;
        for (float t = 0; t <= duration + 0.001f; t += step)
        {
            double x = X(t);
            bool major = Math.Abs(t / (step * 2) - MathF.Round(t / (step * 2))) < 0.01f;
            dc.DrawLine(new Pen(new SolidColorBrush(major ? Color.FromRgb(92, 92, 92) : Color.FromRgb(62, 62, 62)), 1),
                new Point(x, major ? 20 : 26), new Point(x, TrackY + 14));
            if (major)
                DrawText(dc, t.ToString("0.##", CultureInfo.InvariantCulture) + "s", new Point(x - 10, 6), 9.5, Color.FromRgb(150, 150, 150));
        }

        // Keyframes.
        for (int i = 0; i < _clip.Frames.Count; i++)
        {
            double x = X(_clip.Frames[i].Time);
            bool selected = i == _selectedFrame;
            var brush = selected ? new SolidColorBrush(Color.FromRgb(255, 196, 84)) : new SolidColorBrush(Color.FromRgb(118, 186, 240));
            var outline = new Pen(selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(22, 22, 22)), selected ? 1.4 : 1);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x, TrackY - Diamond), true, true);
                ctx.LineTo(new Point(x + Diamond, TrackY), true, false);
                ctx.LineTo(new Point(x, TrackY + Diamond), true, false);
                ctx.LineTo(new Point(x - Diamond, TrackY), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(brush, outline, geometry);
        }

        // Playhead.
        double px = X(_time);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(255, 110, 96)), 1.6), new Point(px, 18), new Point(px, ActualHeight - 12));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(px - 5, 14), true, true);
            ctx.LineTo(new Point(px + 5, 14), true, false);
            ctx.LineTo(new Point(px, 21), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 110, 96)), null, head);
        DrawText(dc, _time.ToString("0.00", CultureInfo.InvariantCulture) + "s", new Point(px + 7, 24), 9.5, Color.FromRgb(230, 150, 140));
    }

    private void DrawText(DrawingContext dc, string text, Point at, double size, Color color)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    private int HitFrame(double x)
    {
        if (_clip == null) return -1;
        for (int i = 0; i < _clip.Frames.Count; i++)
            if (Math.Abs(X(_clip.Frames[i].Time) - x) <= Diamond + 3) return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        int hit = HitFrame(p.X);
        if (hit >= 0)
        {
            _selectedFrame = hit;
            FrameSelected?.Invoke(hit);
            _dragging = true;
            _dragFrame = hit;
        }
        else
        {
            _scrubbing = true;
            Scrub?.Invoke(T(p.X));
        }
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (_dragging && _dragFrame >= 0)
        {
            float t = MathF.Round(T(p.X) * 100f) / 100f;
            FrameMoved?.Invoke(_dragFrame, MathF.Max(0, t));
        }
        else if (_scrubbing)
        {
            Scrub?.Invoke(T(p.X));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _scrubbing = false;
        _dragging = false;
        _dragFrame = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int hit = HitFrame(e.GetPosition(this).X);
        if (hit >= 0)
        {
            _selectedFrame = hit;
            FrameSelected?.Invoke(hit);
            DeleteFrame?.Invoke(hit);
        }
    }
}
