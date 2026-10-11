using System;
using System.Linq;
using Shoji.Core;
using Shoji.Sensors;

namespace Shoji.UI;

/// <summary>
/// Shared logic of the album-art backdrop (both editions): which cover to show behind the tiles, and the blur, done
/// once per track on a small copy of the cover so drawing it is just scaling a still picture.
/// </summary>
public sealed class AlbumArt
{
    /// <summary>A paused track keeps its cover this long (a quick pause shouldn't flash the backdrop away and back).</summary>
    static readonly TimeSpan PausedGrace = TimeSpan.FromMinutes(2);

    /// <summary>Width the cover is blurred at; scaled up to the window it is soft anyway.</summary>
    public const int BlurWidth = 64;

    DateTime _playingAt = DateTime.MinValue;
    byte[]? _lastThumb;

    /// <summary>The cover to show now, or null: something plays (or just paused) with cover art, on a page that shows media.</summary>
    public byte[]? Pick(AppConfig config, PageConfig? page, MediaInfo? media)
    {
        if (media is { IsPlaying: true, Thumbnail: not null })
        {
            _playingAt = DateTime.UtcNow;
            _lastThumb = media.Thumbnail;
        }
        if (!config.AlbumArtBackdrop || !ShowsMedia(page) || media?.Thumbnail == null || string.IsNullOrEmpty(media.Title))
            return null;
        if (media.IsPlaying)
            return media.Thumbnail;
        return ReferenceEquals(media.Thumbnail, _lastThumb) && DateTime.UtcNow - _playingAt < PausedGrace ? media.Thumbnail : null;
    }

    /// <summary>A page with a Now playing tile switched on, or the page called "Media".</summary>
    public static bool ShowsMedia(PageConfig? page) =>
        page != null && (page.Name.Trim().Equals("Media", StringComparison.OrdinalIgnoreCase)
                         || page.Modules.Any(m => m.Enabled && m.Type.Equals("media", StringComparison.OrdinalIgnoreCase)));

    /// <summary>How strongly the cover is covered by the theme's background colour: more on light themes, where text is dark.</summary>
    public static double DimFor(double backgroundLuminance) => backgroundLuminance > 0.5 ? 0.72 : 0.62;

    /// <summary>
    /// Blurs 32-bit pixels (BGRA or RGBA, premultiplied or not: every channel is treated alike) in place with a box blur
    /// run three times each way, which looks close to a Gaussian. Edges repeat the border pixels.
    /// </summary>
    public static void Blur(byte[] pixels, int width, int height, int stride, int radius, int passes = 3)
    {
        if (width <= 0 || height <= 0 || radius <= 0)
            return;
        var temp = new byte[pixels.Length];
        for (int p = 0; p < passes; p++)
        {
            BoxPass(pixels, temp, width, height, stride, radius, horizontal: true);
            BoxPass(temp, pixels, width, height, stride, radius, horizontal: false);
        }
    }

    static void BoxPass(byte[] src, byte[] dst, int width, int height, int stride, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        int window = radius * 2 + 1;
        Span<int> sum = stackalloc int[4];
        for (int line = 0; line < lines; line++)
        {
            int Offset(int i)
            {
                i = Math.Clamp(i, 0, length - 1);
                return horizontal ? line * stride + i * 4 : i * stride + line * 4;
            }
            sum.Clear();
            for (int i = -radius; i <= radius; i++)
            {
                int o = Offset(i);
                for (int c = 0; c < 4; c++)
                    sum[c] += src[o + c];
            }
            for (int i = 0; i < length; i++)
            {
                int o = Offset(i);
                for (int c = 0; c < 4; c++)
                    dst[o + c] = (byte)(sum[c] / window);
                int add = Offset(i + radius + 1), remove = Offset(i - radius);
                for (int c = 0; c < 4; c++)
                    sum[c] += src[add + c] - src[remove + c];
            }
        }
    }
}
