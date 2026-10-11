using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Shoji.Core;
using Shoji.Sensors;

namespace Shoji.UI;

/// <summary>
/// The cover of what's playing, blurred and dimmed, behind the tiles of a page that shows media (above the theme's
/// backdrop). The blur happens once per track on a 64-pixel copy, on a worker thread; the result is drawn once into
/// a full-size cached bitmap, so the dashboard only ever composites a still picture. Tracks crossfade (~1.2 s).
/// </summary>
public sealed class AlbumArtBackdrop : Avalonia.Controls.Grid
{
    static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(1.2);

    /// <summary>One blurred cover, scaled to fill the window and covered by the theme's background colour.</summary>
    sealed class Picture : CachedDrawnControl
    {
        WriteableBitmap? _bitmap;
        Color _dim;

        public Picture()
        {
            IsHitTestVisible = false;
            IsVisible = false;
            Opacity = 0;
        }

        public bool HasPicture => _bitmap != null;

        public void Set(WriteableBitmap? bitmap)
        {
            var old = _bitmap;
            _bitmap = bitmap;
            IsVisible = bitmap != null;
            InvalidateVisual();
            old?.Dispose(); // the compositor keeps its own reference while it still shows the old frame
        }

        public void SetDim(Color dim)
        {
            if (dim == _dim)
                return;
            _dim = dim;
            if (_bitmap != null)
                InvalidateVisual();
        }

        protected override bool Paint(DrawingContext dc, double w, double h)
        {
            if (_bitmap is not { } bitmap)
                return false;
            // Uniform-to-fill: crop the cover to the window's shape, keeping its middle.
            double bw = bitmap.PixelSize.Width, bh = bitmap.PixelSize.Height;
            double scale = Math.Max(w / bw, h / bh);
            double cw = w / scale, ch = h / scale;
            var source = new Rect((bw - cw) / 2, (bh - ch) / 2, cw, ch);
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                dc.DrawImage(bitmap, source, new Rect(0, 0, w, h));
            dc.FillRectangle(new SolidColorBrush(_dim), new Rect(0, 0, w, h));
            return true;
        }
    }

    readonly Picture _a = new(), _b = new();
    readonly AlbumArt _logic = new();
    readonly Transitions _fade = new() { new DoubleTransition { Property = OpacityProperty, Duration = FadeTime } };
    Picture _front;
    byte[]? _wanted;
    int _generation, _fadeId;
    Action? _finishFade;

    public AlbumArtBackdrop()
    {
        IsHitTestVisible = false;
        Children.Add(_a);
        Children.Add(_b);
        _front = _a;
    }

    /// <summary>Called on every refresh and page change; cheap unless the cover to show changed.</summary>
    public void Update(AppConfig config, PageConfig? page, MediaInfo? media)
    {
        var bg = UI.Theme.BackgroundColor;
        var dim = UI.Theme.WithAlpha(bg, (byte)Math.Round(255 * AlbumArt.DimFor(UI.Theme.Luminance(bg))));
        _a.SetDim(dim);
        _b.SetDim(dim);

        var cover = _logic.Pick(config, page, media);
        if (ReferenceEquals(cover, _wanted))
            return;
        _wanted = cover;
        int generation = ++_generation;
        if (cover == null)
        {
            FadeOut();
            return;
        }
        Task.Run(() => Blur(cover)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var bitmap = t.IsCompletedSuccessfully ? t.Result : null;
            if (generation != _generation)
            {
                bitmap?.Dispose(); // another track (or none) meanwhile
                return;
            }
            if (bitmap == null)
                FadeOut();
            else
                FadeIn(bitmap);
        }));
    }

    /// <summary>Decode small, blur once. Runs on a worker thread.</summary>
    static WriteableBitmap? Blur(byte[] cover)
    {
        try
        {
            using var decoded = Bitmap.DecodeToWidth(new MemoryStream(cover), AlbumArt.BlurWidth, BitmapInterpolationMode.MediumQuality);
            var size = decoded.PixelSize;
            if (size.Width < 2 || size.Height < 2)
                return null;
            var result = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = result.Lock())
            {
                decoded.CopyPixels(fb, AlphaFormat.Premul);
                var pixels = new byte[fb.RowBytes * size.Height];
                Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
                AlbumArt.Blur(pixels, size.Width, size.Height, fb.RowBytes, radius: 3);
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("album-art", $"Album art backdrop: cover couldn't be read: {ex.Message}", TimeSpan.FromMinutes(10));
            return null;
        }
    }

    void FadeIn(WriteableBitmap bitmap)
    {
        _finishFade?.Invoke(); // a crossfade still running ends now, so only two covers are ever kept
        var incoming = _front.HasPicture ? Other(_front) : _front;
        var outgoing = Other(incoming);
        _front = incoming;
        incoming.Set(bitmap);
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;
        Fade(incoming, 1, () =>
        {
            outgoing.Transitions = null;
            outgoing.Opacity = 0;
            outgoing.Set(null);
        });
    }

    void FadeOut()
    {
        _finishFade?.Invoke();
        if (!_front.HasPicture)
            return;
        var leaving = _front;
        Fade(leaving, 0, () => leaving.Set(null));
    }

    /// <summary>A one-off opacity transition; <paramref name="done"/> runs when it has finished (or when the next fade starts).</summary>
    void Fade(Picture picture, double to, Action done)
    {
        int id = ++_fadeId;
        void Finish()
        {
            if (_finishFade != Finish)
                return;
            _finishFade = null;
            picture.Transitions = null;
            picture.Opacity = to;
            done();
        }
        _finishFade = Finish;
        if (!UI.Theme.Animate || UI.FrameClock.Paused)
        {
            Finish();
            return;
        }
        picture.Transitions = _fade;
        Dispatcher.UIThread.Post(() =>
        {
            if (id == _fadeId)
                picture.Opacity = to;
        }, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() =>
        {
            if (id == _fadeId)
                Finish();
        }, FadeTime + TimeSpan.FromMilliseconds(100));
    }

    Picture Other(Picture p) => p == _a ? _b : _a;
}
