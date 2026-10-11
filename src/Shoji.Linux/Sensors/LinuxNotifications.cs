using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Feeds <see cref="NotificationHub"/> with the desktop notifications apps send (Discord, Thunderbird, browsers…):
/// one long-running <c>dbus-monitor</c> watching the session bus for org.freedesktop.Notifications.Notify calls.
/// Monitoring the session bus is allowed for the same user, needs no notification-daemon cooperation and sees
/// every app. Runs only while a Notifications tile exists (it's a <see cref="ServiceStore"/> helper).
/// </summary>
public sealed class LinuxNotifications : IDisposable
{
    static readonly TimeSpan ToolRecheck = TimeSpan.FromMinutes(5);
    const string Missing = "Notifications need dbus-monitor: sudo apt install dbus-bin";

    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    Process? _monitor;

    public LinuxNotifications()
    {
        if (Demo.Enabled)
            return; // regression screenshots (Platform/DemoData.cs): only the made-up notifications, never the desktop's
        _ = Task.Run(() => RunAsync(_stop.Token));
        // dbus-monitor waits for bus traffic, so it would outlive us until the next message: stop it on the way out.
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    void OnExit(object? sender, EventArgs e) => Stop();

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var p = Start();
            if (p == null)
            {
                NotificationHub.Hint = Missing;
                Log.WriteThrottled("notify-missing", "Notifications: dbus-monitor not found (package dbus-bin)", TimeSpan.FromHours(6));
                await Delay(ToolRecheck, ct).ConfigureAwait(false);
                continue;
            }
            lock (_gate)
                _monitor = p;

            var started = DateTime.UtcNow;
            var parser = new NotifyCallParser();
            string lastError = "";
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim(); };
            p.BeginErrorReadLine();
            try
            {
                bool working = false;
                while (await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    // Its first line (NameAcquired) means it's monitoring: an old hint can go.
                    if (!working)
                    {
                        working = true;
                        NotificationHub.Hint = null;
                    }
                    if (parser.Feed(line) is { } call)
                        Publish(call);
                }
            }
            catch (Exception)
            {
                // stopped, or a broken pipe
            }
            LinuxCli.Kill(p);
            lock (_gate)
                _monitor = null;
            p.Dispose();
            if (ct.IsCancellationRequested)
                break;

            // A monitor that ran for a while just lost its bus (logout, session restart): try again soon.
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1))
                backoff = TimeSpan.FromSeconds(2);
            else if (lastError.Length > 0)
                NotificationHub.Hint = L.F("Couldn't watch notifications: {0}", lastError);
            Log.WriteThrottled("notify-exit", $"Notifications: dbus-monitor stopped ({(lastError.Length > 0 ? lastError : "no message")}); retrying in {backoff.TotalSeconds:0} s", TimeSpan.FromMinutes(10));
            await Delay(backoff, ct).ConfigureAwait(false);
            backoff = TimeSpan.FromSeconds(Math.Min(300, backoff.TotalSeconds * 2));
        }
    }

    static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    static Process? Start()
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-monitor")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("type='method_call',interface='org.freedesktop.Notifications',member='Notify'");
            psi.Environment["LC_ALL"] = "C";
            return Process.Start(psi);
        }
        catch (Win32Exception)
        {
            return null; // not installed
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("notify-start", $"Notifications: dbus-monitor failed to start: {ex.Message}", TimeSpan.FromHours(1));
            return null;
        }
    }

    static void Publish(NotifyCall c)
    {
        string title = NotificationText.Tidy(NotificationText.StripMarkup(c.Summary), 200);
        string body = NotificationText.Tidy(NotificationText.StripMarkup(c.Body));
        string app = c.AppName.Trim();
        if (app.Length == 0)
            app = c.DesktopEntry ?? "";
        if (app.Length == 0)
            app = L.T("Notification");
        else if (app.All(ch => !char.IsLetter(ch) || char.IsLower(ch)))
            app = char.ToUpperInvariant(app[0]) + app[1..]; // "discord" → "Discord"
        string? icon = IconFile.Find(c.AppIcon) ?? IconFile.Find(c.DesktopEntry) ?? IconFile.Find(c.ImagePath);
        NotificationHub.Add(new NotificationItem(app, title, body, DateTime.Now, icon));
    }

    void Stop()
    {
        Process? p;
        lock (_gate)
            p = _monitor;
        LinuxCli.Kill(p);
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _stop.Cancel();
        Stop();
        NotificationHub.Clear();
        NotificationHub.Hint = null;
    }
}

/// <summary>The arguments of one Notify call (the spec's app_name, app_icon, summary, body and two useful hints).</summary>
public sealed record NotifyCall(string AppName, uint ReplacesId, string AppIcon, string Summary, string Body, string? DesktopEntry, string? ImagePath);

