using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Shoji.UI;

/// <summary>
/// Gentle per-theme motion, as on Windows: drifting petals (Sakura), a scanning line (Futuristic), glitch slivers
/// (Cyberpunk), slow mist, aurora curtains, snow. Drawn by one layer on the shared frame clock, and only while animations are on.
/// </summary>
public sealed class AnimationLayer : DrawnControl
{
    readonly bool _front;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Random _rnd = new();
    bool _ticking, _glitchShown;
    int _frame;
    readonly List<Sprite> _sprites = new();
    readonly List<(double X, double Size, double Drift, double Period, double Phase, double Spin, byte Alpha)> _bits = new();
    readonly List<(double X0, double Y0, double R, double Dx, double Dy, double Period)> _blobs = new();
    readonly List<(double Width, double Height, Color Color, double Period, double At)> _glitches = new();
    BackdropStyle _style = BackdropStyle.None;
    Size _size;

    /// <param name="front">The layer in front of the tiles (scan line, glitches); otherwise the one behind them.</param>
    public AnimationLayer(bool front)
    {
        _front = front;
        IsHitTestVisible = false;
        DetachedFromVisualTree += (_, _) => Stop();
    }

    static readonly Geometry PetalShape = Geometry.Parse("M -1,0 C -0.5,-1 1,-0.8 1,-0.15 L 0.76,0 L 1,0.15 C 1,0.8 -0.5,1 -1,0 Z");

    /// <summary>Sets the layer up for the current theme and size (called after a theme change or resize).</summary>
    public void Build(double w, double h)
    {
        Stop();
        _bits.Clear();
        _blobs.Clear();
        _glitches.Clear();
        _sprites.Clear();
        VisualChildren.Clear();
        _size = new Size(w, h);
        _style = UI.Theme.Animate && w > 0 && h > 0 ? UI.Theme.Current.Backdrop : BackdropStyle.None;
        bool mine = _style switch
        {
            BackdropStyle.Grid or BackdropStyle.Scanlines => _front,
            BackdropStyle.Sakura or BackdropStyle.Ink or BackdropStyle.Glow or BackdropStyle.Synthwave or BackdropStyle.Space
                or BackdropStyle.Aurora or BackdropStyle.Fjord or BackdropStyle.Kintsugi => !_front,
            _ => false,
        };
        if (!mine)
        {
            _style = BackdropStyle.None;
            InvalidateVisual();
            return;
        }
        switch (_style)
        {
            case BackdropStyle.Sakura:
                for (int i = 0; i < 14; i++)
                    _bits.Add((_rnd.NextDouble() * w, 8 + _rnd.NextDouble() * 9, 80 + _rnd.NextDouble() * 220, 14 + _rnd.NextDouble() * 14, _rnd.NextDouble(), (_rnd.Next(2) == 0 ? 1 : -1) * 360 / (6 + _rnd.NextDouble() * 8), (byte)(0x40 + _rnd.Next(0x40))));
                break;
            case BackdropStyle.Fjord:
                for (int i = 0; i < 40; i++)
                    _bits.Add((_rnd.NextDouble() * w, 1.5 + _rnd.NextDouble() * 2.5, (_rnd.NextDouble() - 0.3) * 80, 16 + _rnd.NextDouble() * 18, _rnd.NextDouble(), 0, (byte)(0x60 + _rnd.Next(0x80))));
                break;
            case BackdropStyle.Scanlines:
                var colors = new[] { UI.Theme.AccentColor, UI.Theme.Accent2Color, UI.Theme.Parse(UI.Theme.Current.Crit) };
                for (int i = 0; i < 4; i++)
                {
                    double period = 4 + _rnd.NextDouble() * 5;
                    _glitches.Add((40 + _rnd.NextDouble() * w * 0.4, 2 + _rnd.Next(4), colors[i % colors.Length], period, _rnd.NextDouble() * (period - 0.4)));
                }
                break;
            case BackdropStyle.Grid:
            case BackdropStyle.Aurora:
                break;
            default: // mist
                for (int i = 0; i < 2; i++)
                {
                    double r = Math.Max(w, h) * (0.45 + _rnd.NextDouble() * 0.2);
                    _blobs.Add((_rnd.NextDouble() * w - r, _rnd.NextDouble() * h - r, r, (_rnd.NextDouble() - 0.5) * w * 0.6, (_rnd.NextDouble() - 0.5) * h * 0.4, 40 + _rnd.NextDouble() * 30));
                }
                break;
        }
        // Petals, snowflakes and the scan band are sprites the renderer moves around without repainting the screen;
        // only the full-screen motion (mist, aurora) is redrawn, and at a third of the frame rate since it drifts slowly.
        switch (_style)
        {
            case BackdropStyle.Sakura:
                foreach (var b in _bits)
                {
                    var brush = UI.Theme.Solid(UI.Theme.AccentColor, b.Alpha);
                    AddSprite(dc => dc.DrawGeometry(brush, null, PetalShape));
                }
                break;
            case BackdropStyle.Fjord:
                foreach (var b in _bits)
                {
                    var brush = new SolidColorBrush(Color.FromArgb(b.Alpha, 0xEC, 0xEF, 0xF4)).ToImmutable();
                    double r = b.Size / 2;
                    AddSprite(dc => dc.DrawEllipse(brush, null, new Point(0, 0), r, r));
                }
                break;
            case BackdropStyle.Grid:
                // A soft band scanning down the screen every 9 seconds.
                var band = DrawCompat.Linear(new Point(0, 0), new Point(0, 1), false,
                    (UI.Theme.WithAlpha(UI.Theme.AccentColor, 0), 0), (UI.Theme.WithAlpha(UI.Theme.AccentColor, 0x14), 0.85), (UI.Theme.WithAlpha(UI.Theme.AccentColor, 0x40), 0.98), (UI.Theme.WithAlpha(UI.Theme.AccentColor, 0), 1));
                AddSprite(dc => dc.DrawRectangle(band, null, new Rect(0, 0, w, 140)));
                break;
        }
        _ticking = true;
        FrameClock.Subscribe(OnFrame);
        MoveSprites();
        InvalidateVisual();
    }

