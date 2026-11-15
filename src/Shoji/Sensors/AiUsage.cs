using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>Tokens one model used: in the last five hours and in the last 24.</summary>
public sealed record AiModelUse(string Model, long Tokens5h, long Tokens24h);

/// <summary>One of Codex's limits as its own log last reported it: "5 h" 37 % used, resets at…</summary>
public sealed record AiLimit(string Label, double UsedPercent, DateTime? ResetsAt);

/// <summary>
/// How much the AI coding assistants on this PC were used. Tokens are what was sent and written (input, cache
/// writes, output); cache reads are counted apart, as they cost a tenth and would drown the rest.
/// </summary>
/// <param name="Hourly">Tokens per hour for the last 24 hours, oldest first; the last one is the running hour.</param>
public sealed record AiUsage(bool Found, long Tokens5h, long Tokens24h, long Output5h, long Output24h, int Messages5h, int Messages24h,
    long CacheRead24h, DateTime? OldestIn5h, IReadOnlyList<AiModelUse> Models, IReadOnlyList<double> Hourly, IReadOnlyList<AiLimit> Limits, AiCost? Cost = null);

/// <summary>What one tool or model would have cost at list prices: today, and in the last seven days (today included).</summary>
public sealed record AiCostLine(string Name, double Today, double Week);

/// <summary>
/// The same work priced as pay-as-you-go (US dollars, the providers' public prices per token). On a subscription
/// nothing like this is paid; it says how much was got out of the plan.
/// </summary>
/// <param name="Partial">Some tokens came from a model whose price isn't known here: the sums leave those out.</param>
public sealed record AiCost(double Today, double Week, IReadOnlyList<AiCostLine> Tools, IReadOnlyList<AiCostLine> Models, bool Partial);

/// <summary>
/// Public prices in US dollars per million tokens, as read from the providers' price pages on 2026-11-15. They change:
/// a new release brings the new table.
/// </summary>
public static class AiPrices
{
    /// <param name="Write">Writing to the cache for 5 minutes; <paramref name="WriteLong"/> for an hour.</param>
    public readonly record struct Price(double Input, double Write, double WriteLong, double CacheRead, double Output);

    static Price Claude(double input, double cacheRead, double output) => new(input, input * 1.25, input * 2, cacheRead, output);
    static Price Plain(double input, double cached, double output) => new(input, input, input, cached, output);

    /// <summary>Newest first within a family: a version not listed takes the price of the nearest one below it.</summary>
    static readonly (string Family, double Version, Price Price)[] ClaudeTable =
    {
        ("fable", 5.2, Claude(8, 0.20, 40)), ("fable", 5.1, Claude(10, 0.25, 50)), ("fable", 5, Claude(10, 1, 50)),
        ("mythos", 5.2, Claude(8, 0.20, 40)), ("mythos", 5.1, Claude(10, 0.25, 50)), ("mythos", 5, Claude(10, 1, 50)),
        ("opus", 5.5, Claude(4, 0.20, 20)), ("opus", 4.5, Claude(5, 0.50, 25)), ("opus", 4, Claude(15, 1.50, 75)),
        ("sonnet", 5.5, Claude(2, 0.10, 10)), ("sonnet", 5, Claude(2, 0.20, 10)), ("sonnet", 4, Claude(3, 0.30, 15)),
        ("haiku", 5.5, Claude(0.10, 0.01, 0.50)), ("haiku", 4.5, Claude(1, 0.10, 5)),
    };

    /// <summary>OpenAI's models as Codex names them; the longest matching start wins.</summary>
    static readonly (string Prefix, Price Price)[] OpenAiTable =
    {
        ("gpt-6.1-sol", Plain(2, 0.10, 10)), ("gpt-6.1-terra", Plain(1, 0.05, 5)), ("gpt-6-astra", Plain(10, 1, 50)), ("gpt-6-sol", Plain(2, 0.20, 10)),
        ("gpt-6-luna", Plain(0.10, 0.01, 0.50)), ("gpt-5.6-sol", Plain(4, 0.40, 20)), ("gpt-5.6-terra", Plain(2, 0.20, 12)), ("gpt-5.6-luna", Plain(0.20, 0.02, 1.20)),
        ("gpt-5.5", Plain(5, 0.50, 30)), ("gpt-5.4-mini", Plain(0.75, 0.075, 4.50)), ("gpt-5.4-nano", Plain(0.20, 0.02, 1.25)), ("gpt-5.4", Plain(2.50, 0.25, 15)),
        ("gpt-5.3-codex", Plain(1.75, 0.175, 14)), ("gpt-5.2", Plain(1.75, 0.175, 14)), ("gpt-5-mini", Plain(0.25, 0.025, 2)), ("gpt-5-nano", Plain(0.05, 0.005, 0.40)),
        ("gpt-5.1", Plain(1.25, 0.125, 10)), ("gpt-5", Plain(1.25, 0.125, 10)),
    };

