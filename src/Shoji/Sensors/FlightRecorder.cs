using System;
using System.Collections.Generic;
using System.Linq;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Something that went wrong for a moment, and what else was going on right then.
/// <paramref name="Kind"/>: stutter, cpu, heat, memory, vram, ping. <paramref name="Level"/>: 1 caution, 2 warning.
/// <paramref name="Culprit"/>: the short name of what it coincided with (an app, "GPU memory"…), for tallies.
/// </summary>
public sealed record Incident(DateTime Time, string Kind, string Title, string Cause, int Level, string? Culprit = null);

/// <summary>
/// A flight recorder for the PC: keeps the last minute of readings and, when something goes wrong (the frame rate
/// drops, the CPU is pinned, a part overheats, memory fills up, the ping jumps), writes down that moment together
/// with what changed just before it: the app that suddenly got busy, a full graphics memory, a part at its limit.
/// By the time a task manager is open the cause is usually gone; this is the answer to "what was that?".
/// Fed by the collector while a Flight recorder tile exists; lives for the whole run, so a theme or page change
/// doesn't wipe the list.
/// </summary>
public sealed class FlightRecorder
{
    public static readonly FlightRecorder Shared = new();

    static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    const int Keep = 40, MinBaseline = 6;

    sealed record Reading(DateTime Time, double? Fps, double? Cpu, double? CpuTemp, double? Gpu, double? GpuTemp, double? VramPercent,
        double? VramUsedGb, double? VramTotalGb, double? Mem, double? Ping, bool PingLost, double? DownloadMbps,
        IReadOnlyList<ProcessInfo> Apps);

    readonly object _gate = new();
    readonly List<Reading> _before = new();
    readonly Dictionary<string, DateTime> _quietUntil = new();
    IReadOnlyList<Incident> _incidents = Array.Empty<Incident>();
    int _cpuPinned;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Incident> Recent => _incidents;

    public void Clear()
    {
        lock (_gate)
            _incidents = Array.Empty<Incident>();
    }

    /// <summary>For demo data and tests: put these in the list as they are.</summary>
    public void Seed(IEnumerable<Incident> incidents)
    {
        lock (_gate)
            _incidents = incidents.OrderByDescending(i => i.Time).Take(Keep).ToArray();
    }

    /// <summary>Looks at one refresh. Returns what it noticed just now (usually nothing).</summary>
    public IReadOnlyList<Incident> Sample(Snapshot s, double tempCrit)
    {
        lock (_gate)
        {
            var now = ToReading(s);
            _before.RemoveAll(r => now.Time - r.Time > Window || r.Time > now.Time);
            var found = _before.Count >= MinBaseline ? Detect(now, s, tempCrit) : new List<Incident>();
            _before.Add(now);
            if (found.Count > 0)
                _incidents = found.Concat(_incidents).Take(Keep).ToArray();
            return found;
        }
    }

    static Reading ToReading(Snapshot s)
    {
        double? vramPercent = s.Gpu is { VramUsedMb: double used, VramTotalMb: double total } && total > 0 ? used / total * 100 : null;
        return new Reading(s.Time, s.Fps is { Fps: double f } && f > 0 ? f : null, s.Cpu?.LoadPercent, s.Cpu?.TemperatureC,
            s.Gpu?.LoadPercent, s.Gpu?.TemperatureC, vramPercent, s.Gpu?.VramUsedMb / 1024, s.Gpu?.VramTotalMb / 1024,
            s.Memory?.LoadPercent, s.Ping?.LastMs, s.Ping != null && s.Ping.LastMs == null,
            s.Network == null ? null : s.Network.DownloadBytesPerSec * 8 / 1e6, s.TopProcesses);
    }

