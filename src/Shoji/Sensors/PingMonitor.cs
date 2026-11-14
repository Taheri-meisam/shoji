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
                if (OperatingSystem.IsLinux() && _direct)
                {
                    try
                    {
                        ms = await DirectPingAsync(2000, ct);
                    }
                    catch (Exception ex) when (ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied or System.Net.Sockets.SocketError.ProtocolNotSupported or System.Net.Sockets.SocketError.SocketNotSupported or System.Net.Sockets.SocketError.AddressFamilyNotSupported })
                    {
                        _direct = false; // this system doesn't hand out ping sockets: the slow way from here on
                    }
                }
                if (!OperatingSystem.IsLinux() || !_direct)
                {
                    var reply = await ping.SendPingAsync(_host, 2000);
                    if (reply.Status == IPStatus.Success)
                        ms = reply.RoundtripTime;
                }
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

    bool _direct = true;
    System.Net.IPAddress? _address;
    long _resolvedAt;
    ushort _sequence;

    /// <summary>
    /// One echo over a ping socket (Linux lets ordinary programs open one). .NET's own Ping starts the "ping" program
    /// for every single echo there, which is a new process a second; this is one small packet instead.
    /// Null when no reply came in time.
    /// </summary>
    async Task<double?> DirectPingAsync(int timeoutMs, CancellationToken ct)
    {
        if (_address == null || Environment.TickCount64 - _resolvedAt > 60_000)
        {
            _address = System.Net.IPAddress.TryParse(_host, out var literal)
                ? literal
                : (await System.Net.Dns.GetHostAddressesAsync(_host, ct)).OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).First();
            _resolvedAt = Environment.TickCount64;
        }
        bool v6 = _address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        using var socket = new System.Net.Sockets.Socket(_address.AddressFamily, System.Net.Sockets.SocketType.Dgram,
            v6 ? System.Net.Sockets.ProtocolType.IcmpV6 : System.Net.Sockets.ProtocolType.Icmp);
        // Echo request: type, code, checksum, id (both filled in by the system for this kind of socket), sequence, 32 bytes.
        var packet = new byte[8 + 32];
        packet[0] = (byte)(v6 ? 128 : 8);
        ushort sequence = ++_sequence;
        packet[6] = (byte)(sequence >> 8);
        packet[7] = (byte)sequence;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await socket.SendToAsync(packet, System.Net.Sockets.SocketFlags.None, new System.Net.IPEndPoint(_address, 0), timeout.Token);
            var reply = new byte[256];
            while (true)
            {
                int n = await socket.ReceiveAsync(reply, System.Net.Sockets.SocketFlags.None, timeout.Token);
                // Echo reply (0, or 129 for IPv6) to this very request; anything else (an error about another packet) is skipped.
                if (n >= 8 && reply[0] == (v6 ? 129 : 0) && reply[6] == packet[6] && reply[7] == packet[7])
                    return Math.Max(1, Math.Round(clock.Elapsed.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // no reply in time
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable)
        {
            return null;
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
