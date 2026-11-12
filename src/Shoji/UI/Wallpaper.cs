using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// The user's own picture behind the tiles (Settings → Appearance), over the theme's backdrop and covered by some of
/// the theme's background colour so text stays readable. Decoded once, at no more than screen size.
/// </summary>
public sealed class Wallpaper : Grid
{
    readonly Image _image = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Rectangle _dim = new();
    string? _path;

    public Wallpaper()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);
        Children.Add(_dim);
    }

    /// <summary>Call when the settings or the theme changed.</summary>
    public void Update(AppConfig config)
    {
        string? path = string.IsNullOrWhiteSpace(config.BackgroundImage) ? null : config.BackgroundImage.Trim();
        if (path != _path)
        {
            _path = path;
            _image.Source = null;
            if (path != null)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // read it now and let go of the file
                    bitmap.DecodePixelWidth = 2560;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _image.Source = bitmap;
                }
                catch (Exception ex)
                {
                    Log.Write($"Background picture \"{path}\": {ex.Message}");
                }
            }
        }
        _dim.Fill = Theme.Freeze(new SolidColorBrush(Theme.WithAlpha(Theme.BackgroundColor, (byte)Math.Round(255 * Math.Clamp(config.BackgroundImageDim, 0, 95) / 100.0))));
        Visibility = _image.Source == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
