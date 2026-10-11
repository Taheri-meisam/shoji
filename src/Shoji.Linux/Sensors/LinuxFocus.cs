using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// The app in focus on Linux (what scenes key off), the twin of the Windows ForegroundReader.
/// <para>X11 and XWayland (most games: Steam, Proton, Wine): one long-running "xprop -root -spy _NET_ACTIVE_WINDOW"
/// reports every focus change, and for the focused window a second "xprop -id W -spy" reports its pid, WM_CLASS and
/// full-screen state, also when they change later. Event-driven: nothing is polled.</para>
/// <para>Native Wayland windows: X only sees that no X window has focus (on GNOME the active window becomes Mutter's
/// empty "no focus" window), and that is reported as unknown, never guessed. On GNOME the "Window Calls" extension fills
/// the gap: its List call over D-Bus, at most every 2 s, only while a native Wayland window has focus and someone is at
/// the PC. Whether it's installed is checked once and again every 5 minutes.</para>
/// Everything runs only while something reads <see cref="Current"/> (the collector, when a scene has app or full-screen
/// conditions) and stops 30 s after the last read. Window titles are never read.
/// </summary>
public sealed class LinuxFocus
{
    public static LinuxFocus Shared { get; } = new();

    static readonly TimeSpan StopAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ToolRecheck = TimeSpan.FromMinutes(5);

    readonly object _gate = new();
    readonly int _ownPid = Environment.ProcessId;
    readonly Dictionary<int, (string Name, string? Path)> _procs = new();
    readonly List<string> _recent = new();
    DateTime _readAt = DateTime.MinValue;
    bool _running;

    // X11 / XWayland
    Process? _rootSpy, _windowSpy;
    DateTime _rootRetryAt = DateTime.MinValue, _rootStartedAt, _xpropAt = DateTime.MinValue;
    TimeSpan _rootBackoff = TimeSpan.FromSeconds(2);
    bool _hasXprop;
    string _active = "";       // focused X window id ("0x2a00007"), "" when none
    int _generation;           // bumped on every focus change, so a late spy can't report an old window
    int _xPid;
    string? _xClass;
    bool _xFullscreen, _xIsApp;

    // GNOME "Window Calls" extension (native Wayland windows)
    bool? _windowCalls;
    DateTime _windowCallsAt = DateTime.MinValue, _polledAt = DateTime.MinValue;
    bool _polling;
    ForegroundInfo _wayland = ForegroundInfo.None;
    string _waylandWindow = "";   // "id:widthxheight" of the focused native window: when it changes, Details says if it's full screen
    bool _waylandFullscreen;

