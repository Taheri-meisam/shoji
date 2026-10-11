using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Colours, fonts and style of the active theme, shared by every module. Rebuilt from config on reload.</summary>
public static class Theme
{
    public static ThemeDef Current { get; private set; } = Themes.All[0];

    /// <summary>Window background (may be a gradient).</summary>
    public static Brush WindowBackground { get; private set; } = Freeze("#0B0E14");
    public static Color BackgroundColor { get; private set; } = Parse("#0B0E14");
    public static Color Background2Color { get; private set; } = Parse("#0B0E14");
    public static Brush Background { get; private set; } = Freeze("#0B0E14");
    public static Brush Tile { get; private set; } = Freeze("#151A24");
    public static Brush? TileBorder { get; private set; }
    public static Brush Accent { get; private set; } = Freeze("#4FC3F7");
    public static Color AccentColor { get; private set; } = Parse("#4FC3F7");
    public static Brush Accent2 { get; private set; } = Freeze("#CE93D8");
    public static Color Accent2Color { get; private set; } = Parse("#CE93D8");

    public static Brush Text { get; private set; } = Freeze("#E8EBF2");
    public static Color TextColor { get; private set; } = Parse("#E8EBF2");
    public static Brush Dim { get; private set; } = Freeze("#8A93A6");
    public static Brush Track { get; private set; } = Freeze("#252C3A");
    public static Brush Warn { get; private set; } = Freeze("#FFB74D");
    public static Brush Crit { get; private set; } = Freeze("#EF5350");
    public static Brush Good { get; private set; } = Freeze("#81C784");

    /// <summary>Second graph series (upload). Same as <see cref="Accent2"/>.</summary>
    public static Brush Upload => Accent2;

    public static double TempWarn { get; private set; } = 70;
    public static double TempCrit { get; private set; } = 85;
    public static bool Fahrenheit { get; private set; }

    public static FontFamily Font { get; private set; } = new("Segoe UI Variable Display, Segoe UI");
    public static FontFamily Display { get; private set; } = new("Segoe UI Variable Display, Segoe UI");
    public static FontFamily TitleFont { get; private set; } = new("Segoe UI Variable Display, Segoe UI");
    public static FontFamily Mono { get; private set; } = new("Cascadia Mono, Consolas");

    /// <summary>Effect for big numbers (neon glow / glitch), or null. Shared and frozen.</summary>
    public static Effect? NumberEffect { get; private set; }

    public const double Title = 13;
    public const double Big = 34;
    public const double Medium = 17;
    public const double Small = 12.5;

