using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Shoji.Sensors;

/// <summary>Pings a host once a second on its own loop and keeps the last minute of results.</summary>
public sealed class PingMonitor : IDisposable
{
    const int Window = 60;

    readonly string _host;
    readonly CancellationTokenSource _cts = new();
    readonly Queue<double?> _results = new();

    public PingMonitor(string? host)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "1.1.1.1" : host.Trim();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            double? ms = null;
            try
            {
                var reply = await ping.SendPingAsync(_host, 2000);
                if (reply.Status == IPStatus.Success)
                    ms = reply.RoundtripTime;
            }
            catch
            {
                // Name doesn't resolve / no network: counts as a lost packet.
            }

            lock (_results)
            {
                _results.Enqueue(ms);
                while (_results.Count > Window)
                    _results.Dequeue();
            }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public PingInfo Read()
    {
        double?[] r;
        lock (_results)
            r = _results.ToArray();
        if (r.Length == 0)
            return new PingInfo(_host, null, null, null, 0);

        var ok = r.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        double? jitter = ok.Count > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return new PingInfo(_host, r[^1], ok.Count > 0 ? ok.Average() : null, jitter, (r.Length - ok.Count) * 100.0 / r.Length);
    }

    public void Dispose() => _cts.Cancel();
}
