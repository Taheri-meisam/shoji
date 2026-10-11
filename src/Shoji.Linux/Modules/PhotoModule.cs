using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>
/// Photo frame: pictures from a folder, one after another with a crossfade; a tap shows the next. Each picture is
/// decoded on a worker thread at the size the tile shows it, and at most two are in memory (the one showing and the
/// next). Nothing advances or decodes while the tile can't be seen (another page, night mode black, idle).
/// </summary>
public sealed class PhotoModule : Module
{
    sealed record Prepared(string Path, Bitmap Bitmap, PhotoInfo Info, string? Caption);

    /// <summary>One picture on the stage: an Image in a transform that turns it the right way up.</summary>
    sealed class Slot
    {
        public readonly Image Image = new() { Stretch = Stretch.UniformToFill };
        public readonly LayoutTransformControl Turn = new();
        public Prepared? Shown;

        public Slot(bool fill)
        {
            Image.Stretch = fill ? Stretch.UniformToFill : Stretch.Uniform;
            RenderOptions.SetBitmapInterpolationMode(Image, BitmapInterpolationMode.MediumQuality);
            Turn.Child = Image;
            Turn.Opacity = 0;
            Turn.IsHitTestVisible = false;
        }

        public void Set(Prepared? p)
        {
            var old = Shown;
            Shown = p;
            Image.Source = p?.Bitmap;
            var (angle, mirror) = p?.Info.Turn ?? (0, false);
            Turn.LayoutTransform = angle == 0 && !mirror ? null : new TransformGroup
            {
                Children = { new RotateTransform(angle), new ScaleTransform(mirror ? -1 : 1, 1) },
            };
            // The compositor keeps its own reference to a bitmap still on screen, so this only frees ours.
            if (old != null && old != p)
                old.Bitmap.Dispose();
        }
    }

