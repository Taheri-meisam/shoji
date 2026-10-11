using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Shoji.Core;

namespace Shoji.Sensors;

public sealed record AppVolume(string Id, string Name, int ProcessId, float Volume, bool Muted);

/// <summary>
/// Windows audio: master volume and mute, microphone mute, per-app volume and switching the default
/// output device. Used from the UI thread only (deck buttons and the mixer tile).
/// </summary>
public sealed class AudioService
{
    static AudioService? _shared;
    public static AudioService Shared => _shared ??= new AudioService();

    readonly MMDeviceEnumerator _enumerator = new();
    MMDevice? _output, _mic;
    DateTime _devicesCheckedAt;
    readonly Dictionary<string, AudioSessionControl> _sessions = new();
    readonly Dictionary<int, string> _names = new();
    DateTime _sessionsAt;

    AudioService() { }

    MMDevice? Output { get { RefreshDevices(); return _output; } }
    MMDevice? Mic { get { RefreshDevices(); return _mic; } }

    /// <summary>The default devices change when headsets are plugged in; look again every few seconds.</summary>
    void RefreshDevices(bool force = false)
    {
        if (!force && DateTime.UtcNow - _devicesCheckedAt < TimeSpan.FromSeconds(3))
            return;
        _devicesCheckedAt = DateTime.UtcNow;
        _output = Swap(_output, () => _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        _mic = Swap(_mic, () => _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
    }

    MMDevice? Swap(MMDevice? current, Func<MMDevice> getDefault)
    {
        try
        {
            var latest = getDefault();
            if (current != null && current.ID == latest.ID)
            {
                latest.Dispose();
                return current;
            }
            current?.Dispose();
            if (ReferenceEquals(current, _output))
                ClearSessions();
            return latest;
        }
        catch
        {
            return null; // no device of that kind
        }
    }

    public string? OutputName => Try(() => Output?.FriendlyName);
    public string? MicName => Try(() => Mic?.FriendlyName);

    public float? Volume
    {
        get => Try(() => Output?.AudioEndpointVolume.MasterVolumeLevelScalar);
        set => Try(() => { if (Output != null && value is float v) Output.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1); return 0; });
    }

    public bool? Muted
    {
        get => Try(() => Output?.AudioEndpointVolume.Mute);
        set => Try(() => { if (Output != null && value is bool m) Output.AudioEndpointVolume.Mute = m; return 0; });
    }

    public bool? MicMuted
    {
        get => Try(() => Mic?.AudioEndpointVolume.Mute);
        set => Try(() => { if (Mic != null && value is bool m) Mic.AudioEndpointVolume.Mute = m; return 0; });
    }

    public float? MicVolume
    {
        get => Try(() => Mic?.AudioEndpointVolume.MasterVolumeLevelScalar);
        set => Try(() => { if (Mic != null && value is float v) Mic.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1); return 0; });
    }

    public void ToggleMute() => Muted = !(Muted ?? false);
    public void ToggleMic() => MicMuted = !(MicMuted ?? false);

