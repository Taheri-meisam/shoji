using System;
using System.Collections.Generic;
using System.Linq;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>
/// Picks the active scene from the current state. A scene has to match for a moment before it takes over,
/// and has to stop matching for a few seconds before it lets go, so a quick alt-tab doesn't flip pages.
/// </summary>
public sealed class SceneEngine
{
    static readonly TimeSpan EnterDelay = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(4);

    string? _pending;
    DateTime _pendingSince;

    /// <summary>Name of the active scene, or null.</summary>
    public string? Active { get; private set; }

    /// <summary>Returns true when the active scene changed.</summary>
    public bool Update(AppConfig config, Snapshot s)
    {
        string? candidate = config.ScenesEnabled ? config.Scenes.FirstOrDefault(sc => sc.Enabled && Matches(sc, s))?.Name : null;
        if (candidate == Active)
        {
            _pending = Active;
            return false;
        }
        if (candidate != _pending)
        {
            _pending = candidate;
            _pendingSince = DateTime.UtcNow;
            return false;
        }
        // Switching scenes off applies at once.
        if (!config.ScenesEnabled || DateTime.UtcNow - _pendingSince >= (candidate == null ? LeaveDelay : EnterDelay))
        {
            Active = candidate;
            return true;
        }
        return false;
    }

    public static bool Matches(SceneConfig sc, Snapshot s)
    {
        var f = s.Foreground;
        bool any = false;
        if (sc.Apps is { Count: > 0 })
        {
            any = true;
            if (!sc.Apps.Any(a => AppPattern.Matches(f, a)))
                return false;
        }
        if (sc.Fullscreen)
        {
            any = true;
            if (!f.IsFullscreen)
                return false;
        }
        if (sc.IdleMinutes is int minutes && minutes > 0)
        {
            any = true;
            if (s.IdleSeconds < minutes * 60)
                return false;
        }
        if (!string.IsNullOrWhiteSpace(sc.From) && !string.IsNullOrWhiteSpace(sc.To))
        {
            any = true;
            if (!InWindow(s.Time.TimeOfDay, sc.From, sc.To))
                return false;
        }
        if (sc.ExceptApps?.Any(a => AppPattern.Matches(f, a)) == true)
            return false;
        return any;
    }

    /// <summary>True while night mode should dim the dashboard.</summary>
    public static bool NightActive(NightConfig night, DateTime now) =>
        night.Enabled && InWindow(now.TimeOfDay, night.From, night.To);

    /// <summary>"22:00"–"06:00" wraps past midnight.</summary>
    public static bool InWindow(TimeSpan now, string from, string to)
    {
        if (!TryParseClock(from, out var a) || !TryParseClock(to, out var b))
            return false;
        return a <= b ? now >= a && now < b : now >= a || now < b;
    }

    static readonly System.Text.RegularExpressions.Regex ClockPattern = new(
        @"^([0-9]{1,2})(?:[:.h]([0-9]{2}))?(?::([0-9]{2}))?\s*(?:([ap])\.?\s*m?\.?)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A time of day as people type it: "7", "07:30", "19.30", "7pm", "7:30 am". (TimeSpan.TryParse reads "7" as seven days.)
    /// The result is between 0:00 and 24:00.
    /// </summary>
    public static bool TryParseClock(string? text, out TimeSpan time)
    {
        time = default;
        var m = ClockPattern.Match(text?.Trim() ?? "");
        if (!m.Success)
            return false;
        int hours = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minutes = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        int seconds = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (minutes > 59 || seconds > 59)
            return false;
        if (m.Groups[4].Success)
        {
            // 12 am is midnight, 12 pm is noon.
            if (hours is < 1 or > 12)
                return false;
            hours = hours % 12 + (char.ToLowerInvariant(m.Groups[4].Value[0]) == 'p' ? 12 : 0);
        }
        else if (hours > 24 || hours == 24 && minutes + seconds > 0)
        {
            return false;
        }
        time = new TimeSpan(hours, minutes, seconds);
        return true;
    }
}

/// <summary>Threshold checks for the alert banner. A problem has to last a few seconds before it alerts.</summary>
public sealed class AlertEngine
{
    static readonly TimeSpan Sustain = TimeSpan.FromSeconds(5);

    readonly Dictionary<string, DateTime> _since = new();
    readonly Dictionary<string, DateTime> _snoozed = new();
    readonly HashSet<string> _announced = new();