    void AddSprite(Action<DrawingContext> paint)
    {
        var sprite = new Sprite(paint);
        _sprites.Add(sprite);
        VisualChildren.Add(sprite);
    }

    void OnFrame()
    {
        if (!IsEffectivelyVisible)
            return;
        _frame++;
        switch (_style)
        {
            case BackdropStyle.Sakura or BackdropStyle.Fjord or BackdropStyle.Grid:
                MoveSprites();
                break;
            case BackdropStyle.Scanlines:
                // Repaint only while a glitch is showing (and once more to clear it).
                bool shown = GlitchShowing(_clock.Elapsed.TotalSeconds);
                if (shown || _glitchShown)
                    InvalidateVisual();
                _glitchShown = shown;
                break;
            default:
                if (_frame % 3 == 0)
                    InvalidateVisual();
                break;
        }
    }

    void MoveSprites()
    {
        double t = _clock.Elapsed.TotalSeconds, h = _size.Height;
        for (int i = 0; i < _sprites.Count; i++)
        {
            switch (_style)
            {
                case BackdropStyle.Sakura:
                {
                    var b = _bits[i];
                    double f = Frac(t / b.Period + b.Phase);
                    double x = b.X + b.Drift * f, y = -30 + (h + 60) * f;
                    _sprites[i].Position = Matrix.CreateScale(b.Size / 2, b.Size * 0.3) * Matrix.CreateRotation(t * b.Spin * Math.PI / 180) * Matrix.CreateTranslation(x, y);
                    break;
                }
                case BackdropStyle.Fjord:
                {
                    var b = _bits[i];
                    double f = Frac(t / b.Period + b.Phase);
                    _sprites[i].Position = Matrix.CreateTranslation(b.X + b.Drift * f, -10 + (h + 20) * f);
                    break;
                }
                case BackdropStyle.Grid:
                    _sprites[i].Position = Matrix.CreateTranslation(0, -140 + (h + 140) * Frac(t / 9));
                    break;
            }
        }
    }

    bool GlitchShowing(double t)
    {
        foreach (var g in _glitches)
        {
            double since = t % g.Period - g.At;
            if (since >= 0 && since <= 0.25)
                return true;
        }
        return false;
    }

    public void Stop()
    {
        if (_ticking)
            FrameClock.Unsubscribe(OnFrame);
        _ticking = false;
    }

    static double Frac(double x) => x - Math.Floor(x);

