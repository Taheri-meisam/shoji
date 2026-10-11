using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Shoji.UI;

/// <summary>
/// Gentle per-theme motion: drifting petals (Sakura), a scanning line (Futuristic), glitch slivers (Cyberpunk),
/// slow mist (Sumi-e), drifting colour (Modern). Neon flicker for Tokyo lives in <see cref="Tile"/>.
/// Everything runs on WPF's render thread at a low frame rate, and moving things also help against burn-in.
/// While <see cref="Motion.Paused"/> the layers stay empty (the dashboard calls Build again when it changes).
/// </summary>
public static class Animations
{
    const int FrameRate = 20;

    /// <summary>Fills the layers behind and in front of the tiles for the current theme (called on resize too).</summary>
    public static void Build(Canvas back, Canvas front, double w, double h)
    {
        Stop(back);
        Stop(front);
        back.Children.Clear();
        front.Children.Clear();
        if (!Theme.Animate || Motion.Paused || w <= 0 || h <= 0)
            return;

        switch (Theme.Current.Backdrop)
        {
            case BackdropStyle.Sakura: Petals(back, w, h); break;
            case BackdropStyle.Grid: ScanLine(front, w, h); break;
            case BackdropStyle.Scanlines: Glitches(front, w, h); break;
            case BackdropStyle.Ink: Mist(back, w, h, Theme.TextColor, 0x10); break;
            case BackdropStyle.Glow: Mist(back, w, h, Theme.AccentColor, 0x22); break;
            case BackdropStyle.Synthwave: Mist(back, w, h, Theme.Accent2Color, 0x18); break;
            case BackdropStyle.Space: Mist(back, w, h, Theme.Accent2Color, 0x1C); break;
            case BackdropStyle.Aurora: AuroraCurtains(back, w, h); break;
            case BackdropStyle.Fjord: Snow(back, w, h); break;
            case BackdropStyle.Kintsugi: Mist(back, w, h, Theme.AccentColor, 0x14); break;
        }
    }

    /// <summary>Ends the repeat-forever clocks of a layer's children, so removed elements don't keep ticking.</summary>
    static void Stop(Panel layer)
    {
        foreach (UIElement e in layer.Children)
        {
            Stop(e);
            var transforms = e.RenderTransform is TransformGroup g ? g.Children.ToArray() : new[] { e.RenderTransform };
            foreach (var t in transforms)
            {
                switch (t)
                {
                    case TranslateTransform tt:
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        break;
                    case RotateTransform rt:
                        rt.BeginAnimation(RotateTransform.AngleProperty, null);
                        break;
                }
            }
        }
    }

    public static void Stop(UIElement e) => e.BeginAnimation(UIElement.OpacityProperty, null);