    public sealed record Alert(string Key, string Message);

    /// <summary>Active alerts; <paramref name="isNew"/> is true when one appeared that wasn't showing before (for the sound).</summary>
    public IReadOnlyList<Alert> Evaluate(AlertsConfig a, Snapshot s, out bool isNew)
    {
        isNew = false;
        if (!a.Enabled)
        {
            _since.Clear();
            return Array.Empty<Alert>();
        }

        var raw = new List<Alert>();
        void Check(string key, bool condition, string message)
        {
            if (condition)
                raw.Add(new Alert(key, message));
        }

        Check("cpu", a.CpuTemp > 0 && s.Cpu?.TemperatureC >= a.CpuTemp, L.F("CPU is hot: {0}", UI.W.Temp(s.Cpu?.TemperatureC, unit: true)));
        Check("gpu", a.GpuTemp > 0 && s.Gpu?.TemperatureC >= a.GpuTemp, L.F("GPU is hot: {0}", UI.W.Temp(s.Gpu?.TemperatureC, unit: true)));
        Check("ram", a.RamPercent > 0 && s.Memory?.LoadPercent >= a.RamPercent, L.F("Memory almost full: {0:0}%", s.Memory?.LoadPercent));
        Check("ping", a.PingMs > 0 && s.Ping is { } p && (p.LastMs >= a.PingMs || p.LastMs == null && p.LossPercent > 0),
              s.Ping?.LastMs is double ms ? L.F("High ping: {0:0} ms", ms) : L.T("Connection dropping packets"));
        foreach (var site in s.Sites)
        {
            Check("site:" + site.Url, a.SiteDown && site.State == SiteState.Down, L.F("{0} is down: {1}", site.Name, site.Problem));
            Check("cert:" + site.Url, a.CertDays > 0 && site.CertDaysLeft <= a.CertDays,
                site.CertDaysLeft <= 0 ? L.F("{0}: SSL certificate has expired", site.Name) : L.F("{0}: SSL certificate expires in {1} days", site.Name, site.CertDaysLeft));
            Check("domain:" + site.Url, a.CertDays > 0 && site.DomainDaysLeft <= a.CertDays * 2, L.F("{0}: domain expires in {1} days", site.Name, site.DomainDaysLeft));
        }
        foreach (var d in s.Disks)
        {
            double free = d.TotalGb > 0 ? (d.TotalGb - d.UsedGb) / d.TotalGb * 100 : 100;
            Check("disk:" + d.Name, a.DiskFreePercent > 0 && free <= a.DiskFreePercent, L.F("Drive {0} almost full: {1:0} GB left", d.Name, d.TotalGb - d.UsedGb));
        }

        var now = DateTime.UtcNow;
        var keys = raw.Select(r => r.Key).ToHashSet();
        foreach (var gone in _since.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _since.Remove(gone);
            _announced.Remove(gone);
        }

        var active = new List<Alert>();
        foreach (var r in raw)
        {
            if (!_since.TryGetValue(r.Key, out var since))
                _since[r.Key] = since = now;
            if (now - since < Sustain || _snoozed.TryGetValue(r.Key, out var until) && now < until)
                continue;
            active.Add(r);
            if (_announced.Add(r.Key))
                isNew = true;
        }
        return active;
    }

    /// <summary>Hides the given alerts for a while (tap on the banner).</summary>
    public void Snooze(IEnumerable<Alert> alerts, TimeSpan forHowLong)
    {
        foreach (var a in alerts)
            _snoozed[a.Key] = DateTime.UtcNow + forHowLong;
    }
}

/// <summary>App patterns in scenes: "spotify", "*launcher*" (wildcards), or part of the program's path ("\\steamapps\\").</summary>
public static class AppPattern
{
    /// <summary>
    /// Does an app pattern match? "spotify" matches the process name, "*launcher*" uses wildcards,
    /// and anything with a slash matches part of the exe path ("\steamapps\").
    /// </summary>
    public static bool Matches(ForegroundInfo f, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0 || f.ProcessId == 0)
            return false;
        if (pattern.Contains('\\') || pattern.Contains('/'))
            return f.Path != null && f.Path.Replace('/', '\\').Contains(pattern.Replace('/', '\\').Trim('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            pattern = pattern[..^4];
        return Wildcard(f.ProcessName, pattern);
    }

    static bool Wildcard(string text, string pattern)
    {
        if (!pattern.Contains('*'))
            return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