    List<Incident> Detect(Reading now, Snapshot s, double tempCrit)
    {
        var found = new List<Incident>();
        var last = _before[^1];

        void Add(string kind, double quietSeconds, int level, string title, (string Text, string? Culprit) cause)
        {
            if (_quietUntil.TryGetValue(kind, out var until) && now.Time < until)
                return;
            _quietUntil[kind] = now.Time.AddSeconds(quietSeconds);
            found.Add(new Incident(now.Time, kind, title, cause.Text, level, cause.Culprit));
        }

        // Frame rate: well under what it was a moment ago (the median, so one earlier dip doesn't move the bar).
        var fps = _before.Where(r => r.Fps != null).Select(r => r.Fps!.Value).OrderBy(v => v).ToList();
        if (now.Fps is double f && fps.Count >= MinBaseline)
        {
            double usual = fps[fps.Count / 2];
            if (usual >= 24 && f < usual * 0.6 && usual - f >= 12)
                Add("stutter", 15, 1, L.F("Stutter: {0} → {1} fps", usual.ToString("0"), f.ToString("0")), StutterCause(now, s, tempCrit));
        }

        // CPU flat out for three refreshes in a row, when it wasn't before.
        _cpuPinned = now.Cpu >= 95 ? _cpuPinned + 1 : 0;
        if (_cpuPinned == 3)
        {
            var earlier = _before.Take(Math.Max(0, _before.Count - 2)).Where(r => r.Cpu != null).Select(r => r.Cpu!.Value).ToList();
            if (earlier.Count >= 3 && earlier.Average() < 70)
                Add("cpu", 45, 1, L.F("CPU pinned at {0}%", now.Cpu!.Value.ToString("0")), BusiestApp(now, s, grewOnly: true) ?? (L.T("no single app stood out"), null));
        }

        // A part crossing its limit (the "hot" temperature from Settings → Alerts).
        if (now.CpuTemp >= tempCrit && last.CpuTemp < tempCrit)
            Add("heat-cpu", 120, 2, L.F("CPU reached {0}°", now.CpuTemp!.Value.ToString("0")), BusiestApp(now, s, grewOnly: false) ?? (L.F("CPU load {0}%", (now.Cpu ?? 0).ToString("0")), null));
        if (now.GpuTemp >= tempCrit && last.GpuTemp < tempCrit)
            Add("heat-gpu", 120, 2, L.F("GPU reached {0}°", now.GpuTemp!.Value.ToString("0")), (L.F("GPU load {0}%", (now.Gpu ?? 0).ToString("0")), null));

        // Memory and graphics memory filling up.
        if (now.Mem >= 95 && last.Mem < 95)
            Add("memory", 120, 2, L.F("Memory almost full ({0}%)", now.Mem!.Value.ToString("0")), GrewMost(now) ?? BusiestByMemory(now) ?? (L.T("no single app stood out"), null));
        if (now.VramPercent >= 97 && last.VramPercent < 97)
            Add("vram", 120, 1, L.T("Graphics memory full"), (VramText(now), L.T("graphics memory")));

        // Ping: a jump well over what it was, or a lost reply.
        var pings = _before.Where(r => r.Ping != null).Select(r => r.Ping!.Value).ToList();
        if (pings.Count >= MinBaseline)
        {
            double usual = pings.Average();
            if (now.Ping is double ms && ms >= Math.Max(120, usual * 3))
                Add("ping", 30, 1, L.F("Ping jumped to {0} ms", ms.ToString("0")), NetworkCause(now, usual));
            else if (now.PingLost && !last.PingLost)
                Add("ping", 30, 1, L.T("Ping lost"), NetworkCause(now, usual));
        }
        return found;
    }

    // ---------------------------------------------------------------- what else was going on

    /// <summary>How much of the CPU an app used on average over the minute before.</summary>
    double UsualCpu(string app)
    {
        double sum = 0;
        foreach (var r in _before)
            sum += r.Apps.FirstOrDefault(a => a.Name == app)?.CpuPercent ?? 0;
        return sum / _before.Count;
    }

