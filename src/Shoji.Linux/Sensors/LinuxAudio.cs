using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>An audio output (PipeWire / PulseAudio sink) as listed by wpctl or pactl.</summary>
public sealed record LinuxAudioOutput(int Id, string Name, bool IsDefault);

/// <summary>An app playing sound: all its playback streams as one row. Id lists the streams ("88|95"), Volume is 0–1 as wpctl shows it.</summary>
public sealed record LinuxAppVolume(string Id, string Name, double Volume, bool Muted);

/// <summary>
/// PipeWire audio through wpctl (or pactl, for PulseAudio or PipeWire without WirePlumber's CLI): master volume and mute,
/// microphone mute and switching the default output. Getters never block: they return the last known state and refresh
/// it in the background (at most every 1.5 s, and only while something keeps reading). Setters return immediately; the
/// commands run in order in the background. Without either tool nothing is spawned; it looks again every few minutes.
/// Apps playing sound (the mixer's per-app rows) come from one long-running "pw-dump --monitor" that reports changes as
/// they happen, kept only while a mixer reads them; with pactl alone from "pactl -f json list sink-inputs" every 3 s.
/// </summary>
public sealed class LinuxAudio
{
    public static LinuxAudio Shared { get; } = new();

    readonly object _gate = new();
    DateTime _statusAt = DateTime.MinValue, _micAt = DateTime.MinValue;
    bool _refreshing, _again;
    Task _commands = Task.CompletedTask;

    double? _volume;
    bool? _muted, _micMuted;
    IReadOnlyList<LinuxAudioOutput> _outputs = Array.Empty<LinuxAudioOutput>();
    // pactl switches outputs by sink name, not by the index shown in the list.
    Dictionary<int, string> _sinkNames = new();

    // Per-app streams, keyed by PipeWire node id (pactl: sink-input index).
    sealed class AppStream
    {
        public string Name = "";
        public double Volume;
        public bool Muted, Playing;
        public DateTime PlayedAt;
    }
    readonly Dictionary<int, AppStream> _streams = new();
    DateTime _appsReadAt = DateTime.MinValue, _pactlAppsAt = DateTime.MinValue, _monitorRetryAt = DateTime.MinValue, _monitorStartedAt;
    TimeSpan _monitorBackoff = TimeSpan.FromSeconds(5);
    Process? _monitor;
    bool _noPwDumpLogged;

    enum Tool { None, Wpctl, Pactl }
    Tool _tool;
    DateTime _toolAt = DateTime.MinValue;

