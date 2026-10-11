using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>What deck buttons (and scenes) can ask the dashboard to do.</summary>
public interface IDashboardHost
{
    /// <summary>"next", "prev", a page name or a page number (1-based).</summary>
    void ShowPage(string target);
    void Toast(string text);
    MediaService? Media { get; }

    string PageName { get; }
    string ThemeName { get; }
    bool ScenesEnabled { get; }
}

/// <summary>Runs control-deck buttons. Every action is something you configured yourself; nothing runs on its own.</summary>
public static class DeckActions
{
    /// <summary>Action id → (name in Settings, what Target means).</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string TargetHint)> All = new[]
    {
        ("launch", "Open a program", "program or file, e.g. notepad.exe, C:\\Games\\game.exe (Args for arguments)"),
        ("url", "Open a website / folder", "https://… , steam://rungameid/570, shell:Downloads, C:\\Folder"),
        ("keys", "Press keys (hotkey)", "Ctrl+Shift+M, Win+Shift+S, F13, MediaPlayPause… goes to the app in focus"),
        ("media", "Media control", "playpause, next, previous"),
        ("mic", "Microphone mute on/off", "(nothing)"),
        ("mute", "Speaker mute on/off", "(nothing)"),
        ("volume", "Volume", "+5, -5, or 40 for 40%"),
        ("output", "Switch audio output", "empty = next device; or names, e.g. Headphones|Speakers"),
        ("powerplan", "Power plan", "balanced, high, saver, or a plan name / GUID"),
        ("screenshot", "Screenshot", "empty = save main screen to Pictures\\Screenshots; snip = Snipping Tool"),
        ("command", "Run a command", "cmd command, runs hidden"),
        ("powershell", "Run PowerShell", "PowerShell command or script path, runs hidden"),
        ("page", "Go to page", "next, prev, or a page name / number"),
        ("theme", "Change theme", "next, or a theme id (tokyo, sumi, cyberpunk…)"),
        ("scenes", "Auto scenes on/off", "(nothing)"),
        ("obs", "OBS Studio", "record, pause, stream, replay (save), replaybuffer, virtualcam, scene:Gaming, mute:Mic/Aux"),
        ("lock", "Lock PC", "(nothing)"),
        ("sleep", "Sleep PC", "(nothing)"),
    };

