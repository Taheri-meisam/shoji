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
    long CacheRead24h, DateTime? OldestIn5h, IReadOnlyList<AiModelUse> Models, IReadOnlyList<double> Hourly, IReadOnlyList<AiLimit> Limits);

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
    }

    readonly record struct Entry(DateTime Time, string Model, long Sent, long Output, long CacheRead);

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
        var since = now.AddHours(-24);
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

        foreach (string old in _entries.Where(e => e.Value.Time < since).Select(e => e.Key).ToList())
            _entries.Remove(old);

        var last5 = now.AddHours(-5);
        var hourly = new double[24];
        long t5 = 0, t24 = 0, o5 = 0, o24 = 0, cache = 0;
        int m5 = 0, m24 = 0;
        DateTime? oldest = null;
        var models = new Dictionary<string, (long T5, long T24)>();
        foreach (var e in _entries.Values)
        {
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
            models.OrderByDescending(m => m.Value.T24).Select(m => new AiModelUse(AiUsageText.Model(m.Key), m.Value.T5, m.Value.T24)).ToList(), hourly, limits);
    }, ct);

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
                Line(line.GetBuffer().AsSpan(0, (int)line.Length), codex, since);
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
        TokenCountMark = Encoding.UTF8.GetBytes("\"token_count\"");

    void Line(ReadOnlySpan<byte> line, bool codex, DateTime since)
    {
        // Most lines are prompts and tool output, some of them megabytes long: only parse the ones that can matter.
        if (codex ? line.IndexOf(TokenCountMark) < 0 : line.IndexOf(UsageMark) < 0 || line.IndexOf(AssistantMark) < 0)
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
                Codex(root, time, since);
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
        long sent = Long(usage, "input_tokens") + Long(usage, "cache_creation_input_tokens");
        var entry = new Entry(time, model, sent, Long(usage, "output_tokens"), Long(usage, "cache_read_input_tokens"));
        // The same answer again (streamed in parts, or a resumed session copied it): keep the fullest count, once.
        if (!_entries.TryGetValue(id, out var have) || entry.Output >= have.Output)
            _entries[id] = have.Model == null ? entry : entry with { Time = have.Time };
    }

    /// <summary>
    /// Codex writes a "token_count" event after each answer: what that answer used ("last_token_usage") and how full
    /// its limits are ("rate_limits": primary = the short window, secondary = the week).
    /// </summary>
    void Codex(JsonElement root, DateTime time, DateTime since)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("type", out var type) || type.GetString() != "token_count")
            return;
        if (time >= since && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            long cached = Long(last, "cached_input_tokens");
            long sent = Math.Max(0, Long(last, "input_tokens") - cached);
            _entries["codex:" + time.Ticks] = new Entry(time, "Codex", sent, Long(last, "output_tokens"), cached);
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
            hourly, new[] { new AiLimit(L.F("Codex {0:0} h", 5.0), 37, Demo.Now.ToUniversalTime().AddMinutes(80)), new AiLimit(L.T("Codex week"), 12, Demo.Now.ToUniversalTime().AddDays(4)) });
    }
#endif
}
