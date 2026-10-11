using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>
/// Photo frame: pictures from a folder, one after another with a crossfade; a tap shows the next. Each picture is
/// decoded on a worker thread at the size the tile shows it (frozen, so the UI thread only draws it), and at most two
/// are in memory (the one showing and the next). Nothing advances or decodes while the tile can't be seen (another
/// page, the display off, night mode black).
/// </summary>
public sealed class PhotoModule : Module
{
    sealed record Prepared(string Path, BitmapSource Bitmap, PhotoInfo Info, string? Caption);

    static readonly Duration FadeTime = new(TimeSpan.FromSeconds(1));

    readonly PhotoLibrary _library;
    readonly TimeSpan _every;
    readonly bool _fill;
    readonly string? _captionMode;
    readonly Grid _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Image _a, _b;
    readonly Border _captionBox;
    readonly TextBlock _caption, _message;
    Image _front, _back;
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

        _a = Slot();
        _b = Slot();
        _front = _a;
        _back = _b;
        _stage.Children.Add(_a);
        _stage.Children.Add(_b);

        _message = W.Text(L.T("Looking for photos…"), Theme.Small, Theme.Dim);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextAlignment = TextAlignment.Center;
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.VerticalAlignment = VerticalAlignment.Center;
        _message.Margin = new Thickness(8);
        Panel.SetZIndex(_message, 2);
        _stage.Children.Add(_message);

        // The caption sits on the photo, so it gets its own dark backing whatever the theme.
        _caption = W.Text("", Theme.Small, Brushes.White);
        _captionBox = new Border
        {
            Child = _caption, Background = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0))), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(8), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            HorizontalAlignment = _fill ? HorizontalAlignment.Left : HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        Panel.SetZIndex(_captionBox, 2); // above the photos, which take turns at 0 and 1
        _stage.Children.Add(_captionBox);

        Tap.On(_stage, OnTap);
        View = MakeTile(_stage);
    }

    Image Slot()
    {
        var image = new Image { Stretch = _fill ? Stretch.UniformToFill : Stretch.Uniform, Opacity = 0, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>Only while someone can see the tile: on the page showing, and the motion isn't paused (display off, locked, night).</summary>
    bool Active => !_disposed && View.IsVisible && !Motion.Paused;

    public override void Update(Snapshot s)
    {
        if (!Active)
            return;
        _library.ScanIfDue();
        if (!_library.Ready)
            return;
        if (_library.Count == 0 && _front.Source == null)
        {
            _message.Text = _library.Problem ?? "";
            _message.Visibility = Visibility.Visible;
            return;
        }
        if (_front.Source == null || DateTime.UtcNow - _shownAt >= _every)
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
        var dispatcher = View.Dispatcher;
        Task.Run(() => Decode(path, w, h, _fill, _captionMode)).ContinueWith(t => dispatcher.BeginInvoke(() =>
        {
            _loading = false;
            var p = t.IsCompletedSuccessfully ? t.Result : null;
            if (_disposed)
                return;
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
            var image = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // read it all now, so the file isn't kept open
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; // odd colour profiles make WPF throw
                image.DecodePixelWidth = width;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return new Prepared(path, image, info, PhotoInfo.Caption(path, captionMode, info.Taken));
        }
        catch (Exception ex)
        {
            // Includes WebP without the Windows WebP codec ("Webp Image Extensions" from the Store).
            Log.WriteThrottled("photos-decode", $"Photo frame: couldn't open {path}: {ex.Message}", TimeSpan.FromMinutes(10));
            return null;
        }
    }

    void Show(Prepared p)
    {
        _advanceWhenReady = false;
        _shownAt = DateTime.UtcNow;
        _message.Visibility = Visibility.Collapsed;
        _caption.Text = p.Caption ?? "";
        _captionBox.Visibility = string.IsNullOrEmpty(p.Caption) ? Visibility.Collapsed : Visibility.Visible;

        // The new photo goes on the spare image, above the one showing, and fades in; then the old one is let go.
        var incoming = _back;
        var outgoing = _front;
        _front = incoming;
        _back = outgoing;
        incoming.Source = p.Bitmap;
        var (angle, mirror) = p.Info.Turn;
        incoming.LayoutTransform = angle == 0 && !mirror ? Transform.Identity : new TransformGroup
        {
            Children = { new RotateTransform(angle), new ScaleTransform(mirror ? -1 : 1, 1) },
        };
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        if (outgoing.Source == null || !Theme.Animate)
        {
            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Opacity = 1;
            Release(outgoing);
            Prefetch();
            return;
        }
        _fading = true;
        var fade = new DoubleAnimation(0, 1, FadeTime);
        fade.Completed += (_, _) =>
        {
            _fading = false;
            Release(outgoing);
            if (!_disposed)
                Prefetch();
        };
        incoming.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    static void Release(Image image)
    {
        image.BeginAnimation(UIElement.OpacityProperty, null);
        image.Opacity = 0;
        image.Source = null;
    }

    /// <summary>The stage's size in device pixels (page scaling and DPI included), so photos decode no larger than shown.</summary>
    (double W, double H) BoxPixels()
    {
        try
        {
            var source = PresentationSource.FromVisual(_stage);
            if (source?.RootVisual is Visual root && source.CompositionTarget != null && _stage.ActualWidth >= 8 && _stage.ActualHeight >= 8)
            {
                var r = _stage.TransformToAncestor(root).TransformBounds(new Rect(0, 0, _stage.ActualWidth, _stage.ActualHeight));
                var device = source.CompositionTarget.TransformToDevice;
                return (r.Width * device.M11, r.Height * device.M22);
            }
        }
        catch (InvalidOperationException)
        {
            // Not in the window's tree right now.
        }
        return (1280, 800);
    }

    protected override void OnDispose()
    {
        _disposed = true;
        Release(_a);
        Release(_b);
        _next = null;
    }
}