    LinuxFocus()
    {
        // The spies wait for X events, so they'd outlive us until the next focus change: stop them on the way out.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Process?[] spies;
            lock (_gate)
                spies = new[] { _rootSpy, _windowSpy };
            foreach (var p in spies)
                LinuxCli.Kill(p);
        };
    }

    /// <summary>The app in focus, or <see cref="ForegroundInfo.None"/> when unknown. Never blocks; starts tracking on first read.</summary>
    public ForegroundInfo Current
    {
        get
        {
            lock (_gate)
            {
                _readAt = DateTime.UtcNow;
                if (!_running)
                {
                    _running = true;
                    _ = Task.Run(RunAsync);
                }
                return Combined();
            }
        }
    }

    /// <summary>Apps that had focus lately (process names, newest first), for the hint in Settings → Scenes.</summary>
    public IReadOnlyList<string> RecentApps { get { lock (_gate) return _recent.ToList(); } }

    /// <summary>An X display to ask (X11, or XWayland on a Wayland desktop).</summary>
    public static bool HasX => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static bool IsWayland => string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static bool IsGnome => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the Window Calls extension answered (null: not asked yet). Asking starts a check in the background.</summary>
    public bool? WindowCalls
    {
        get
        {
            bool due;
            lock (_gate)
            {
                due = !_polling && IsGnome && DateTime.UtcNow - _windowCallsAt > ToolRecheck;
                if (due)
                    _polling = true;
            }
            if (due)
                _ = Task.Run(PollWindowCallsAsync);
            lock (_gate)
                return _windowCalls;
        }
    }

    ForegroundInfo Combined()
    {
        if (_xIsApp)
            return Describe(_xPid, _xClass, _xFullscreen);
        return _windowCalls == true ? _wayland : ForegroundInfo.None;
    }

    // ---------------------------------------------------------------- the loop

    /// <summary>Keeps the X spy alive (restarting it with a growing pause if it dies), asks Window Calls when needed, and stops when nobody reads.</summary>
    async Task RunAsync()
    {
        try
        {
            while (true)
            {
                bool needWayland, pollNow;
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    if (now - _readAt > StopAfter)
                    {
                        _running = false;
                        StopX();
                        _wayland = ForegroundInfo.None;
                        return;
                    }
                    if (HasX && _rootSpy == null && now >= _rootRetryAt)
                        StartRootSpy();
                    needWayland = !_xIsApp && IsGnome && IsWayland;
                    // While away from the keyboard focus rarely changes (a game played with a gamepad keeps its old answer).
                    bool due = _windowCalls == true
                        ? now - _polledAt >= TimeSpan.FromSeconds(2) && IdleTracker.Seconds < 120
                        : now - _windowCallsAt > ToolRecheck;
                    pollNow = needWayland && due && !_polling;
                    if (pollNow)
                        _polling = true;
                }
                if (pollNow)
                    await PollWindowCallsAsync().ConfigureAwait(false);
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Focus tracking stopped: {ex.Message}");
            lock (_gate)
            {
                _running = false;
                StopX();
            }
        }
    }

    // ---------------------------------------------------------------- X11 / XWayland

    void StartRootSpy()
    {
        if (DateTime.UtcNow - _xpropAt > ToolRecheck)
        {
            bool had = _hasXprop || _xpropAt == DateTime.MinValue;
            _hasXprop = LinuxSetup.Which("xprop");
            _xpropAt = DateTime.UtcNow;
            if (!_hasXprop && had)
                Log.Write("xprop not found: scenes can't see which app is in focus (sudo apt install x11-utils)");
        }
        if (!_hasXprop)
        {
            _rootRetryAt = _xpropAt + ToolRecheck;
            return;
        }
        var p = Spawn("-root", "-spy", "_NET_ACTIVE_WINDOW");
        if (p == null)
        {
            RootDied(null);
            return;
        }
        _rootSpy = p;
        _rootStartedAt = DateTime.UtcNow;
        _ = Task.Run(() => ReadLinesAsync(p, OnActiveLine, () => RootDied(p)));
    }

    void RootDied(Process? p)
    {
        lock (_gate)
        {
            if (p != null && _rootSpy != p)
                return; // stopped on purpose
            _rootSpy = null;
            // A spy that ran for a while just lost its X server (logout, XWayland restart): try again soon.
            if (DateTime.UtcNow - _rootStartedAt > TimeSpan.FromMinutes(1))
                _rootBackoff = TimeSpan.FromSeconds(2);
            _rootRetryAt = DateTime.UtcNow + _rootBackoff;
            _rootBackoff = TimeSpan.FromSeconds(Math.Min(300, _rootBackoff.TotalSeconds * 2));
            SetActive("");
        }
        LinuxCli.Kill(p);
        p?.Dispose();
    }

    // "_NET_ACTIVE_WINDOW(WINDOW): window id # 0x2a00007" (0x0 when no X window has focus)
    static readonly Regex WindowId = new(@"#\s*(0x[0-9a-fA-F]+)", RegexOptions.Compiled);

    void OnActiveLine(string line)
    {
        var m = WindowId.Match(line);
        string id = m.Success && Convert.ToInt64(m.Groups[1].Value, 16) != 0 ? m.Groups[1].Value : "";
        lock (_gate)
            SetActive(id);
    }

    /// <summary>A new window has focus: forget the old one's details and, if it stays focused for a moment (alt-tab passes through windows), watch it.</summary>
    void SetActive(string id)
    {
        if (id == _active)
            return;
        _active = id;
        int generation = ++_generation;
        var old = _windowSpy;
        _windowSpy = null;
        (_xPid, _xClass, _xFullscreen, _xIsApp) = (0, null, false, false);
        if (old != null)
            _ = Task.Run(() => { LinuxCli.Kill(old); old.Dispose(); });
        if (id.Length == 0)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || !_running)
                    return;
                var p = Spawn("-id", id, "-notype", "-spy", "_NET_WM_PID", "WM_CLASS", "_NET_WM_STATE");
                if (p == null)
                    return;
                _windowSpy = p;
                _ = Task.Run(() => ReadLinesAsync(p, l => OnWindowLine(generation, l), () => { lock (_gate) if (_windowSpy == p) _windowSpy = null; p.Dispose(); }));
            }
        });
    }

    // "_NET_WM_PID = 4242", "WM_CLASS = "steam_app_1091500", "steam_app_1091500"", "_NET_WM_STATE = _NET_WM_STATE_FULLSCREEN, …",
    // or "_NET_WM_PID:  not found." Mutter's "no focus" window has none of them.
    void OnWindowLine(int generation, string line)
    {
        lock (_gate)
        {
            if (generation != _generation)
                return;
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            string name = eq > 0 ? line[..eq].Trim() : line.Split(':')[0].Trim();
            string? value = eq > 0 ? line[(eq + 3)..].Trim() : null;
            switch (name)
            {
                case "_NET_WM_PID":
                    _xPid = value != null && int.TryParse(value, out int pid) ? pid : 0;
                    break;
                case "WM_CLASS":
                    // The second string is the class ("Steam"), the first the instance ("steamwebhelper").
                    var parts = value == null ? Array.Empty<string>() : Regex.Matches(value, "\"([^\"]*)\"").Select(x => x.Groups[1].Value).ToArray();
                    _xClass = parts.LastOrDefault(x => x.Length > 0);
                    break;
                case "_NET_WM_STATE":
                    _xFullscreen = value?.Contains("_NET_WM_STATE_FULLSCREEN", StringComparison.Ordinal) == true;
                    break;
                default:
                    return;
            }
            _xIsApp = _xPid > 0 || _xClass != null;
            if (_xIsApp)
                Remember(Describe(_xPid, _xClass, _xFullscreen).ProcessName);
        }
    }

    void StopX()
    {
        var root = _rootSpy;
        _rootSpy = null;
        SetActive("");
        if (root != null)
            _ = Task.Run(() => { LinuxCli.Kill(root); root.Dispose(); });
    }

    static Process? Spawn(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("xprop")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C";
            var p = Process.Start(psi);
            if (p != null)
            {
                p.ErrorDataReceived += (_, _) => { }; // "BadWindow" when the window closes: drained and ignored
                p.BeginErrorReadLine();
            }
            return p;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static async Task ReadLinesAsync(Process p, Action<string> onLine, Action onExit)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                onLine(line);
        }
        catch (Exception)
        {
            // killed or broken pipe
        }
        onExit();
    }

    // ---------------------------------------------------------------- GNOME Window Calls

    async Task PollWindowCallsAsync()
    {
        try
        {
            var (code, output) = await LinuxCli.RunAsync("busctl", new[]
            {
                "--user", "--json=short", "call", "org.gnome.Shell", "/org/gnome/Shell/Extensions/Windows",
                "org.gnome.Shell.Extensions.Windows", "List",
            }, 1500).ConfigureAwait(false);
            ForegroundInfo found = ForegroundInfo.None;
            string window = "";
            long windowId = 0;
            bool ok = code == 0 && TryParseWindowList(output, out found, out windowId, out window);
            // List doesn't say whether a window is full screen; Details does. Ask it only when the focused window or its size changes.
            bool fullscreen;
            lock (_gate)
                fullscreen = _waylandFullscreen;
            if (ok && windowId != 0 && window != _waylandWindow)
            {
                var (detailsCode, details) = await LinuxCli.RunAsync("busctl", new[]
                {
                    "--user", "--json=short", "call", "org.gnome.Shell", "/org/gnome/Shell/Extensions/Windows",
                    "org.gnome.Shell.Extensions.Windows", "Details", "u", windowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, 1500).ConfigureAwait(false);
                fullscreen = detailsCode == 0 && IsFullscreenDetails(details);
            }
            lock (_gate)
            {
                if (ok != (_windowCalls == true) && _windowCalls != null)
                    Log.Write(ok ? "GNOME Window Calls extension found: scenes see native Wayland apps" : "GNOME Window Calls extension gone");
                if (!ok && _windowCalls == null)
                    Log.Write("No GNOME Window Calls extension: scenes only see X11 / XWayland apps in focus");
                _windowCalls = ok;
                _windowCallsAt = _polledAt = DateTime.UtcNow;
                (_waylandWindow, _waylandFullscreen) = ok ? (window, fullscreen) : ("", false);
                _wayland = ok ? Describe(found.ProcessId, found.ProcessName.Length > 0 ? found.ProcessName : null, fullscreen) : ForegroundInfo.None;
                if (ok && !_xIsApp && _wayland.ProcessId != 0)
                    Remember(_wayland.ProcessName);
            }
        }
        finally
        {
            lock (_gate)
                _polling = false;
        }
    }

    /// <summary>
    /// busctl --json=short prints {"type":"s","data":["[{\"wm_class\":\"firefox\",\"pid\":4242,\"id\":1234,\"focus\":true,…},…]"]}.
    /// Returns false if it isn't the extension's answer; true with <see cref="ForegroundInfo.None"/> when no window has focus.
    /// ProcessName carries the window's class here (Describe turns the pid into the real process). Titles are ignored.
    /// </summary>
    internal static bool TryParseWindowList(string busctlJson, out ForegroundInfo focused, out long windowId, out string window)
    {
        focused = ForegroundInfo.None;
        windowId = 0;
        window = "";
        try
        {
            using var outer = JsonDocument.Parse(busctlJson);
            if (!outer.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 ||
                data[0].ValueKind != JsonValueKind.String)
                return false;
            using var list = JsonDocument.Parse(data[0].GetString() ?? "[]");
            if (list.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var w in list.RootElement.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object || !(w.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.True))
                    continue;
                int pid = w.TryGetProperty("pid", out var p) && p.TryGetInt32(out int n) ? n : 0;
                string cls = w.TryGetProperty("wm_class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                windowId = w.TryGetProperty("id", out var i) && i.TryGetInt64(out long id) ? id : 0;
                window = $"{windowId}:{(w.TryGetProperty("width", out var wd) ? wd.ToString() : "")}x{(w.TryGetProperty("height", out var ht) ? ht.ToString() : "")}";
                focused = new ForegroundInfo(pid, cls, null, "", false);
                break;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Details(id) answers like List, with one object that has "fullscreen".</summary>
    internal static bool IsFullscreenDetails(string busctlJson)
    {
        try
        {
            using var outer = JsonDocument.Parse(busctlJson);
            if (!outer.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 ||
                data[0].ValueKind != JsonValueKind.String)
                return false;
            using var details = JsonDocument.Parse(data[0].GetString() ?? "{}");
            return details.RootElement.ValueKind == JsonValueKind.Object &&
                   details.RootElement.TryGetProperty("fullscreen", out var f) && f.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- processes

    /// <summary>
    /// What scenes match against: the program's file name ("firefox") and path. Wine and Proton games run as
    /// wine64-preloader, so for them it's the Windows program from the command line ("Game", "Z:\…\steamapps\common\…\Game.exe").
    /// Without a usable pid the window's class stands in for the name (and pid -1 keeps it matchable).
    /// </summary>
    ForegroundInfo Describe(int pid, string? windowClass, bool fullscreen)
    {
        if (pid == _ownPid)
            return ForegroundInfo.None;
        if (pid > 0)
        {
            if (!_procs.TryGetValue(pid, out var proc))
            {
                proc = ReadProcess(pid);
                if (_procs.Count > 300)
                    _procs.Clear();
                _procs[pid] = proc;
            }
            if (proc.Name.Length > 0)
                return new ForegroundInfo(pid, proc.Name, proc.Path, "", fullscreen);
        }
        return string.IsNullOrEmpty(windowClass) ? ForegroundInfo.None : new ForegroundInfo(pid > 0 ? pid : -1, windowClass, null, "", fullscreen);
    }

    static (string Name, string? Path) ReadProcess(int pid)
    {
        string? exe = null;
        try { exe = new FileInfo($"/proc/{pid}/exe").LinkTarget; }
        catch { /* another user's process, or gone */ }
        if (exe != null && exe.EndsWith(" (deleted)", StringComparison.Ordinal))
            exe = exe[..^10];
        string name = exe != null ? Path.GetFileName(exe) : LinuxCli.ReadLine($"/proc/{pid}/comm") ?? "";
        if (name.Contains("wine", StringComparison.OrdinalIgnoreCase) || name.Contains("preloader", StringComparison.OrdinalIgnoreCase))
        {
            string argv0 = (LinuxCli.ReadText($"/proc/{pid}/cmdline") ?? "").Split('\0')[0];
            if (argv0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string file = argv0[(argv0.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
                return (file[..^4], argv0);
            }
        }
        return (name, exe);
    }

    void Remember(string name)
    {
        if (name.Length == 0 || _recent.Count > 0 && _recent[0] == name)
            return;
        _recent.Remove(name);
        _recent.Insert(0, name);
        if (_recent.Count > 8)
            _recent.RemoveAt(_recent.Count - 1);
    }
}
