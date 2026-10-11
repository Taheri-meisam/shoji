using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>One desktop notification as the Notifications tile shows it. <see cref="Id"/> is Shoji's own, increasing.</summary>
public sealed record NotificationItem(string App, string Title, string Body, DateTime Time, string? IconPath = null)
{
    public long Id { get; init; }
}

/// <summary>
/// The notifications seen while a Notifications tile exists, newest first. Filled by the platform reader
/// (WindowsNotifications / LinuxNotifications), read by the tiles. Kept in memory only: never saved, never put in
/// the <see cref="Snapshot"/> (so never sent to the phone page), and dropped when the last tile goes away.
/// </summary>
public static class NotificationHub
{
    public const int Max = 30;

    /// <summary>Nothing is kept longer than this, whatever a tile's "Keep for" says.</summary>
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    static readonly object Gate = new();
    static NotificationItem[] _items = Array.Empty<NotificationItem>();
    static long _nextId;
    static int _version;
    static string? _hint;

    /// <summary>Raised on the reader's thread after an item was added.</summary>
    public static event Action<NotificationItem>? Added;

    /// <summary>Bumped on every change, so a tile rebuilds only when something happened.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Newest first. An immutable array: safe to enumerate on any thread.</summary>
    public static NotificationItem[] Items => Volatile.Read(ref _items);

    /// <summary>A problem the reader wants shown on the tile ("install dbus-bin"), or null.</summary>
    public static string? Hint
    {
        get => Volatile.Read(ref _hint);
        set
        {
            if (Interlocked.Exchange(ref _hint, value) != value)
                Interlocked.Increment(ref _version);
        }
    }

    public static void Add(NotificationItem item)
    {
        if (item.Title.Length == 0 && item.Body.Length == 0)
            return;
        lock (Gate)
        {
            // Some apps send the same toast twice in a row (or update it in place): keep one.
            var same = _items.FirstOrDefault(i => i.App == item.App && i.Title == item.Title && i.Body == item.Body
                                                  && (item.Time - i.Time).Duration() < TimeSpan.FromSeconds(10));
            if (same != null)
                return;
            item = item with { Id = ++_nextId };
            var cutoff = DateTime.Now - MaxAge;
            _items = _items.Prepend(item).Where(i => i.Time > cutoff).OrderByDescending(i => i.Time).Take(Max).ToArray();
            Interlocked.Increment(ref _version);
        }
        Added?.Invoke(item);
    }

    public static void Dismiss(long id)
    {
        lock (Gate)
        {
            _items = _items.Where(i => i.Id != id).ToArray();
            Interlocked.Increment(ref _version);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _items = Array.Empty<NotificationItem>();
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>SHOJI_DEMO_NOTIFICATIONS=1 (test only): fills the hub with made-up notifications for screenshots.</summary>
    public static void DemoIfRequested()
    {
        if (Environment.GetEnvironmentVariable("SHOJI_DEMO_NOTIFICATIONS") != "1" || _nextId > 0)
            return;
        var now = DateTime.Now;
        Add(new("Mail", "Invoice #2291", "Your invoice for October is ready. Open the attachment to see the details.", now.AddMinutes(-41)));
        Add(new("WhatsApp", "Mum", "Are you coming for dinner on Sunday? We're making ramen.", now.AddMinutes(-12)));
        Add(new("Discord", "#general · Kai", "anyone up for a match tonight?\nwe need one more for the squad", now.AddMinutes(-2)));
        Add(new("Steam", "Download complete", "Elden Ring is ready to play.", now.AddSeconds(-20)));
    }
}

/// <summary>A tile's "Only these apps" / "Hide these apps" / "Hide message text" / "Keep for" options.</summary>
public sealed class NotificationFilter
{
    readonly List<string> _only, _hide;
    public bool HideText { get; }
    public TimeSpan KeepFor { get; }

    public NotificationFilter(ModuleConfig config)
    {
        _only = config.OptList("Only");
        _hide = config.OptList("Hide");
        HideText = config.OptBool("HideText");
        KeepFor = TimeSpan.FromMinutes(Math.Clamp(config.OptInt("Keep", 60), 1, 24 * 60));
    }

    /// <summary>App names match by part, ignoring case: "outlook" matches "Outlook (new)".</summary>
    public bool Allows(NotificationItem item, DateTime now)
    {
        if (now - item.Time > KeepFor)
            return false;
        bool Matches(string pattern) => item.App.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        if (_only.Count > 0 && !_only.Any(Matches))
            return false;
        return !_hide.Any(Matches);
    }
}

/// <summary>Text clean-up shared by the readers.</summary>
public static class NotificationText
{
    static readonly Regex Tags = new(@"</?(b|i|u|a|s|img|span|font|br)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Drops the simple markup notification bodies may carry (&lt;b&gt;, &lt;i&gt;, &lt;a href&gt;…) and decodes entities.</summary>
    public static string StripMarkup(string s)
    {
        if (s.IndexOf('<') < 0 && s.IndexOf('&') < 0)
            return s;
        s = Tags.Replace(s, m => m.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? "\n" : "");
        return System.Net.WebUtility.HtmlDecode(s);
    }

    /// <summary>Trims, removes control characters and blank lines; long texts are cut (the tile shows two lines anyway).</summary>
    public static string Tidy(string s, int max = 400)
    {
        var lines = s.Replace("\r", "").Split('\n')
            .Select(l => new string(l.Where(c => !char.IsControl(c) || c == '\t').ToArray()).Trim())
            .Where(l => l.Length > 0);
        s = string.Join("\n", lines);
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>"just now", "2 min ago", "1 h ago".</summary>
    public static string Ago(DateTime time, DateTime now)
    {
        var d = now - time;
        return d.TotalMinutes < 1 ? L.T("just now") : d.TotalHours < 1 ? L.F("{0} min ago", (int)d.TotalMinutes) : L.F("{0} h ago", (int)d.TotalHours);
    }
}
