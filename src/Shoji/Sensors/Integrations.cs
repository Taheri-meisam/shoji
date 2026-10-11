using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Background poller for an online service: fetches every <c>interval</c>, keeps the last good result,
/// and records a short error message when something fails. Subclasses call <see cref="Start"/> last in their constructor.
/// </summary>
public abstract class Poller<T> : IDisposable where T : class
{
    protected static readonly HttpClient Http = CreateClient();

    readonly TimeSpan _interval;
    readonly CancellationTokenSource _cts = new();
    volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public T? State { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Set by a fetch that only partly worked (e.g. rate-limited): the result is kept and this shows as the error.</summary>
    protected string? Warning { get; set; }

    protected Poller(TimeSpan interval) => _interval = interval;

    static HttpClient CreateClient()
    {
#if SHOJI_LINUX
        // Demo mode (regression screenshots, Platform/DemoData.cs): canned answers, and never the network.
        var c = Demo.Enabled ? new HttpClient(Demo.HttpHandler()) : new HttpClient();
        c.Timeout = TimeSpan.FromSeconds(20);
#else
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
#endif
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Shoji/1.2 (+https://github.com)");
        return c;
    }

    protected void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Fetch again now (e.g. right after toggling a light).</summary>
    public void Refresh() => _wake.TrySetResult();

    protected abstract Task<T> FetchAsync(CancellationToken ct);

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // New wake-up signal *before* fetching, so a Refresh() during the fetch triggers another one right after.
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Warning = null;
                State = await FetchAsync(ct);
                Error = Warning;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Error = ex is HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code}" : ex.Message;
                Log.Write($"{GetType().Name}: {ex.Message}");
            }

            try { await Task.WhenAny(Task.Delay(_interval, ct), _wake.Task); }
            catch (OperationCanceledException) { return; }
        }
    }

    public virtual void Dispose() => _cts.Cancel();
}

// ---------------------------------------------------------------- Home Assistant

public sealed record HaEntity(string Id, string Label, string Domain, string State, string? Unit, byte[]? Image);

public sealed class HomeAssistantService : Poller<IReadOnlyList<HaEntity>>
{
    readonly string _url, _token;
    readonly List<(string Id, string? Label)> _entities;

    public HomeAssistantService(string url, string token, List<(string Id, string? Label)> entities) : base(TimeSpan.FromSeconds(10))
    {
        _url = url.TrimEnd('/');
        _token = token;
        _entities = entities;
        Start();
    }

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, _url + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    protected override async Task<IReadOnlyList<HaEntity>> FetchAsync(CancellationToken ct)
    {
        var list = new List<HaEntity>();
        foreach (var (id, label) in _entities)
        {
            using var req = Request(HttpMethod.Get, "/api/states/" + id);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                list.Add(new HaEntity(id, label ?? id, Domain(id), resp.StatusCode == System.Net.HttpStatusCode.NotFound ? "not found" : $"HTTP {(int)resp.StatusCode}", null, null));
                continue;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var attrs = root.GetProperty("attributes");
            string name = label ?? (attrs.TryGetProperty("friendly_name", out var fn) ? fn.GetString() ?? id : id);
            string? unit = attrs.TryGetProperty("unit_of_measurement", out var u) ? u.GetString() : null;

            byte[]? image = null;
            if (Domain(id) == "camera" && attrs.TryGetProperty("entity_picture", out var pic) && pic.GetString() is { } picPath)
            {
                using var imgReq = Request(HttpMethod.Get, picPath);
                using var img = await Http.SendAsync(imgReq, ct);
                if (img.IsSuccessStatusCode)
                    image = await img.Content.ReadAsByteArrayAsync(ct);
            }
            list.Add(new HaEntity(id, name, Domain(id), root.GetProperty("state").GetString() ?? "", unit, image));
        }
        return list;
    }

    static string Domain(string id) => id.Split('.')[0];

    public static bool IsToggleable(string domain) =>
        domain is "light" or "switch" or "fan" or "input_boolean" or "cover" or "scene" or "script" or "automation" or "lock" or "media_player";

