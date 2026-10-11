using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>One response of a web address: the text, and the parsed JSON when it is JSON.</summary>
public sealed record WebBody(string Text, JsonElement? Json, DateTime Time);

/// <summary>
/// What a "Web value" tile shows for its field: a number (with its recent history for the sparkline) or a text,
/// when it last worked, and the current problem if any. <see cref="Version"/> changes with every new reading.
/// </summary>
public sealed record WebReading(double? Number, string? Text, string? Error, DateTime? Updated, double[] History, int Version);

/// <summary>
/// Fetches a web address every N seconds for the "Web value" tiles. One per (address, header, interval), shared by
/// every tile that reads it, so two tiles showing different fields of the same API cost one request. Each field's
/// last good value and recent numbers are kept here, so they survive theme and page changes.
/// </summary>
public sealed class WebValueService : Poller<WebBody>
{
    public const int HistoryLength = 120;
    const int MaxBytes = 4 * 1024 * 1024;

    readonly string _url;
    readonly (string Name, string Value)? _header;
    readonly object _gate = new();
    readonly Dictionary<string, Field> _fields = new();
    int _version;

    sealed class Field
    {
        public double? Number;
        public string? Text, Error;
        public DateTime? Updated;
        public readonly List<double> History = new();
    }

    public WebValueService(string url, (string Name, string Value)? header, TimeSpan interval) : base(interval)
    {
        _url = url.Trim();
        _header = header;
        Interval = interval;
        Start();
    }

    public TimeSpan Interval { get; }

    /// <summary>Changes with every fetch (good or not); a tile re-reads its field only when this moved.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>ServiceStore key. The header (usually a token) goes in as a hash.</summary>
    public static string Key(string url, string? header, int seconds) => $"webvalue:{url.Trim()}:{header?.GetHashCode()}:{seconds}";