/// <summary>
/// Turns dbus-monitor's text output into <see cref="NotifyCall"/>s, one line at a time. dbus-monitor prints strings
/// raw, without escaping: <c>string "a "quoted"\nsecond line"</c> spans two lines and may even contain something
/// that looks like another argument. The parser leans on the fixed Notify signature (s u s s s as a{sv} i): the app
/// name runs up to the "uint32" line, the three strings after it up to the actions "array [", and the call ends
/// at the top-level "int32" (expire_timeout). Anything odd in the body stays in the body.
/// </summary>
public sealed class NotifyCallParser
{
    const string Str = "   string \"";
    const int MaxLines = 400;     // the strings before the actions array; a longer "body" is junk
    static readonly Regex Header = new(@"^(method call|method return|signal|error) time=", RegexOptions.Compiled);
    static readonly Regex Uint = new(@"^   uint32 (\d+)$", RegexOptions.Compiled);
    static readonly Regex Int = new(@"^   int32 -?\d+$", RegexOptions.Compiled);
    static readonly Regex HintString = new(@"^\s*variant\s+string ""(.*)""$", RegexOptions.Compiled);

    readonly List<string> _head = new();   // lines of the five leading arguments
    readonly List<string> _tail = new();   // string lines of the actions and hints (hex dumps of image-data are skipped)
    bool _inCall, _inArrays;

    /// <summary>Feeds one output line; returns a call when this line completed one.</summary>
    public NotifyCall? Feed(string line)
    {
        if (Header.IsMatch(line))
        {
            // A new message. (A call cut short, e.g. by a dbus-monitor that died, is dropped.)
            Reset();
            _inCall = line.StartsWith("method call", StringComparison.Ordinal)
                      && line.Contains("interface=org.freedesktop.Notifications;", StringComparison.Ordinal)
                      && line.EndsWith("member=Notify", StringComparison.Ordinal);
            return null;
        }
        if (!_inCall)
            return null;
        if (!_inArrays)
        {
            if (line == "   array [" && _head.Count > 0)
                _inArrays = true;
            else if (_head.Count < MaxLines)
                _head.Add(line);
            else
                Reset();
            return null;
        }
        if (Int.IsMatch(line))
        {
            var call = Parse();
            Reset();
            return call;
        }
        if (line.Contains("string \"", StringComparison.Ordinal) && _tail.Count < MaxLines)
            _tail.Add(line);
        return null;
    }

    void Reset()
    {
        _head.Clear();
        _tail.Clear();
        _inCall = _inArrays = false;
    }

    NotifyCall? Parse()
    {
        int u = _head.FindIndex(l => Uint.IsMatch(l));
        if (u < 1)
            return null;
        string? app = Unquote(string.Join("\n", _head.Take(u)));
        string? rest = Unquote(string.Join("\n", _head.Skip(u + 1)));
        if (app == null || rest == null)
            return null;
        // icon, summary, body: split at the first two string boundaries; the body keeps whatever is left.
        var parts = rest.Split("\"\n" + Str, 3);
        if (parts.Length < 3)
            return null;
        uint replaces = uint.Parse(Uint.Match(_head[u]).Groups[1].Value, CultureInfo.InvariantCulture);
        return new NotifyCall(app, replaces, parts[0], parts[1], parts[2], Hint("desktop-entry"), Hint("image-path") ?? Hint("image_path"));
    }

    /// <summary>"   string "x"" (possibly over several lines) → x.</summary>
    static string? Unquote(string s) => s.StartsWith(Str, StringComparison.Ordinal) && s.EndsWith('"') && s.Length > Str.Length
        ? s[Str.Length..^1]
        : null;

    /// <summary>A string-valued hint: its key line is followed by "variant string "value"".</summary>
    string? Hint(string key)
    {
        int i = _tail.IndexOf($"         string \"{key}\"");
        if (i < 0 || i + 1 >= _tail.Count)
            return null;
        var m = HintString.Match(_tail[i + 1]);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }
}

/// <summary>Finds a PNG for a notification's icon: a file path / URI, or an icon name looked up in the usual theme folders.</summary>
static class IconFile
{
    static readonly Dictionary<string, string?> Cache = new();
    static readonly string[] Sizes = { "48x48", "64x64", "128x128", "96x96", "256x256", "32x32" };

    public static string? Find(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(icon, out var hit))
                return hit;
            if (Cache.Count > 200)
                Cache.Clear();
            return Cache[icon] = Look(icon);
        }
    }

    static string? Look(string icon)
    {
        try
        {
            if (icon.StartsWith("file://", StringComparison.Ordinal))
                icon = new Uri(icon).LocalPath;
            if (icon.StartsWith('/'))
                return File.Exists(icon) && !icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? icon : null;
            if (icon.Contains('/') || icon.Contains(".."))
                return null;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new[]
            {
                Path.Combine(home, ".local/share/icons/hicolor"), "/usr/share/icons/hicolor",
                "/var/lib/flatpak/exports/share/icons/hicolor", Path.Combine(home, ".local/share/flatpak/exports/share/icons/hicolor"),
                "/var/lib/snapd/desktop/icons",
            };
            foreach (var root in roots)
            {
                foreach (var size in Sizes)
                    if (Path.Combine(root, size, "apps", icon + ".png") is var p && File.Exists(p))
                        return p;
                if (Path.Combine(root, icon + ".png") is var flat && File.Exists(flat))
                    return flat;
            }
            return File.Exists($"/usr/share/pixmaps/{icon}.png") ? $"/usr/share/pixmaps/{icon}.png" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
