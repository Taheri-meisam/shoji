using System;
using System.Collections.Generic;
using System.Windows;

namespace Shoji.UI;

public enum TileShape
{
    /// <summary>Rounded rectangle.</summary>
    Rounded,
    /// <summary>Square panel with targeting brackets in the corners.</summary>
    Hud,
    /// <summary>Two corners cut off at 45°, with an accent notch.</summary>
    Chamfer,
    /// <summary>Paper card with a brush-stroke line along the top.</summary>
    Ink,
    /// <summary>Supercar panel: two big rounded corners, two tight ones.</summary>
    Blade,
    /// <summary>Technical drawing: hairline frame with registration marks and a dimension line.</summary>
    Drafting,
}

public enum MeterStyle { Pill, Segmented, Line }

public enum BackdropStyle { None, Glow, Grid, Scanlines, Synthwave, Ink, Sakura, Carbon, Space, Aurora, Blueprint, Kintsugi, Fjord, Bamboo, Blobs, Ocean, Sunset, Paper, Dots, Wave }

public enum TitleStyle
{
    Plain,
    /// <summary>"▸ CPU"</summary>
    Arrow,
    /// <summary>"// CPU"</summary>
    Slash,
    /// <summary>"CPU 処理装置"</summary>
    Kanji,
    /// <summary>"&gt; cpu_" (terminal prompt)</summary>
    Prompt,
}

/// <summary>Everything that makes a theme look the way it does. Colours are "#RRGGBB" or "#AARRGGBB".</summary>
public sealed class ThemeDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Where the theme sits in the Settings gallery.</summary>
    public string Group { get; init; } = "Everyday";

    public string Background { get; init; } = "#0B0E14";
    /// <summary>Bottom colour of a vertical background gradient (null = flat).</summary>
    public string? Background2 { get; init; }
    public string Tile { get; init; } = "#151A24";
    public string? TileBorder { get; init; }
    public double BorderThickness { get; init; } = 1;
    public string Accent { get; init; } = "#4FC3F7";
    /// <summary>Second series colour (upload, second graph line, highlights).</summary>
    public string Accent2 { get; init; } = "#CE93D8";
    public string Text { get; init; } = "#E8EBF2";
    public string Dim { get; init; } = "#8A93A6";
    public string Track { get; init; } = "#252C3A";
    public string Warn { get; init; } = "#FFB74D";
    public string Crit { get; init; } = "#EF5350";
    public string Good { get; init; } = "#81C784";

    public string Font { get; init; } = "Segoe UI Variable Display, Segoe UI";
    /// <summary>Font for big numbers (null = <see cref="Font"/>).</summary>
    public string? DisplayFont { get; init; }
    /// <summary>Font for tile titles and kanji (null = <see cref="Font"/>).</summary>
    public string? TitleFont { get; init; }
    public string Mono { get; init; } = "Cascadia Mono, Consolas";
    public FontWeight NumberWeight { get; init; } = FontWeights.SemiBold;
    public bool UpperTitles { get; init; } = true;

    public double Radius { get; init; } = 12;
    public TileShape Shape { get; init; } = TileShape.Rounded;
    public MeterStyle Meter { get; init; } = MeterStyle.Pill;
    public BackdropStyle Backdrop { get; init; } = BackdropStyle.None;
    public TitleStyle Title { get; init; } = TitleStyle.Plain;

    /// <summary>Neon glow around the tile outline.</summary>
    public bool TileGlow { get; init; }
    /// <summary>Soft drop shadow under tiles (light themes).</summary>
    public bool SoftShadow { get; init; }
    /// <summary>Accent-coloured glow behind big numbers.</summary>
    public bool TextGlow { get; init; }
    /// <summary>Red/cyan offset "glitch" shadow behind big numbers.</summary>
    public bool Glitch { get; init; }
    /// <summary>Wide faint stroke under graph lines.</summary>
    public bool GraphGlow { get; init; }
    /// <summary>Opacity (0–255) of the area under graph lines.</summary>
    public byte GraphFill { get; init; } = 90;
    /// <summary>Japanese date line and weekday names, kanji weather.</summary>
    public bool Japanese { get; init; }

    /// <summary>Hairline gauge arcs (Capsule).</summary>
    public bool ThinGauges { get; init; }
    /// <summary>Slanted numbers on gauges (Hypercar).</summary>
    public bool ItalicNumbers { get; init; }
    /// <summary>CRT scanlines and curved-glass vignette over the whole screen (Cassette).</summary>
    public bool Crt { get; init; }

    /// <summary>
    /// The gauge style each tile type uses with this theme unless the tile picks its own,
    /// e.g. { ["cpu"] = "led", ["fps"] = "speedometer" }. Missing types use "classic".
    /// </summary>
    public IReadOnlyDictionary<string, string> Styles { get; init; } = new Dictionary<string, string>();
}

