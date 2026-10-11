using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>One occurrence of a calendar event, in local time. All-day events run from midnight to midnight.</summary>
public sealed record AgendaEvent(DateTime Start, DateTime? End, string Title, string? Location, bool AllDay, int Calendar)
{
    /// <summary>When it's over: the end, else the next midnight (all-day) or the start itself.</summary>
    public DateTime Until => End is DateTime e && e > Start ? e : AllDay ? Start.Date.AddDays(1) : Start;

    /// <summary>Whether any part of it falls on <paramref name="day"/> (multi-day events count on every day).</summary>
    public bool OnDay(DateTime day) => Start < day.AddDays(1) && (Until > day || Start >= day);
}

/// <summary>
/// Events from one or more iCal feeds (Google, Outlook, iCloud, Nextcloud, a local .ics file), fetched every
/// 10 minutes. One service per set of links is shared by every tile showing them (Agenda, Clock, Calendar), so it
/// holds the whole current month and the next five weeks, and each tile picks what it needs.
/// </summary>
public sealed class AgendaService : Poller<IReadOnlyList<AgendaEvent>>
{
    // Ical.Net keeps internal caches that aren't documented as thread-safe; parsing is rare, so one at a time.
    static readonly object ParseGate = new();

    readonly List<CalendarLink> _feeds;
    // A feed that fails (offline, a hiccup at Google) keeps showing what it had, the others update.
    readonly Dictionary<int, List<AgendaEvent>> _lastGood = new();

    public AgendaService(IEnumerable<CalendarLink> feeds) : base(TimeSpan.FromMinutes(10))
    {
        _feeds = feeds.ToList();
        Start();
    }

    public IReadOnlyList<CalendarLink> Feeds => _feeds;

    /// <summary>The span kept: from the 1st of this month (the Calendar tile's dots) to five weeks ahead (Agenda: 31 days at most).</summary>
    public static (DateTime From, DateTime To) Window(DateTime today)
    {
        var first = new DateTime(today.Year, today.Month, 1);
        var to = today.AddDays(35);
        var monthEnd = first.AddMonths(1);
        return (first, to > monthEnd ? to : monthEnd);
    }

    protected override async Task<IReadOnlyList<AgendaEvent>> FetchAsync(CancellationToken ct)
    {
        var (from, to) = Window(DateTime.Today);
        var list = new List<AgendaEvent>();
        var failed = new List<string>();
        Exception? firstError = null;
        for (int i = 0; i < _feeds.Count; i++)
        {
            try
            {
                var events = Parse(await LoadAsync(_feeds[i].Url, ct), i, from, to);
                _lastGood[i] = events;
                list.AddRange(events);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                firstError ??= ex;
                failed.Add($"{NameOf(_feeds[i], i)}: {Describe(ex)}");
                Log.WriteThrottled("agenda:" + _feeds[i].Url, $"Calendar {NameOf(_feeds[i], i)} failed: {ex.Message}", TimeSpan.FromHours(1));
                if (_lastGood.TryGetValue(i, out var old))
                    list.AddRange(old);
            }
        }
        if (firstError != null && _lastGood.Count == 0)
            throw new InvalidOperationException(_feeds.Count == 1 ? Describe(firstError) : string.Join("; ", failed));
        if (failed.Count > 0)
            Warning = string.Join("; ", failed);
        return list.OrderBy(e => e.Start).ThenBy(e => !e.AllDay).ToList();
    }

    static string NameOf(CalendarLink f, int i) => string.IsNullOrWhiteSpace(f.Name) ? L.F("calendar {0}", i + 1) : f.Name!;

    /// <summary>A short reason for the tile and the Settings test ("HTTP 404", "not an iCal calendar"…).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => (int)code is 401 or 403 or 404
            ? L.F("HTTP {0} (link wrong, private or expired?)", (int)code)
            : $"HTTP {(int)code}",
        HttpRequestException => L.T("can't reach it (offline?)"),
        TaskCanceledException => L.T("timed out"),
        FileNotFoundException or DirectoryNotFoundException => L.T("file not found"),
        UriFormatException or InvalidOperationException { Source: "System.Net.Http" } => L.T("not a valid link"),
        _ => ex.Message,
    };

    /// <summary>webcal:// (what calendar apps hand out) is plain https.</summary>
    public static string Normalize(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url[9..];
        if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url[10..];
        return url;
    }

    /// <summary>Downloads a feed; file:// links and plain paths read a local .ics file (an exported or synced calendar).</summary>
    public static async Task<string> LoadAsync(string url, CancellationToken ct)
    {
        url = Normalize(url);
        string text;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            text = await File.ReadAllTextAsync(uri.LocalPath, ct);
        else if (Path.IsPathFullyQualified(url))
            text = await File.ReadAllTextAsync(url, ct);
        else
            text = await Http.GetStringAsync(url, ct);
        // A sign-in page or an error page comes back as "200 OK" too.
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("not an iCal calendar (use the .ics / iCal address)"));
        return text;
    }

    /// <summary>
    /// Every occurrence between <paramref name="from"/> and <paramref name="to"/> (local), with repeats expanded,
    /// EXDATEs and moved / cancelled single occurrences applied, and cancelled events left out.
    /// </summary>
    public static List<AgendaEvent> Parse(string ics, int calendar, DateTime from, DateTime to)
    {
        var list = new List<AgendaEvent>();
        lock (ParseGate)
        {
            var cal = Ical.Net.Calendar.Load(ics) ?? throw new InvalidDataException(L.T("not an iCal calendar"));
            foreach (var o in cal.GetOccurrences(from, to))
            {
                if (o.Source is not Ical.Net.CalendarComponents.CalendarEvent ev)
                    continue;
                // A cancelled event, or a cancelled single occurrence (its RECURRENCE-ID copy replaces the repeat).
                if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                    continue;
                var startTime = o.Period.StartTime;
                bool allDay = ev.IsAllDay || !startTime.HasTime;
                // AsSystemLocal ignores the event's TZID (a 09:00 New York call would show at 09:00 here), so go
                // through UTC. All-day dates are calendar days and floating times (no zone) are local as written.
                DateTime Local(Ical.Net.DataTypes.IDateTime t) =>
                    !t.HasTime ? t.Value.Date
                    : t.IsUtc || !string.IsNullOrEmpty(t.TzId) ? t.AsUtc.ToLocalTime()
                    : DateTime.SpecifyKind(t.Value, DateTimeKind.Local);
                var start = Local(startTime);
                DateTime? end = o.Period.EndTime is { } e ? Local(e) : null;
                if (allDay)
                {
                    start = start.Date;
                    end = end is DateTime d && d.Date > start ? d.Date : start.AddDays(1);
                }
                list.Add(new AgendaEvent(start, end, string.IsNullOrWhiteSpace(ev.Summary) ? L.T("(no title)") : ev.Summary.Trim(),
                    string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location, allDay, calendar));
            }
        }
        return list;
    }

    /// <summary>For Settings' "Test" button: "12 events in the next 2 weeks", or why it didn't work.</summary>
    public static async Task<(bool Ok, string Text)> TestAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var now = DateTime.Now;
            var events = await Task.Run(async () => Parse(await LoadAsync(url, cts.Token), 0, now.Date, now.Date.AddDays(14)), cts.Token);
            int n = events.Count(e => e.Until > now);
            return (true, n == 0 ? L.T("Works, but nothing in the next 2 weeks")
                : n == 1 ? L.T("1 event in the next 2 weeks")
                : L.F("{0} events in the next 2 weeks", n));
        }
        catch (Exception ex)
        {
            return (false, L.F("Didn't work: {0}", Describe(ex)));
        }
    }
}