    /// <summary>Toggles a light/switch/…, or runs a scene/script.</summary>
    public async Task ToggleAsync(string id)
    {
        string domain = Domain(id);
        string service = domain switch
        {
            "scene" or "script" => $"{domain}/turn_on",
            "lock" => State?.FirstOrDefault(e => e.Id == id)?.State == "locked" ? "lock/unlock" : "lock/lock",
            "media_player" => "media_player/media_play_pause",
            _ => $"{domain}/toggle",
        };
        try
        {
            using var r = Request(HttpMethod.Post, "/api/services/" + service);
            r.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id }), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(r);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Log.Write($"Home Assistant {service} {id} failed: {ex.Message}");
        }
        Refresh();
    }

    /// <summary>"light.desk=Desk lamp, sensor.temp" → (id, label).</summary>
    public static List<(string Id, string? Label)> ParseEntities(IEnumerable<string> items) =>
        items.Select(e => e.Split('=', 2, StringSplitOptions.TrimEntries)).Select(p => (p[0], p.Length > 1 ? p[1] : null)).ToList();
}

// The calendar (iCal) service lives in Calendars.cs.

// ---------------------------------------------------------------- GitHub

public sealed record RepoStatus(string Repo, string? Workflow, string? Branch, string? Status, string? Conclusion, DateTime? When, int OpenPulls, string? Error);

public sealed class GitHubService : Poller<IReadOnlyList<RepoStatus>>
{
    readonly List<string> _repos;
    readonly string? _token;
    readonly Dictionary<string, (EntityTagHeaderValue ETag, string Body)> _cache = new();
    DateTime _limitedUntil; // UTC; no requests before this

    sealed class RateLimitedException : Exception { }

    public GitHubService(List<string> repos, string? token) : base(Interval(repos.Count, token))
    {
        _repos = repos;
        _token = token;
        Start();
    }

    /// <summary>
    /// Two requests per repository per poll. Without a token GitHub allows 60 an hour, so with more repositories
    /// it polls less often (keeping ~50 an hour, leaving room for a refresh).
    /// </summary>
    static TimeSpan Interval(int repos, string? token) => string.IsNullOrEmpty(token)
        ? TimeSpan.FromMinutes(Math.Max(5, repos * 2 * 60 / 50.0))
        : TimeSpan.FromMinutes(2);

