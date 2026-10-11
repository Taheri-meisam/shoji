using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// MangoHud, the Linux in-game overlay, and the few lines of its config that make it log frame times into Shoji's
/// folder: Linux has no system-wide frame timing like Windows' DirectX present events, but MangoHud sees every frame
/// of the games it's started with (Steam launch options "mangohud %command%", or MANGOHUD=1).
/// </summary>
public static class MangoHud
{
    const string BlockStart = "# >>> Shoji: log frame times for Shoji's FPS tile (remove this block to stop)";
    const string BlockEnd = "# <<< Shoji";

    /// <summary>Where MangoHud writes its CSV logs for Shoji (~/.local/share/Shoji/mangohud). Not the config folder: the logs are large-ish.</summary>
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Shoji", "mangohud");

    /// <summary>MangoHud's own config file, ~/.config/MangoHud/MangoHud.conf.</summary>
    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "MangoHud", "MangoHud.conf");

    /// <summary>The mangohud launcher, or its Vulkan layer (some packages ship only the layer).</summary>
    public static bool Installed =>
        LinuxSetup.Which("mangohud")
        || new[] { "/usr/share/vulkan/implicit_layer.d", "/etc/vulkan/implicit_layer.d", "/usr/local/share/vulkan/implicit_layer.d" }
            .Any(d => Directory.Exists(d) && Directory.GetFiles(d, "MangoHud*.json").Length > 0);

    /// <summary>
    /// Whether MangoHud.conf makes MangoHud log into <see cref="LogFolder"/> on its own: output_folder pointing there and
    /// autostart_log set (it's a delay in seconds; 0 / unset = never). MangoHud applies the lines in order, last one wins.
    /// </summary>
    public static bool Configured
    {
        get
        {
            var values = Read();
            return values.TryGetValue("output_folder", out var folder) && SameFolder(folder, LogFolder)
                   && values.TryGetValue("autostart_log", out var start) && double.TryParse(start, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0;
        }
    }

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a.Trim()), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);

    /// <summary>The active key=value lines of MangoHud.conf (comments skipped; for repeated keys the last wins, like MangoHud).</summary>
    static Dictionary<string, string> Read()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ConfigPath))
                return d;
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = line.IndexOf('=');
                if (eq > 0)
                    d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                else
                    d[line] = "1"; // bare flags such as "fps_only"
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("mangohud-conf", $"MangoHud.conf unreadable: {ex.Message}", TimeSpan.FromMinutes(10));
        }
        return d;
    }

    /// <summary>
    /// Adds Shoji's block at the end of MangoHud.conf (creating the file if needed). The user's own lines are kept
    /// as they are; Shoji's come last so they win. Running it again only refreshes Shoji's block.
    /// </summary>
    public static void SetUp()
    {
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : new List<string>();
        int start = lines.FindIndex(l => l.TrimStart().StartsWith(BlockStart, StringComparison.Ordinal));
        if (start >= 0)
        {
            int end = lines.FindIndex(start, l => l.Trim() == BlockEnd);
            lines.RemoveRange(start, (end < 0 ? start : end) - start + 1);
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0)
            lines.Add("");
        lines.Add(BlockStart);
        lines.Add("autostart_log=1");   // start logging 1 s after the game starts
        lines.Add("log_interval=100");  // a sample every 100 ms: enough for the FPS tile, ~3 MB per hour of play
        lines.Add("output_folder=" + LogFolder);
        lines.Add(BlockEnd);
        AtomicFile.WriteAllText(ConfigPath, string.Join("\n", lines) + "\n");
        Log.Write($"MangoHud set up to log frame times to {LogFolder}");
    }
}

/// <summary>
/// The frame rate on Linux, from the CSV logs MangoHud writes while a game runs (see <see cref="MangoHud"/>).
/// The newest log that is still growing is tailed: only the bytes appended since the last read are parsed.
/// An inotify watcher (FileSystemWatcher) points at a new log at once; a scan every few seconds backs it up.
/// Read on the collector's thread only.
/// </summary>
public sealed class LinuxFps : IDisposable
{
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(2);
    static readonly TimeSpan KeepLogs = TimeSpan.FromDays(2);

    FileSystemWatcher? _watcher;
    volatile string? _changed;          // the log the watcher last saw written
    MangoHudLog? _log;
    DateTime _scanAt = DateTime.MinValue, _cleanAt = DateTime.MinValue, _checkAt = DateTime.MinValue;
    string? _error;

