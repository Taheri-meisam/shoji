using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shoji.Sensors;

/// <summary>One period of the day-ahead electricity price (an hour, or a quarter where the market has moved to those), in NOK per kWh.</summary>
public sealed record PricePoint(DateTime Start, DateTime End, double Price);

/// <summary>Today's prices and, once published (around 13:00), tomorrow's.</summary>
public sealed record PowerPrices(string Area, IReadOnlyList<PricePoint> Points)
{
    public PricePoint? At(DateTime now) => Points.FirstOrDefault(p => p.Start <= now && now < p.End);

    /// <summary>The cheapest stretch of <paramref name="hours"/> still ahead (or running): when to start the washing machine.</summary>
    public (DateTime Start, double Average)? Cheapest(DateTime now, double hours)
    {
        var ahead = Points.Where(p => p.End > now).ToList();
        (DateTime, double)? best = null;
        for (int i = 0; i < ahead.Count; i++)
        {
            var until = ahead[i].Start.AddHours(hours);
            var span = ahead.Skip(i).TakeWhile(p => p.Start < until).ToList();
            if (span.Count == 0 || span[^1].End < until)
                break; // not enough of the day left for a full stretch
            double average = span.Average(p => p.Price);
            if (best == null || average < best.Value.Item2)
                best = (ahead[i].Start, average);
        }
        return best;
    }
}

/// <summary>
/// Norway's day-ahead spot prices per price area (NO1–NO5) from hvakosterstrommen.no, a free public feed
/// ("Strømpriser levert av Hva koster strømmen.no"). Spot price without VAT, grid rent and surcharges.
/// </summary>
public sealed class PowerPriceService : Poller<PowerPrices>
{
    public static readonly string[] Areas = { "NO1", "NO2", "NO3", "NO4", "NO5" };

    readonly string _area;

    public PowerPriceService(string? area) : base(TimeSpan.FromMinutes(30))
    {
        _area = Areas.FirstOrDefault(a => a.Equals(area?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "NO1";
        Start();
    }

    static string BaseUrl =>
#if SHOJI_LINUX
        Demo.Enabled ? "https://demo.shoji.invalid/prices/" :
#endif
        "https://www.hvakosterstrommen.no/api/v1/prices/";

    protected override async Task<PowerPrices> FetchAsync(CancellationToken ct)
    {
        var today = DateTime.Now.Date;
        var points = await DayAsync(today, ct);
        try
        {
            // Tomorrow's prices are published in the early afternoon; before that the address simply isn't there.
            points.AddRange(await DayAsync(today.AddDays(1), ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // not published yet
        }
        return new PowerPrices(_area, points);
    }

    async Task<List<PricePoint>> DayAsync(DateTime day, CancellationToken ct)
    {
        string url = $"{BaseUrl}{day:yyyy}/{day:MM}-{day:dd}_{_area}.json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var list = new List<PricePoint>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var start = DateTimeOffset.Parse(e.GetProperty("time_start").GetString()!, CultureInfo.InvariantCulture).LocalDateTime;
            var end = DateTimeOffset.Parse(e.GetProperty("time_end").GetString()!, CultureInfo.InvariantCulture).LocalDateTime;
            list.Add(new PricePoint(start, end, e.GetProperty("NOK_per_kWh").GetDouble()));
        }
        return list;
    }
}