    string LimitText => L.F("rate-limited, retry in {0} min", Math.Max(1, (int)Math.Ceiling((_limitedUntil - DateTime.UtcNow).TotalMinutes)));

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (DateTime.UtcNow < _limitedUntil)
            throw new RateLimitedException();
        using var r = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        r.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(_token))
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        // Ask "has it changed?": an unchanged answer is an empty 304 (which with a token doesn't count against the limit).
        bool cached = _cache.TryGetValue(path, out var last);
        if (cached)
            r.Headers.IfNoneMatch.Add(last.ETag);
        using var resp = await Http.SendAsync(r, ct);
        bool limited = NoteLimits(resp);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && cached)
            return JsonDocument.Parse(last.Body);
        if (limited && !resp.IsSuccessStatusCode)
            throw new RateLimitedException();
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.Headers.ETag is { } etag)
            _cache[path] = (etag, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>Reads GitHub's rate-limit headers. Returns true (and stops polling until the reset) when the limit is used up.</summary>
    bool NoteLimits(HttpResponseMessage resp)
    {
        var now = DateTime.UtcNow;
        DateTime? until = null;
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests && resp.Headers.RetryAfter is { } retry)
            until = retry.Delta is TimeSpan d ? now + d : retry.Date?.UtcDateTime;
        if (until == null && Header(resp, "x-ratelimit-remaining") == "0" && long.TryParse(Header(resp, "x-ratelimit-reset"), out long reset))
            until = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime;
        if (until is not DateTime u || u <= now)
            return false;
        // Never trust a wild value (a wrong clock, a bad header) for more than the hour the limit covers.
        u = u > now.AddMinutes(65) ? now.AddMinutes(65) : u;
        if (u > _limitedUntil)
            _limitedUntil = u;
        return true;
    }

    static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    protected override async Task<IReadOnlyList<RepoStatus>> FetchAsync(CancellationToken ct)
    {
        var list = new List<RepoStatus>();
        bool limited = false;
        foreach (var repo in _repos)
        {
            try
            {
                using var runs = await GetAsync($"/repos/{repo}/actions/runs?per_page=1", ct);
                using var pulls = await GetAsync($"/repos/{repo}/pulls?state=open&per_page=100", ct);
                var arr = runs.RootElement.GetProperty("workflow_runs");
                if (arr.GetArrayLength() == 0)
                {
                    list.Add(new RepoStatus(repo, null, null, null, null, null, pulls.RootElement.GetArrayLength(), null));
                    continue;
                }
                var run = arr[0];
                list.Add(new RepoStatus(repo,
                    run.GetProperty("name").GetString(),
                    run.GetProperty("head_branch").GetString(),
                    run.GetProperty("status").GetString(),
                    run.GetProperty("conclusion").ValueKind == JsonValueKind.String ? run.GetProperty("conclusion").GetString() : null,
                    run.GetProperty("updated_at").GetDateTime().ToLocalTime(),
                    pulls.RootElement.GetArrayLength(), null));
            }
            catch (RateLimitedException)
            {
                // Keep showing what we had for this repository; the tile's status line says why it isn't updating.
                limited = true;
                list.Add(State?.FirstOrDefault(s => s.Repo == repo && s.Error == null)
                         ?? new RepoStatus(repo, null, null, null, null, null, 0, LimitText));
            }
            catch (HttpRequestException ex)
            {
                list.Add(new RepoStatus(repo, null, null, null, null, null, 0,
                    ex.StatusCode is { } c ? (int)c == 404 ? L.T("not found (private? add a token)") : $"HTTP {(int)c}" : L.T("offline")));
            }
        }
        if (limited)
            Warning = "GitHub " + LimitText + (string.IsNullOrEmpty(_token) ? L.T(" (a token raises the limit)") : "");
        return list;
    }
}

// ---------------------------------------------------------------- stocks & crypto

public sealed record Quote(string Symbol, double Price, double? ChangePercent, string Currency, double[] Spark);

public sealed class TickerService : Poller<IReadOnlyList<Quote>>
{
    readonly List<string> _stocks, _coins;
    readonly string _currency;

    public TickerService(List<string> stocks, List<string> coins, string? currency) : base(TimeSpan.FromMinutes(5))
    {
        _stocks = stocks;
        _coins = coins.Select(c => c.ToLowerInvariant()).ToList();
        _currency = string.IsNullOrWhiteSpace(currency) ? "usd" : currency.ToLowerInvariant();
        Start();
    }

