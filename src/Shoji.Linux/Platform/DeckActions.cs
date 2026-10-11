using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>What deck buttons can ask of the dashboard (page changes, a short message, the media player).</summary>
public interface IDashboardHost
{
    /// <summary>"next", "prev", a page name or a page number (1-based).</summary>
    void ShowPage(string target);
    void Toast(string text);
    LinuxMedia? Media { get; }

    string PageName { get; }
    string ThemeName { get; }
    bool ScenesEnabled { get; }
}

/// <summary>
/// Runs control-deck buttons on Linux. Same actions and config as on Windows; the Windows-only ones map to their
/// Linux counterparts (power plans → power profiles, PowerShell → shell, hotkeys → xdotool / wtype / ydotool).
/// </summary>
public static class DeckActions
{
    public static readonly IReadOnlyList<(string Id, string Name, string TargetHint)> All = new[]
    {
        ("launch", "Open a program", "program, e.g. firefox, code, /opt/app/run.sh (Args for arguments)"),
        ("url", "Open a website / folder", "https://… , steam://rungameid/570, ~/Downloads"),
        ("keys", "Press keys (hotkey)", "Ctrl+Shift+M, Super+D, F13, MediaPlayPause… (needs xdotool on X11; ydotool or wtype on Wayland)"),
        ("media", "Media control", "playpause, next, previous"),
        ("mic", "Microphone mute on/off", "(nothing)"),
        ("mute", "Speaker mute on/off", "(nothing)"),
        ("volume", "Volume", "+5, -5, or 40 for 40%"),
        ("output", "Switch audio output", "empty = next device; or names, e.g. Headphones|Speakers"),
        ("powerplan", "Power profile", "balanced, performance or power-saver"),
        ("screenshot", "Screenshot", "empty = whole screen to Pictures; snip = choose an area"),
        ("command", "Run a command", "shell command, e.g. notify-send Hello"),
        ("powershell", "Run a script", "script path or shell command"),
        ("page", "Go to page", "next, prev, or a page name / number"),
        ("theme", "Change theme", "next, or a theme id (tokyo, sumi, cyberpunk…)"),
        ("scenes", "Auto scenes on/off", "(nothing)"),
        ("obs", "OBS Studio", "record, pause, stream, replay (save), replaybuffer, virtualcam, scene:Gaming, mute:Mic/Aux"),
        ("lock", "Lock screen", "(nothing)"),
        ("sleep", "Sleep PC", "(nothing)"),
    };

    /// <summary>Icon names usable on buttons (shown with Material Icons).</summary>
    public static IReadOnlyDictionary<string, string> Icons => UI.Icon.Names;

    public static bool IsIconName(string? icon) => icon != null && Icons.ContainsKey(icon.Trim());

