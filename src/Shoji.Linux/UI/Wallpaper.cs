using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// The user's own picture behind the tiles (Settings → Appearance), over the theme's backdrop and covered by some of
/// the theme's background colour so text stays readable. Decoded once at screen size and drawn into a cached bitmap,
/// so it costs nothing per frame.
/// </summary>
public sealed class Wallpaper : CachedDrawnControl
{
    Bitmap? _bitmap;
    string? _path;
    Color _dim;

    public Wallpaper() => IsHitTestVisible = false;

    /// <summary>Call when the settings or the theme changed.</summary>
    public void Update(AppConfig config)
    {
        string? path = string.IsNullOrWhiteSpace(config.BackgroundImage) ? null : config.BackgroundImage.Trim();
        if (path != _path)
        {
            _path = path;
            var old = _bitmap;
            _bitmap = null;
            if (path != null)
            {
                try
                {
                    using var file = File.OpenRead(path);
                    _bitmap = Bitmap.DecodeToWidth(file, 2560, BitmapInterpolationMode.MediumQuality);
                }
                catch (Exception ex)
                {
                    Log.Write($"Background picture \"{path}\": {ex.Message}");
                }
            }
            old?.Dispose();
        }
        _dim = UI.Theme.WithAlpha(UI.Theme.BackgroundColor, (byte)Math.Round(255 * Math.Clamp(config.BackgroundImageDim, 0, 95) / 100.0));
        IsVisible = _bitmap != null;
        InvalidateVisual();
    }

    protected override bool Paint(DrawingContext dc, double w, double h)
    {
        if (_bitmap is not { } bitmap)
            return false;
        // Fill the window, cropping the picture around its middle.
        double bw = bitmap.PixelSize.Width, bh = bitmap.PixelSize.Height;
        double scale = Math.Max(w / bw, h / bh);
        double cw = w / scale, ch = h / scale;
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            dc.DrawImage(bitmap, new Rect((bw - cw) / 2, (bh - ch) / 2, cw, ch), new Rect(0, 0, w, h));
        dc.FillRectangle(new SolidColorBrush(_dim), new Rect(0, 0, w, h));
        return true;
    }
}
