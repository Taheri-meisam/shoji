using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>
/// Colours, fonts and style of the active theme for the Linux dashboard (the Avalonia twin of the Windows Theme).
/// The themes themselves are the shared list in Themes.cs.
/// </summary>
public static class Theme
{
    public static ThemeDef Current { get; private set; } = Themes.All[0];

    public static IBrush WindowBackground { get; private set; } = Solid("#0B0E14");
    public static Color BackgroundColor { get; private set; } = Parse("#0B0E14");
    public static Color Background2Color { get; private set; } = Parse("#0B0E14");
    public static IBrush Background { get; private set; } = Solid("#0B0E14");
    public static IBrush Tile { get; private set; } = Solid("#151A24");
    public static Color TileColor { get; private set; } = Parse("#151A24");
    public static IBrush? TileBorder { get; private set; }
    public static IBrush Accent { get; private set; } = Solid("#4FC3F7");
    public static Color AccentColor { get; private set; } = Parse("#4FC3F7");
    public static IBrush Accent2 { get; private set; } = Solid("#CE93D8");
    public static Color Accent2Color { get; private set; } = Parse("#CE93D8");
    public static IBrush Text { get; private set; } = Solid("#E8EBF2");
    public static Color TextColor { get; private set; } = Parse("#E8EBF2");
    public static IBrush Dim { get; private set; } = Solid("#8A93A6");
    public static Color DimColor { get; private set; } = Parse("#8A93A6");
    public static IBrush Track { get; private set; } = Solid("#252C3A");
    public static Color TrackColor { get; private set; } = Parse("#252C3A");
    public static IBrush Warn { get; private set; } = Solid("#FFB74D");
    public static IBrush Crit { get; private set; } = Solid("#EF5350");
    public static IBrush Good { get; private set; } = Solid("#81C784");
    public static IBrush Upload => Accent2;

    public static double TempWarn { get; private set; } = 70;
    public static double TempCrit { get; private set; } = 85;
    public static bool Fahrenheit { get; private set; }
    public static bool Use24Hour { get; private set; } = true;
    public static bool Animate { get; private set; }

    public static FontFamily Font { get; private set; } = FontFamily.Default;
    public static FontFamily Display { get; private set; } = FontFamily.Default;
    public static FontFamily TitleFont { get; private set; } = FontFamily.Default;
    public static FontFamily Mono { get; private set; } = FontFamily.Default;
    public static FontFamily Icons { get; } = new("avares://shoji/Assets/Fonts#Material Icons");

    /// <summary>Glow or glitch shadow for big numbers, or null.</summary>
    public static IEffect? NumberEffect { get; private set; }

    public const double Title = 13;
    public const double Big = 34;
    public const double Medium = 17;
    public const double Small = 12.5;

    public static ThemeDef Find(string? id) =>
        Themes.All.FirstOrDefault(t => t.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Themes.All[0];

    public static void Apply(AppConfig config, string? themeOverride = null)
    {
        var t = Current = Find(themeOverride ?? config.Theme);
        Animate = config.Animations;
        Use24Hour = config.Use24HourClock;

        BackgroundColor = Parse(config.BackgroundColor ?? t.Background, t.Background);
        Background2Color = config.BackgroundColor == null && t.Background2 != null ? Parse(t.Background2) : BackgroundColor;
        Background = new ImmutableSolidColorBrush(BackgroundColor);
        WindowBackground = BackgroundColor == Background2Color
            ? Background
            : new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
                GradientStops = { new GradientStop(BackgroundColor, 0), new GradientStop(Background2Color, 1) },
            }.ToImmutable();

        TileColor = Parse(config.TileColor ?? t.Tile, t.Tile);
        Tile = new ImmutableSolidColorBrush(TileColor);
        TileBorder = t.TileBorder == null ? null : Solid(t.TileBorder);
        AccentColor = Parse(config.AccentColor ?? t.Accent, t.Accent);
        Accent = new ImmutableSolidColorBrush(AccentColor);
        Accent2Color = Parse(t.Accent2);
        Accent2 = new ImmutableSolidColorBrush(Accent2Color);
        TextColor = Parse(t.Text);
        DimColor = Parse(t.Dim);
        // A custom tile or background colour can be light on a dark theme (or the other way round).
        if ((config.TileColor != null || config.BackgroundColor != null) && Contrast(TextColor, TileColor) < 3)
        {
            bool light = Luminance(TileColor) > 0.4;
            TextColor = light ? Parse("#1B1D22") : Parse("#ECEEF2");
            DimColor = light ? Parse("#5A606B") : Parse("#9AA3B2");
        }
        Text = new ImmutableSolidColorBrush(TextColor);
        Dim = new ImmutableSolidColorBrush(DimColor);
        TrackColor = Parse(t.Track);
        Track = new ImmutableSolidColorBrush(TrackColor);
        Warn = Solid(t.Warn);
        Crit = Solid(t.Crit);
        Good = Solid(t.Good);

        Font = Family(t.Font);
        Display = Family(t.DisplayFont ?? t.Font);
        TitleFont = Family(t.TitleFont ?? t.Font);
        Mono = Family(t.Mono);

        NumberEffect = t.TextGlow
            ? new DropShadowEffect { Color = AccentColor, BlurRadius = 14, OffsetX = 0, OffsetY = 0, Opacity = 0.75 }.ToImmutable()
            : t.Glitch
                ? new DropShadowEffect { Color = Parse(t.Crit), BlurRadius = 0, OffsetX = -2.3, OffsetY = 0.9, Opacity = 0.9 }.ToImmutable()
                : null;

        TempWarn = config.TempWarn;
        TempCrit = config.TempCrit;
        Fahrenheit = config.Fahrenheit;
    }