    /// <summary>Icon names you can use instead of text/emoji (Segoe Fluent Icons glyphs).</summary>
    public static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mic"] = "\uE720", ["micoff"] = "\uEC54", ["mute"] = "\uE74F", ["volume"] = "\uE767", ["voldown"] = "\uE993",
        ["speaker"] = "\uE7F5", ["headphones"] = "\uE7F6", ["play"] = "\uE768", ["pause"] = "\uE769",
        ["next"] = "\uE893", ["prev"] = "\uE892", ["camera"] = "\uE722", ["lock"] = "\uE72E", ["power"] = "\uE7E8",
        ["sleep"] = "\uE708", ["settings"] = "\uE713", ["web"] = "\uE774", ["folder"] = "\uE8B7", ["terminal"] = "\uE756",
        ["game"] = "\uE7FC", ["music"] = "\uEC4F", ["light"] = "\uEA80", ["home"] = "\uE80F", ["pages"] = "\uE8A9",
        ["theme"] = "\uE790", ["timer"] = "\uE916", ["chat"] = "\uE8BD", ["calc"] = "\uE8EF", ["chart"] = "\uE9D9",
        ["mail"] = "\uE715", ["calendar"] = "\uE787", ["clock"] = "\uE823", ["star"] = "\uE734", ["heart"] = "\uEB51",
        ["refresh"] = "\uE72C", ["download"] = "\uE896", ["code"] = "\uE943", ["video"] = "\uE714", ["record"] = "\uE7C8",
        ["scenes"] = "\uE945", ["brightness"] = "\uE706", ["bolt"] = "\uE945", ["keyboard"] = "\uE765", ["app"] = "\uE71D",
        ["live"] = "\uE93E",
    };

    public static bool IsIconName(string? icon) => icon != null && Icons.ContainsKey(icon.Trim());

    public static void Run(DeckButton b, IDashboardHost host)
    {
        string target = (b.Target ?? "").Trim();
        try
        {
            switch (b.Action.ToLowerInvariant())
            {
                case "launch":
                    Process.Start(new ProcessStartInfo(target) { Arguments = b.Args ?? "", UseShellExecute = true });
                    break;
                case "url":
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    break;
                case "command":
                    Hidden("cmd.exe", "/c " + target);
                    break;
                case "powershell":
                    Hidden("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                            File.Exists(target) ? $"& '{target.Replace("'", "''")}'" : target)));
                    break;
                case "keys":
                    if (!KeySender.Send(target))
                        host.Toast(L.T(KeySender.BlockedMessage));
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
                    AudioService.Shared.ToggleMic();
                    host.Toast(AudioService.Shared.MicMuted == true ? L.T("Microphone muted") : L.T("Microphone on"));
                    break;
                case "mute":
                    AudioService.Shared.ToggleMute();
                    break;
                case "volume":
                    AudioService.Shared.ChangeVolume(target);
                    host.Toast(L.F("Volume {0:0}%", AudioService.Shared.Volume * 100));
                    break;
                case "output":
                    var name = AudioService.Shared.SwitchOutput(target);
                    host.Toast(name == null ? L.T("No other output device found") : L.F("Output: {0}", name));
                    break;
                case "powerplan":
                    // powercfg runs up to three times: off the UI thread, the result is shown when it's done.
                    var ui = System.Windows.Application.Current.Dispatcher;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        string result;
                        try { result = SetPowerPlan(target); }
                        catch (Exception ex)
                        {
                            Log.Write($"Deck button \"{b.Label}\" (powerplan {target}) failed: {ex.Message}");
                            result = $"{b.Label}: {ex.Message}";
                        }
                        ui.BeginInvoke(() => host.Toast(result));
                    });
                    break;
                case "screenshot":
                    if (target.Equals("snip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!KeySender.Send("Win+Shift+S"))
                            host.Toast(L.T(KeySender.BlockedMessage));
                    }
                    else
                        host.Toast(L.F("Saved {0}", Path.GetFileName(Screenshot())));
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
                    host.Toast(on ? L.T("Automatic scenes on") : L.T("Automatic scenes off"));
                    break;
                case "obs":
                    RunObs(target, host);
                    break;
                case "lock":
                    NativeMethods.LockWorkStation();
                    break;
                case "sleep":
                    Application.SetSuspendState(PowerState.Suspend, false, false);
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

    /// <summary>OBS answers over the network: the toast (an error, "Replay saved") comes back on the UI thread.</summary>
    static async void RunObs(string target, IDashboardHost host)
    {
        try
        {
            if (await ObsClient.Shared.RunAsync(target) is { } message)
                host.Toast(message);
        }
        catch (Exception ex)
        {
            Log.Write($"OBS button ({target}) failed: {ex.Message}");
        }
    }

    static void Hidden(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();

    // ---------------------------------------------------------------- power plans

    static string SetPowerPlan(string target)
    {
        string? guid = target.ToLowerInvariant() switch
        {
            "balanced" => "SCHEME_BALANCED",
            "high" or "performance" or "high performance" => "SCHEME_MIN",
            "saver" or "power saver" => "SCHEME_MAX",
            _ => Regex.IsMatch(target, "^[0-9a-fA-F-]{36}$") ? target : null,
        };

        var plans = ListPowerPlans();
        if (guid == null)
        {
            // A plan by (part of) its name, e.g. "Ultimate".
            var match = plans.FirstOrDefault(p => p.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (match.Guid == null)
                return L.F("No power plan called \"{0}\"", target);
            guid = match.Guid;
        }

        using var p = Process.Start(new ProcessStartInfo("powercfg", "/setactive " + guid) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
        var active = ListPowerPlans().FirstOrDefault(x => x.Active);
        return L.F("Power plan: {0}", active.Name ?? target);
    }

    static List<(string Guid, string Name, bool Active)> ListPowerPlans()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/list")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            });
            if (p == null)
                return list;
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                // "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *" (wording is localised, the shape isn't)
                var m = Regex.Match(line, @"([0-9a-fA-F-]{36})\s+\((.+?)\)\s*(\*)?");
                if (m.Success)
                    list.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"powercfg failed: {ex.Message}");
        }
        return list;
    }

    // ---------------------------------------------------------------- screenshot

    /// <summary>Saves the main screen as a PNG in Pictures\Screenshots and returns the path.</summary>
    static string Screenshot()
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}

