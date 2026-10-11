using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shoji.UI;

/// <summary>
/// WPF-style drawing calls on Avalonia, so the gauges, backdrops and charts drawn for Windows run unchanged in
/// spirit: Push…/Pop pairs (Avalonia uses disposable states), figures closed per BeginFigure, and the gradient
/// and text helpers WPF has built in.
/// </summary>
public static class DrawCompat
{
    static readonly ConditionalWeakTable<DrawingContext, Stack<DrawingContext.PushedState>> States = new();

    static Stack<DrawingContext.PushedState> StackOf(DrawingContext dc) => States.GetOrCreateValue(dc);

    public static void PushTransformW(this DrawingContext dc, Transform t) => StackOf(dc).Push(dc.PushTransform(t.Value));
    public static void PushTransformW(this DrawingContext dc, Matrix m) => StackOf(dc).Push(dc.PushTransform(m));
    public static void PushOpacityW(this DrawingContext dc, double opacity) => StackOf(dc).Push(dc.PushOpacity(opacity));
    public static void PushClipW(this DrawingContext dc, Rect r) => StackOf(dc).Push(dc.PushClip(r));
    public static void PushClipW(this DrawingContext dc, Geometry g) => StackOf(dc).Push(dc.PushGeometryClip(g));

    public static void PopW(this DrawingContext dc)
    {
        if (StackOf(dc).TryPop(out var state))
            state.Dispose();
    }

    /// <summary>How many Push…W states are open on this context (see <see cref="DrawnControl"/>).</summary>
    public static int DepthW(this DrawingContext dc) => StackOf(dc).Count;

    /// <summary>Pops whatever a Render left pushed (an early return or an exception), back to <paramref name="depth"/>.</summary>
    public static void UnwindW(this DrawingContext dc, int depth)
    {
        var stack = StackOf(dc);
        while (stack.Count > depth)
        {
            try { stack.Pop().Dispose(); }
            catch { stack.Clear(); } // the context already dropped them
        }
    }

    public static Transform Rotate(double angle, double cx, double cy) => new MatrixTransform(
        Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(angle * Math.PI / 180) * Matrix.CreateTranslation(cx, cy));

    public static Transform Skew(double angleX, double angleY, double cx, double cy) => new MatrixTransform(
        Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateSkew(angleX * Math.PI / 180, angleY * Math.PI / 180) * Matrix.CreateTranslation(cx, cy));

    public static Transform Translate(double x, double y) => new MatrixTransform(Matrix.CreateTranslation(x, y));

    public static Transform Scale(double sx, double sy, double cx = 0, double cy = 0) => new MatrixTransform(
        Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(cx, cy));

    /// <summary>WPF's LinearGradientBrush(start, end, angle): angle 0 = left to right, 90 = top to bottom.</summary>
    public static IBrush Linear(Color start, Color end, double angle)
    {
        double a = angle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        // Same end points WPF uses for an angle in a unit box.
        var s = new Point(0.5 - dx / 2, 0.5 - dy / 2);
        var e = new Point(0.5 + dx / 2, 0.5 + dy / 2);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(s, RelativeUnit.Relative), EndPoint = new RelativePoint(e, RelativeUnit.Relative),
            GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) },
        };
    }

    /// <summary>WPF's LinearGradientBrush(stops, angle).</summary>
    public static IBrush Linear(GradientStops stops, double angle)
    {
        var b = (LinearGradientBrush)Linear(Colors.Transparent, Colors.Transparent, angle);
        b.GradientStops = stops;
        return b;
    }

    public static IBrush Linear(Point start, Point end, bool absolute, params (Color Color, double Offset)[] stops)
    {
        var unit = absolute ? RelativeUnit.Absolute : RelativeUnit.Relative;
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(start, unit), EndPoint = new RelativePoint(end, unit) };
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(c, o));
        return b;
    }

    public static IBrush Radial(Color center, Color edge) => new RadialGradientBrush
    {
        GradientStops = { new GradientStop(center, 0), new GradientStop(edge, 1) },
    };

    public static IBrush Radial(params (Color Color, double Offset)[] stops)
    {
        var b = new RadialGradientBrush();
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(c, o));
        return b;
    }

    public static Geometry Ellipse(Point center, double rx, double ry) => new EllipseGeometry(new Rect(center.X - rx, center.Y - ry, rx * 2, ry * 2));

    public static Pen Pen(IBrush brush, double thickness, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter, IDashStyle? dash = null) =>
        new(brush, thickness, dash, cap, join);

    public static FormattedText Text(string s, double size, IBrush brush, FontFamily font, FontWeight weight = FontWeight.Normal, FontStyle style = FontStyle.Normal) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font, style, weight), Math.Max(1, size), brush);
}

/// <summary>
/// Base for the controls that draw themselves. Avalonia renders every visual with one shared DrawingContext, and an
/// exception thrown from a Render stops the window from ever repainting (the dashboard freezes). So the drawing goes
/// in <see cref="Draw"/>: a failure is logged once per type and only that control is left blank, and Push…W states
/// a Draw leaves open are popped so they can't pile up on the shared context.
/// </summary>
public abstract class DrawnControl : Control
{
    static readonly HashSet<Type> Logged = new();