    public static Price? For(string model)
    {
        var m = Regex.Match(model, @"^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?$");
        if (m.Success)
        {
            double version = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 10.0 : 0);
            foreach (var (family, from, price) in ClaudeTable)
                if (family == m.Groups[1].Value && version >= from - 0.001)
                    return price;
            return null;
        }
        Price? best = null;
        int length = 0;
        foreach (var (prefix, price) in OpenAiTable)
            if (prefix.Length > length && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                best = price;
                length = prefix.Length;
            }
        return best;
    }

    /// <summary>"$0.42", "$7.40", "$138".</summary>
    public static string Money(double dollars) => "$" + dollars.ToString(dollars >= 100 ? "0" : "0.00", L.Culture);
}

public static class AiUsageText
{
    /// <summary>"950", "12.4K", "3.2M", "1.1B".</summary>
    public static string Tokens(long n) => n switch
    {
        < 1000 => n.ToString(L.Culture),
        < 100_000 => (n / 1e3).ToString("0.#", L.Culture) + "K",
        < 1_000_000 => (n / 1e3).ToString("0", L.Culture) + "K",
        < 100_000_000 => (n / 1e6).ToString("0.#", L.Culture) + "M",
        < 1_000_000_000 => (n / 1e6).ToString("0", L.Culture) + "M",
        _ => (n / 1e9).ToString("0.#", L.Culture) + "B",
    };

    /// <summary>"claude-opus-4-5-20251101" → "Opus 4.5"; other names stay as they are.</summary>
    public static string Model(string id)
    {
        var m = Regex.Match(id, @"^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?$");
        if (!m.Success)
            return id;
        string name = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..];
        return m.Groups[3].Success ? $"{name} {m.Groups[2].Value}.{m.Groups[3].Value}" : $"{name} {m.Groups[2].Value}";
    }

    /// <summary>"resets in 1 h 20 min" for a limit, or "" when the log didn't say (or it already passed).</summary>
    public static string Resets(DateTime? at, DateTime now)
    {
        if (at is not DateTime t || t <= now)
            return "";
        var left = t - now;
        return left.TotalHours >= 24 ? L.F("resets in {0:0} d", Math.Ceiling(left.TotalDays))
            : left.TotalMinutes >= 60 ? L.F("resets in {0} h {1} min", (int)left.TotalHours, left.Minutes)
            : L.F("resets in {0} min", Math.Max(1, (int)left.TotalMinutes));
    }
}

/// <summary>
/// Reads the session logs Claude Code and Codex keep on this PC (~/.claude/projects, ~/.codex/sessions) and adds up
/// the token counts in them. Nothing else in the logs is looked at, nothing leaves the PC, and no account or key is
/// needed. Each file is read once and then only what was appended, every minute.
/// </summary>
public sealed class AiUsageService : Poller<AiUsage>
{
    public const string Key = "aiusage";

    sealed class FileState
    {
        public long Offset;
        public byte[] Rest = Array.Empty<byte>();
        /// <summary>Codex names the model once per turn ("turn_context"); the token counts that follow belong to it.</summary>
        public string Model = "";
    }

    /// <param name="Cost">In dollars at list prices; negative when the model's price isn't known.</param>
    readonly record struct Entry(DateTime Time, string Model, long Sent, long Output, long CacheRead, string Tool, double Cost);

    public const string ClaudeCode = "Claude Code", CodexTool = "Codex";

    /// <summary>
    /// Set by a tile that shows money. Only then are day totals kept (ai-usage-days.json in the data folder), and the
    /// first time the last week of logs is read once to fill them.
    /// </summary>
    public static bool TrackCost { get; set; }

    /// <summary>
    /// Per local day ("2026-11-15"): dollars per "tool:Claude Code" / "model:Opus 5.5", and "unpriced" tokens. Each
    /// answer is added once, when it is first read; "until" remembers how far that got, so a restart doesn't add the
    /// same answers again and a few days without Shoji are caught up from the logs.
    /// </summary>
    Dictionary<string, Dictionary<string, double>>? _days;
    string _daysSaved = "";
    DateTime _countedUntil, _latest;

    static string DaysPath => Path.Combine(AppPaths.DataFolder, "ai-usage-days.json");

    readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    // One entry per answer: a streamed answer is logged several times with the same ids, the last line has the totals.
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly List<AiLimit> _limits = new();
    DateTime _limitsAt;

    public AiUsageService() : base(TimeSpan.FromSeconds(60)) => Start();

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string ClaudeFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude"), "projects");

    static string CodexFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex"), "sessions");

    protected override Task<AiUsage> FetchAsync(CancellationToken ct) => Task.Run(() =>
    {
#if SHOJI_LINUX
        if (Demo.Enabled)
            return DemoUsage();
#endif
        var now = DateTime.UtcNow;
        var day = now.AddHours(-24);
        // Money wanted: the first time (and after days without Shoji) the logs are read back to where the day totals
        // stop, a week at most.
        var since = day;
        if (TrackCost && _days == null)
        {
            _days = LoadDays();
            // Switched on while running: what is in memory was never counted, so it is read again from the logs.
            _files.Clear();
            _entries.Clear();
            _countedUntil = _latest = _days.TryGetValue("until", out var until) && until.TryGetValue("ticks", out double ticks) && ticks > 0 && ticks < now.Ticks
                ? new DateTime((long)ticks, DateTimeKind.Utc) : DateTime.MinValue;
            var from = _countedUntil > now.AddDays(-7) ? _countedUntil : now.AddDays(-7);
            if (from < since)
                since = from;
        }
        bool found = false;
        foreach (var (folder, codex) in new[] { (ClaudeFolder, false), (CodexFolder, true) })
        {
            if (!Directory.Exists(folder))
                continue;
            found = true;
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Untouched for a day: nothing in it counts any more.
                    if (File.GetLastWriteTimeUtc(file) >= since)
                        ReadNew(file, codex, since, ct);
                }
                catch (IOException) { /* being written right now: next minute */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        var cost = _days != null ? Cost(now) : null;
        foreach (string old in _entries.Where(e => e.Value.Time < day).Select(e => e.Key).ToList())
            _entries.Remove(old);

        var last5 = now.AddHours(-5);
        var hourly = new double[24];
        long t5 = 0, t24 = 0, o5 = 0, o24 = 0, cache = 0;
        int m5 = 0, m24 = 0;
        DateTime? oldest = null;
        var models = new Dictionary<string, (long T5, long T24)>();
        foreach (var e in _entries.Values)
        {
            if (e.Time < day)
                continue; // only there while the week is read for the day totals
            long tokens = e.Sent + e.Output;
            t24 += tokens;
            o24 += e.Output;
            cache += e.CacheRead;
            m24++;
            int hour = 23 - (int)Math.Clamp((now - e.Time).TotalHours, 0, 23);
            hourly[hour] += tokens;
            var m = models.GetValueOrDefault(e.Model);
            if (e.Time >= last5)
            {
                t5 += tokens;
                o5 += e.Output;
                m5++;
                m.T5 += tokens;
                if (oldest == null || e.Time < oldest)
                    oldest = e.Time;
            }
            m.T24 += tokens;
            models[e.Model] = m;
        }
        // Limits older than their own window say nothing any more.
        var limits = now - _limitsAt < TimeSpan.FromHours(5) ? _limits.ToList() : new List<AiLimit>();
        return new AiUsage(found, t5, t24, o5, o24, m5, m24, cache, oldest,
            models.OrderByDescending(m => m.Value.T24).Select(m => new AiModelUse(AiUsageText.Model(m.Key), m.Value.T5, m.Value.T24)).ToList(), hourly, limits, cost);
    }, ct);

    static Dictionary<string, Dictionary<string, double>> LoadDays()
    {
        try
        {
            if (File.Exists(DaysPath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(DaysPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"AI usage: the day totals could not be read ({ex.Message}); starting again.");
        }
        return new();
    }

    static string Day(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Puts an answer into its day's totals: all of it when new, the difference when a fuller copy replaces it.</summary>
    void Count(Entry e, Entry before)
    {
        if (_days == null || e.Time <= _countedUntil)
            return;
        if (e.Time > _latest)
            _latest = e.Time;
        if (!_days.TryGetValue(Day(e.Time), out var sums))
            _days[Day(e.Time)] = sums = new Dictionary<string, double>();
        bool again = before.Model != null;
        if (e.Cost < 0)
        {
            sums["unpriced"] = sums.GetValueOrDefault("unpriced") + e.Sent + e.Output - (again ? before.Sent + before.Output : 0);
            return;
        }
        double more = e.Cost - (again && before.Cost > 0 ? before.Cost : 0);
        foreach (string key in new[] { "tool:" + e.Tool, "model:" + AiUsageText.Model(e.Model) })
            sums[key] = sums.GetValueOrDefault(key) + more;
    }

    /// <summary>Today and the last seven days from the day totals, which are saved when they changed.</summary>
    AiCost Cost(DateTime now)
    {
        var days = _days!;
        days["until"] = new Dictionary<string, double> { ["ticks"] = _latest.Ticks };
        string today = Day(now), oldest = Day(now.AddDays(-6));
        foreach (string old in days.Keys.Where(d => d != "until" && string.CompareOrdinal(d, Day(now.AddDays(-40))) < 0).ToList())
            days.Remove(old);

        var lines = new Dictionary<string, (double Today, double Week)>();
        bool partial = false;
        foreach (var (date, sums) in days)
        {
            if (date == "until" || string.CompareOrdinal(date, oldest) < 0 || string.CompareOrdinal(date, today) > 0)
                continue;
            foreach (var (key, value) in sums)
            {
                if (key == "unpriced")
                {
                    partial |= value > 0;
                    continue;
                }
                var line = lines.GetValueOrDefault(key);
                lines[key] = (line.Today + (date == today ? value : 0), line.Week + value);
            }
        }
        List<AiCostLine> Lines(string prefix) => lines.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(l => l.Value.Week).Select(l => new AiCostLine(l.Key[prefix.Length..], l.Value.Today, l.Value.Week)).ToList();
        var tools = Lines("tool:");

        try
        {
            string json = JsonSerializer.Serialize(days);
            if (json != _daysSaved)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                string temp = DaysPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, DaysPath, overwrite: true);
                _daysSaved = json;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.WriteThrottled("aiusage-days", $"AI usage: the day totals could not be saved: {ex.Message}", TimeSpan.FromHours(6));
        }
        return new AiCost(tools.Sum(t => t.Today), tools.Sum(t => t.Week), tools, Lines("model:"), partial);
    }

    /// <summary>Reads what was appended to a log since last time, whole lines only.</summary>
    void ReadNew(string file, bool codex, DateTime since, CancellationToken ct)
    {
        if (!_files.TryGetValue(file, out var state))
            _files[file] = state = new FileState();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        if (stream.Length < state.Offset)
        {
            state.Offset = 0; // replaced by a shorter file
            state.Rest = Array.Empty<byte>();
        }
        if (stream.Length == state.Offset)
            return;
        stream.Seek(state.Offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        var line = new MemoryStream();
        line.Write(state.Rest);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                line.Write(buffer, start, i - start);
                Line(line.GetBuffer().AsSpan(0, (int)line.Length), codex, since, state);
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buffer, start, read - start);
            state.Offset += read;
        }
        // A last line without its newline is still being written: keep it for next time (unless it's absurdly long).
        state.Rest = line.Length is > 0 and < 64 << 20 ? line.ToArray() : Array.Empty<byte>();
    }

    static readonly byte[] UsageMark = Encoding.UTF8.GetBytes("\"usage\""), AssistantMark = Encoding.UTF8.GetBytes("\"assistant\""),
        TokenCountMark = Encoding.UTF8.GetBytes("\"token_count\""), TurnContextMark = Encoding.UTF8.GetBytes("\"turn_context\"");

    void Line(ReadOnlySpan<byte> line, bool codex, DateTime since, FileState state)
    {
        // Most lines are prompts and tool output, some of them megabytes long: only parse the ones that can matter.
        if (codex ? line.IndexOf(TokenCountMark) < 0 && line.IndexOf(TurnContextMark) < 0 : line.IndexOf(UsageMark) < 0 || line.IndexOf(AssistantMark) < 0)
            return;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                return;
            if (codex)
                Codex(root, time, since, state);
            else
                Claude(root, time, since);
        }
        catch (JsonException) { /* a cut-off line */ }
        catch (InvalidOperationException) { /* not the shape expected */ }
    }

    void Claude(JsonElement root, DateTime time, DateTime since)
    {
        if (time < since || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;
        string model = message.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
        if (model.Length == 0 || model.StartsWith('<'))
            return; // "<synthetic>": an error message the app wrote itself
        string id = (message.TryGetProperty("id", out var mi) ? mi.GetString() : null) + ":" + (root.TryGetProperty("requestId", out var ri) ? ri.GetString() : null);
        if (id.Length == 1)
            id = root.TryGetProperty("uuid", out var uu) ? uu.GetString() ?? "" : "";
        long input = Long(usage, "input_tokens"), written = Long(usage, "cache_creation_input_tokens"), output = Long(usage, "output_tokens"), cacheRead = Long(usage, "cache_read_input_tokens");
        // How much of the cache writing was for an hour (dearer) is said apart, when it is said at all.
        long writtenLong = usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object ? Math.Min(written, Long(creation, "ephemeral_1h_input_tokens")) : 0;
        double cost = AiPrices.For(model) is { } p
            ? (input * p.Input + (written - writtenLong) * p.Write + writtenLong * p.WriteLong + cacheRead * p.CacheRead + output * p.Output) / 1e6
            : -1;
        var entry = new Entry(time, model, input + written, output, cacheRead, ClaudeCode, cost);
        // The same answer again (streamed in parts, or a resumed session copied it): keep the fullest count, once.
        if (!_entries.TryGetValue(id, out var have) || entry.Output >= have.Output)
        {
            if (have.Model != null)
                entry = entry with { Time = have.Time };
            Count(entry, have);
            _entries[id] = entry;
        }
    }

    /// <summary>
    /// Codex writes a "token_count" event after each answer: what that answer used ("last_token_usage") and how full
    /// its limits are ("rate_limits": primary = the short window, secondary = the week).
    /// </summary>
    void Codex(JsonElement root, DateTime time, DateTime since, FileState state)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;
        if (root.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "turn_context")
        {
            if (payload.TryGetProperty("model", out var named) && named.ValueKind == JsonValueKind.String)
                state.Model = named.GetString() ?? "";
            return;
        }
        if (!payload.TryGetProperty("type", out var type) || type.GetString() != "token_count")
            return;
        if (time >= since && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            long cached = Long(last, "cached_input_tokens");
            long sent = Math.Max(0, Long(last, "input_tokens") - cached);
            long output = Long(last, "output_tokens");
            double cost = AiPrices.For(state.Model) is { } p ? (sent * p.Input + cached * p.CacheRead + output * p.Output) / 1e6 : -1;
            var entry = new Entry(time, state.Model.Length > 0 ? state.Model : "Codex", sent, output, cached, CodexTool, cost);
            _entries.TryGetValue("codex:" + time.Ticks, out var have);
            Count(entry, have);
            _entries["codex:" + time.Ticks] = entry;
        }
        if (time > _limitsAt && payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            var list = new List<AiLimit>();
            foreach (string name in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(name, out var l) || l.ValueKind != JsonValueKind.Object || !l.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number)
                    continue;
                double minutes = l.TryGetProperty("window_minutes", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : name == "primary" ? 300 : 10080;
                DateTime? resets = l.TryGetProperty("resets_in_seconds", out var rs) && rs.ValueKind == JsonValueKind.Number ? time.AddSeconds(rs.GetDouble())
                    : l.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number ? DateTime.UnixEpoch.AddSeconds(ra.GetDouble())
                    : null;
                string label = minutes >= 1440 * 6 ? L.T("Codex week") : minutes >= 1440 ? L.F("Codex {0:0} d", minutes / 1440) : L.F("Codex {0:0} h", minutes / 60);
                list.Add(new AiLimit(label, used.GetDouble(), resets));
            }
            if (list.Count > 0)
            {
                _limits.Clear();
                _limits.AddRange(list);
                _limitsAt = time;
            }
        }
    }

    static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

#if SHOJI_LINUX
    static AiUsage DemoUsage()
    {
        var hourly = new double[24];
        for (int i = 0; i < 24; i++)
            hourly[i] = i is < 9 or 15 or 16 ? 0 : 60_000 + 170_000 * Math.Abs(Math.Sin(i * 1.7));
        return new AiUsage(true, 1_240_000, 3_870_000, 96_400, 318_000, 212, 804, 48_600_000, Demo.Now.ToUniversalTime().AddHours(-3.4),
            new[] { new AiModelUse("Opus 4.5", 980_000, 2_900_000), new AiModelUse("Sonnet 4.5", 210_000, 760_000), new AiModelUse("Haiku 4.5", 50_000, 210_000) },
            hourly, new[] { new AiLimit(L.F("Codex {0:0} h", 5.0), 37, Demo.Now.ToUniversalTime().AddMinutes(80)), new AiLimit(L.T("Codex week"), 12, Demo.Now.ToUniversalTime().AddDays(4)) },
            TrackCost ? new AiCost(7.40, 38.15, new[] { new AiCostLine(ClaudeCode, 6.10, 31.05), new AiCostLine(CodexTool, 1.30, 7.10) },
                new[] { new AiCostLine("Opus 4.5", 4.95, 25.40), new AiCostLine("Sonnet 4.5", 0.98, 4.70), new AiCostLine("Haiku 4.5", 0.17, 0.95) }, false) : null);
    }
#endif
}