    protected override async Task<IReadOnlyList<Quote>> FetchAsync(CancellationToken ct)
    {
        var list = new List<Quote>();
        foreach (var symbol in _stocks)
        {
            // Yahoo's public chart endpoint (no key). Unofficial, so failures are expected now and then.
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=5m", ct));
                var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
                var meta = result.GetProperty("meta");
                double price = meta.GetProperty("regularMarketPrice").GetDouble();
                double? prev = meta.TryGetProperty("chartPreviousClose", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null;
                var closes = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close")
                    .EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToArray();
                list.Add(new Quote(symbol.ToUpperInvariant(), price, prev > 0 ? (price - prev) / prev * 100 : null,
                    meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "", closes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write($"Quote {symbol} failed: {ex.Message}");
            }
        }

        if (_coins.Count > 0)
        {
            // CoinGecko's free API answers 429 when busy; that mustn't throw away the stock prices above.
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://api.coingecko.com/api/v3/simple/price?ids={string.Join(",", _coins.Select(Uri.EscapeDataString))}" +
                    $"&vs_currencies={_currency}&include_24hr_change=true", ct));
                foreach (var coin in _coins)
                {
                    if (!doc.RootElement.TryGetProperty(coin, out var c) || !c.TryGetProperty(_currency, out var p))
                        continue;
                    double? change = c.TryGetProperty(_currency + "_24h_change", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetDouble() : null;
                    list.Add(new Quote(coin, p.GetDouble(), change, _currency.ToUpperInvariant(), Array.Empty<double>()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write($"Crypto prices failed: {ex.Message}");
            }
        }

        if (list.Count == 0 && _stocks.Count + _coins.Count > 0)
            throw new InvalidOperationException(L.T("no prices"));
        return list;
    }
}

// ---------------------------------------------------------------- lyrics

public sealed record LyricLine(TimeSpan Time, string Text);
public sealed record Lyrics(string Key, IReadOnlyList<LyricLine> Synced, string? Plain);

/// <summary>Synced lyrics from lrclib.net (free, no key), fetched once per song.</summary>
public sealed class LyricsService : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly Dictionary<string, Lyrics?> _cache = new();
    readonly HashSet<string> _loading = new();
    readonly Dictionary<string, DateTime> _failed = new(); // network errors aren't cached, but retried only after a minute

    static LyricsService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Shoji/1.2");

    /// <summary>Lyrics for the song, or null while loading / when there are none. Starts the download on first ask.</summary>
    public Lyrics? Get(string artist, string title, TimeSpan? duration, out bool loading)
    {
        string key = artist + "|" + title;
        lock (_cache)
        {
            loading = _loading.Contains(key);
            if (_cache.TryGetValue(key, out var hit))
                return hit;
            if (_failed.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(1))
                return null;
            if (!loading)
            {
                _loading.Add(key);
                loading = true;
                _ = LoadAsync(key, artist, title, duration);
            }
            return null;
        }
    }

    async Task LoadAsync(string key, string artist, string title, TimeSpan? duration)
    {
        Lyrics? result = null;
        bool answered = false; // lrclib really answered (found or not found), so the result can be cached
        try
        {
            string q = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(Clean(title))}";
            if (duration is TimeSpan d)
                q += $"&duration={(int)d.TotalSeconds}";
            using var resp = await Http.GetAsync("https://lrclib.net/api/get?" + q);
            if (!resp.IsSuccessStatusCode)
            {
                // Fall back to a search without the duration (live versions, remasters…).
                using var search = await Http.GetAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(Clean(title))}&artist_name={Uri.EscapeDataString(artist)}");
                if (search.IsSuccessStatusCode)
                {
                    answered = true;
                    using var sdoc = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
                    if (sdoc.RootElement.GetArrayLength() > 0)
                        result = Parse(key, sdoc.RootElement[0]);
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                result = Parse(key, doc.RootElement);
                answered = true;
            }
        }
        catch (Exception ex)
        {
            answered = false;
            Log.Write($"Lyrics failed: {ex.Message}");
        }
        lock (_cache)
        {
            _loading.Remove(key);
            if (!answered)
            {
                if (_failed.Count > 50)
                    _failed.Clear();
                _failed[key] = DateTime.UtcNow;
                return;
            }
            _failed.Remove(key);
            if (_cache.Count > 50)
                _cache.Clear();
            _cache[key] = result;
        }
    }

    // "Song (Remastered 2011)" / "Song - Live" → "Song"
    static string Clean(string title) => Regex.Replace(title, @"\s*(\(.*?\)|\[.*?\]|-\s*(Remaster|Live|Radio|Single|Mono|Stereo).*)$", "", RegexOptions.IgnoreCase).Trim();

    static Lyrics? Parse(string key, JsonElement e)
    {
        string? synced = e.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        string? plain = e.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
        var lines = new List<LyricLine>();
        foreach (var line in (synced ?? "").Split('\n'))
        {
            var m = Regex.Match(line, @"^\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)$");
            if (m.Success)
                lines.Add(new LyricLine(TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) +
                                        TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), m.Groups[3].Value.Trim()));
        }
        return lines.Count == 0 && string.IsNullOrWhiteSpace(plain) ? null : new Lyrics(key, lines, plain);
    }

    public void Dispose() { }
}

// ---------------------------------------------------------------- Twitch chat

public sealed record ChatLine(string User, string? Color, string Text, DateTime Time);

/// <summary>Read-only Twitch chat over the public IRC websocket, logged in anonymously (no account needed).</summary>
public sealed class TwitchChat : IDisposable
{
    readonly string _channel;
    readonly CancellationTokenSource _cts = new();
    readonly LinkedList<ChatLine> _lines = new();
    public string Status { get; private set; } = L.T("connecting…");