/// <summary>Which calendars a tile shows, and the bits of calendar text the Clock and Calendar tiles share.</summary>
public static class CalendarFeeds
{
    /// <summary>An Agenda tile's own links ("Calendars" option) win; otherwise the ones from Settings → Weather & time.</summary>
    public static List<CalendarLink> For(ModuleConfig? tile, AppConfig app)
    {
        var own = tile?.OptList("Calendars") ?? new List<string>();
        if (own.Count > 0)
            return own.Select(u => new CalendarLink { Url = u }).ToList();
        return app.Calendars.Where(c => !string.IsNullOrWhiteSpace(c.Url)).ToList();
    }

    /// <summary>The shared service for these links (null when there are none).</summary>
    public static AgendaService? Service(IReadOnlyList<CalendarLink> feeds)
    {
        if (feeds.Count == 0)
            return null;
        // Names and colours are in the key too, so renaming a calendar shows at once.
        string key = "agenda:" + string.Join("\n", feeds.Select(f => $"{f.Url.Trim()}|{f.Name}|{f.Color}"));
        return ServiceStore.Get(key, () => new AgendaService(feeds));
    }

    /// <summary>The calendars from Settings for the Clock (unless "Next event" is hidden on it) and the Calendar tile.</summary>
    public static AgendaService? Shared(AppConfig app, ModuleConfig? tile = null) =>
        tile?.OptBool("HideNextEvent") == true ? null : Service(For(null, app));

    /// <summary>
    /// "Now: Standup (until 10:30)" while a timed event is on, "Next: Standup in 12 min" when one starts within two
    /// hours, otherwise null. All-day events are left out (they'd always be "now").
    /// </summary>
    public static string? NextLine(IReadOnlyList<AgendaEvent>? events, DateTime now, bool use24Hour)
    {
        if (events == null)
            return null;
        string Time(DateTime t) => t.ToString(use24Hour ? "H:mm" : "h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        AgendaEvent? current = null, next = null;
        foreach (var e in events)
        {
            if (e.AllDay)
                continue;
            if (e.Start <= now && e.Until > now)
                current ??= e;
            else if (e.Start > now && e.Start - now <= TimeSpan.FromHours(2))
                next = next == null || e.Start < next.Start ? e : next;
        }
        // Something about to start matters more than the last minutes of what's on.
        if (current != null && (next == null || next.Start - now > TimeSpan.FromMinutes(10)))
            return L.F("Now: {0} (until {1})", current.Title, Time(current.Until));
        if (next == null)
            return null;
        int minutes = (int)Math.Ceiling((next.Start - now).TotalMinutes);
        string when = minutes < 60 ? L.F("in {0} min", minutes) : minutes % 60 == 0 ? L.F("in {0} h", minutes / 60) : L.F("in {0} h {1} min", minutes / 60, minutes % 60);
        return L.F("Next: {0} {1}", next.Title, when);
    }

    /// <summary>For each day of the month: which calendars have something on it (at most three, for the dots).</summary>
    public static Dictionary<int, List<int>> Marks(IReadOnlyList<AgendaEvent>? events, int year, int month)
    {
        var marks = new Dictionary<int, List<int>>();
        if (events == null)
            return marks;
        var first = new DateTime(year, month, 1);
        int days = DateTime.DaysInMonth(year, month);
        foreach (var e in events)
        {
            if (e.Start >= first.AddDays(days) || e.Until <= first && e.Start < first)
                continue;
            for (int d = 1; d <= days; d++)
            {
                if (!e.OnDay(first.AddDays(d - 1)))
                    continue;
                if (!marks.TryGetValue(d, out var list))
                    marks[d] = list = new List<int>();
                if (list.Count < 3 && !list.Contains(e.Calendar))
                    list.Add(e.Calendar);
            }
        }
        return marks;
    }
}