    /// <summary>"Authorization: Bearer abc" → (name, value); null when it isn't "Name: value".</summary>
    public static (string Name, string Value)? ParseHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;
        int colon = header.IndexOf(':');
        if (colon <= 0)
            return null;
        string name = header[..colon].Trim(), value = header[(colon + 1)..].Trim();
        return name.Length == 0 || name.Any(char.IsWhiteSpace) ? null : (name, value);
    }

    protected override async Task<WebBody> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.5");
            if (_header is { } h)
                req.Headers.TryAddWithoutValidation(h.Name, h.Value);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException(L.T("Response too big"));
            string text = await resp.Content.ReadAsStringAsync(ct);
            var body = new WebBody(text, TryJson(text), DateTime.Now);
            lock (_gate)
            {
                _version++;
                foreach (var (path, field) in _fields)
                    Apply(field, body, path);
            }
            return body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Kept as a warning (the last good response stays), and logged once in a while: a tile checking every
            // 10 seconds against a server that's down shouldn't fill the log.
            Log.WriteThrottled("webvalue:" + _url, $"Web value {_url}: {ex.Message}", TimeSpan.FromMinutes(30));
            Warning = ex switch
            {
                HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                HttpRequestException => L.T("Can't reach it (offline?)"),
                TaskCanceledException => L.T("Timed out"),
                _ => ex.Message,
            };
            lock (_gate)
                _version++;
            return State!; // null until the first success, which is what the tiles expect
        }
    }

    static JsonElement? TryJson(string text)
    {
        var t = text.AsSpan().TrimStart();
        if (t.Length == 0 || t[0] is not ('{' or '[' or '"') && !char.IsDigit(t[0]) && t[0] is not ('-' or 't' or 'f' or 'n'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The reading for a field ("" = the whole response). Asking for a field also starts keeping its history.</summary>
    public WebReading Read(string? path)
    {
        path = (path ?? "").Trim();
        lock (_gate)
        {
            if (!_fields.TryGetValue(path, out var f))
            {
                _fields[path] = f = new Field();
                if (State is { } body)
                    Apply(f, body, path);
            }
            string? error = Error ?? f.Error;
            return new WebReading(f.Number, f.Text, error, f.Updated, f.History.ToArray(), _version);
        }
    }

    static void Apply(Field f, WebBody body, string path)
    {
        (double? Number, string? Text, string? Error) v;
        try { v = Evaluate(body, path); }
        catch (Exception ex) { v = (null, null, ex.Message); } // an odd answer mustn't take the tile down
        f.Error = v.Error;
        if (v.Error != null)
            return;
        f.Number = v.Number;
        f.Text = v.Text;
        f.Updated = body.Time;
        if (v.Number is double n)
        {
            f.History.Add(n);
            if (f.History.Count > HistoryLength)
                f.History.RemoveAt(0);
        }
    }

    /// <summary>Picks the value out of a response: a number, a text, or the reason it couldn't.</summary>
    public static (double? Number, string? Text, string? Error) Evaluate(WebBody body, string? path)
    {
        path = (path ?? "").Trim();
        if (body.Json is not { } root)
        {
            if (path.Length > 0)
                return (null, null, L.T("Not JSON"));
            // A plain answer: "21.5", "OK", the first line of a text.
            string text = body.Text.Trim();
            if (text.StartsWith('<'))
                return (null, null, L.T("Not JSON (got a web page)"));
            if (JsonPath.TryNumber(text, out double n))
                return (n, null, null);
            string line = text.Split('\n')[0].Trim();
            return (null, line.Length > 80 ? line[..80] + "…" : line, null);
        }
        if (path.Length == 0 && root.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return (null, null, JsonPath.FirstValue(root, "") is { } hint ? L.F("Set a Field, e.g. {0}", hint) : L.T("Set a Field"));
        if (!JsonPath.TryFind(root, path, out var value, out string? error))
            return (null, null, error);
        return JsonPath.Value(value, path);
    }
}

/// <summary>
/// A small JSON path: <c>data.items[0].price</c>, <c>$.main.temp</c>, <c>['a key'].x</c>, <c>items[-1]</c> (last).
/// Keys match exactly first, then ignoring case.
/// </summary>
public static class JsonPath
{
    public static bool TryFind(JsonElement root, string path, out JsonElement value, out string? error)
    {
        value = root;
        error = null;
        string p = path.Trim();
        int i = 0;
        if (p.StartsWith('$'))
            i = 1;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                i++;
                if (i < p.Length && p[i] == '[')
                    continue; // "$.['a b']"
            }
            object key;
            if (i < p.Length && p[i] == '[')
            {
                int close;
                if (i + 1 < p.Length && p[i + 1] is '\'' or '"')
                {
                    char quote = p[i + 1];
                    var sb = new StringBuilder();
                    int j = i + 2;
                    for (; j < p.Length && p[j] != quote; j++)
                    {
                        if (p[j] == '\\' && j + 1 < p.Length)
                            j++;
                        sb.Append(p[j]);
                    }
                    if (j + 1 >= p.Length || p[j + 1] != ']')
                    {
                        error = L.F("Bad field: unclosed [{0} in {1}", quote, path);
                        return false;
                    }
                    key = sb.ToString();
                    close = j + 1;
                }
                else
                {
                    close = p.IndexOf(']', i);
                    if (close < 0)
                    {
                        error = L.F("Bad field: missing ] in {0}", path);
                        return false;
                    }
                    string inner = p[(i + 1)..close].Trim();
                    if (inner == "*" || !int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index))
                    {
                        error = L.F("Bad field: [{0}] isn't a position (use [0], [1]… or ['name'])", inner);
                        return false;
                    }
                    key = index;
                }
                i = close + 1;
            }
            else
            {
                int end = i;
                while (end < p.Length && p[end] != '.' && p[end] != '[')
                    end++;
                key = p[i..end].Trim();
                if (((string)key).Length == 0)
                {
                    error = L.F("Bad field: empty name in {0}", path);
                    return false;
                }
                i = end;
            }

            string sofar = p[..i].TrimStart('$').TrimStart('.');
            if (key is int n)
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    error = L.F("Field not found: {0} (not a list)", sofar);
                    return false;
                }
                int count = value.GetArrayLength();
                int at = n < 0 ? count + n : n;
                if (at < 0 || at >= count)
                {
                    error = L.F("Field not found: {0} (the list has {1})", sofar, count);
                    return false;
                }
                value = value[at];
            }
            else
            {
                string name = (string)key;
                if (value.ValueKind == JsonValueKind.Array && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int pos) && pos < value.GetArrayLength())
                {
                    value = value[pos]; // "items.0.price" works too
                    continue;
                }
                if (value.ValueKind != JsonValueKind.Object || !TryProperty(value, name, out var next))
                {
                    error = L.F("Field not found: {0}", sofar);
                    return false;
                }
                value = next;
            }
        }
        return true;
    }

    static bool TryProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>A found value as a number or text; objects and lists can't be shown, so they name a value inside instead.</summary>
    public static (double? Number, string? Text, string? Error) Value(JsonElement v, string path) => v.ValueKind switch
    {
        JsonValueKind.Number => (v.GetDouble(), null, null),
        JsonValueKind.String => TryNumber(v.GetString() ?? "", out double n) ? (n, null, null) : (null, v.GetString() ?? "", null),
        JsonValueKind.True => (null, L.T("On"), null),
        JsonValueKind.False => (null, L.T("Off"), null),
        JsonValueKind.Null => (null, null, L.F("Field is empty (null): {0}", path)),
        _ => (null, null, FirstValue(v, path.Trim()) is { } inside
            ? (v.ValueKind == JsonValueKind.Array ? L.F("{0} is a list: try {1}", path, inside) : L.F("{0} is a group: try {1}", path, inside))
            : L.F("{0} has no value to show", path)),
    };

    // "1,234.5" (thousands commas) – but "12,5" stays text instead of quietly becoming 125.
    static readonly Regex Grouped = new(@"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>A number written in text, the invariant way ("12.5", "-3", "1,234.5", "1e6").</summary>
    public static bool TryNumber(string s, out double value)
    {
        s = s.Trim();
        if (Grouped.IsMatch(s))
            s = s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>The path of the first number (or else text) inside, to suggest as a field ("current.temperature_2m").</summary>
    public static string? FirstValue(JsonElement v, string prefix) => Find(v, prefix, true, 0) ?? Find(v, prefix, false, 0);

    static string? Find(JsonElement v, string prefix, bool number, int depth)
    {
        if (depth > 8)
            return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return prefix.Length > 0 ? prefix : null;
            case JsonValueKind.String:
                return !number && prefix.Length > 0 ? prefix : null;
            case JsonValueKind.Object:
                foreach (var p in v.EnumerateObject())
                {
                    bool plain = Regex.IsMatch(p.Name, @"^[A-Za-z_][A-Za-z0-9_\-]*$");
                    string sub = plain ? (prefix.Length == 0 ? p.Name : prefix + "." + p.Name) : $"{prefix}['{p.Name.Replace("'", "\\'")}']";
                    if (Find(p.Value, sub, number, depth + 1) is { } found)
                        return found;
                }
                return null;
            case JsonValueKind.Array:
                return v.GetArrayLength() > 0 ? Find(v[0], prefix + "[0]", number, depth + 1) : null;
            default:
                return null;
        }
    }
}