    public static void Run(DeckButton b, IDashboardHost host)
    {
        string target = (b.Target ?? "").Trim();
        try
        {
            switch ((b.Action ?? "").ToLowerInvariant())
            {
                case "launch":
                    Start(Expand(target), SplitArgs(b.Args ?? "").ToArray());
                    break;
                case "url":
                    Start("xdg-open", Expand(target));
                    break;
                case "command":
                case "powershell":
                    // A script path is quoted for the shell (it may contain spaces); anything else is a shell command line.
                    Start("sh", "-c", File.Exists(Expand(target)) ? Quote(Expand(target)) : target);
                    break;
                case "keys":
                    // The tools are waited for (up to a second each), so off the UI thread; a failure shows a hint.
                    Task.Run(async () =>
                    {
                        if (!await KeySender.SendAsync(target).ConfigureAwait(false))
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => host.Toast(KeySender.Hint));
                    });
                    break;
                case "media":
                    var media = host.Media;
                    _ = target.ToLowerInvariant() switch
                    {
                        "next" => media?.NextAsync(),
                        "previous" or "prev" => media?.PreviousAsync(),
                        _ => media?.PlayPauseAsync(),
                    };
                    break;
                case "mic":
                    LinuxAudio.Shared.ToggleMic();
                    host.Toast(L.T(LinuxAudio.Shared.MicMuted == true ? "Microphone muted" : "Microphone on"));
                    break;
                case "mute":
                    LinuxAudio.Shared.ToggleMute();
                    break;
                case "volume":
                    ChangeVolume(target);
                    host.Toast(L.F("Volume {0:0}%", LinuxAudio.Shared.Volume * 100));
                    break;
                case "output":
                    var name = LinuxAudio.Shared.NextOutput(target.Length == 0 ? null : target);
                    host.Toast(name == null ? L.T("No other output device found") : L.F("Output: {0}", name));
                    break;
                case "powerplan":
                    string profile = target.ToLowerInvariant() switch
                    {
                        "high" or "performance" => "performance",
                        "saver" or "power-saver" or "powersaver" => "power-saver",
                        _ => "balanced",
                    };
                    Start("powerprofilesctl", "set", profile);
                    host.Toast(L.F("Power profile: {0}", L.T(profile)));
                    break;
                case "screenshot":
                    bool area = target.Equals("snip", StringComparison.OrdinalIgnoreCase);
                    if (!TryStart("gnome-screenshot", area ? new[] { "-a" } : Array.Empty<string>()) && !TryStart("spectacle", area ? new[] { "-r" } : new[] { "-f", "-b" }))
                        host.Toast(L.T("Screenshots need gnome-screenshot or spectacle (or press Print Screen)"));
                    break;
                case "page":
                    host.ShowPage(target.Length == 0 ? "next" : target);
                    break;
                case "theme":
                    AppConfig.Edit(c =>
                    {
                        if (target.Length == 0 || target.Equals("next", StringComparison.OrdinalIgnoreCase))
                        {
                            var all = UI.Themes.All;
                            int i = all.ToList().FindIndex(t => t.Id == UI.Theme.Find(c.Theme).Id);
                            c.Theme = all[(i + 1) % all.Count].Id;
                        }
                        else
                        {
                            c.Theme = target;
                        }
                    });
                    break;
                case "scenes":
                    bool on = false;
                    AppConfig.Edit(c => on = c.ScenesEnabled = !c.ScenesEnabled);
                    host.Toast(L.T(on ? "Automatic scenes on" : "Automatic scenes off"));
                    break;
                case "obs":
                    RunObs(target, host);
                    break;
                case "lock":
                    if (!TryStart("loginctl", "lock-session"))
                        Start("xdg-screensaver", "lock");
                    break;
                case "sleep":
                    Start("systemctl", "suspend");
                    break;
                default:
                    host.Toast(L.F("Unknown action \"{0}\"", b.Action));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Deck button \"{b.Label}\" ({b.Action} {target}) failed: {ex.Message}");
            host.Toast($"{b.Label}: {ex.Message}");
        }
    }

    /// <summary>OBS answers over the network: the toast (an error, "Replay saved") is posted back to the UI thread.</summary>
    static void RunObs(string target, IDashboardHost host) => Task.Run(async () =>
    {
        if (await ObsClient.Shared.RunAsync(target).ConfigureAwait(false) is { } message)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => host.Toast(message));
    });

    /// <summary>"+5" / "-5" change the volume by that many percent, "40" sets it.</summary>
    static void ChangeVolume(string target)
    {
        var audio = LinuxAudio.Shared;
        if (target.Length == 0)
            target = "+5";
        if (!double.TryParse(target.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            return;
        if (target.StartsWith('+') || target.StartsWith('-'))
            audio.ChangeVolume(n / 100);
        else
            audio.SetVolume(n / 100);
    }

    /// <summary>Windows programs and folders a config from Windows may name, and their usual Linux counterparts.</summary>
    static readonly Dictionary<string, string> FromWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["taskmgr.exe"] = "gnome-system-monitor", ["taskmgr"] = "gnome-system-monitor", ["calc.exe"] = "gnome-calculator", ["calc"] = "gnome-calculator",
        ["notepad.exe"] = "gnome-text-editor", ["notepad"] = "gnome-text-editor", ["wt.exe"] = "gnome-terminal", ["cmd.exe"] = "gnome-terminal",
        ["explorer.exe"] = "nautilus", ["shell:Downloads"] = "~/Downloads", ["shell:Pictures"] = "~/Pictures", ["shell:Documents"] = "~/Documents",
    };

    /// <summary>~ and $HOME in paths, and Windows names mapped to Linux ones.</summary>
    static string Expand(string path)
    {
        if (FromWindows.TryGetValue(path.Trim(), out var linux))
            path = linux;
        return path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>POSIX single-quoting, for text that a shell ("sh -c") parses.</summary>
    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Splits a program's Args the way a shell would for the simple cases: spaces separate, '…' and "…" group
    /// (no expansion inside), a backslash escapes the next character outside single quotes.
    /// </summary>
    internal static List<string> SplitArgs(string args)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool any = false;
        char quote = '\0';
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                else cur.Append(c);
            }
            else if (c == '\\' && i + 1 < args.Length && (quote == '\0' || args[i + 1] is '"' or '\\' or '$' or '`'))
            {
                cur.Append(args[++i]);
                any = true;
            }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else cur.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                any = true; // '' is an empty argument
            }
            else if (char.IsWhiteSpace(c))
            {
                if (any || cur.Length > 0)
                    list.Add(cur.ToString());
                cur.Clear();
                any = false;
            }
            else
            {
                cur.Append(c);
            }
        }
        if (any || cur.Length > 0)
            list.Add(cur.ToString());
        return list;
    }

    // Arguments go through ArgumentList: .NET splits a single Arguments string by Windows rules, which would hand
    // POSIX quotes to the program literally.
    // Not redirected: nobody reads those pipes, so a launched app (or the browser xdg-open starts) that prints a lot
    // would block on a full pipe, and once the pipe is collected it would die of SIGPIPE on its next message.
    static void Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    static bool TryStart(string file, params string[] args)
    {
        try
        {
            Start(file, args);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // not installed
        }
    }
}

