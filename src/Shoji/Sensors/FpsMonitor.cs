using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Shoji.Core;

namespace Shoji.Sensors;


/// <summary>
/// Frame rate of any app, measured like PresentMon does it: by listening to the "Present" events
/// DirectX 9/10/11/12 raise through Windows event tracing. Needs admin rights; no overlay or injection,
/// so it's anti-cheat safe. (Vulkan / OpenGL games that don't present through DXGI aren't seen.)
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const string SessionName = "Shoji-FPS";
    const double WindowMs = 10_000;

    readonly TraceEventSession? _session;
    readonly object _gate = new();
    readonly Dictionary<int, Queue<double>> _frames = new();
    readonly Dictionary<int, long> _lastSeen = new();
    public string? Error { get; }

    public FpsMonitor()
    {
        if (!Startup.IsElevated)
        {
            Error = "needs admin";
            return;
        }
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.AllEvents += OnEvent;
            new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Log.Write($"FPS trace stopped: {ex.Message}"); }
            }) { IsBackground = true, Name = "FPS trace" }.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"FPS counter unavailable: {ex}");
            Error = "unavailable";
            _session?.Dispose();
            _session = null;
        }
    }

    void OnEvent(TraceEvent e)
    {
        // DXGI Present_Start (42) and PresentMultiplaneOverlay_Start (55); D3D9 Present_Start (1).
        int id = (int)e.ID;
        bool present = e.ProviderGuid == Dxgi ? id is 42 or 55 : e.ProviderGuid == D3D9 && id == 1;
        if (!present)
            return;

        double t = e.TimeStampRelativeMSec;
        int pid = e.ProcessID;
        lock (_gate)
        {
            if (!_frames.TryGetValue(pid, out var q))
                _frames[pid] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > WindowMs)
                q.Dequeue();
            _lastSeen[pid] = Environment.TickCount64;

            if (_frames.Count > 64)
            {
                foreach (var stale in _lastSeen.Where(kv => Environment.TickCount64 - kv.Value > 30_000).Select(kv => kv.Key).ToList())
                {
                    _frames.Remove(stale);
                    _lastSeen.Remove(stale);
                }
            }
        }
    }

    /// <summary>Frame stats for one process (normally the one in focus).</summary>
    public FpsInfo Read(int pid, string app)
    {
        if (Error != null)
            return new FpsInfo(app, null, null, null, Error);

        double[] t;
        lock (_gate)
        {
            if (pid == 0 || !_frames.TryGetValue(pid, out var q) || Environment.TickCount64 - _lastSeen[pid] > 1500)
                return new FpsInfo(app, null, null, null, null);
            t = q.ToArray();
        }
        if (t.Length < 3)
            return new FpsInfo(app, null, null, null, null);

        double last = t[^1];
        int lastSecond = t.Count(x => last - x <= 1000);
        var frameTimes = new double[t.Length - 1];
        for (int i = 1; i < t.Length; i++)
            frameTimes[i - 1] = t[i] - t[i - 1];
        Array.Sort(frameTimes);
        double p99 = frameTimes[(int)Math.Min(frameTimes.Length - 1, Math.Floor(frameTimes.Length * 0.99))];
        double recent = t[^1] - t[^2];
        return new FpsInfo(app, lastSecond, p99 > 0 ? 1000 / p99 : null, recent, null);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* shutting down */ }
    }
}