    protected abstract void Draw(DrawingContext dc);

    public sealed override void Render(DrawingContext dc)
    {
        int depth = dc.DepthW();
        try
        {
            Draw(dc);
        }
        catch (Exception ex)
        {
            if (Logged.Add(GetType()))
                Shoji.Core.Log.Write($"{GetType().Name} failed to draw: {ex}");
        }
        finally
        {
            dc.UnwindW(depth);
        }
    }
}

/// <summary>WPF's StreamGeometryContext calls (BeginFigure with isClosed) on Avalonia's, which closes with EndFigure.</summary>
public sealed class FigureWriter : IDisposable
{
    readonly StreamGeometryContext _ctx;
    bool _open, _closed;

    public FigureWriter(StreamGeometry g) => _ctx = g.Open();

    public void BeginFigure(Point p, bool isFilled, bool isClosed)
    {
        End();
        _ctx.BeginFigure(p, isFilled);
        _open = true;
        _closed = isClosed;
    }

    public void LineTo(Point p, bool isStroked = true, bool isSmoothJoin = false) => _ctx.LineTo(p);

    public void ArcTo(Point p, Size size, double rotation, bool isLargeArc, SweepDirection sweep, bool isStroked = true, bool isSmoothJoin = false) =>
        _ctx.ArcTo(p, size, rotation, isLargeArc, sweep);

    public void QuadraticBezierTo(Point control, Point end, bool isStroked = true, bool isSmoothJoin = false) => _ctx.QuadraticBezierTo(control, end);

    public void BezierTo(Point c1, Point c2, Point end, bool isStroked = true, bool isSmoothJoin = false) => _ctx.CubicBezierTo(c1, c2, end);

    void End()
    {
        if (_open)
            _ctx.EndFigure(_closed);
        _open = false;
    }

    public void Dispose()
    {
        End();
        _ctx.Dispose();
    }
}

public static class StreamGeometryExtensions
{
    /// <summary>Like WPF's StreamGeometry.Open(), returning a context with WPF's figure calls.</summary>
    public static FigureWriter OpenW(this StreamGeometry g) => new(g);
}

/// <summary>
/// A <see cref="DrawnControl"/> whose picture is static (full-screen backdrops and overlays): it is painted once into
/// a bitmap whenever the control is invalidated, and every later frame only copies that bitmap. Without this, the
/// software renderer redraws the big gradients each time anything above or below them moves (20 times a second on
/// an animated theme), which kept a whole CPU core busy.
/// </summary>
public abstract class CachedDrawnControl : DrawnControl
{
    Avalonia.Media.Imaging.RenderTargetBitmap? _cache;

    /// <summary>Paints the picture; false when there is nothing to show (no bitmap is kept then).</summary>
    protected abstract bool Paint(DrawingContext dc, double w, double h);

    /// <summary>How far the picture may reach outside the control on each side (e.g. a shadow).</summary>
    protected virtual double Bleed => 0;

    protected sealed override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height, bleed = Bleed;
        _cache?.Dispose(); // the compositor holds its own reference to a bitmap it is still showing
        _cache = null;
        if (w <= 0 || h <= 0)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var area = new Rect(-bleed, -bleed, w + 2 * bleed, h + 2 * bleed);
        var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale)), new Vector(96 * scale, 96 * scale));
        bool drawn;
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateTranslation(bleed, bleed)))
            drawn = Paint(ctx, w, h);
        if (!drawn)
        {
            bitmap.Dispose();
            return;
        }
        _cache = bitmap;
        dc.DrawImage(bitmap, area);
    }
}


/// <summary>
/// The one timer every moving thing on the dashboard draws from (backdrop motion, gauges easing, weather icons).
/// Sharing it means they all repaint in the same frame: separate timers each cost a full window repaint, and on a
/// desktop without GPU drawing every frame costs ~15–20 ms of CPU whatever changed in it.
/// </summary>
public static class FrameClock
{
    static readonly List<Action> Subscribers = new();
    static Avalonia.Threading.DispatcherTimer? _timer;

    /// <summary>Frames per second; ambient motion is slow, so this stays low.</summary>
    public const double Fps = 15;

    /// <summary>Nobody is looking (the session is idle or the window is minimized): no frames until it's cleared.</summary>
    public static bool Paused { get; set; }

    public static void Subscribe(Action frame)
    {
        if (Subscribers.Contains(frame))
            return;
        Subscribers.Add(frame);
        if (_timer != null)
            return;
        _timer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / Fps) };
        _timer.Tick += (_, _) =>
        {
            if (Paused)
                return;
            foreach (var f in Subscribers.ToArray())
                f();
        };
        _timer.Start();
    }

    public static void Unsubscribe(Action frame)
    {
        Subscribers.Remove(frame);
        if (Subscribers.Count == 0 && _timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
    }
}