    /// <summary>The app whose CPU use jumped the most against the last minute (or simply the busiest one).</summary>
    (string, string?)? BusiestApp(Reading now, Snapshot s, bool grewOnly)
    {
        ProcessInfo? best = null;
        double bestRise = 0;
        foreach (var a in now.Apps)
        {
            double rise = a.CpuPercent - UsualCpu(a.Name);
            if (rise > bestRise)
                (best, bestRise) = (a, rise);
        }
        if (best != null && bestRise >= 8)
            return (L.F("{0} jumped to {1}% CPU", best.Name, best.CpuPercent.ToString("0")), best.Name);
        if (grewOnly)
            return null;
        var top = now.Apps.OrderByDescending(a => a.CpuPercent).FirstOrDefault();
        return top is { CpuPercent: >= 5 } ? (L.F("busiest: {0} at {1}% CPU", top.Name, top.CpuPercent.ToString("0")), top.Name) : null;
    }

    /// <summary>The app whose memory grew by a gigabyte or more within the minute.</summary>
    (string, string?)? GrewMost(Reading now)
    {
        var first = _before[0];
        ProcessInfo? best = null;
        double bestGrowth = 0;
        foreach (var a in now.Apps)
        {
            double growth = a.MemoryMb - (first.Apps.FirstOrDefault(x => x.Name == a.Name)?.MemoryMb ?? a.MemoryMb);
            if (growth > bestGrowth)
                (best, bestGrowth) = (a, growth);
        }
        return best != null && bestGrowth >= 1024
            ? (L.F("{0} grew to {1} GB", best.Name, (best.MemoryMb / 1024).ToString("0.0")), best.Name)
            : null;
    }

    static (string, string?)? BusiestByMemory(Reading now)
    {
        var top = now.Apps.OrderByDescending(a => a.MemoryMb).FirstOrDefault();
        return top is { MemoryMb: >= 1024 } ? (L.F("biggest: {0} with {1} GB", top.Name, (top.MemoryMb / 1024).ToString("0.0")), top.Name) : null;
    }

    static string VramText(Reading now) => now is { VramUsedGb: double used, VramTotalGb: double total }
        ? L.F("{0} of {1} GB in use", used.ToString("0.0"), total.ToString("0.#"))
        : L.T("the graphics card ran out of memory");

    /// <summary>A stutter's most likely company, in the order they usually matter.</summary>
    (string, string?) StutterCause(Reading now, Snapshot s, double tempCrit)
    {
        string game = s.Foreground.ProcessName;
        if (BusiestApp(now, s, grewOnly: true) is { } app)
        {
            // The game itself getting busy (loading, compiling shaders) is worth saying, but it isn't a culprit to hunt.
            if (!string.IsNullOrEmpty(game) && string.Equals(app.Item2, game, StringComparison.OrdinalIgnoreCase))
                return (L.F("the game itself got busy ({0})", app.Item1), null);
            return app;
        }
        if (now.VramPercent >= 95)
            return (L.F("graphics memory is full ({0})", VramText(now)), L.T("graphics memory"));
        if (now.GpuTemp >= tempCrit - 2)
            return (L.F("GPU at {0}°: it may be slowing itself down", now.GpuTemp!.Value.ToString("0")), L.T("GPU heat"));
        if (now.CpuTemp >= tempCrit - 2)
            return (L.F("CPU at {0}°: it may be slowing itself down", now.CpuTemp!.Value.ToString("0")), L.T("CPU heat"));
        if (now.Mem >= 92)
            return (L.F("memory is {0}% full", now.Mem!.Value.ToString("0")), L.T("memory"));
        if (now.Cpu >= 95)
            return (L.F("CPU at {0}%", now.Cpu!.Value.ToString("0")), L.T("CPU load"));
        return (L.T("nothing else stood out"), null);
    }

    (string, string?) NetworkCause(Reading now, double usual)
    {
        string normally = L.F("usually {0} ms", usual.ToString("0"));
        return now.DownloadMbps >= 20
            ? (L.F("{0}, while downloading at {1} Mbps", normally, now.DownloadMbps!.Value.ToString("0")), L.T("a download"))
            : (normally, null);
    }
}