    public LinuxFps()
    {
        // No folder yet: MangoHud isn't set up, nothing to watch (the scan notices it once "Set up" makes it).
        if (!Directory.Exists(MangoHud.LogFolder))
            return;
        try
        {
            _watcher = new FileSystemWatcher(MangoHud.LogFolder, "*.csv") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Created += (_, e) => _changed = e.FullPath;
            _watcher.Changed += (_, e) => _changed = e.FullPath;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // No inotify watches left, or the folder can't be made: the periodic scan still finds the logs.
            Log.WriteThrottled("fps-watch", $"MangoHud log folder not watched: {ex.Message}", TimeSpan.FromHours(1));
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public FpsInfo? Read()
    {
        var now = DateTime.UtcNow;
        if (now >= _cleanAt)
        {
            _cleanAt = now.AddHours(1);
            DeleteOldLogs(now);
        }

        // A log the watcher saw written that isn't the one being tailed: switch if it's newer (a new game started).
        string? hint = _changed;
        if (hint != null && hint != _log?.Path && IsLog(hint))
        {
            _changed = null;
            Follow(hint);
        }
        if (now >= _scanAt)
        {
            _scanAt = now.AddSeconds(_log?.Live(now, Stale) == true ? 10 : 3);
            if (Newest() is string newest && newest != _log?.Path)
                Follow(newest);
        }

        if (_log != null)
        {
            _log.Pump();
            if (_log.Live(now, Stale))
                return _log.Compute();
            if (now - _log.LastRowAt > TimeSpan.FromMinutes(1) && File.Exists(_log.Path) && File.GetLastWriteTimeUtc(_log.Path) < now.AddMinutes(-1))
            {
                _log.Dispose(); // the game ended a while ago
                _log = null;
            }
        }

        // Not playing (or MangoHud isn't logging): say what's missing only when it really is missing.
        if (now >= _checkAt)
        {
            _checkAt = now.AddSeconds(30);
            _error = !MangoHud.Installed ? "needs MangoHud (Settings → Hardware)" : !MangoHud.Configured ? "needs setting up (Settings → Hardware)" : null;
        }
        return new FpsInfo(_log?.App ?? "", null, null, null, _log == null ? _error : null);
    }

    void Follow(string path)
    {
        try
        {
            // Only a log that's still being written (a game running now); old ones from earlier sessions are ignored.
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(10))
                return;
            if (_log != null && _log.Live(DateTime.UtcNow, Stale) && File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(_log.Path))
                return;
            _log?.Dispose();
            _log = new MangoHudLog(path);
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("fps-open", $"MangoHud log {Path.GetFileName(path)} unreadable: {ex.Message}", TimeSpan.FromMinutes(10));
        }
    }

    /// <summary>"Game_2026-10-08_21-14-03.csv", not the "…_summary.csv" MangoHud adds when logging stops.</summary>
    static bool IsLog(string path) =>
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("_summary.csv", StringComparison.OrdinalIgnoreCase);

    static string? Newest()
    {
        if (!Directory.Exists(MangoHud.LogFolder))
            return null;
        try
        {
            return new DirectoryInfo(MangoHud.LogFolder).EnumerateFiles("*.csv")
                .Where(f => IsLog(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null; // folder missing
        }
    }

    /// <summary>The logs grow by a few MB per hour of play; ones not written for two days go (only Shoji's folder is touched).</summary>
    static void DeleteOldLogs(DateTime now)
    {
        if (!Directory.Exists(MangoHud.LogFolder))
            return;
        try
        {
            foreach (var f in new DirectoryInfo(MangoHud.LogFolder).EnumerateFiles("*.csv"))
                if (now - f.LastWriteTimeUtc > KeepLogs)
                    f.Delete();
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("fps-clean", $"Old MangoHud logs not deleted: {ex.Message}", TimeSpan.FromHours(6));
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _log?.Dispose();
        _log = null;
    }
}

/// <summary>
/// One MangoHud CSV log, read as it grows. The file starts with a few lines of system info, then a header row
/// ("fps,frametime,cpu_load,…,elapsed"), then a row per sample: fps (MangoHud's own, averaged over ~0.5 s),
/// the frame time of that frame in ms and the time since logging started in ns.
/// </summary>
public sealed class MangoHudLog : IDisposable
{
    // "Cyberpunk2077.exe_2026-10-08_21-14-03.csv" → "Cyberpunk2077"
    static readonly Regex Name = new(@"^(?<app>.+?)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(\.csv)?$", RegexOptions.CultureInvariant);
    const int TailBytes = 64 * 1024;   // when joining a long log midway, only its end matters
    const int MaxRead = 1 << 20;        // per Pump; a log that grew more than this since the last read jumps to its end

    readonly FileStream _file;
    readonly byte[] _buffer = new byte[64 * 1024];
    readonly StringBuilder _partial = new();
    readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    readonly char[] _chars = new char[64 * 1024 + 8];
    int _fpsCol = -1, _frameCol = -1, _elapsedCol = -1, _columns;
    bool _header, _skipPartialLine;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Samples of the last ~10 s: time (s), MangoHud's fps, frame time (ms).
    readonly Queue<(double T, double Fps, double FrameMs)> _rows = new();
    double _lastElapsed = double.NaN;

    public string Path { get; }
    public string App { get; }
    public DateTime LastRowAt { get; private set; } = DateTime.MinValue;

    public MangoHudLog(string path)
    {
        Path = path;
        var m = Name.Match(System.IO.Path.GetFileNameWithoutExtension(path));
        string app = m.Success ? m.Groups["app"].Value : System.IO.Path.GetFileNameWithoutExtension(path);
        App = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
    }

    /// <summary>Whether a sample arrived within <paramref name="stale"/> (else the game paused logging, lost focus to a loading screen, or quit).</summary>
    public bool Live(DateTime now, TimeSpan stale) => now - LastRowAt <= stale && _rows.Count > 0;

    /// <summary>Reads what was appended since the last call. Never throws.</summary>
    public void Pump()
    {
        try
        {
            long length = _file.Length;
            if (length < _file.Position)
            {
                // Truncated / rewritten: start over.
                _file.Position = 0;
                _partial.Clear();
                _header = false;
            }
            if (_header && length - _file.Position > MaxRead)
                JumpTo(length - TailBytes);
            int budget = MaxRead;
            while (budget > 0)
            {
                int n = _file.Read(_buffer, 0, Math.Min(_buffer.Length, budget));
                if (n <= 0)
                    break;
                budget -= n;
                int c = _utf8.GetChars(_buffer, 0, n, _chars, 0, flush: false);
                Feed(_chars.AsSpan(0, c));
                // Joining a long log: once its header is known, skip to the last part.
                if (_header && !_skipPartialLine && length - _file.Position > TailBytes * 2)
                    JumpTo(length - TailBytes);
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("fps-read", $"MangoHud log read failed: {ex.Message}", TimeSpan.FromMinutes(10));
        }
    }

    void JumpTo(long position)
    {
        _file.Position = Math.Max(0, position);
        _partial.Clear();
        _utf8.Reset();
        _skipPartialLine = true; // landed mid-line
    }

    void Feed(ReadOnlySpan<char> text)
    {
        while (text.Length > 0)
        {
            int nl = text.IndexOf('\n');
            if (nl < 0)
            {
                _partial.Append(text);
                return;
            }
            _partial.Append(text[..nl]);
            text = text[(nl + 1)..];
            string line = _partial.ToString().TrimEnd('\r');
            _partial.Clear();
            if (_skipPartialLine)
            {
                _skipPartialLine = false;
                continue;
            }
            Line(line);
        }
    }

    void Line(string line)
    {
        if (line.Length == 0)
            return;
        var fields = line.Split(',');
        if (!_header)
        {
            // Everything before the column names is system info / version lines.
            int fps = Array.FindIndex(fields, f => f.Trim().Equals("fps", StringComparison.OrdinalIgnoreCase));
            int frame = Array.FindIndex(fields, f => f.Trim().Equals("frametime", StringComparison.OrdinalIgnoreCase));
            if (fps < 0 && frame < 0)
                return;
            _fpsCol = fps;
            _frameCol = frame;
            _elapsedCol = Array.FindIndex(fields, f => f.Trim().Equals("elapsed", StringComparison.OrdinalIgnoreCase));
            _columns = fields.Length;
            _header = true;
            return;
        }
        if (fields.Length < _columns)
            return; // a row cut short
        double? fpsValue = Number(fields, _fpsCol), frameMs = Number(fields, _frameCol);
        if (fpsValue is not > 0 && frameMs is not > 0)
            return;
        double fpsNow = fpsValue is > 0 ? fpsValue.Value : 1000 / frameMs!.Value;
        double ft = frameMs is > 0 ? frameMs.Value : 1000 / fpsNow;

        // MangoHud's own clock (ns since logging started) keeps the spacing right even if rows reach us in bursts;
        // without it (older versions) the time they're read is close enough.
        double t = _clock.Elapsed.TotalSeconds;
        if (Number(fields, _elapsedCol) is double ns && ns >= 0)
        {
            double s = ns / 1e9;
            if (double.IsNaN(_lastElapsed) || s >= _lastElapsed)
            {
                _lastElapsed = s;
                t = s;
            }
        }
        _rows.Enqueue((t, fpsNow, ft));
        while (_rows.Count > 0 && t - _rows.Peek().T > 10)
            _rows.Dequeue();
        // A clock change (switched source of time) would leave old rows "in the future": start the window over.
        if (_rows.Count > 0 && _rows.Peek().T > t)
        {
            _rows.Clear();
            _rows.Enqueue((t, fpsNow, ft));
        }
        LastRowAt = DateTime.UtcNow;
    }

    static double? Number(string[] fields, int col) =>
        col >= 0 && col < fields.Length && double.TryParse(fields[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>FPS over the last second, 1% low over the last ~10 s (from the slowest 1% of sampled frame times) and the average frame time.</summary>
    public FpsInfo Compute()
    {
        if (_rows.Count == 0)
            return new FpsInfo(App, null, null, null, null);
        double last = 0;
        foreach (var r in _rows)
            last = r.T;
        double fpsSum = 0, ftSum = 0;
        int n = 0;
        var frames = new double[_rows.Count];
        int i = 0;
        foreach (var r in _rows)
        {
            frames[i++] = r.FrameMs;
            if (last - r.T <= 1)
            {
                fpsSum += r.Fps;
                ftSum += r.FrameMs;
                n++;
            }
        }
        Array.Sort(frames);
        double p99 = frames[(int)Math.Min(frames.Length - 1, Math.Floor(frames.Length * 0.99))];
        return new FpsInfo(App, fpsSum / n, frames.Length >= 3 && p99 > 0 ? 1000 / p99 : null, ftSum / n, null);
    }

    public void Dispose() => _file.Dispose();
}