    static void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        Timeline.SetDesiredFrameRate(animation, FrameRate);
        target.BeginAnimation(property, animation);
    }

    static void Petals(Canvas c, double w, double h)
    {
        var rnd = new Random();
        for (int i = 0; i < 14; i++)
        {
            double size = 8 + rnd.NextDouble() * 9;
            var petal = new Path
            {
                Data = Geometry.Parse("M -1,0 C -0.5,-1 1,-0.8 1,-0.15 L 0.76,0 L 1,0.15 C 1,0.8 -0.5,1 -1,0 Z"),
                Fill = new SolidColorBrush(Theme.WithAlpha(Theme.AccentColor, (byte)(0x40 + rnd.Next(0x40)))),
                Width = size, Height = size * 0.6, Stretch = Stretch.Fill,
            };
            var rotate = new RotateTransform();
            var move = new TranslateTransform();
            petal.RenderTransform = new TransformGroup { Children = { rotate, move } };
            petal.RenderTransformOrigin = new Point(0.5, 0.5);
            c.Children.Add(petal);

            double x = rnd.NextDouble() * w, drift = 80 + rnd.NextDouble() * 220;
            var duration = TimeSpan.FromSeconds(14 + rnd.NextDouble() * 14);
            var begin = TimeSpan.FromSeconds(-rnd.NextDouble() * duration.TotalSeconds); // start mid-fall, not all at the top
            Animate(move, TranslateTransform.YProperty, new DoubleAnimation(-30, h + 30, duration) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
            Animate(move, TranslateTransform.XProperty, new DoubleAnimation(x, x + drift, duration) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
            Animate(rotate, RotateTransform.AngleProperty, new DoubleAnimation(0, rnd.Next(2) == 0 ? 360 : -360, TimeSpan.FromSeconds(6 + rnd.NextDouble() * 8)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    static void ScanLine(Canvas c, double w, double h)
    {
        var band = new Rectangle
        {
            Width = w, Height = 140, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Theme.WithAlpha(Theme.AccentColor, 0), 0),
                new(Theme.WithAlpha(Theme.AccentColor, 0x14), 0.85),
                new(Theme.WithAlpha(Theme.AccentColor, 0x40), 0.98),
                new(Theme.WithAlpha(Theme.AccentColor, 0), 1),
            }, 90),
        };
        var move = new TranslateTransform();
        band.RenderTransform = move;
        c.Children.Add(band);
        Animate(move, TranslateTransform.YProperty, new DoubleAnimation(-140, h, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    static void Glitches(Canvas c, double w, double h)
    {
        var rnd = new Random();
        var colors = new[] { Theme.AccentColor, Theme.Accent2Color, Theme.Parse(Theme.Current.Crit) };
        for (int i = 0; i < 4; i++)
        {
            var bar = new Rectangle
            {
                Width = 40 + rnd.NextDouble() * w * 0.4, Height = 2 + rnd.Next(4), IsHitTestVisible = false,
                Fill = new SolidColorBrush(Theme.WithAlpha(colors[i % colors.Length], 0x55)), Opacity = 0,
            };
            var move = new TranslateTransform();
            bar.RenderTransform = move;
            c.Children.Add(bar);

            // Mostly invisible; every few seconds it blinks in at a random spot for a split second.
            var period = TimeSpan.FromSeconds(4 + rnd.NextDouble() * 5);
            var opacity = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
            var y = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
            var x = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
            double at = rnd.NextDouble() * (period.TotalSeconds - 0.4);
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            for (int k = 0; k < 3; k++)
            {
                var t = TimeSpan.FromSeconds(at + k * 0.08);
                opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(k == 1 ? 0.3 : 1, KeyTime.FromTimeSpan(t)));
                y.KeyFrames.Add(new DiscreteDoubleKeyFrame(rnd.NextDouble() * h, KeyTime.FromTimeSpan(t)));
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(rnd.NextDouble() * (w - bar.Width), KeyTime.FromTimeSpan(t)));
            }
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.25))));
            Animate(bar, UIElement.OpacityProperty, opacity);
            Animate(move, TranslateTransform.YProperty, y);
            Animate(move, TranslateTransform.XProperty, x);
        }
    }

    /// <summary>Two aurora curtains that sway sideways and breathe in brightness.</summary>
    static void AuroraCurtains(Canvas c, double w, double h)
    {
        var bands = new[] { (Theme.AccentColor, 0.5, 1.2), (Theme.Accent2Color, 0.38, 3.0) };
        int i = 0;
        foreach (var (color, y, phase) in bands)
        {
            var band = new Path
            {
                Data = Backdrop.AuroraBand(w * 1.3, h, h * y, h * 0.07, phase, h * 0.3),
                Fill = Backdrop.AuroraFill(color, 0x40), IsHitTestVisible = false,
            };
            var move = new TranslateTransform(-w * 0.15, 0);
            band.RenderTransform = move;
            c.Children.Add(band);
            Animate(move, TranslateTransform.XProperty, new DoubleAnimation(-w * 0.15, 0, TimeSpan.FromSeconds(28 + i * 9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            Animate(band, UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(7 + i * 3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            i++;
        }
    }

    static void Snow(Canvas c, double w, double h)
    {
        var rnd = new Random();
        for (int i = 0; i < 40; i++)
        {
            double size = 1.5 + rnd.NextDouble() * 2.5;
            var flake = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb((byte)(0x60 + rnd.Next(0x80)), 0xEC, 0xEF, 0xF4)), IsHitTestVisible = false };
            var move = new TranslateTransform();
            flake.RenderTransform = move;
            c.Children.Add(flake);
            double x = rnd.NextDouble() * w;
            var duration = TimeSpan.FromSeconds(16 + rnd.NextDouble() * 18);
            var begin = TimeSpan.FromSeconds(-rnd.NextDouble() * duration.TotalSeconds);
            Animate(move, TranslateTransform.YProperty, new DoubleAnimation(-10, h + 10, duration) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
            Animate(move, TranslateTransform.XProperty, new DoubleAnimation(x, x + (rnd.NextDouble() - 0.3) * 80, duration) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
        }
    }

    static void Mist(Canvas c, double w, double h, Color color, byte alpha)
    {
        var rnd = new Random();
        for (int i = 0; i < 2; i++)
        {
            double r = Math.Max(w, h) * (0.45 + rnd.NextDouble() * 0.2);
            var blob = new Ellipse
            {
                Width = r * 2, Height = r * 2, IsHitTestVisible = false,
                Fill = new RadialGradientBrush(Theme.WithAlpha(color, alpha), Theme.WithAlpha(color, 0)),
            };
            var move = new TranslateTransform();
            blob.RenderTransform = move;
            c.Children.Add(blob);
            double x0 = rnd.NextDouble() * w - r, y0 = rnd.NextDouble() * h - r;
            var duration = TimeSpan.FromSeconds(40 + rnd.NextDouble() * 30);
            Animate(move, TranslateTransform.XProperty, new DoubleAnimation(x0, x0 + (rnd.NextDouble() - 0.5) * w * 0.6, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            Animate(move, TranslateTransform.YProperty, new DoubleAnimation(y0, y0 + (rnd.NextDouble() - 0.5) * h * 0.4, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    /// <summary>Neon sign flicker on a tile's glow layer: steady, with an occasional quick stutter.</summary>
    public static void Flicker(UIElement glow)
    {
        if (!Theme.Animate)
            return;
        var rnd = new Random(glow.GetHashCode());
        double period = 7 + rnd.NextDouble() * 9, at = 1 + rnd.NextDouble() * (period - 2);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.07))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.14))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.24))));
        Animate(glow, UIElement.OpacityProperty, a);
    }
}

/// <summary>
/// The moving part of the newer backdrops (<see cref="BackdropArt.Frame"/>: drifting colour, plankton, falling leaves…),
/// redrawn at ~20 fps while animations are on, the dashboard is showing and motion isn't paused.
/// </summary>
public sealed class ArtLayer : FrameworkElement
{
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly System.Windows.Threading.DispatcherTimer _timer = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    BackdropStyle _style;

    public ArtLayer()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) =>
        {
            if (IsVisible)
                InvalidateVisual();
        };
        Loaded += (_, _) => Motion.Changed += Restart;
        Unloaded += (_, _) =>
        {
            Motion.Changed -= Restart;
            _timer.Stop();
        };
    }

    /// <summary>Picks up the current theme (call after a theme change); also runs when motion pauses or resumes.</summary>
    public void Restart()
    {
        _style = Theme.Current.Backdrop;
        if (Theme.Animate && !Motion.Paused && BackdropArt.HasMotion(_style))
            _timer.Start();
        else
            _timer.Stop();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || !Theme.Animate || !BackdropArt.HasMotion(_style))
            return;
        BackdropArt.Frame(dc, _style, w, h, _clock.Elapsed.TotalSeconds);
    }
}
