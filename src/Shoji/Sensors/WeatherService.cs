using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
#if !SHOJI_LINUX
using Windows.Devices.Geolocation;
#endif
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Weather from Open-Meteo (free, no account or API key). The location is a city name from config,
/// "lat,lon", or – if left empty – Windows location services. Refreshed every 15 minutes.
/// </summary>
public sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    readonly string _city;
    (double Lat, double Lon, string Name)? _place;
    volatile WeatherState _state = new(null, L.T("Loading weather…"));
    DateTime _nextUpdate = DateTime.MinValue;
    int _busy;

    public WeatherService(string? city) => _city = (city ?? "").Trim();

    /// <summary>Latest weather; starts a background refresh when it's due. Never blocks.</summary>
    public WeatherState Read()
    {
        if (DateTime.UtcNow >= _nextUpdate && System.Threading.Interlocked.Exchange(ref _busy, 1) == 0)
            _ = RefreshAsync();
        return _state;
    }

    async Task RefreshAsync()
    {
        try
        {
            _place ??= await ResolveAsync();
            if (_place is not { } place)
            {
                _state = new WeatherState(null, _city.Length > 0
                    ? L.F("Couldn't find \"{0}\". Check it in Settings → Weather & time.", Label(_city))
                    : L.T("Set your city in Settings → Weather & time (or allow location access in Windows)."));
                _nextUpdate = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                return;
            }

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={place.Lat:0.####}&longitude={place.Lon:0.####}") +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                "&hourly=temperature_2m,precipitation_probability,weather_code,is_day" +
                "&timezone=auto&forecast_days=4";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");

            var days = new List<WeatherDay>();
            var dates = daily.GetProperty("time");
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                days.Add(new WeatherDay(
                    DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                    daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                    daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                    daily.GetProperty("weather_code")[i].GetInt32(),
                    daily.GetProperty("precipitation_probability_max")[i].ValueKind == JsonValueKind.Number
                        ? daily.GetProperty("precipitation_probability_max")[i].GetInt32() : null));
            }

            // Hourly data starts at midnight; keep the next 24 hours from the location's current hour.
            var hours = new List<WeatherHour>();
            if (root.TryGetProperty("hourly", out var hourly))
            {
                var nowThere = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
                var from = new DateTime(nowThere.Year, nowThere.Month, nowThere.Day, nowThere.Hour, 0, 0);
                var times = hourly.GetProperty("time");
                for (int i = 0; i < times.GetArrayLength() && hours.Count < 24; i++)
                {
                    var t = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
                    if (t < from || hourly.GetProperty("temperature_2m")[i].ValueKind != JsonValueKind.Number
                                  || hourly.GetProperty("weather_code")[i].ValueKind != JsonValueKind.Number)
                        continue;
                    var rain = hourly.GetProperty("precipitation_probability")[i];
                    hours.Add(new WeatherHour(t,
                        hourly.GetProperty("temperature_2m")[i].GetDouble(),
                        rain.ValueKind == JsonValueKind.Number ? rain.GetInt32() : null,
                        hourly.GetProperty("weather_code")[i].GetInt32(),
                        hourly.GetProperty("is_day")[i].GetInt32() == 1));
                }
            }

            _state = new WeatherState(new WeatherInfo(
                place.Name,
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("apparent_temperature").GetDouble(),
                cur.GetProperty("relative_humidity_2m").GetInt32(),
                cur.GetProperty("wind_speed_10m").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("is_day").GetInt32() == 1,
                days,
                DateTime.Now,
                hours), null);
            _nextUpdate = DateTime.UtcNow + Interval;
        }
        catch (Exception ex)
        {
            Log.Write($"Weather update failed: {ex.Message}");
            // Keep showing the last forecast if we had one; try again soon.
            if (_state.Info == null)
                _state = new WeatherState(null, L.T("Weather unavailable (offline?)"));
            _nextUpdate = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
        }
    }

    async Task<(double, double, string)?> ResolveAsync()
    {
        if (_city.Length > 0)
            return await GeocodeAsync(_city) is { } p ? (p.Lat, p.Lon, p.Name) : null;

        return await WindowsLocationAsync();
    }

    /// <summary>A place found for what the user typed, with a full name to confirm it ("Paris, Île-de-France, France").</summary>
    public sealed record Place(double Lat, double Lon, string Name, string FullName);

    /// <summary>
    /// Looks a city up (Open-Meteo geocoding). "Berlin, DE" keeps the first match in that country or region;
    /// "35.68,139.76" is taken as coordinates. Null when nothing matches. Throws when offline.
    /// </summary>
    public static async Task<Place?> GeocodeAsync(string city)
    {
        city = city.Trim();
        // "Tokyo, Japan|35.68,139.69": a place picked in Settings, with its exact position.
        if (Picked(city) is { } picked)
            return picked;
        var parts = city.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return new Place(lat, lon, L.T("Here"), FormattableString.Invariant($"{lat:0.###}, {lon:0.###}"));
        string name = parts[0].Trim();
        if (name.Length == 0)
            return null;
        string? country = parts.Length > 1 ? parts[1].Trim() : null;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&name={Uri.EscapeDataString(name)}"));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;
        JsonElement pick = results[0];
        foreach (var r in results.EnumerateArray())
        {
            if (country != null && (Matches(r, "country_code", country) || Matches(r, "country", country) || Matches(r, "admin1", country)))
            {
                pick = r;
                break;
            }
        }
        string found = pick.GetProperty("name").GetString() ?? name;
        var full = new List<string> { found };
        foreach (var prop in new[] { "admin1", "country" })
            if (pick.TryGetProperty(prop, out var v) && v.GetString() is { Length: > 0 } text && !full.Contains(text))
                full.Add(text);
        return new Place(pick.GetProperty("latitude").GetDouble(), pick.GetProperty("longitude").GetDouble(), found, string.Join(", ", full));
    }

    /// <summary>Places matching what was typed so far, for the city picker in Settings (most relevant first).</summary>
    public static async Task<IReadOnlyList<Place>> SearchAsync(string text, System.Threading.CancellationToken ct)
    {
        string name = text.Split(',')[0].Trim();
        if (name.Length < 2)
            return Array.Empty<Place>();
        using var response = await Http.GetAsync($"https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&name={Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<Place>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var r in results.EnumerateArray())
            {
                string found = r.GetProperty("name").GetString() ?? name;
                var full = new List<string> { found };
                foreach (var prop in new[] { "admin1", "country" })
                    if (r.TryGetProperty(prop, out var v) && v.GetString() is { Length: > 0 } t && !full.Contains(t))
                        full.Add(t);
                list.Add(new Place(r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), found, string.Join(", ", full)));
            }
        }
        return list;
    }

    /// <summary>How a picked place is stored in config.json: "Full name|lat,lon".</summary>
    public static string Store(Place p) => FormattableString.Invariant($"{p.FullName.Replace("|", " ")}|{p.Lat:0.####},{p.Lon:0.####}");

    /// <summary>The name to show for a stored city ("Tokyo, Japan|35.68,139.69" → "Tokyo, Japan"; typed text as is).</summary>
    public static string Label(string? city) => city == null ? "" : city.Split('|')[0].Trim();

    static Place? Picked(string city)
    {
        int bar = city.LastIndexOf('|');
        if (bar < 0)
            return null;
        var xy = city[(bar + 1)..].Split(',');
        if (xy.Length != 2
            || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return null;
        string full = city[..bar].Trim();
        string shortName = full.Split(',')[0].Trim();
        return new Place(lat, lon, shortName.Length > 0 ? shortName : L.T("Here"), full.Length > 0 ? full : FormattableString.Invariant($"{lat:0.###}, {lon:0.###}"));
    }

    /// <summary>Windows location (for Settings to show whether it works). Null when it's off or denied.</summary>
    public static async Task<Place?> WindowsPlaceAsync() =>
        await WindowsLocationAsync() is { } p ? new Place(p.Item1, p.Item2, p.Item3, L.F("your location ({0})", FormattableString.Invariant($"{p.Item1:0.##}, {p.Item2:0.##}"))) : null;

    static bool Matches(JsonElement e, string prop, string value) =>
        e.TryGetProperty(prop, out var v) && string.Equals(v.GetString(), value, StringComparison.OrdinalIgnoreCase);

    static async Task<(double, double, string)?> WindowsLocationAsync()
    {
#if SHOJI_LINUX
        // No location service is assumed on Linux: the user picks a city in Settings.
        await Task.CompletedTask;
        return null;
#else
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
                return null;
            var pos = await new Geolocator { DesiredAccuracy = PositionAccuracy.Default }
                .GetGeopositionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            return (pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude, L.T("Your location"));
        }
        catch (Exception ex)
        {
            Log.Write($"Windows location unavailable: {ex.Message}");
            return null;
        }
#endif
    }

    /// <summary>WMO weather code → (English text, one-kanji Japanese label, symbol glyph).</summary>
    public static (string Text, string Kanji, string Glyph) Describe(int code, bool day = true) => code switch
    {
        0 => (L.T("Clear", "weather"), "晴", day ? "☀" : "☾"),
        1 => (L.T("Mostly clear"), "晴", day ? "☀" : "☾"),
        2 => (L.T("Partly cloudy"), "曇", "⛅"),
        3 => (L.T("Overcast"), "曇", "☁"),
        45 or 48 => (L.T("Fog"), "霧", "🌫"),
        51 or 53 or 55 => (L.T("Drizzle"), "雨", "🌦"),
        56 or 57 or 66 or 67 => (L.T("Freezing rain"), "雨", "🌧"),
        61 or 63 => (L.T("Rain"), "雨", "🌧"),
        65 => (L.T("Heavy rain"), "雨", "🌧"),
        71 or 73 or 75 or 77 => (L.T("Snow"), "雪", "❄"),
        80 or 81 or 82 => (L.T("Showers"), "雨", "🌦"),
        85 or 86 => (L.T("Snow showers"), "雪", "🌨"),
        95 or 96 or 99 => (L.T("Thunderstorm"), "雷", "⛈"),
        _ => ("–", "天", "☁"),
    };
}
