using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Shoji.Core;
using Shoji.Sensors;

namespace Shoji.UI;

/// <summary>
/// The cover of what's playing, blurred and dimmed, behind the tiles of a page that shows media (above the theme's
/// backdrop). The blur happens once per track on a 64-pixel copy, on a worker thread; the GPU just scales the frozen
/// result up, so no effect is re-run per frame. Tracks crossfade (~1.2 s).
/// </summary>
public sealed class AlbumArtBackdrop : Grid
{
    static readonly Duration FadeTime = new(TimeSpan.FromSeconds(1.2));

    /// <summary>One blurred cover filling the window, with the theme's background colour over it.</summary>
    sealed class Picture : Grid
    {
        public readonly Image Image = new() { Stretch = Stretch.UniformToFill };
        public readonly Rectangle Dim = new();

        public Picture()
        {
            IsHitTestVisible = false;
            Opacity = 0;
            Visibility = Visibility.Collapsed;
            RenderOptions.SetBitmapScalingMode(Image, BitmapScalingMode.Linear);
            Children.Add(Image);
            Children.Add(Dim);
        }

        public bool HasPicture => Image.Source != null;

        public void Set(BitmapSource? bitmap)
        {
            Image.Source = bitmap;
            Visibility = bitmap == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    readonly Picture _a = new(), _b = new();
    readonly AlbumArt _logic = new();
    Picture _front;
    byte[]? _wanted;
    Color _dim;
    int _generation, _fadeId;
    Action? _finishFade;

    public AlbumArtBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_a);
        Children.Add(_b);
        _front = _a;
    }

    /// <summary>Called on every refresh and page change; cheap unless the cover to show changed.</summary>
    public void Update(AppConfig config, PageConfig? page, MediaInfo? media)
    {
        var bg = Theme.BackgroundColor;
        var dim = Theme.WithAlpha(bg, (byte)Math.Round(255 * AlbumArt.DimFor(Theme.Luminance(bg))));
        if (dim != _dim)
        {
            _dim = dim;
            var brush = Theme.Freeze(new SolidColorBrush(dim));
            _a.Dim.Fill = brush;
            _b.Dim.Fill = brush;
        }

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
        Task.Run(() => Blur(cover)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (generation != _generation)
                return; // another track (or none) meanwhile
            var bitmap = t.IsCompletedSuccessfully ? t.Result : null;
            if (bitmap == null)
                FadeOut();
            else
                FadeIn(bitmap);
        }));
    }

    /// <summary>Decode small, blur once, freeze. Runs on a worker thread.</summary>
    static BitmapSource? Blur(byte[] cover)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = AlbumArt.BlurWidth;
            image.StreamSource = new MemoryStream(cover);
            image.EndInit();
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            if (w < 2 || h < 2)
                return null;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            AlbumArt.Blur(pixels, w, h, stride, radius: 3);
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("album-art", $"Album art backdrop: cover couldn't be read: {ex.Message}", TimeSpan.FromMinutes(10));
            return null;
        }
    }

    void FadeIn(BitmapSource bitmap)
    {
        _finishFade?.Invoke(); // a crossfade still running ends now, so only two covers are ever kept
        var incoming = _front.HasPicture ? Other(_front) : _front;
        var outgoing = Other(incoming);
        _front = incoming;
        incoming.Set(bitmap);
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        Fade(incoming, 1, () =>
        {
            outgoing.BeginAnimation(OpacityProperty, null);
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

    /// <summary>A one-off opacity animation; <paramref name="done"/> runs when it has finished (or when the next fade starts).</summary>
    void Fade(Picture picture, double to, Action done)
    {
        int id = ++_fadeId;
        void Finish()
        {
            if (id != _fadeId || _finishFade == null)
                return;
            _finishFade = null;
            picture.BeginAnimation(OpacityProperty, null);
            picture.Opacity = to;
            done();
        }
        _finishFade = Finish;
        if (!Theme.Animate || Motion.Paused)
        {
            Finish();
            return;
        }
        var animation = new DoubleAnimation(to, FadeTime);
        animation.Completed += (_, _) => Finish();
        picture.BeginAnimation(OpacityProperty, animation);
    }

    Picture Other(Picture p) => p == _a ? _b : _a;
}