    public TwitchChat(string channel)
    {
        _channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public IReadOnlyList<ChatLine> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), ct);
                async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, ct);
                await Send("CAP REQ :twitch.tv/tags");
                await Send("PASS SCHMOOPIIE");
                await Send($"NICK justinfan{Random.Shared.Next(10000, 99999)}");
                await Send($"JOIN #{_channel}");
                Status = "#" + _channel;

                var buffer = new byte[16384];
                var pending = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage)
                        continue;
                    foreach (var line in pending.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("PING"))
                            await Send("PONG" + line[4..]);
                        else
                            Handle(line);
                    }
                    pending.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"Twitch chat: {ex.Message}");
            }
            Status = L.T("reconnecting…");
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    void Handle(string line)
    {
        // @tags :user!user@user.tmi.twitch.tv PRIVMSG #channel :message
        var m = Regex.Match(line, @"^(?:@(?<tags>\S+) )?:(?<nick>[^!]+)!\S+ PRIVMSG #\S+ :(?<text>.*)$");
        if (!m.Success)
            return;
        var tags = m.Groups["tags"].Value.Split(';').Select(t => t.Split('=', 2)).Where(t => t.Length == 2).ToDictionary(t => t[0], t => t[1]);
        string user = tags.TryGetValue("display-name", out var dn) && dn.Length > 0 ? dn : m.Groups["nick"].Value;
        string? color = tags.TryGetValue("color", out var c) && c.Length > 0 ? c : null;
        string text = m.Groups["text"].Value;
        if (text.StartsWith("\u0001ACTION "))
            text = text[8..].TrimEnd('\u0001');
        lock (_lines)
        {
            _lines.AddLast(new ChatLine(user, color, text, DateTime.Now));
            while (_lines.Count > 40)
                _lines.RemoveFirst();
        }
    }

    public void Dispose() => _cts.Cancel();
}

// ---------------------------------------------------------------- focus timer

/// <summary>Pomodoro: focus / break cycles. Kept outside the tile so changing theme or page doesn't reset it.</summary>
public sealed class FocusTimer : IDisposable
{
    public TimeSpan Focus { get; }
    public TimeSpan Break { get; }
    public bool OnBreak { get; private set; }
    public bool Running { get; private set; }
    public int Completed { get; private set; }

    TimeSpan _remaining;
    DateTime _endsAt;

    public FocusTimer(int focusMinutes, int breakMinutes)
    {
        Focus = TimeSpan.FromMinutes(Math.Clamp(focusMinutes, 1, 180));
        Break = TimeSpan.FromMinutes(Math.Clamp(breakMinutes, 1, 60));
        _remaining = Focus;
    }

    public TimeSpan Length => OnBreak ? Break : Focus;

    /// <summary>Time left; moves to the next phase (and plays a sound) when one ends.</summary>
    public TimeSpan Remaining
    {
        get
        {
            if (!Running)
                return _remaining;
            var left = _endsAt - DateTime.UtcNow;
            if (left > TimeSpan.Zero)
                return left;
            if (!OnBreak)
                Completed++;
            OnBreak = !OnBreak;
            _remaining = Length;
            _endsAt = DateTime.UtcNow + _remaining;
#if SHOJI_LINUX
            Linux.Sound.Chime();
#else
            System.Media.SystemSounds.Asterisk.Play();
#endif
            return _remaining;
        }
    }

    public void StartPause()
    {
        if (Running)
        {
            _remaining = Remaining;
            Running = false;
        }
        else
        {
            _endsAt = DateTime.UtcNow + _remaining;
            Running = true;
        }
    }

    public void Reset()
    {
        Running = false;
        OnBreak = false;
        _remaining = Focus;
    }

    public void Skip()
    {
        bool wasRunning = Running;
        OnBreak = !OnBreak;
        _remaining = Length;
        _endsAt = DateTime.UtcNow + _remaining;
        Running = wasRunning;
    }

    public void Dispose() { }
}