/// <summary>Types key combinations like "Ctrl+Shift+M" into whatever app has focus (our window never takes focus).</summary>
public static class KeySender
{
    static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Keys.ControlKey, ["control"] = Keys.ControlKey, ["shift"] = Keys.ShiftKey, ["alt"] = Keys.Menu,
        ["win"] = Keys.LWin, ["windows"] = Keys.LWin, ["esc"] = Keys.Escape, ["enter"] = Keys.Return,
        ["del"] = Keys.Delete, ["ins"] = Keys.Insert, ["pgup"] = Keys.PageUp, ["pgdn"] = Keys.PageDown,
        ["prtsc"] = Keys.PrintScreen, ["printscreen"] = Keys.PrintScreen, ["playpause"] = Keys.MediaPlayPause,
        ["mute"] = Keys.VolumeMute, ["volup"] = Keys.VolumeUp, ["voldown"] = Keys.VolumeDown,
        ["nexttrack"] = Keys.MediaNextTrack, ["prevtrack"] = Keys.MediaPreviousTrack,
        ["up"] = Keys.Up, ["down"] = Keys.Down, ["left"] = Keys.Left, ["right"] = Keys.Right,
        ["backspace"] = Keys.Back, ["`"] = Keys.Oemtilde, ["-"] = Keys.OemMinus, ["="] = Keys.Oemplus,
    };

    static readonly HashSet<Keys> Extended = new()
    {
        Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Insert, Keys.Delete, Keys.LWin, Keys.RWin, Keys.Divide, Keys.NumLock, Keys.PrintScreen,
    };

    public const string BlockedMessage = "Windows blocked the keys (the app in front runs as admin)";

    /// <summary>
    /// Presses the combination. False when Windows refused the input, typically because the app in front runs
    /// as admin and Shoji doesn't (a normal app may not type into an elevated one).
    /// </summary>
    public static bool Send(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse).ToList();
        if (keys.Count == 0)
            return true;
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var k in keys)
            inputs.Add(Input(k, up: false));
        for (int i = keys.Count - 1; i >= 0; i--)
            inputs.Add(Input(keys[i], up: true));
        uint sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == inputs.Count)
            return true;
        Log.Write($"SendInput sent {sent} of {inputs.Count} key events for \"{combo}\" (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    static Keys Parse(string name)
    {
        if (Aliases.TryGetValue(name, out var k))
            return k;
        if (name.Length == 1 && char.IsDigit(name[0]))
            return Keys.D0 + (name[0] - '0');
        if (Enum.TryParse<Keys>(name, ignoreCase: true, out k))
            return k;
        throw new ArgumentException(L.F("Unknown key \"{0}\"", name));
    }

    static NativeMethods.INPUT Input(Keys key, bool up)
    {
        uint vk = (uint)key;
        // Both the virtual key and the scan code, so games that read raw input see it too.
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)NativeMethods.MapVirtualKey(vk, 0),
                dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (Extended.Contains(key) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
            },
        };
    }
}
