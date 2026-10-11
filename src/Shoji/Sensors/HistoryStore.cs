using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Every graph's data in one place, shared by all tiles and the web dashboard:
/// the last few minutes at full rate (for the small graphs) and one averaged point per minute for 24 hours
/// (for the History tile), saved to history.json so it survives restarts.
/// Because it lives outside the tiles, graphs keep their history when the theme or page changes.
/// </summary>
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    const int RecentCapacity = 120;
    const int MinuteCapacity = 24 * 60;
    static string FilePath => Path.Combine(AppConfig.Folder, "history.json");

    readonly object _gate = new();
    readonly Dictionary<string, Queue<double>> _recent = new();
    readonly Dictionary<string, List<MinutePoint>> _minutes = new();
    readonly Dictionary<string, (long Minute, double Sum, int Count)> _bucket = new();
    DateTime _lastSave = DateTime.UtcNow;

    public sealed record MinutePoint(long Minute, double Value);

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["cpu.load"] = "CPU load %", ["cpu.temp"] = "CPU °", ["cpu.power"] = "CPU W",
        ["gpu.load"] = "GPU load %", ["gpu.temp"] = "GPU °", ["gpu.power"] = "GPU W",
        ["mem.load"] = "RAM %", ["net.down"] = "Download", ["net.up"] = "Upload",
        ["ping"] = "Ping ms", ["fps"] = "FPS", ["power.total"] = "System W",
    };

    HistoryStore() => Load();

    /// <summary>Adds a sample (ignored when null).</summary>
    public void Add(string series, double? value)
    {
        if (value is not double v || double.IsNaN(v))
            return;
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            if (!_recent.TryGetValue(series, out var q))
                _recent[series] = q = new Queue<double>();
            q.Enqueue(v);
            while (q.Count > RecentCapacity)
                q.Dequeue();

            _bucket.TryGetValue(series, out var b);
            if (b.Count > 0 && b.Minute != minute)
            {
                if (!_minutes.TryGetValue(series, out var list))
                    _minutes[series] = list = new List<MinutePoint>();
                list.Add(new MinutePoint(b.Minute, b.Sum / b.Count));
                if (list.Count > MinuteCapacity)
                    list.RemoveRange(0, list.Count - MinuteCapacity);
                b = default;
            }
            _bucket[series] = (minute, b.Sum + v, b.Count + 1);
        }
    }

    public double[] Recent(string series, int count)
    {
        lock (_gate)
            return _recent.TryGetValue(series, out var q) ? q.Skip(Math.Max(0, q.Count - count)).ToArray() : Array.Empty<double>();
    }

    /// <summary>Per-minute averages from the last <paramref name="hours"/> hours.</summary>
    public MinutePoint[] Minutes(string series, double hours)
    {
        long from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 - (long)(hours * 60);
        lock (_gate)
            return _minutes.TryGetValue(series, out var list) ? list.Where(p => p.Minute >= from).ToArray() : Array.Empty<MinutePoint>();
    }

    /// <summary>Demo mode only (Linux Platform/DemoData.cs): replaces a series' per-minute history, the last value being a minute ago.</summary>
    public void Seed(string series, IReadOnlyList<double> perMinute)
    {
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        lock (_gate)
            _minutes[series] = perMinute.Select((v, i) => new MinutePoint(minute - perMinute.Count + i, v)).ToList();
    }

    public void SaveIfDue()
    {
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5))
            Save();
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Dictionary<string, long[][]> data;
            lock (_gate)
                data = _minutes.ToDictionary(kv => kv.Key, kv => kv.Value.Select(p => new[] { p.Minute, (long)Math.Round(p.Value * 10) }).ToArray());
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            Log.Write($"History could not be saved: {ex.Message}");
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var data = JsonSerializer.Deserialize<Dictionary<string, long[][]>>(File.ReadAllText(FilePath));
            long from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 - MinuteCapacity;
            foreach (var (series, points) in data ?? new())
                _minutes[series] = points.Where(p => p.Length == 2 && p[0] >= from).Select(p => new MinutePoint(p[0], p[1] / 10.0)).ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"History could not be read: {ex.Message}");
        }
    }
}

/// <summary>
/// Saves by writing a temporary file and swapping it in (like <see cref="AppConfig.Save"/>), so a crash or power cut
/// mid-save can't leave half a file. One save at a time, so two collectors can't trip over the same temporary file.
/// </summary>
internal static class AtomicFile
{
    static readonly object Gate = new();

    public static void WriteAllText(string path, string text)
    {
        lock (Gate)
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
    }
}