    LinuxAudio()
    {
        // pw-dump would only notice we're gone at its next write.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Process? monitor;
            lock (_gate)
                monitor = _monitor;
            LinuxCli.Kill(monitor);
        };
    }

    // Demo mode (regression screenshots, Platform/DemoData.cs): a fixed state, and wpctl / pactl / pw-dump never run.
    bool _demo;

    internal void UseDemo(double volume, bool micMuted, IReadOnlyList<LinuxAudioOutput> outputs, IEnumerable<(string Name, double Volume)> apps)
    {
        lock (_gate)
        {
            _demo = true;
            (_volume, _muted, _micMuted, _outputs) = (volume, false, micMuted, outputs);
            int id = 100;
            foreach (var (name, level) in apps)
                _streams[id++] = new AppStream { Name = name, Volume = level, Playing = true };
        }
    }

    /// <summary>wpctl, else pactl, else none; looked up again every 5 minutes (a PATH scan, no process).</summary>
    Tool CurrentTool()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _toolAt > TimeSpan.FromMinutes(5))
            {
                var tool = LinuxSetup.Which("wpctl") ? Tool.Wpctl : LinuxSetup.Which("pactl") ? Tool.Pactl : Tool.None;
                if (tool == Tool.None && _toolAt == DateTime.MinValue)
                    Log.Write("Neither wpctl nor pactl found: volume and microphone controls are off (sudo apt install wireplumber)");
                (_tool, _toolAt) = (tool, DateTime.UtcNow);
            }
            return _tool;
        }
    }

    /// <summary>Default output volume, 0–1 (null until first read or without PipeWire).</summary>
    public double? Volume { get { Touch(); lock (_gate) return _volume; } }

    public bool? Muted { get { Touch(); lock (_gate) return _muted; } }

    /// <summary>Whether the default input (microphone) is muted.</summary>
    public bool? MicMuted { get { Touch(); lock (_gate) return _micMuted; } }

    public string? OutputName { get { Touch(); lock (_gate) return _outputs.FirstOrDefault(o => o.IsDefault)?.Name; } }

    /// <summary>Every output device, the default one marked.</summary>
    public IReadOnlyList<LinuxAudioOutput> Outputs { get { Touch(); lock (_gate) return _outputs; } }

    /// <summary>
    /// Apps playing sound, by name, at most 8. Streams of one app (browser tabs) are one row. Streams that stopped
    /// playing (paused, corked) drop off after 30 s; ones already silent when the mixer appears aren't shown.
    /// </summary>
    public IReadOnlyList<LinuxAppVolume> Apps
    {
        get
        {
            Touch();
            TouchApps();
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                return _streams
                    .Where(kv => kv.Value.Playing || now - kv.Value.PlayedAt < TimeSpan.FromSeconds(30))
                    .GroupBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new LinuxAppVolume(string.Join("|", g.Select(kv => kv.Key).OrderBy(k => k)), g.First().Value.Name,
                        g.Max(kv => kv.Value.Volume), g.All(kv => kv.Value.Muted)))
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
            }
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Starts a background refresh when the cached state is stale.</summary>
    void Touch(bool force = false)
    {
        lock (_gate)
        {
            if (_demo)
                return;
            if (_refreshing)
            {
                // A read already running may have started before the change: read once more after it.
                _again |= force;
                return;
            }
            if (!force && DateTime.UtcNow - _statusAt < TimeSpan.FromSeconds(1.5))
                return;
            _refreshing = true;
        }
        _ = Task.Run(RefreshAsync);
    }

    async Task RefreshAsync()
    {
        try
        {
            var tool = CurrentTool();
            if (tool == Tool.None)
                return;
            if (tool == Tool.Pactl)
            {
                await RefreshPactlAsync().ConfigureAwait(false);
                return;
            }
            // One "wpctl status" gives every sink, which is the default, and its volume / mute.
            var (code, output) = await LinuxCli.RunAsync("wpctl", new[] { "status" }).ConfigureAwait(false);
            var (outputs, volume, muted) = code == 0 ? ParseSinks(output) : (new List<LinuxAudioOutput>(), null, null);
            if (code == 0 && volume == null && outputs.Count > 0)
                (volume, muted) = await GetVolumeAsync("@DEFAULT_AUDIO_SINK@").ConfigureAwait(false);

            // The default source can be a sink's monitor (it is on this kind of setup), so ask for it by name. It rarely changes
            // behind our back (our own toggles force a re-read), so every 5 s is enough.
            bool readMic;
            lock (_gate)
                readMic = DateTime.UtcNow - _micAt > TimeSpan.FromSeconds(5);
            bool? micMuted = null;
            if (readMic)
                (_, micMuted) = await GetVolumeAsync("@DEFAULT_AUDIO_SOURCE@").ConfigureAwait(false);

            lock (_gate)
            {
                _outputs = outputs;
                _volume = volume;
                _muted = muted;
                if (readMic)
                {
                    _micMuted = micMuted;
                    _micAt = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Audio refresh failed: {ex.Message}");
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                _statusAt = DateTime.UtcNow;
                again = _again;
                _again = false;
                _refreshing = again;
            }
            if (again)
                _ = Task.Run(RefreshAsync);
        }
    }

    /// <summary>pactl: "list sinks" has every sink with its volume and mute; the default sink and the microphone are asked separately.</summary>
    async Task RefreshPactlAsync()
    {
        var (code, output) = await LinuxCli.RunAsync("pactl", new[] { "list", "sinks" }).ConfigureAwait(false);
        var (_, defaultSink) = await LinuxCli.RunAsync("pactl", new[] { "get-default-sink" }).ConfigureAwait(false);
        var (outputs, names, volume, muted) = code == 0 ? ParsePactlSinks(output, defaultSink.Trim()) : (new List<LinuxAudioOutput>(), new Dictionary<int, string>(), null, null);
        bool readMic;
        lock (_gate)
            readMic = DateTime.UtcNow - _micAt > TimeSpan.FromSeconds(5);
        bool readApps;
        lock (_gate)
            readApps = DateTime.UtcNow - _appsReadAt < TimeSpan.FromSeconds(5) && DateTime.UtcNow - _pactlAppsAt > TimeSpan.FromSeconds(3);
        if (readApps)
        {
            var (appsCode, inputs) = await LinuxCli.RunAsync("pactl", new[] { "-f", "json", "list", "sink-inputs" }).ConfigureAwait(false);
            lock (_gate)
            {
                _pactlAppsAt = DateTime.UtcNow;
                if (appsCode == 0)
                    ParsePactlInputs(inputs);
            }
        }
        bool? micMuted = null;
        if (readMic)
        {
            var (micCode, mic) = await LinuxCli.RunAsync("pactl", new[] { "get-source-mute", "@DEFAULT_SOURCE@" }).ConfigureAwait(false);
            if (micCode == 0)
                micMuted = mic.Contains("yes", StringComparison.OrdinalIgnoreCase);
        }
        lock (_gate)
        {
            _outputs = outputs;
            _sinkNames = names;
            _volume = volume;
            _muted = muted;
            if (readMic)
            {
                _micMuted = micMuted;
                _micAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>"Sink #56 / Name: alsa_output… / Description: Built-in Audio / Mute: no / Volume: front-left: 26214 /  40% / …" blocks.</summary>
    internal static (List<LinuxAudioOutput> Outputs, Dictionary<int, string> Names, double? Volume, bool? Muted) ParsePactlSinks(string list, string defaultSink)
    {
        var outputs = new List<LinuxAudioOutput>();
        var names = new Dictionary<int, string>();
        double? volume = null;
        bool? muted = null;
        int id = -1;
        string name = "", description = "";
        bool? mute = null;
        double? vol = null;
        void Flush()
        {
            if (id < 0)
                return;
            bool isDefault = name.Length > 0 && name == defaultSink;
            outputs.Add(new LinuxAudioOutput(id, description.Length > 0 ? description : name, isDefault));
            names[id] = name;
            if (isDefault)
                (volume, muted) = (vol, mute);
        }
        foreach (var raw in list.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Sink #", StringComparison.Ordinal))
            {
                Flush();
                id = int.TryParse(line[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
                (name, description, mute, vol) = ("", "", null, null);
            }
            else if (line.StartsWith("Name:", StringComparison.Ordinal))
                name = line[5..].Trim();
            else if (line.StartsWith("Description:", StringComparison.Ordinal))
                description = line[12..].Trim();
            else if (line.StartsWith("Mute:", StringComparison.Ordinal))
                mute = line.Contains("yes", StringComparison.Ordinal);
            else if (line.StartsWith("Volume:", StringComparison.Ordinal) && vol == null && Regex.Match(line, @"(\d+)%") is { Success: true } m)
                vol = LinuxCli.ParseDouble(m.Groups[1].Value) / 100;
        }
        Flush();
        return (outputs, names, volume, muted);
    }

    // "│  *   56. Built-in Audio Digital Stereo (IEC958) [vol: 0.40 MUTED]"
    static readonly Regex SinkLine = new(@"^[\s│├└─]*(\*)?\s*(\d+)\.\s+(.+?)(?:\s+\[vol:\s*([\d.]+)(\s+MUTED)?\])?\s*$", RegexOptions.Compiled);

    /// <summary>The Sinks block of the Audio section of "wpctl status".</summary>
    internal static (List<LinuxAudioOutput> Outputs, double? Volume, bool? Muted) ParseSinks(string status)
    {
        var outputs = new List<LinuxAudioOutput>();
        double? volume = null;
        bool? muted = null;
        string section = "", block = "";
        foreach (var raw in status.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] is not ('│' or '├' or '└'))
            {
                section = line.Trim(); // "Audio", "Video", "Settings", "PipeWire 'pipewire-0' …"
                continue;
            }
            var header = Regex.Match(line, @"^\s*[├└]─\s*(\w+):");
            if (header.Success)
            {
                block = header.Groups[1].Value;
                continue;
            }
            if (section != "Audio" || block != "Sinks")
                continue;
            var m = SinkLine.Match(line);
            if (!m.Success)
                continue;
            bool isDefault = m.Groups[1].Success;
            outputs.Add(new LinuxAudioOutput(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value.Trim(), isDefault));
            if (isDefault && m.Groups[4].Success)
            {
                volume = LinuxCli.ParseDouble(m.Groups[4].Value);
                muted = m.Groups[5].Success;
            }
        }
        return (outputs, volume, muted);
    }

    /// <summary>"Volume: 0.40 [MUTED]" from wpctl get-volume.</summary>
    static async Task<(double? Volume, bool? Muted)> GetVolumeAsync(string node)
    {
        var (code, output) = await LinuxCli.RunAsync("wpctl", new[] { "get-volume", node }).ConfigureAwait(false);
        var m = Regex.Match(output, @"Volume:\s*([\d.]+)(\s*\[MUTED\])?");
        return code == 0 && m.Success ? (LinuxCli.ParseDouble(m.Groups[1].Value), m.Groups[2].Success) : (null, null);
    }

    // ---------------------------------------------------------------- changing

    /// <summary>
    /// Runs wpctl (or the equivalent pactl) commands one after another (so quick button presses apply in order), then
    /// re-reads the state. Nothing runs without either tool.
    /// </summary>
    void Command(string[] wpctl, string[]? pactl, bool reread = true)
    {
        var tool = CurrentTool();
        var args = tool == Tool.Wpctl ? wpctl : tool == Tool.Pactl ? pactl : null;
        if (args == null)
            return;
        string file = tool == Tool.Wpctl ? "wpctl" : "pactl";
        lock (_gate)
        {
            _commands = _commands.ContinueWith(async _ =>
            {
                await LinuxCli.RunAsync(file, args, 2000).ConfigureAwait(false);
                if (!reread)
                    return; // per-app changes come back through pw-dump (or the next sink-input read)
                lock (_gate)
                    _micAt = DateTime.MinValue; // the change may have been to the microphone
                Touch(force: true);
            }, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Sets the default output volume (0–1) and unmutes it when raised above 0.</summary>
    public void SetVolume(double volume)
    {
        double v = Math.Clamp(volume, 0, 1);
        lock (_gate)
        {
            _volume = v;
            if (v > 0 && _muted == true)
                _muted = false;
        }
        Command(new[] { "set-volume", "@DEFAULT_AUDIO_SINK@", v.ToString("0.###", CultureInfo.InvariantCulture) },
                new[] { "set-sink-volume", "@DEFAULT_SINK@", Percent(v) });
        if (v > 0)
            Command(new[] { "set-mute", "@DEFAULT_AUDIO_SINK@", "0" }, new[] { "set-sink-mute", "@DEFAULT_SINK@", "0" });
    }

    /// <summary>Raises or lowers the volume by <paramref name="delta"/> (0.05 = 5 points), relative to the live value so fast presses add up.</summary>
    public void ChangeVolume(double delta)
    {
        if (delta == 0 || double.IsNaN(delta))
            return;
        double? target = null;
        lock (_gate)
        {
            if (_volume is double v)
                target = _volume = Math.Clamp(v + delta, 0, 1);
            if (delta > 0 && _muted == true)
                _muted = false;
        }
        string step = Math.Abs(delta).ToString("0.###", CultureInfo.InvariantCulture) + (delta > 0 ? "+" : "-");
        // pactl has no upper limit for relative steps, so it gets the absolute value when the current one is known.
        string pactlStep = target is double t ? Percent(t) : (delta > 0 ? "+" : "-") + Math.Round(Math.Abs(delta) * 100).ToString(CultureInfo.InvariantCulture) + "%";
        Command(new[] { "set-volume", "-l", "1.0", "@DEFAULT_AUDIO_SINK@", step }, new[] { "set-sink-volume", "@DEFAULT_SINK@", pactlStep });
        if (delta > 0)
            Command(new[] { "set-mute", "@DEFAULT_AUDIO_SINK@", "0" }, new[] { "set-sink-mute", "@DEFAULT_SINK@", "0" });
    }

    public void ToggleMute()
    {
        lock (_gate)
            _muted = _muted is bool m ? !m : null;
        Command(new[] { "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle" }, new[] { "set-sink-mute", "@DEFAULT_SINK@", "toggle" });
    }

    public void ToggleMic()
    {
        lock (_gate)
            _micMuted = _micMuted is bool m ? !m : null;
        Command(new[] { "set-mute", "@DEFAULT_AUDIO_SOURCE@", "toggle" }, new[] { "set-source-mute", "@DEFAULT_SOURCE@", "toggle" });
    }

    /// <summary>
    /// Makes the next output the default, cycling through all outputs, or only through those whose name contains one of
    /// the "|"-separated <paramref name="namesPipeSeparated"/>. Returns the new output's name, or null if there is none.
    /// </summary>
    public string? NextOutput(string? namesPipeSeparated)
    {
        List<LinuxAudioOutput> devices;
        lock (_gate)
            devices = _outputs.ToList();
        if (devices.Count == 0)
            return null;
        var candidates = devices;
        if (!string.IsNullOrWhiteSpace(namesPipeSeparated))
        {
            var names = namesPipeSeparated.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            candidates = devices.Where(d => names.Any(n => d.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (candidates.Count == 0)
                return null;
        }
        int at = candidates.FindIndex(d => d.IsDefault);
        var target = candidates[(at + 1) % candidates.Count];
        string? sinkName;
        lock (_gate)
        {
            _outputs = devices.Select(d => d with { IsDefault = d.Id == target.Id }).ToList();
            sinkName = _sinkNames.GetValueOrDefault(target.Id);
        }
        Command(new[] { "set-default", target.Id.ToString(CultureInfo.InvariantCulture) }, sinkName == null ? null : new[] { "set-default-sink", sinkName });
        return target.Name;
    }

    static string Percent(double v) => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";

    // ---------------------------------------------------------------- apps

    /// <summary>Sets an app's volume (0–1) on each of its streams.</summary>
    public void SetAppVolume(string id, double volume)
    {
        double v = Math.Clamp(volume, 0, 1);
        foreach (int stream in StreamIds(id))
        {
            lock (_gate)
                if (_streams.TryGetValue(stream, out var s))
                    s.Volume = v;
            string n = stream.ToString(CultureInfo.InvariantCulture);
            Command(new[] { "set-volume", n, v.ToString("0.###", CultureInfo.InvariantCulture) }, new[] { "set-sink-input-volume", n, Percent(v) }, reread: false);
        }
    }

    /// <summary>Mutes or unmutes every stream of an app.</summary>
    public void SetAppMuted(string id, bool muted)
    {
        foreach (int stream in StreamIds(id))
        {
            lock (_gate)
                if (_streams.TryGetValue(stream, out var s))
                    s.Muted = muted;
            string n = stream.ToString(CultureInfo.InvariantCulture);
            Command(new[] { "set-mute", n, muted ? "1" : "0" }, new[] { "set-sink-input-mute", n, muted ? "1" : "0" }, reread: false);
        }
        lock (_gate)
            _pactlAppsAt = DateTime.MinValue;
    }

    static IEnumerable<int> StreamIds(string id) =>
        id.Split('|').Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1).Where(n => n >= 0).ToList();

    /// <summary>
    /// Keeps "pw-dump --monitor" running while someone reads <see cref="Apps"/> (with wpctl; it comes with PipeWire) and stops it
    /// 15 s after the last read. If it dies it's restarted after a growing pause; without pw-dump there are no app rows.
    /// </summary>
    void TouchApps()
    {
        if (_demo)
            return;
        if (CurrentTool() != Tool.Wpctl)
        {
            lock (_gate)
                _appsReadAt = DateTime.UtcNow; // pactl: RefreshPactlAsync reads the sink inputs while this is recent
            return;
        }
        Process? started = null;
        lock (_gate)
        {
            _appsReadAt = DateTime.UtcNow;
            if (_monitor != null || DateTime.UtcNow < _monitorRetryAt)
                return;
            if (!LinuxSetup.Which("pw-dump"))
            {
                if (!_noPwDumpLogged)
                    Log.Write("pw-dump not found: the mixer can't list apps (sudo apt install pipewire-bin)");
                _noPwDumpLogged = true;
                _monitorRetryAt = DateTime.UtcNow.AddMinutes(5);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("pw-dump")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--monitor");
                psi.ArgumentList.Add("--no-colors");
                psi.Environment["LC_ALL"] = "C";
                started = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write($"pw-dump failed to start: {ex.Message}");
            }
            if (started == null)
            {
                _monitorRetryAt = DateTime.UtcNow + _monitorBackoff;
                _monitorBackoff = TimeSpan.FromSeconds(Math.Min(300, _monitorBackoff.TotalSeconds * 2));
                return;
            }
            _monitor = started;
            _monitorStartedAt = DateTime.UtcNow;
        }
        started.ErrorDataReceived += (_, _) => { };
        started.BeginErrorReadLine();
        _ = Task.Run(() => ReadMonitorAsync(started));
        _ = Task.Run(() => WatchMonitorAsync(started));
    }

    /// <summary>Stops pw-dump once no mixer has asked for 15 s (hidden page, tile removed).</summary>
    async Task WatchMonitorAsync(Process p)
    {
        while (true)
        {
            await Task.Delay(5000).ConfigureAwait(false);
            lock (_gate)
            {
                if (_monitor != p)
                    return;
                if (DateTime.UtcNow - _appsReadAt < TimeSpan.FromSeconds(15))
                    continue;
                _monitor = null;
                _streams.Clear();
            }
            LinuxCli.Kill(p);
            return;
        }
    }

    /// <summary>pw-dump prints a JSON array per batch of changes, "[" and "]" alone on their lines; each object in it is complete.</summary>
    async Task ReadMonitorAsync(Process p)
    {
        var chunk = new StringBuilder();
        try
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line == "[")
                    chunk.Clear();
                chunk.Append(line).Append('\n');
                if (line != "]")
                    continue;
                // Most batches are clients coming and going (every wpctl call is one): skip them without parsing.
                string text = chunk.ToString();
                chunk.Clear();
                if (text.Contains("Stream/Output/Audio", StringComparison.Ordinal) || text.Contains("\"info\": null", StringComparison.Ordinal))
                    ApplyDump(text);
            }
        }
        catch (Exception)
        {
            // killed or broken pipe
        }
        lock (_gate)
        {
            if (_monitor == p)
            {
                // It died by itself (PipeWire restarted?): try again later, sooner if it had been running for a while.
                _monitor = null;
                _streams.Clear();
                if (DateTime.UtcNow - _monitorStartedAt > TimeSpan.FromMinutes(1))
                    _monitorBackoff = TimeSpan.FromSeconds(5);
                _monitorRetryAt = DateTime.UtcNow + _monitorBackoff;
                _monitorBackoff = TimeSpan.FromSeconds(Math.Min(300, _monitorBackoff.TotalSeconds * 2));
                Log.Write("pw-dump stopped; the mixer's app list pauses");
            }
        }
        p.Dispose();
    }

    void ApplyDump(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                foreach (var o in doc.RootElement.EnumerateArray())
                {
                    if (!o.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out int id))
                        continue;
                    if (!o.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                    {
                        _streams.Remove(id); // "info": null means the object is gone
                        continue;
                    }
                    if (!info.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object ||
                        Str(props, "media.class") != "Stream/Output/Audio")
                        continue;
                    if (!_streams.TryGetValue(id, out var s))
                        _streams[id] = s = new AppStream();
                    s.Name = Str(props, "application.name") ?? Str(props, "application.process.binary") ?? Str(props, "node.description") ?? Str(props, "node.name") ?? "App";
                    bool corked = props.TryGetProperty("pulse.corked", out var c) && c.ValueKind == JsonValueKind.True;
                    s.Playing = Str(info, "state") == "running" && !corked;
                    if (s.Playing)
                        s.PlayedAt = now;
                    // Volume as wpctl shows it: the cube root of the (linear) channel volumes.
                    if (info.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object &&
                        ps.TryGetProperty("Props", out var propList) && propList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pr in propList.EnumerateArray())
                        {
                            if (pr.ValueKind != JsonValueKind.Object || !pr.TryGetProperty("channelVolumes", out var cv) || cv.ValueKind != JsonValueKind.Array)
                                continue;
                            var levels = cv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList();
                            if (levels.Count > 0)
                                s.Volume = Math.Clamp(Math.Cbrt(levels.Average()), 0, 1);
                            s.Muted = pr.TryGetProperty("mute", out var m) && m.ValueKind == JsonValueKind.True;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"pw-dump output not understood: {ex.Message}");
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>pactl -f json list sink-inputs: [{"index":12,"corked":false,"mute":false,"volume":{"front-left":{"value_percent":"40%",…}},"properties":{"application.name":"Firefox",…}}].</summary>
    void ParsePactlInputs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var now = DateTime.UtcNow;
            var seen = new HashSet<int>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (!o.TryGetProperty("index", out var idx) || !idx.TryGetInt32(out int id))
                    continue;
                seen.Add(id);
                if (!_streams.TryGetValue(id, out var s))
                    _streams[id] = s = new AppStream();
                var props = o.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                s.Name = (props.ValueKind == JsonValueKind.Object ? Str(props, "application.name") ?? Str(props, "application.process.binary") : null) ?? "App";
                s.Muted = o.TryGetProperty("mute", out var m) && m.ValueKind == JsonValueKind.True;
                s.Playing = !(o.TryGetProperty("corked", out var c) && c.ValueKind == JsonValueKind.True);
                if (s.Playing)
                    s.PlayedAt = now;
                if (o.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Object)
                {
                    var levels = vol.EnumerateObject()
                        .Select(ch => ch.Value.ValueKind == JsonValueKind.Object && ch.Value.TryGetProperty("value_percent", out var pc) ? LinuxCli.ParseDouble(pc.GetString()?.TrimEnd('%')) : null)
                        .OfType<double>().ToList();
                    if (levels.Count > 0)
                        s.Volume = Math.Clamp(levels.Average() / 100, 0, 1);
                }
            }
            foreach (int gone in _streams.Keys.Where(k => !seen.Contains(k)).ToList())
                _streams.Remove(gone);
        }
        catch (Exception)
        {
            // an older pactl without JSON output: no app rows
        }
    }
}

/// <summary>
/// Seconds since the last keyboard / mouse input, from GNOME Mutter's IdleMonitor (Wayland has no general API).
/// Asked through busctl and extrapolated in between (while away the value just grows). Scenes only wait for minutes of
/// idleness, so while the user is active it's asked every 10 s; once idle for a while every 2 s, so their return is
/// noticed quickly. 0 when unavailable (other desktops): then it's asked again every 5 minutes.
/// </summary>
public static class IdleTracker
{
    static readonly object Gate = new();
    static double _idleMs;
    static DateTime _readAt = DateTime.MinValue, _retryAt = DateTime.MinValue;
    static bool _busy, _available = true;

    public static double Seconds
    {
        get
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                // _idleMs is the idle time at the last read: under a minute means someone was using the PC then.
                var every = TimeSpan.FromSeconds(_idleMs < 60_000 ? 10 : 2);
                bool due = _available ? now - _readAt > every : now > _retryAt;
                if (due && !_busy)
                {
                    _busy = true;
                    _ = Task.Run(RefreshAsync);
                }
                if (!_available || _readAt == DateTime.MinValue)
                    return 0;
                return (_idleMs + (now - _readAt).TotalMilliseconds) / 1000;
            }
        }
    }

    static async Task RefreshAsync()
    {
        try
        {
            var (code, output) = await LinuxCli.RunAsync("busctl", new[]
            {
                "--user", "call", "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
                "org.gnome.Mutter.IdleMonitor", "GetIdletime",
            }, 1000).ConfigureAwait(false);
            // Prints "t 12345" (milliseconds).
            var parts = output.Trim().Split(' ');
            lock (Gate)
            {
                if (code == 0 && parts.Length == 2 && parts[0] == "t" && LinuxCli.ParseDouble(parts[1]) is double ms)
                {
                    _idleMs = ms;
                    _readAt = DateTime.UtcNow;
                    _available = true;
                }
                else
                {
                    if (_available)
                        Log.Write("Idle time unavailable (no GNOME Mutter IdleMonitor); scenes that wait for idle won't trigger");
                    _available = false;
                    _retryAt = DateTime.UtcNow.AddMinutes(5);
                }
            }
        }
        finally
        {
            lock (Gate)
                _busy = false;
        }
    }
}