    /// <summary>0 → 1 → 0 smoothly over a period (like WPF's AutoReverse with a sine ease).</summary>
    static double Swing(double t, double period) => 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / (2 * period));

    protected override void Draw(DrawingContext dc)
    {
        if (_style == BackdropStyle.None)
            return;
        double t = _clock.Elapsed.TotalSeconds, w = _size.Width, h = _size.Height;
        switch (_style)
        {
            case BackdropStyle.Sakura or BackdropStyle.Fjord or BackdropStyle.Grid:
                break; // sprites
            case BackdropStyle.Scanlines:
                foreach (var g in _glitches)
                {
                    // Mostly invisible; every few seconds a sliver blinks in at a random spot for a split second.
                    double local = t % g.Period, since = local - g.At;
                    if (since < 0 || since > 0.25)
                        continue;
                    int k = (int)(since / 0.08);
                    var rnd = new Random((int)(t / g.Period) * 31 + k + (int)g.Width);
                    using (dc.PushOpacity(k == 1 ? 0.3 : 1))
                        dc.DrawRectangle(UI.Theme.Solid(g.Color, 0x55), null, new Rect(rnd.NextDouble() * Math.Max(1, w - g.Width), rnd.NextDouble() * h, g.Width, g.Height));
                }
                break;
            case BackdropStyle.Aurora:
            {
                var bands = new[] { (UI.Theme.AccentColor, 0.5, 1.2), (UI.Theme.Accent2Color, 0.38, 3.0) };
                for (int i = 0; i < bands.Length; i++)
                {
                    var (color, y, phase) = bands[i];
                    double sway = -w * 0.15 * (1 - Swing(t, 28 + i * 9));
                    double glow = 0.35 + 0.65 * Swing(t, 7 + i * 3);
                    using (dc.PushTransform(Matrix.CreateTranslation(sway, 0)))
                    using (dc.PushOpacity(glow))
                        dc.DrawGeometry(Backdrop.AuroraFill(color, 0x40), null, Backdrop.AuroraBand(w * 1.3, h, h * y, h * 0.07, phase, h * 0.3));
                }
                break;
            }
            default:
            {
                var (color, alpha) = _style switch
                {
                    BackdropStyle.Ink => (UI.Theme.TextColor, (byte)0x10),
                    BackdropStyle.Glow => (UI.Theme.AccentColor, (byte)0x22),
                    BackdropStyle.Synthwave => (UI.Theme.Accent2Color, (byte)0x18),
                    BackdropStyle.Space => (UI.Theme.Accent2Color, (byte)0x1C),
                    _ => (UI.Theme.AccentColor, (byte)0x14),
                };
                var fill = DrawCompat.Radial((UI.Theme.WithAlpha(color, alpha), 0), (UI.Theme.WithAlpha(color, 0), 1));
                foreach (var b in _blobs)
                {
                    double s = Swing(t, b.Period);
                    dc.DrawEllipse(fill, null, new Point(b.X0 + b.Dx * s + b.R, b.Y0 + b.Dy * s + b.R), b.R, b.R);
                }
                break;
            }
        }
    }
}

/// <summary>
/// The moving part of the newer backdrops (<see cref="BackdropArt.Frame"/>: drifting colour, plankton, falling leaves…),
/// redrawn on the shared frame clock while animations are on and the dashboard is showing.
/// </summary>
public sealed class ArtLayer : DrawnControl
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<Sprite> _leaves = new();
    BackdropStyle _style;
    int _frame;

    public ArtLayer()
    {
        IsHitTestVisible = false;
        DetachedFromVisualTree += (_, _) => FrameClock.Unsubscribe(OnFrame);
    }

    /// <summary>
    /// Falling leaves are sprites moved every frame without repainting the screen. The other scenes are full-screen
    /// and slow (drifting colour, light rays, a bobbing wave), so they are repainted every second or third frame.
    /// </summary>
    void OnFrame()
    {
        if (!IsEffectivelyVisible)
            return;
        if (_style == BackdropStyle.Bamboo)
        {
            MoveLeaves();
            return;
        }
        if (++_frame % (_style == BackdropStyle.Blobs ? 3 : 2) == 0)
            InvalidateVisual();
    }

    void MoveLeaves()
    {
        double t = _clock.Elapsed.TotalSeconds;
        for (int i = 0; i < _leaves.Count; i++)
        {
            var (at, angle) = BackdropArt.LeafAt(i, Bounds.Width, Bounds.Height, t);
            _leaves[i].Position = Matrix.CreateRotation(angle * Math.PI / 180) * Matrix.CreateTranslation(at.X, at.Y);
        }
    }

    /// <summary>Picks up the current theme (call after a theme change).</summary>
    public void Restart()
    {
        _style = UI.Theme.Current.Backdrop;
        _leaves.Clear();
        VisualChildren.Clear();
        bool moving = UI.Theme.Animate && BackdropArt.HasMotion(_style);
        if (moving && _style == BackdropStyle.Bamboo)
            for (int i = 0; i < BackdropArt.LeafCount; i++)
            {
                var (shape, brush) = BackdropArt.LeafSprite(i);
                var leaf = new Sprite(dc => dc.DrawGeometry(brush, null, shape));
                _leaves.Add(leaf);
                VisualChildren.Add(leaf);
            }
        if (moving)
            FrameClock.Subscribe(OnFrame);
        else
            FrameClock.Unsubscribe(OnFrame);
        MoveLeaves();
        InvalidateVisual();
    }

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || !UI.Theme.Animate || !BackdropArt.HasMotion(_style) || _style == BackdropStyle.Bamboo)
            return;
        BackdropArt.Frame(dc, _style, w, h, _clock.Elapsed.TotalSeconds);
    }
}

/// <summary>
/// One small moving thing (a petal, a leaf, a snowflake) drawn once at the origin and then only moved: changing its
/// transform makes the renderer repaint just the few pixels it leaves and enters, not the whole screen.
/// </summary>
public sealed class Sprite : Control
{
    readonly Action<DrawingContext> _paint;
    readonly MatrixTransform _move = new();

    public Sprite(Action<DrawingContext> paint)
    {
        _paint = paint;
        IsHitTestVisible = false;
        Width = Height = 1;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        RenderTransform = _move;
    }

    public Matrix Position
    {
        set
        {
            if (_move.Matrix != value)
                _move.Matrix = value;
        }
    }

    public override void Render(DrawingContext dc) => _paint(dc);
}