/// <summary>The built-in themes. Add one here and it shows up in the tray menu and Settings.</summary>
public static class Themes
{
    const string JpSans = "Yu Gothic UI, Meiryo UI, Segoe UI";
    const string JpSerif = "Yu Mincho, MS PMincho, Georgia";
    const string Bahn = "Bahnschrift SemiCondensed, Bahnschrift, Segoe UI";

    // Fonts bundled in the exe (Fonts/, SIL Open Font License). "./Fonts/#Name" loads them from the app's resources.
    const string Michroma = "./Fonts/#Michroma, Bahnschrift, Segoe UI";
    const string Orbitron = "./Fonts/#Orbitron, Bahnschrift, Segoe UI";
    const string Barlow = "./Fonts/#Barlow, Segoe UI";
    const string BarlowCondensed = "./Fonts/#Barlow Condensed, Bahnschrift SemiCondensed, Segoe UI";
    const string TechMono = "./Fonts/#Share Tech Mono, Cascadia Mono, Consolas";
    const string Vt323 = "./Fonts/#VT323, Consolas";
    const string JetMono = "./Fonts/#JetBrains Mono, Cascadia Mono, Consolas";
    const string Caveat = "./Fonts/#Caveat, Segoe Print, Segoe UI";

    static Dictionary<string, string> S(params string[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pairs)
        {
            var kv = p.Split('=');
            d[kv[0]] = kv[1];
        }
        return d;
    }

