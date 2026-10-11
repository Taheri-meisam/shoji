using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoji.Core;

namespace Shoji.Sensors;

public sealed record RecapInfo(
    string App,
    DateTime Started,
    TimeSpan Duration,
    double? AverageFps,
    double? LowFps,
    double? MaxCpuTemp,
    double? MaxGpuTemp,
    double? AverageCpuLoad,
    double? AverageGpuLoad,
    double? MaxPing,
    bool Live);

/// <summary>
/// Notices game sessions (a full-screen app that isn't a browser or video player) and keeps a summary:
/// time played, average / 1% low FPS, peak temperatures and loads. The last one is saved to recap.json.
/// </summary>
public sealed class RecapTracker
{
    static string FilePath => Path.Combine(AppConfig.Folder, "recap.json");
    static readonly TimeSpan MinimumLength = TimeSpan.FromMinutes(2);
    static readonly TimeSpan AwayGrace = TimeSpan.FromMinutes(3); // alt-tab out and back keeps the session

    public static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "vlc", "mpv", "mpc-hc64", "PotPlayerMini64",
        "explorer", "ApplicationFrameHost", "Netflix", "Video.UI", "Microsoft.Media.Player", "obs64", "Shoji",
        // Linux
        "chromium", "google-chrome", "totem", "celluloid", "showtime", "obs", "nautilus", "gnome-shell", "steam", "steamwebhelper",
    };

    // Times are UTC so durations survive clock changes (DST); RecapInfo.Started is shown in local time.
    sealed class Session
    {
        public int Pid;
        public string App = "";
        public DateTime Started;
        public DateTime LastSeen;
        public readonly List<double> Fps = new(), Lows = new(), CpuLoad = new(), GpuLoad = new();
        public double? MaxCpuTemp, MaxGpuTemp, MaxPing;
    }

    readonly object _gate = new();
    Session? _current;
    RecapInfo? _last;

    public RecapTracker() => _last = Load();

    public void Sample(Snapshot s)
    {
        lock (_gate)
            SampleLocked(s);
    }

    void SampleLocked(Snapshot s)
    {
        var f = s.Foreground;
        var now = DateTime.UtcNow;
        bool gameInFront = f.IsFullscreen && f.ProcessId != 0 && !NotGames.Contains(f.ProcessName);

        if (_current != null && (gameInFront && f.ProcessId != _current.Pid || now - _current.LastSeen > AwayGrace || !Alive(_current.Pid)))
            Finish();

        if (gameInFront)
        {
            _current ??= new Session { Pid = f.ProcessId, App = Friendly(f), Started = now };
            var c = _current;
            c.LastSeen = now;
            if (s.Fps is { Fps: double fps } info && fps > 0)
            {
                c.Fps.Add(fps);
                if (info.OnePercentLow is double low)
                    c.Lows.Add(low);
            }
            if (s.Cpu?.LoadPercent is double cl) c.CpuLoad.Add(cl);
            if (s.Gpu?.LoadPercent is double gl) c.GpuLoad.Add(gl);
            c.MaxCpuTemp = Max(c.MaxCpuTemp, s.Cpu?.TemperatureC);
            c.MaxGpuTemp = Max(c.MaxGpuTemp, s.Gpu?.TemperatureC);
            c.MaxPing = Max(c.MaxPing, s.Ping?.LastMs);
        }
    }

    /// <summary>The game being played right now, or else the last finished session.</summary>
    public RecapInfo? Read()
    {
        lock (_gate)
            return _current != null && DateTime.UtcNow - _current.Started >= TimeSpan.FromSeconds(30)
                ? ToInfo(_current, live: true)
                : _last;
    }

    /// <summary>Ends a game session in progress and saves it (call when the collector shuts down).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_current != null)
                Finish();
        }
    }

    void Finish()
    {
        var c = _current!;
        _current = null;
        if (c.LastSeen - c.Started < MinimumLength)
            return;
        _last = ToInfo(c, live: false);
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_last));
        }
        catch (Exception ex)
        {
            Log.Write($"Recap could not be saved: {ex.Message}");
        }
    }

    static RecapInfo ToInfo(Session c, bool live) => new(
        c.App, c.Started.ToLocalTime(), (live ? DateTime.UtcNow : c.LastSeen) - c.Started,
        Avg(c.Fps), c.Lows.Count > 0 ? c.Lows.Min() : null,
        c.MaxCpuTemp, c.MaxGpuTemp, Avg(c.CpuLoad), Avg(c.GpuLoad), c.MaxPing, live);

    static double? Avg(List<double> v) => v.Count > 0 ? v.Average() : null;
    static double? Max(double? a, double? b) => b is double x ? Math.Max(a ?? x, x) : a;

    static bool Alive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    static string Friendly(ForegroundInfo f)
    {
        if (f.Path != null)
        {
            try
            {
                var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(f.Path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40)
                    return desc.Trim();
            }
            catch
            {
                // no version info
            }
        }
        return string.IsNullOrWhiteSpace(f.Title) || f.Title.Length > 40 ? f.ProcessName : f.Title;
    }

    static RecapInfo? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<RecapInfo>(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }
}