/// <summary>How the "Web value" tiles write numbers and their status line (the same in both editions).</summary>
public static class WebValueText
{
    /// <summary>
    /// A number with the tile's decimals, or automatically: "1,234", "123.4", "12.35", "0.004". Invariant (no
    /// thousands separator) for the LED digits, which can only draw digits, '-' and '.'.
    /// </summary>
    public static string Format(double v, int? decimals, bool invariant = false)
    {
        var culture = invariant ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;
        if (decimals is int d)
            return v.ToString((invariant ? "F" : "N") + Math.Clamp(d, 0, 6), culture);
        double a = Math.Abs(v);
        string format = a >= 1000 ? (invariant ? "0" : "#,0") : a >= 100 ? "0.#" : a >= 1 || a == 0 ? "0.##" : "0.###";
        return v.ToString(format, culture);
    }

    /// <summary>
    /// The line under the value: the problem (and how old the shown value is), "updated 5 min ago" when the value
    /// is getting old, "Loading…" before the first answer; null when all is well.
    /// </summary>
    public static (string Text, bool Warn)? Status(WebReading? r, TimeSpan interval, DateTime now)
    {
        if (r == null || r.Error == null && r.Updated == null)
            return (L.T("Loading…"), false);
        string? age = r.Updated is DateTime u ? L.F("updated {0}", Ago(now - u)) : null;
        if (r.Error != null)
            return (age == null ? r.Error : $"{r.Error} · {age}", true);
        var stale = TimeSpan.FromTicks(Math.Max(interval.Ticks * 2, TimeSpan.FromMinutes(3).Ticks));
        return now - r.Updated > stale ? (age!, false) : null;
    }

    static string Ago(TimeSpan d) =>
        d.TotalMinutes < 1 ? L.T("just now") : d.TotalHours < 1 ? L.F("{0} min ago", (int)d.TotalMinutes) : d.TotalDays < 1 ? L.F("{0} h ago", (int)d.TotalHours) : L.F("{0} d ago", (int)d.TotalDays);
}