    public static readonly IReadOnlyList<ThemeDef> All = new[]
    {
        new ThemeDef
        {
            Id = "midnight", Name = "Midnight", Description = "The original: calm dark blue, rounded tiles",
            Background = "#0B0E14", Background2 = "#0F1522", Backdrop = BackdropStyle.Dots,
        },
        new ThemeDef
        {
            Id = "modern", Name = "Modern", Description = "Graphite glass, soft colour glow, light numbers",
            Background = "#0D0E12", Background2 = "#171923", Tile = "#D91B1D26", TileBorder = "#2C303D",
            Accent = "#8B9CFF", Accent2 = "#5EEAD4", Text = "#F2F3F7", Dim = "#9097A8", Track = "#2A2D3A",
            Warn = "#FBBF24", Crit = "#F87171", Good = "#34D399",
            NumberWeight = FontWeights.Light, UpperTitles = false, Radius = 18,
            Backdrop = BackdropStyle.Glow, GraphFill = 70,
            Styles = S("memory=rings", "power=arc", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "futuristic", Group = "Sci-fi & speed", Name = "Futuristic", Description = "Holographic HUD, corner brackets, segmented meters",
            Background = "#02070D", Background2 = "#05141F", Tile = "#B3081822", TileBorder = "#1A4F63",
            Accent = "#00E5FF", Accent2 = "#FFB000", Text = "#D8F6FF", Dim = "#5E8C9C", Track = "#0F2C38",
            Warn = "#FFB000", Crit = "#FF3D57", Good = "#3DFFA8",
            Font = Bahn, DisplayFont = "Bahnschrift Light SemiCondensed, " + Bahn, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 0, Shape = TileShape.Hud, Meter = MeterStyle.Segmented,
            Backdrop = BackdropStyle.Grid, Title = TitleStyle.Arrow, TextGlow = true, GraphGlow = true, GraphFill = 50,
            Styles = S("cpu=arc", "gpu=arc", "memory=arc", "fps=tape", "power=arc", "temps=tapes", "network=scope", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "cyberpunk", Group = "Sci-fi & speed", Name = "Cyberpunk", Description = "Night-city yellow, chamfered panels, glitchy numbers",
            Background = "#09090D", Tile = "#14141B", TileBorder = "#3A3A22",
            Accent = "#FCEE0A", Accent2 = "#00F0FF", Text = "#F5F5EE", Dim = "#8E8C76", Track = "#26261A",
            Warn = "#FF8A00", Crit = "#FF003C", Good = "#00FF9F",
            Font = Bahn, DisplayFont = "Bahnschrift Condensed, " + Bahn,
            NumberWeight = FontWeights.Bold, Radius = 0, Shape = TileShape.Chamfer, Meter = MeterStyle.Segmented,
            Backdrop = BackdropStyle.Scanlines, Title = TitleStyle.Slash, Glitch = true, GraphFill = 40,
            Styles = S("cpu=led", "gpu=led", "memory=led", "fps=segments", "processes=radar", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "tokyo", Group = "Japanese", Name = "Tokyo Neon", Description = "Neon tubes over a synthwave grid, katakana signs",
            Background = "#0A0318", Background2 = "#1D0634", Tile = "#B8120726", TileBorder = "#FF3EA5", BorderThickness = 1.2,
            Accent = "#FF3EA5", Accent2 = "#00F0FF", Text = "#FCEEFF", Dim = "#A58BC6", Track = "#2E1A4A",
            Warn = "#FFC857", Crit = "#FF4D4D", Good = "#7CFFCB",
            Font = "Bahnschrift, " + JpSans, DisplayFont = "Bahnschrift SemiBold, Bahnschrift, " + JpSans, TitleFont = JpSans,
            Radius = 10, Backdrop = BackdropStyle.Synthwave, Title = TitleStyle.Kanji,
            TileGlow = true, TextGlow = true, GraphGlow = true, Japanese = true,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "sumi", Group = "Japanese", Name = "Sumi-e", Description = "Japanese ink: charcoal, vermilion and gold, Mincho type",
            Background = "#13100D", Tile = "#1C1814", TileBorder = "#342C23",
            Accent = "#D9503A", Accent2 = "#C9A45C", Text = "#EFE7D7", Dim = "#9A8E7C", Track = "#2C251E",
            Warn = "#E0A040", Crit = "#F5336A", Good = "#8DB580",
            Font = JpSans, DisplayFont = JpSerif, TitleFont = JpSerif,
            NumberWeight = FontWeights.Normal, Radius = 3, Shape = TileShape.Ink, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Ink, Title = TitleStyle.Kanji, Japanese = true, GraphFill = 45,
            Styles = S("cpu=enso", "gpu=enso", "memory=enso", "timer=enso"),
        },
        new ThemeDef
        {
            Id = "sakura", Group = "Japanese", Name = "Sakura", Description = "Light washi paper, cherry-blossom pink and indigo",
            Background = "#F4EEE5", Background2 = "#ECE2D5", Tile = "#E6FCFAF6", TileBorder = "#E2D6C6",
            Accent = "#D2557A", Accent2 = "#3D5A8A", Text = "#2A2623", Dim = "#766B61", Track = "#ECE2D6",
            Warn = "#AD6810", Crit = "#C62828", Good = "#4E8A5A",
            Font = JpSans, DisplayFont = JpSerif, TitleFont = JpSerif,
            NumberWeight = FontWeights.Normal, Radius = 8, Shape = TileShape.Ink, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Sakura, Title = TitleStyle.Kanji, SoftShadow = true, Japanese = true, GraphFill = 60,
            Styles = S("cpu=enso", "gpu=enso", "memory=enso", "timer=enso"),
        },
        new ThemeDef
        {
            Id = "mission", Group = "Sci-fi & speed", Name = "Mission Control", Description = "Flight-director calm: navy consoles, caution lamps, cockpit tapes",
            Background = "#0A1220", Background2 = "#0C1729", Tile = "#0F1A2C", TileBorder = "#1F3150",
            Accent = "#8CC4FF", Accent2 = "#3DDC84", Text = "#E6EDF7", Dim = "#7F93B3", Track = "#1D2C45",
            Warn = "#FFB020", Crit = "#FF4D4D", Good = "#3DDC84",
            Font = BarlowCondensed, DisplayFont = BarlowCondensed, TitleFont = BarlowCondensed, Mono = TechMono,
            NumberWeight = FontWeights.SemiBold, Radius = 2, Backdrop = BackdropStyle.Grid, GraphFill = 55,
            Styles = S("cpu=arc", "gpu=arc", "memory=arc", "fps=tape", "temps=tapes", "network=scope", "ping=scope", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "capsule", Group = "Sci-fi & speed", Name = "Capsule", Description = "Spacecraft-touchscreen minimalism: true black, white hairlines, big targets",
            Background = "#000000", Tile = "#050505", TileBorder = "#262626",
            Accent = "#FFFFFF", Accent2 = "#5AA9FF", Text = "#F2F2F2", Dim = "#8A8A8A", Track = "#1E1E1E",
            Warn = "#FFC24B", Crit = "#FF5A5A", Good = "#7FE0A8",
            Font = Barlow, DisplayFont = Barlow, TitleFont = Barlow, Mono = Barlow,
            NumberWeight = FontWeights.Light, Radius = 14, ThinGauges = true, GraphFill = 40,
            Styles = S("cpu=arc", "gpu=arc", "memory=arc", "fps=arc", "power=arc", "battery=arc", "timer=arc", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "cassette", Group = "Retro", Name = "Cassette", Description = "Used-future 1979: amber phosphor CRT, scanlines, LED digits, oscilloscopes",
            Background = "#12110C", Tile = "#17150F", TileBorder = "#3B3424",
            Accent = "#FFB000", Accent2 = "#33FF77", Text = "#FFB000", Dim = "#A27C28", Track = "#2A2414",
            Warn = "#FFF07A", Crit = "#FF4A2E", Good = "#33FF77",
            Font = TechMono, DisplayFont = Vt323, TitleFont = Vt323, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 3, Meter = MeterStyle.Segmented, Crt = true,
            TextGlow = true, GraphGlow = true, GraphFill = 35,
            Styles = S("cpu=segments", "gpu=segments", "fps=segments", "power=segments", "temps=segments", "memory=led", "network=scope", "ping=scope", "clock=segments"),
        },
        new ThemeDef
        {
            Id = "hypercar", Group = "Sci-fi & speed", Name = "Hypercar", Description = "Electric supercar cluster: carbon weave, speedometer, shift lights, coolant tubes",
            Background = "#0B0B0D", Tile = "#E6141417", TileBorder = "#2A2A2E",
            Accent = "#FF5A1F", Accent2 = "#FFD2B8", Text = "#F0F0F0", Dim = "#8D8D93", Track = "#232327",
            Warn = "#FFB020", Crit = "#FF1F4B", Good = "#46E0A8",
            Font = Barlow, DisplayFont = Michroma, TitleFont = Michroma, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 18, Shape = TileShape.Blade, Meter = MeterStyle.Segmented,
            Backdrop = BackdropStyle.Carbon, ItalicNumbers = true, GraphFill = 50,
            Styles = S("cpu=led", "gpu=led", "fps=speedometer", "temps=tubes", "power=speedometer", "memory=arc", "battery=arc", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "space", Group = "Sci-fi & speed", Name = "Deep Space", Description = "Nebula and stars behind glass, your PC as a little solar system",
            Background = "#03040C", Background2 = "#150A33", Tile = "#B30A0C1E", TileBorder = "#3A9DB4FF",
            Accent = "#A9BCFF", Accent2 = "#FF9ECD", Text = "#E8ECFF", Dim = "#8D96C4", Track = "#262E52",
            Warn = "#FFD27A", Crit = "#FF6B8B", Good = "#7EF0D0",
            Font = Barlow, DisplayFont = Orbitron, TitleFont = Orbitron, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 16, Backdrop = BackdropStyle.Space, TextGlow = true, GraphGlow = true,
            Styles = S("overview=orbit", "memory=rings", "processes=radar", "cpu=arc", "gpu=arc", "fps=arc"),
        },
        new ThemeDef
        {
            Id = "aurora", Name = "Aurora", Description = "Northern lights drifting behind frosted glass, green and violet",
            Background = "#040A14", Background2 = "#0A1830", Tile = "#A60B1424", TileBorder = "#2E3F6B8F",
            Accent = "#5CF2C4", Accent2 = "#B07CFF", Text = "#E6F6FF", Dim = "#7FA0B8", Track = "#1A2C40",
            Warn = "#FFD166", Crit = "#FF6B81", Good = "#5CF2C4",
            Font = Barlow, DisplayFont = Barlow, TitleFont = Barlow, Mono = TechMono,
            NumberWeight = FontWeights.Light, UpperTitles = false, Radius = 16, Backdrop = BackdropStyle.Aurora,
            GraphGlow = true, GraphFill = 60,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "power=arc", "overview=orbit"),
        },
        new ThemeDef
        {
            Id = "fjord", Name = "Fjord", Description = "Calm arctic palette: frost blue, slate and soft mountains",
            Background = "#2E3440", Background2 = "#3B4252", Tile = "#E63B4252", TileBorder = "#434C5E",
            Accent = "#88C0D0", Accent2 = "#B48EAD", Text = "#ECEFF4", Dim = "#A9B2C3", Track = "#4C566A",
            Warn = "#EBCB8B", Crit = "#E07882", Good = "#A3BE8C",
            Font = Barlow, DisplayFont = Barlow, TitleFont = Barlow, Mono = TechMono,
            NumberWeight = FontWeights.Normal, UpperTitles = false, Radius = 10, Backdrop = BackdropStyle.Fjord, GraphFill = 70,
            Styles = S("memory=rings", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "blueprint", Group = "Sci-fi & speed", Name = "Blueprint", Description = "Engineering drawing: blueprint paper, white line work, a title block",
            Background = "#0E3A6B", Background2 = "#0A2F59", Tile = "#140E3A6B", TileBorder = "#B3E8F1FF",
            Accent = "#FFFFFF", Accent2 = "#9FD3FF", Text = "#F2F8FF", Dim = "#A9C7E8", Track = "#2E5E8F",
            Warn = "#FFE08A", Crit = "#FF8A8A", Good = "#B8F0C8",
            Font = BarlowCondensed, DisplayFont = TechMono, TitleFont = BarlowCondensed, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 0, Shape = TileShape.Drafting, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Blueprint, ThinGauges = true, GraphFill = 25,
            Styles = S("cpu=arc", "gpu=arc", "memory=arc", "fps=tape", "temps=tapes", "power=arc", "network=scope", "ping=scope"),
        },
        new ThemeDef
        {
            Id = "kintsugi", Group = "Japanese", Name = "Kintsugi", Description = "Black lacquer mended with gold: cracks that shimmer, Mincho type",
            Background = "#0B0A09", Background2 = "#15120E", Tile = "#F2141210", TileBorder = "#3A2F1C",
            Accent = "#D4A64A", Accent2 = "#E8D3A0", Text = "#EFE8DA", Dim = "#8C8170", Track = "#2A241B",
            Warn = "#F07F2E", Crit = "#E23A4E", Good = "#9DBF7A",
            Font = JpSans, DisplayFont = JpSerif, TitleFont = JpSerif,
            NumberWeight = FontWeights.Normal, Radius = 4, Shape = TileShape.Ink, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Kintsugi, Title = TitleStyle.Kanji, Japanese = true, GraphFill = 45,
            Styles = S("cpu=enso", "gpu=enso", "memory=enso", "timer=enso", "power=arc"),
        },
        new ThemeDef
        {
            Id = "terminal", Group = "Retro", Name = "Terminal", Description = "Green-phosphor console: prompt titles, LED digits, scanlines",
            Background = "#030603", Tile = "#0A100A", TileBorder = "#1F3A1F",
            Accent = "#39FF6A", Accent2 = "#9CFFB0", Text = "#8BFF9E", Dim = "#3E8A4C", Track = "#13261A",
            Warn = "#E8FF59", Crit = "#FF5555", Good = "#39FF6A",
            Font = TechMono, DisplayFont = Vt323, TitleFont = TechMono, Mono = TechMono,
            NumberWeight = FontWeights.Normal, Radius = 0, Meter = MeterStyle.Segmented, Crt = true,
            UpperTitles = false, Title = TitleStyle.Prompt, TextGlow = true, GraphGlow = true, GraphFill = 30,
            Styles = S("cpu=segments", "gpu=segments", "fps=segments", "temps=segments", "memory=led", "network=scope", "ping=scope", "clock=segments", "power=segments", "processes=radar"),
        },
        new ThemeDef
        {
            Id = "glass", Name = "Glass", Description = "Frosted glass tiles over slowly drifting colour",
            Background = "#0C0A1F", Background2 = "#140E30", Tile = "#26FFFFFF", TileBorder = "#3DFFFFFF",
            Accent = "#7CF5FF", Accent2 = "#FF8BD8", Text = "#FFFFFF", Dim = "#D2CCEB", Track = "#2EFFFFFF",
            Warn = "#FFD36E", Crit = "#FF6B8B", Good = "#7CF0A0",
            Font = Barlow, DisplayFont = Barlow, TitleFont = Barlow, Mono = TechMono,
            NumberWeight = FontWeights.Light, UpperTitles = false, Radius = 22, Backdrop = BackdropStyle.Blobs, GraphFill = 70,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "power=arc", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "notebook", Name = "Notebook", Description = "Squared paper, blue ink and a red pen, handwritten numbers",
            Background = "#FBFAF4", Tile = "#A8FFFFFF", TileBorder = "#D6DEEA",
            Accent = "#2F5DA8", Accent2 = "#D9534F", Text = "#1F2A3D", Dim = "#66718A", Track = "#E2E8F1",
            Warn = "#B87612", Crit = "#C9302C", Good = "#3C8D5A",
            Font = Barlow, DisplayFont = Caveat, TitleFont = Caveat, Mono = TechMono,
            NumberWeight = FontWeights.Bold, UpperTitles = false, Radius = 6, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Paper, SoftShadow = true, GraphFill = 40,
            Styles = S("cpu=arc", "gpu=arc", "memory=arc", "power=arc"),
        },
        new ThemeDef
        {
            Id = "deepsea", Group = "Sci-fi & speed", Name = "Deep Sea", Description = "The abyss: light rays from far above, glowing plankton, a drifting jellyfish",
            Background = "#020A12", Background2 = "#04273A", Tile = "#99031521", TileBorder = "#1C4C60",
            Accent = "#3FE8D6", Accent2 = "#8A7CFF", Text = "#DFF8FF", Dim = "#6E9BAA", Track = "#0D2F3E",
            Warn = "#FFCF5C", Crit = "#FF5C7A", Good = "#3FE8A0",
            Font = Barlow, DisplayFont = Barlow, TitleFont = Barlow, Mono = TechMono,
            NumberWeight = FontWeights.Light, UpperTitles = false, Radius = 20, Backdrop = BackdropStyle.Ocean,
            TextGlow = true, GraphGlow = true, GraphFill = 55,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "overview=orbit", "processes=radar"),
        },
        new ThemeDef
        {
            Id = "kanagawa", Group = "Japanese", Name = "Kanagawa", Description = "The great wave at night: indigo, sea-foam and seigaiha scales",
            Background = "#111729", Background2 = "#1C2742", Tile = "#B8182036", TileBorder = "#2C3A5E",
            Accent = "#E46A4B", Accent2 = "#C8BE9A", Text = "#EDE6D3", Dim = "#8E95AE", Track = "#263050",
            Warn = "#E6A84E", Crit = "#E8366B", Good = "#8FB996",
            Font = JpSans, DisplayFont = JpSerif, TitleFont = JpSerif,
            NumberWeight = FontWeights.Normal, Radius = 6, Shape = TileShape.Ink, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Wave, Title = TitleStyle.Kanji, Japanese = true, GraphFill = 50,
            Styles = S("cpu=arc", "gpu=arc", "memory=enso", "timer=enso"),
        },
        new ThemeDef
        {
            Id = "matcha", Group = "Japanese", Name = "Matcha", Description = "Green tea on washi paper, a bamboo grove, leaves drifting down",
            Background = "#EEF0E2", Background2 = "#E3E8D0", Tile = "#C7FBFCF5", TileBorder = "#D3DBBD",
            Accent = "#5B7F3A", Accent2 = "#A8743F", Text = "#24301C", Dim = "#626C57", Track = "#DCE3CC",
            Warn = "#9A6C10", Crit = "#B23A2E", Good = "#4E8A3A",
            Font = JpSans, DisplayFont = JpSerif, TitleFont = JpSerif,
            NumberWeight = FontWeights.Normal, Radius = 8, Shape = TileShape.Ink, Meter = MeterStyle.Line,
            Backdrop = BackdropStyle.Bamboo, Title = TitleStyle.Kanji, SoftShadow = true, Japanese = true, GraphFill = 55,
            Styles = S("cpu=enso", "gpu=enso", "memory=enso", "timer=enso"),
        },
        new ThemeDef
        {
            Id = "sunset", Group = "Retro", Name = "Lo-fi Sunset", Description = "A striped sun sinking behind purple hills, warm and slow",
            Background = "#1E1238", Background2 = "#7A2E5C", Tile = "#C7201433", TileBorder = "#4DFF9E7A",
            Accent = "#FFB36B", Accent2 = "#FF6FA3", Text = "#FFF2E6", Dim = "#D2B0C8", Track = "#3E2A5A",
            Warn = "#FFE14D", Crit = "#FF5470", Good = "#8EF0B0",
            Font = Barlow, DisplayFont = Barlow, TitleFont = BarlowCondensed, Mono = TechMono,
            NumberWeight = FontWeights.Light, UpperTitles = false, Radius = 18, Backdrop = BackdropStyle.Sunset,
            GraphGlow = true, GraphFill = 60,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "power=arc"),
        },
        new ThemeDef
        {
            Id = "catppuccin", Group = "Code", Name = "Catppuccin", Description = "The Mocha palette: soothing pastels on deep blue-grey",
            Background = "#11111B", Background2 = "#1E1E2E", Tile = "#E61E1E2E", TileBorder = "#313244",
            Accent = "#CBA6F7", Accent2 = "#89DCEB", Text = "#CDD6F4", Dim = "#9399B2", Track = "#313244",
            Warn = "#F9E2AF", Crit = "#F38BA8", Good = "#A6E3A1",
            Font = Barlow, DisplayFont = JetMono, TitleFont = Barlow, Mono = JetMono,
            NumberWeight = FontWeights.Normal, UpperTitles = false, Radius = 16, Backdrop = BackdropStyle.Glow, GraphFill = 60,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "overview=rings"),
        },
        new ThemeDef
        {
            Id = "dracula", Group = "Code", Name = "Dracula", Description = "The famous dark editor palette: purple, pink and neon green",
            Background = "#1A1B24", Background2 = "#282A36", Tile = "#282A36", TileBorder = "#44475A",
            Accent = "#BD93F9", Accent2 = "#FF79C6", Text = "#F8F8F2", Dim = "#8A95C7", Track = "#44475A",
            Warn = "#FFB86C", Crit = "#FF5555", Good = "#50FA7B",
            Font = JetMono, DisplayFont = JetMono, TitleFont = JetMono, Mono = JetMono,
            NumberWeight = FontWeights.Bold, UpperTitles = false, Radius = 8, Backdrop = BackdropStyle.Dots,
            Title = TitleStyle.Slash, GraphFill = 50,
            Styles = S("cpu=led", "gpu=led", "memory=led", "network=scope"),
        },
        new ThemeDef
        {
            Id = "gruvbox", Group = "Code", Name = "Gruvbox", Description = "Retro groove: warm browns, mustard and faded aqua, monospace",
            Background = "#1D2021", Background2 = "#282828", Tile = "#282828", TileBorder = "#3C3836",
            Accent = "#FABD2F", Accent2 = "#83A598", Text = "#EBDBB2", Dim = "#A89984", Track = "#3C3836",
            Warn = "#FE8019", Crit = "#FB4934", Good = "#B8BB26",
            Font = JetMono, DisplayFont = JetMono, TitleFont = JetMono, Mono = JetMono,
            NumberWeight = FontWeights.Bold, UpperTitles = false, Radius = 3, Meter = MeterStyle.Segmented,
            Backdrop = BackdropStyle.Dots, Title = TitleStyle.Prompt, GraphFill = 45,
            Styles = S("cpu=segments", "fps=segments", "memory=led", "network=scope", "ping=scope"),
        },
        new ThemeDef
        {
            Id = "tokyonight", Group = "Code", Name = "Tokyo Night", Description = "The editor theme: city lights after rain, soft blue and violet",
            Background = "#13141C", Background2 = "#1A1B26", Tile = "#E624283B", TileBorder = "#2B3048",
            Accent = "#7AA2F7", Accent2 = "#BB9AF7", Text = "#C0CAF5", Dim = "#848CB5", Track = "#292E42",
            Warn = "#E0AF68", Crit = "#F7768E", Good = "#9ECE6A",
            Font = Barlow, DisplayFont = JetMono, TitleFont = Barlow, Mono = JetMono,
            NumberWeight = FontWeights.Normal, UpperTitles = false, Radius = 12, Backdrop = BackdropStyle.Glow,
            GraphGlow = true, GraphFill = 55,
            Styles = S("cpu=arc", "gpu=arc", "memory=rings", "network=scope"),
        },
        new ThemeDef
        {
            Id = "oled", Name = "OLED Black", Description = "Pure black and thin outlines, kindest to OLED panels",
            Background = "#000000", Tile = "#000000", TileBorder = "#1E242E",
            Dim = "#7C8596", Track = "#1A1F28", GraphFill = 60,
        },
    };
}