    readonly PhotoLibrary _library;
    readonly TimeSpan _every;
    readonly bool _fill;
    readonly string? _captionMode;
    readonly Grid _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Slot _a, _b;
    readonly Border _captionBox;
    readonly TextBlock _caption, _message;
    readonly Transitions _fade = new() { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromSeconds(1) } };
    Slot _front, _back;
    Prepared? _next;
    bool _loading, _advanceWhenReady, _fading, _disposed;
    DateTime _shownAt = DateTime.MinValue;
    int _failures;

    public PhotoModule(ModuleConfig config) : base(config, "Photo frame")
    {
        _library = new PhotoLibrary(config.Opt("Folder"), config.OptBool("Subfolders"), config.OptBool("Shuffle"));
        _every = TimeSpan.FromSeconds(Math.Max(5, config.OptInt("Every", 20)));
        _fill = !(config.Opt("Fit") ?? "fill").Trim().Equals("fit", StringComparison.OrdinalIgnoreCase);
        _captionMode = config.Opt("Caption");

        _a = new Slot(_fill);
        _b = new Slot(_fill);
        _front = _a;
        _back = _b;
        _stage.Children.Add(_a.Turn);
        _stage.Children.Add(_b.Turn);

        _message = W.Text(L.T("Looking for photos…"), UI.Theme.Small, UI.Theme.Dim);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextAlignment = TextAlignment.Center;
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.VerticalAlignment = VerticalAlignment.Center;
        _message.Margin = new Thickness(8);
        _message.ZIndex = 2;
        _stage.Children.Add(_message);

        // The caption sits on the photo, so it gets its own dark backing whatever the theme.
        _caption = W.Text("", UI.Theme.Small, Brushes.White);
        _captionBox = new Border
        {
            Child = _caption, Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(8), IsVisible = false, IsHitTestVisible = false,
            HorizontalAlignment = _fill ? HorizontalAlignment.Left : HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            ZIndex = 2, // above the photos, which take turns at 0 and 1
        };
        _stage.Children.Add(_captionBox);

        Tap.On(_stage, OnTap);
        View = MakeTile(_stage);
    }

    /// <summary>Only while someone can see the tile: on the page showing, and the motion isn't paused (idle, night, locked).</summary>
    bool Active => !_disposed && View.IsEffectivelyVisible && !UI.FrameClock.Paused;

    public override void Update(Snapshot s)
    {
        if (!Active)
            return;
        _library.ScanIfDue();
        if (!_library.Ready)
            return;
        if (_library.Count == 0 && _front.Shown == null)
        {
            _message.Text = _library.Problem is { } problem ? L.T(problem) : "";
            _message.IsVisible = true;
            return;
        }
        if (_front.Shown == null || DateTime.UtcNow - _shownAt >= _every)
            Advance();
        else
            Prefetch();
    }

    void OnTap()
    {
        if (_library.Ready && _library.Count == 0)
        {
            _library.ScanIfDue(force: true); // maybe photos were just added
            return;
        }
        Advance();
    }

    /// <summary>Shows the next photo now if it's ready, or as soon as it is.</summary>
    void Advance()
    {
        if (_fading)
            return;
        if (_next != null)
        {
            var p = _next;
            _next = null;
            Show(p);
            return;
        }
        _advanceWhenReady = true;
        Prefetch();
    }

    /// <summary>Decodes the next photo on a worker thread (one at a time, and not while a crossfade still holds two).</summary>
    void Prefetch()
    {
        if (_loading || _next != null || _fading || !Active)
            return;
        var path = _library.Next();
        if (path == null)
            return;
        var (w, h) = BoxPixels();
        _loading = true;
        Task.Run(() => Decode(path, w, h, _fill, _captionMode)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _loading = false;
            var p = t.IsCompletedSuccessfully ? t.Result : null;
            if (_disposed)
            {
                p?.Bitmap.Dispose();
                return;
            }
            if (p == null)
            {
                _library.Drop(path);
                // A few broken files in a row: try the next one; many (e.g. a folder of unsupported files): wait for the next tick.
                if (++_failures % 5 != 0)
                    Prefetch();
                return;
            }
            _failures = 0;
            _next = p;
            if (_advanceWhenReady && Active)
                Advance();
        }));
    }

    static Prepared? Decode(string path, double boxW, double boxH, bool fill, string? captionMode)
    {
        try
        {
            var info = PhotoInfo.Read(path);
            int width = info.DecodeWidth(boxW, boxH, fill);
            if (width == 0)
                width = (int)Math.Clamp(Math.Max(boxW, boxH), 64, 2560); // size unknown: about the tile's size
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
            return new Prepared(path, bitmap, info, PhotoInfo.Caption(path, captionMode, info.Taken));
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("photos-decode", $"Photo frame: couldn't open {path}: {ex.Message}", TimeSpan.FromMinutes(10));
            return null;
        }
    }

    void Show(Prepared p)
    {
        _advanceWhenReady = false;
        _shownAt = DateTime.UtcNow;
        _message.IsVisible = false;
        _caption.Text = p.Caption ?? "";
        _captionBox.IsVisible = !string.IsNullOrEmpty(p.Caption);

        // The new photo goes on the spare slot, above the one showing, and fades in; then the old one is let go.
        var incoming = _back;
        var outgoing = _front;
        _front = incoming;
        _back = outgoing;
        incoming.Set(p);
        incoming.Turn.ZIndex = 1;
        outgoing.Turn.ZIndex = 0;
        if (outgoing.Shown == null || !UI.Theme.Animate)
        {
            incoming.Turn.Transitions = null;
            incoming.Turn.Opacity = 1;
            outgoing.Turn.Opacity = 0;
            outgoing.Set(null);
            Prefetch();
            return;
        }
        incoming.Turn.Transitions = null;
        incoming.Turn.Opacity = 0;
        incoming.Turn.Transitions = _fade;
        _fading = true;
        Dispatcher.UIThread.Post(() => incoming.Turn.Opacity = 1, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() =>
        {
            _fading = false;
            incoming.Turn.Transitions = null;
            outgoing.Turn.Opacity = 0;
            outgoing.Set(null);
            if (!_disposed)
                Prefetch();
        }, TimeSpan.FromSeconds(1.1));
    }

    /// <summary>The stage's size in device pixels (page scaling and the screen's scale included), so photos decode no larger than shown.</summary>
    (double W, double H) BoxPixels()
    {
        var top = TopLevel.GetTopLevel(_stage);
        double w = _stage.Bounds.Width, h = _stage.Bounds.Height;
        if (top == null || w < 8 || h < 8)
            return (1280, 800);
        double scale = top.RenderScaling;
        if (_stage.TransformToVisual(top) is Matrix m)
            scale *= Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        return (w * scale, h * scale);
    }

    protected override void OnDispose()
    {
        _disposed = true;
        _a.Set(null);
        _b.Set(null);
        _next?.Bitmap.Dispose();
        _next = null;
    }
}