    public static ThemeDef Find(string? id) =>
        Themes.All.FirstOrDefault(t => t.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Themes.All[0];

    /// <summary>Theme animations on (config "Animations").</summary>
    public static bool Animate { get; private set; }

    /// <param name="themeOverride">Theme chosen by a scene or the day/night schedule instead of the configured one.</param>
    public static void Apply(AppConfig config, string? themeOverride = null)
    {
        var t = Current = Find(themeOverride ?? config.Theme);
        Animate = config.Animations;

        BackgroundColor = Parse(config.BackgroundColor ?? t.Background, t.Background);
        Background2Color = config.BackgroundColor == null && t.Background2 != null ? Parse(t.Background2) : BackgroundColor;
        Background = Freeze(new SolidColorBrush(BackgroundColor));
        WindowBackground = BackgroundColor == Background2Color
            ? Background
            : Freeze(new LinearGradientBrush(BackgroundColor, Background2Color, 90));

        Tile = Freeze(config.TileColor ?? t.Tile, t.Tile);
        TileBorder = t.TileBorder == null ? null : Freeze(t.TileBorder);
        AccentColor = Parse(config.AccentColor ?? t.Accent, t.Accent);
        // A custom accent or tile colour can leave the accent unreadable on the tile (a white accent on a light
        // tile): darken or lighten it until it stands out (3:1). Built-in themes are already tuned.
        if (config.AccentColor != null || config.TileColor != null)
            AccentColor = Readable(AccentColor, Over(Parse(config.TileColor ?? t.Tile, t.Tile), BackgroundColor), 3);
        Accent = Freeze(new SolidColorBrush(AccentColor));
        Accent2Color = Parse(t.Accent2);
        Accent2 = Freeze(new SolidColorBrush(Accent2Color));
        TextColor = Parse(t.Text);
        var dim = Parse(t.Dim);
        // A custom tile or background colour can be light on a dark theme (or the other way round):
        // then switch the text to whatever reads on it.
        if (config.TileColor != null || config.BackgroundColor != null)
        {
            var under = Parse(config.TileColor ?? t.Tile, t.Tile);
            if (Contrast(TextColor, under) < 3)
            {
                bool light = Luminance(under) > 0.4;
                TextColor = light ? Parse("#1B1D22") : Parse("#ECEEF2");
                dim = light ? Parse("#5A606B") : Parse("#9AA3B2");
            }
        }
        Text = Freeze(new SolidColorBrush(TextColor));
        Dim = Freeze(new SolidColorBrush(dim));
        Track = Freeze(t.Track);
        Warn = Freeze(t.Warn);
        Crit = Freeze(t.Crit);
        Good = Freeze(t.Good);

        Font = Family(t.Font);
        Display = Family(t.DisplayFont ?? t.Font);
        TitleFont = Family(t.TitleFont ?? t.Font);
        Mono = Family(t.Mono);

        NumberEffect = t.TextGlow
            ? new DropShadowEffect { Color = AccentColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.75 }
            : t.Glitch
                ? new DropShadowEffect { Color = Parse(t.Crit), BlurRadius = 0, ShadowDepth = 2.5, Direction = 200, Opacity = 0.9 }
                : null;
        NumberEffect?.Freeze();

        TempWarn = config.TempWarn;
        TempCrit = config.TempCrit;
        Fahrenheit = config.Fahrenheit;
    }

    static readonly Uri FontBase = new("pack://application:,,,/");

    /// <summary>A font list that may include bundled fonts ("./Fonts/#Michroma, Segoe UI").</summary>
    public static FontFamily Family(string spec) => new(FontBase, spec);

    /// <summary>The gauge style for a tile type: the tile's own choice, else the theme's default, else "classic".</summary>
    public static string StyleFor(string type, string? chosen)
    {
        var allowed = Core.ModuleCatalog.Find(type)?.Styles;
        if (allowed == null || allowed.Length == 0)
            return "classic";
        bool Ok(string? s) => s != null && allowed.Any(a => a.Id.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (Ok(chosen))
            return chosen!.ToLowerInvariant();
        return Current.Styles.TryGetValue(type, out var d) && Ok(d) ? d : allowed[0].Id;
    }

    public static Brush ForTemperature(double? celsius) =>
        celsius >= TempCrit ? Crit : celsius >= TempWarn ? Warn : Text;

    public static Brush ForPercent(double? percent) =>
        percent >= 90 ? Crit : percent >= 75 ? Warn : Accent;

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>A (possibly translucent) colour as it shows over an opaque one.</summary>
    static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>Moves a colour toward black or white (whichever stands out more on the background) until it has the given contrast.</summary>
    static Color Readable(Color c, Color under, double contrast)
    {
        var target = Contrast(Colors.Black, under) > Contrast(Colors.White, under) ? Colors.Black : Colors.White;
        var result = c;
        for (int step = 1; step <= 20 && Contrast(result, under) < contrast; step++)
        {
            double k = step * 0.05;
            byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * k);
            result = Color.FromArgb(c.A, Mix(c.R, target.R), Mix(c.G, target.G), Mix(c.B, target.B));
        }
        return result;
    }

    /// <summary>Relative luminance (0 = black, 1 = white), as in the WCAG contrast formula.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio, 1 (none) to 21 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static Color Parse(string value, string fallback = "#FFFFFF")
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (Exception) { return (Color)ColorConverter.ConvertFromString(fallback); } // bad user colour in config.json
    }

    static Brush Freeze(string value, string? fallback = null) =>
        Freeze(new SolidColorBrush(Parse(value, fallback ?? value)));

    public static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