    /// <summary>"+5", "-5" (percent steps) or "40" (absolute percent).</summary>
    public void ChangeVolume(string? target)
    {
        var t = (target ?? "+5").Trim().TrimEnd('%');
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n))
            return;
        if (t.StartsWith('+') || t.StartsWith('-'))
            Volume = (Volume ?? 0) + (float)(n / 100);
        else
            Volume = (float)(n / 100);
        if (Muted == true && Volume > 0)
            Muted = false;
    }

    // ---------------------------------------------------------------- apps

    void ClearSessions()
    {
        foreach (var s in _sessions.Values)
            s.Dispose();
        _sessions.Clear();
    }

    /// <summary>Apps that currently have an audio stream on the default output.</summary>
    public IReadOnlyList<AppVolume> Apps()
    {
        var output = Output;
        if (output == null)
            return Array.Empty<AppVolume>();

        try
        {
            if (DateTime.UtcNow - _sessionsAt > TimeSpan.FromSeconds(2))
            {
                _sessionsAt = DateTime.UtcNow;
                var manager = output.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                var seen = new HashSet<string>();
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                        continue;
                    string id = s.GetSessionInstanceIdentifier;
                    seen.Add(id);
                    if (!_sessions.ContainsKey(id))
                        _sessions[id] = s;
                }
                foreach (var gone in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _sessions[gone].Dispose();
                    _sessions.Remove(gone);
                }
            }

            // Several sessions of one app (browser tabs) show as one row.
            return _sessions
                .Select(kv => (kv.Key, s: kv.Value, pid: (int)kv.Value.GetProcessID))
                .Where(x => x.s.IsSystemSoundsSession || x.pid != 0)
                .GroupBy(x => x.s.IsSystemSoundsSession ? "System sounds" : AppName(x.pid))
                .Select(g =>
                {
                    var first = g.First();
                    return new AppVolume(string.Join("|", g.Select(x => x.Key)), g.Key, first.pid,
                        first.s.SimpleAudioVolume.Volume, first.s.SimpleAudioVolume.Mute);
                })
                .OrderBy(a => a.Name == "System sounds")
                .ThenBy(a => a.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Audio sessions failed: {ex.Message}");
            ClearSessions();
            return Array.Empty<AppVolume>();
        }
    }

    public void SetAppVolume(string id, float volume) => ForSessions(id, s => s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1));

    public void SetAppMuted(string id, bool muted) => ForSessions(id, s => s.SimpleAudioVolume.Mute = muted);

    void ForSessions(string id, Action<AudioSessionControl> action)
    {
        foreach (var part in id.Split('|'))
        {
            if (_sessions.TryGetValue(part, out var s))
                Try(() => { action(s); return 0; });
        }
    }

    string AppName(int pid)
    {
        if (_names.TryGetValue(pid, out var cached))
            return cached;
        string name = "App";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 32)
                    name = desc.Trim();
            }
            catch
            {
                // protected process
            }
        }
        catch
        {
            // exited
        }
        return _names[pid] = name;
    }

    // ---------------------------------------------------------------- output switching

    /// <summary>Makes the next output device the default, or the first whose name contains <paramref name="match"/>.</summary>
    public string? SwitchOutput(string? match)
    {
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            if (devices.Count == 0)
                return null;
            string? currentId = Output?.ID;
            MMDevice target;
            if (!string.IsNullOrWhiteSpace(match))
            {
                // Several names separated by "|" toggle between those devices.
                var names = match.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var candidates = devices.Where(d => names.Any(n => d.FriendlyName.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (candidates.Count == 0)
                    return null;
                int at = candidates.FindIndex(d => d.ID == currentId);
                target = candidates[(at + 1) % candidates.Count];
            }
            else
            {
                int at = devices.FindIndex(d => d.ID == currentId);
                target = devices[(at + 1) % devices.Count];
            }

            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { 0, 1, 2 }) // console, multimedia, communications
                policy.SetDefaultEndpoint(target.ID, role);
            Marshal.ReleaseComObject(policy);

            string name = target.FriendlyName;
            foreach (var d in devices)
                d.Dispose();
            RefreshDevices(force: true);
            return name;
        }
        catch (Exception ex)
        {
            Log.Write($"Switching audio output failed: {ex.Message}");
            return null;
        }
    }

    static T? Try<T>(Func<T?> f)
    {
        try { return f(); }
        catch { return default; }
    }

    // Undocumented but stable since Vista; it's what every "switch audio device" tool uses.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(IntPtr a, IntPtr b);
        int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        int ResetDeviceFormat(IntPtr a);
        int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        int SetProcessingPeriod(IntPtr a, IntPtr b);
        int GetShareMode(IntPtr a, IntPtr b);
        int SetShareMode(IntPtr a, IntPtr b);
        int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        int SetEndpointVisibility(IntPtr a, int b);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }
}
