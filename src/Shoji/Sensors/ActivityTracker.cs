using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Tracks which application has focus and adds up per-app usage for today.
/// Time while the user is idle (no input for <see cref="IdleThreshold"/>) is not counted.
/// Totals are saved to %APPDATA%\Shoji\activity.json so a restart doesn't lose them.
/// </summary>
public sealed class ActivityTracker
{
    static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);
    static string FilePath => Path.Combine(AppConfig.Folder, "activity.json");

    readonly Dictionary<uint, string> _appNames = new();
    Dictionary<string, double> _todaySeconds = new(StringComparer.OrdinalIgnoreCase);
    DateTime _day = DateTime.Today;
    DateTime _lastSample = DateTime.UtcNow;
    DateTime _lastSave = DateTime.UtcNow;

    IntPtr _focusedWindow;
    DateTime _focusedSince = DateTime.UtcNow;

    public ActivityTracker() => Load();

    public ActivityInfo Sample()
    {
        var now = DateTime.UtcNow;
        double delta = Math.Min((now - _lastSample).TotalSeconds, 10); // clamp across sleep/hibernate
        _lastSample = now;

        if (DateTime.Today != _day)
        {
            _day = DateTime.Today;
            _todaySeconds.Clear();
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string app = AppName(pid);
        string title = WindowTitle(hwnd);

        if (hwnd != _focusedWindow)
        {
            _focusedWindow = hwnd;
            _focusedSince = now;
        }

        var idle = TimeSpan.FromSeconds(NativeMethods.IdleSeconds());
        if (idle < IdleThreshold && pid != 0 && delta > 0)
        {
            _todaySeconds.TryGetValue(app, out var s);
            _todaySeconds[app] = s + delta;
        }

        if (now - _lastSave > TimeSpan.FromMinutes(1))
            Save();

        var top = _todaySeconds
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .Select(kv => new AppUsage(kv.Key, TimeSpan.FromSeconds(kv.Value)))
            .ToList();

        return new ActivityInfo(
            ProcessName: pid == 0 ? "Desktop" : app,
            WindowTitle: title,
            FocusedFor: now - _focusedSince,
            IdleFor: idle,
            TodayTopApps: top,
            TodayActive: TimeSpan.FromSeconds(_todaySeconds.Values.Sum()));
    }

    /// <summary>Friendly app name ("Google Chrome" rather than "chrome"), cached per process id.</summary>
    string AppName(uint pid)
    {
        if (pid == 0)
            return "Desktop";
        if (_appNames.TryGetValue(pid, out var cached))
            return cached;

        string name = "Unknown";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40)
                    name = desc.Trim();
            }
            catch
            {
                // Elevated processes refuse MainModule when we aren't elevated – keep the exe name.
            }
        }
        catch
        {
            // Process already exited.
        }

        if (_appNames.Count > 500)
            _appNames.Clear();
        return _appNames[pid] = name;
    }

    static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    sealed class Saved
    {
        public DateTime Day { get; set; }
        public Dictionary<string, double> Seconds { get; set; } = new();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved != null && saved.Day == DateTime.Today)
                _todaySeconds = new Dictionary<string, double>(saved.Seconds, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Write($"Activity history could not be read: {ex.Message}");
        }
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Day = _day, Seconds = _todaySeconds }));
        }
        catch (Exception ex)
        {
            Log.Write($"Activity history could not be saved: {ex.Message}");
        }
    }
}
