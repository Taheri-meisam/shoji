using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoji.UI;

/// <summary>
/// How tiles are arranged on a page. Shared by the dashboard and the layout preview in Settings,
/// so the preview always matches what the screen shows.
/// </summary>
public static class TileLayout
{
    // Rough "comfortable" tile size in DIPs; the layout picks the column count that fits this shape best.
    public const double IdealTileWidth = 340, IdealTileHeight = 230;

    /// <summary>The column count whose tiles come closest to the ideal shape, and how well they fit (the dashboard's base scale).</summary>
    public static (int Columns, double Fit) Best(IReadOnlyList<int> spans, double width, double height, int fixedColumns)
    {
        if (spans.Count == 0)
            return (Math.Max(1, fixedColumns), 1);
        int best = 1;
        double bestFit = 0;
        int max = fixedColumns > 0 ? fixedColumns : spans.Sum(s => Math.Max(1, Math.Min(s, 64)));
        for (int cols = fixedColumns > 0 ? fixedColumns : 1; cols <= max; cols++)
        {
            int rows = Pack(spans, cols).Max(p => p.Row) + 1;
            double fit = Math.Min(width / cols / IdealTileWidth, height / rows / IdealTileHeight);
            if (fit > bestFit)
            {
                bestFit = fit;
                best = cols;
            }
        }
        return (best, bestFit);
    }

    /// <summary>Smallest tile (in tile units, before scaling) that still shows a tile's essentials in its compact form.</summary>
    public const double MinTileWidth = 170, MinTileHeight = 112;

    /// <summary>Body text is 12.5 units; never scale it below this many physical pixels.</summary>
    public const double MinTextPx = 10.5;

    /// <summary>How far a page may scale up on big screens.</summary>
    public const double MaxScale = 2.4;

    /// <summary>One screenful of a page: how many of its tiles, in how many columns, at what scale.</summary>
    public sealed record ScreenPlan(int Start, int Count, int Columns, double Scale)
    {
        /// <summary>Body text size in physical pixels at this scale.</summary>
        public double TextPx(double displayScale) => 12.5 * Scale * displayScale;
    }

    /// <summary>
    /// Splits a page's tiles into screenfuls. Each screenful is scaled to fit, but never below a readable text size;
    /// when that would leave tiles smaller than their compact minimum, the remaining tiles move to the next screenful
    /// (shown as an extra page you swipe to) instead of shrinking further.
    /// </summary>
    /// <param name="width">Usable width in DIPs.</param>
    /// <param name="displayScale">Windows display scale of the screen (1.25 = 125 %).</param>
    /// <param name="userScale">The "Text size" setting.</param>
    public static List<ScreenPlan> Plan(IReadOnlyList<int> spans, double width, double height, int fixedColumns, double displayScale, double userScale)
    {
        var plans = new List<ScreenPlan>();
        double minScale = MinTextPx / (12.5 * Math.Max(0.5, displayScale));
        int start = 0;
        do
        {
            int take = spans.Count - start;
            while (true)
            {
                var part = spans.Skip(start).Take(take).ToList();
                var (cols, fit) = Best(part, width, height, fixedColumns);
                double scale = Math.Clamp(fit, minScale, MaxScale) * Math.Clamp(userScale, 0.25, 4);
                int rows = part.Count == 0 ? 1 : Pack(part, cols).Max(p => p.Row) + 1;
                double cellW = width / scale / cols, cellH = height / scale / rows;
                if (take <= 1 || cellW >= MinTileWidth && cellH >= MinTileHeight)
                {
                    plans.Add(new ScreenPlan(start, take, cols, scale));
                    break;
                }
                take--;
            }
            start += Math.Max(1, plans[^1].Count);
        }
        while (start < spans.Count);
        return plans;
    }

    /// <summary>
    /// How much height a tile can use, relative to an average tile, when a page is stacked tall (a portrait screen):
    /// a round gauge or a graph grows with the height it gets, a few lines of text don't. Rows share the height by
    /// these weights instead of equally, so gauges stop being squeezed while a three-line list sits in empty space.
    /// </summary>
    public static double HeightWeight(string type, string style)
    {
        bool classic = style is "classic";
        return type switch
        {
            "taskmanager" => 1.5,
            "processes" => classic ? 1.0 : 1.25,
            "mirror" or "photos" => 1.25,
            "overview" => 1.2,
            "cpu" or "gpu" or "fps" => classic ? (type == "fps" ? 0.85 : 1.05) : style == "segments" ? 0.95 : 1.15,
            "memory" or "battery" or "power" or "timer" or "webvalue" or "ping" =>
                classic ? (type is "webvalue" ? 0.8 : type is "ping" ? 0.95 : 0.9) : style == "segments" ? 0.9 : 1.1,
            "temps" => classic ? 0.95 : style == "segments" ? 0.9 : 1.1,
            "network" => classic ? 1.0 : 1.15,
            "calendar" or "notifications" or "lyrics" => 1.15,
            "forecast" or "history" or "deck" or "mixer" or "aiusage" or "homeassistant" => 1.1,
            "status" => 1.05,
            "recap" => 1.05,
            "clock" => classic ? 0.9 : 1.0,
            "sensors" or "fans" or "system" or "disks" or "drivehealth" => 0.9,
            "media" => 0.95,
            "ticker" => 0.6,
            _ => 1.0,
        };
    }

    /// <summary>
    /// A photo frame or mirror that has a whole row to itself is the point of its page: its row gets this many
    /// shares of the height against one for an ordinary row (0 = no such rule for this tile).
    /// </summary>
    public static double ShowpieceWeight(string type, bool large = false) =>
        type is "photos" or "mirror" ? 2.4
        : type == "clock" && large ? 1.7 // a big clock across the page is what the page is for, too
        : 0;

    /// <summary>True when a page's tiles are stacked tall (two or more rows per column), where rows use <see cref="HeightWeight"/>.</summary>
    public static bool Tall(int rows, int columns) => columns <= 2 && rows >= columns * 2 && rows >= 3;

    /// <summary>
    /// Places tiles left to right, wrapping when a tile doesn't fit. The last tile in a row that isn't
    /// full is stretched to the right edge, so there are never empty holes.
    /// </summary>
    public static List<(int Row, int Col, int Span)> Pack(IReadOnlyList<int> spans, int cols)
    {
        var result = new List<(int Row, int Col, int Span)>();
        int row = 0, col = 0;
        foreach (var s in spans)
        {
            int span = Math.Clamp(s, 1, cols);
            if (col + span > cols)
            {
                Stretch(result, cols);
                row++;
                col = 0;
            }
            result.Add((row, col, span));
            col += span;
            if (col == cols)
            {
                row++;
                col = 0;
            }
        }
        Stretch(result, cols);
        return result;
    }

    static void Stretch(List<(int Row, int Col, int Span)> placed, int cols)
    {
        if (placed.Count == 0)
            return;
        var last = placed[^1];
        placed[^1] = (last.Row, last.Col, cols - last.Col);
    }
}