/// <summary>
/// Sends a hotkey to the focused app with whichever tool works in this session. X11: xdotool. Wayland: wtype (wlroots
/// compositors, e.g. Sway), then ydotool (any compositor, GNOME included, but needs the ydotoold service), then xdotool,
/// which there only reaches apps running under XWayland.
/// </summary>
public static class KeySender
{
    static readonly Dictionary<string, string> XNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "ctrl", ["control"] = "ctrl", ["shift"] = "shift", ["alt"] = "alt", ["win"] = "super", ["super"] = "super", ["meta"] = "super",
        ["enter"] = "Return", ["return"] = "Return", ["esc"] = "Escape", ["escape"] = "Escape", ["space"] = "space", ["tab"] = "Tab", ["backspace"] = "BackSpace",
        ["delete"] = "Delete", ["del"] = "Delete", ["insert"] = "Insert", ["ins"] = "Insert", ["home"] = "Home", ["end"] = "End", ["pageup"] = "Prior", ["pagedown"] = "Next",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right", ["printscreen"] = "Print", ["print"] = "Print",
        ["mediaplaypause"] = "XF86AudioPlay", ["medianexttrack"] = "XF86AudioNext", ["mediaprevioustrack"] = "XF86AudioPrev",
        ["mediastop"] = "XF86AudioStop", ["volumemute"] = "XF86AudioMute", ["volumeup"] = "XF86AudioRaiseVolume", ["volumedown"] = "XF86AudioLowerVolume",
    };

    /// <summary>Linux input event codes (linux/input-event-codes.h) for ydotool, keyed by the X names above (or the key itself).</summary>
    static readonly Dictionary<string, int> Evdev = BuildEvdev();

    static Dictionary<string, int> BuildEvdev()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 29, ["shift"] = 42, ["alt"] = 56, ["super"] = 125, ["altgr"] = 100,
            ["Return"] = 28, ["Escape"] = 1, ["space"] = 57, ["Tab"] = 15, ["BackSpace"] = 14, ["Delete"] = 111, ["Insert"] = 110,
            ["Home"] = 102, ["End"] = 107, ["Prior"] = 104, ["Next"] = 109, ["Up"] = 103, ["Down"] = 108, ["Left"] = 105, ["Right"] = 106,
            ["Print"] = 99, ["capslock"] = 58,
            ["XF86AudioPlay"] = 164, ["XF86AudioNext"] = 163, ["XF86AudioPrev"] = 165, ["XF86AudioStop"] = 166,
            ["XF86AudioMute"] = 113, ["XF86AudioLowerVolume"] = 114, ["XF86AudioRaiseVolume"] = 115,
            ["-"] = 12, ["minus"] = 12, ["="] = 13, ["equal"] = 13, ["["] = 26, ["]"] = 27, [";"] = 39, ["'"] = 40, ["`"] = 41,
            ["\\"] = 43, [","] = 51, ["comma"] = 51, ["."] = 52, ["period"] = 52, ["/"] = 53, ["slash"] = 53,
        };
        const string rows = "qwertyuiop|asdfghjkl|zxcvbnm";
        int[] starts = { 16, 30, 44 };
        var parts = rows.Split('|');
        for (int r = 0; r < parts.Length; r++)
            for (int i = 0; i < parts[r].Length; i++)
                d[parts[r][i].ToString()] = starts[r] + i;
        for (int i = 1; i <= 9; i++)
            d[i.ToString(CultureInfo.InvariantCulture)] = 1 + i; // 1 = 2 … 9 = 10
        d["0"] = 11;
        for (int i = 1; i <= 24; i++)
            d["F" + i.ToString(CultureInfo.InvariantCulture)] = i <= 10 ? 58 + i : i <= 12 ? 76 + i : 170 + i; // F1 59, F11 87, F13 183
        return d;
    }

    static bool Wayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>What to tell the user when no tool could send the keys.</summary>
    public static string Hint => L.T(Wayland
        ? "Hotkeys on Wayland need ydotool with ydotoold running (GNOME, KDE; see Settings → Hardware sensors) or wtype (Sway, Hyprland)"
        : "Hotkeys need xdotool: sudo apt install xdotool");

    /// <summary>"Ctrl+Shift+M" → xdotool key ctrl+shift+m (or the wtype / ydotool equivalent). False when no tool managed it.</summary>
    public static async Task<bool> SendAsync(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => XNames.TryGetValue(k, out var x) ? x : k.Length == 1 ? k.ToLowerInvariant() : k).ToList();
        if (keys.Count == 0)
            return true;
        var xdotool = new[] { "key", string.Join("+", keys) };
        if (!Wayland)
            return await TryAsync("xdotool", xdotool).ConfigureAwait(false);

        // wtype: modifiers with -M / -m, the key itself with -k. GNOME has no virtual-keyboard protocol, so it fails there (exit 1).
        var mods = keys.Take(keys.Count - 1).Select(m => m == "super" ? "logo" : m).ToList();
        var wtype = mods.SelectMany(m => new[] { "-M", m }).Concat(new[] { "-k", keys[^1] }).Concat(mods.SelectMany(m => new[] { "-m", m })).ToArray();
        if (await TryAsync("wtype", wtype).ConfigureAwait(false))
            return true;
        // ydotool 1.x: "29:1 42:1 50:1 50:0 42:0 29:0" (press in order, release in reverse). Needs ydotoold; without it it exits non-zero.
        var codes = keys.Select(k => Evdev.TryGetValue(k, out int c) ? c : -1).ToList();
        if (!codes.Contains(-1))
        {
            var ydotool = new[] { "key" }.Concat(codes.Select(c => c + ":1")).Concat(Enumerable.Reverse(codes).Select(c => c + ":0")).ToArray();
            if (await TryAsync("ydotool", ydotool).ConfigureAwait(false))
                return true;
        }
        return await TryAsync("xdotool", xdotool).ConfigureAwait(false);
    }

    /// <summary>Runs a tool and waits up to a second: true if it exited with 0; false if it is missing, failed or hung (then it is killed).</summary>
    static async Task<bool> TryAsync(string file, string[] args)
    {
        var (code, _) = await LinuxCli.RunAsync(file, args, 1000).ConfigureAwait(false);
        return code == 0;
    }
}