    // Windows fonts the themes name, and what stands in for them on Linux (bundled fonts first, then common ones).
    static readonly (string Windows, string Linux)[] FontMap =
    {
        ("Bahnschrift Condensed", "avares://shoji/Assets/Fonts#Barlow Condensed"),
        ("Bahnschrift SemiCondensed", "avares://shoji/Assets/Fonts#Barlow Condensed"),
        ("Bahnschrift Light SemiCondensed", "avares://shoji/Assets/Fonts#Barlow Condensed"),
        ("Bahnschrift SemiBold", "avares://shoji/Assets/Fonts#Barlow"),
        ("Bahnschrift", "avares://shoji/Assets/Fonts#Barlow"),
        ("Segoe UI Variable Display", "Inter, Ubuntu, Noto Sans"),
        ("Segoe UI Variable Text", "Inter, Ubuntu, Noto Sans"),
        ("Segoe UI", "Inter, Ubuntu, Noto Sans, DejaVu Sans"),
        ("Yu Gothic UI", "Noto Sans CJK JP"),
        ("Meiryo UI", "Noto Sans CJK JP"),
        ("Yu Mincho", "Noto Serif CJK JP, Noto Sans CJK JP"),
        ("MS PMincho", "Noto Serif CJK JP"),
        ("Georgia", "DejaVu Serif"),
        ("Cascadia Mono", "JetBrains Mono, Ubuntu Mono, DejaVu Sans Mono"),
        ("Consolas", "DejaVu Sans Mono"),
    };

    static readonly Dictionary<string, FontFamily> FamilyCache = new();

    /// <summary>A theme font list ("./Fonts/#Michroma, Bahnschrift, Segoe UI") as an Avalonia family with Linux fallbacks.</summary>
    public static FontFamily Family(string spec)
    {
        if (FamilyCache.TryGetValue(spec, out var cached))
            return cached;
        var names = new List<string>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = raw.StartsWith("./Fonts/#") ? "avares://shoji/Assets/Fonts#" + raw["./Fonts/#".Length..] : raw;
            var mapped = FontMap.FirstOrDefault(m => m.Windows.Equals(raw, StringComparison.OrdinalIgnoreCase)).Linux;
            foreach (var n in (mapped ?? name).Split(',', StringSplitOptions.TrimEntries))
                if (!names.Contains(n))
                    names.Add(n);
        }
        // Japanese characters (kanji titles, weekday names) need a CJK font somewhere in the list.
        if (!names.Contains("Noto Sans CJK JP"))
            names.Add("Noto Sans CJK JP");
        var family = new FontFamily(string.Join(", ", names));
        FamilyCache[spec] = family;
        return family;
    }

    public static string StyleFor(string type, string? chosen)
    {
        var allowed = ModuleCatalog.Find(type)?.Styles;
        if (allowed == null || allowed.Length == 0)
            return "classic";
        bool Ok(string? s) => s != null && allowed.Any(a => a.Id.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (Ok(chosen))
            return chosen!.ToLowerInvariant();
        return Current.Styles.TryGetValue(type, out var d) && Ok(d) ? d : allowed[0].Id;
    }

    public static IBrush ForTemperature(double? celsius) =>
        celsius >= TempCrit ? Crit : celsius >= TempWarn ? Warn : Text;

    public static IBrush ForPercent(double? percent) =>
        percent >= 90 ? Crit : percent >= 75 ? Warn : Accent;

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static IBrush Solid(Color c, byte alpha = 0xFF) => new ImmutableSolidColorBrush(WithAlpha(c, alpha));

    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static Color Parse(string value, string fallback = "#FFFFFF") =>
        Color.TryParse(value, out var c) ? c : Color.TryParse(fallback, out var f) ? f : Colors.White;

    static IBrush Solid(string value) => new ImmutableSolidColorBrush(Parse(value));
}
