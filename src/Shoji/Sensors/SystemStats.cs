using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>Plain Windows APIs: memory, network, drives, disk activity, battery and per-process CPU. No admin rights needed.</summary>
public sealed class SystemStats : IDisposable
{
    // ---------------------------------------------------------------- memory

    public static MemoryInfo? ReadMemory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m))
            return null;

        const double gb = 1024d * 1024 * 1024;
        double total = m.ullTotalPhys / gb;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / gb;
        double commitTotal = m.ullTotalPageFile / gb;
        double commitUsed = (m.ullTotalPageFile - m.ullAvailPageFile) / gb;
        return new MemoryInfo(used, total, total > 0 ? used / total * 100 : 0, commitUsed, commitTotal);
    }

    // ---------------------------------------------------------------- network

    NetworkInterface? _nic;
    DateTime _nicPickedAt;
    long _lastRx, _lastTx;
    DateTime _lastNetSample;

    public NetworkInfo? ReadNetwork()
    {
        try
        {
            // Re-pick the adapter now and then (Wi-Fi ↔ Ethernet, VPN, cable unplugged…).
            if (_nic == null || DateTime.UtcNow - _nicPickedAt > TimeSpan.FromSeconds(30))
            {
                var picked = PickAdapter();
                if (picked?.Id != _nic?.Id)
                    _lastNetSample = default;
                _nic = picked;
                _nicPickedAt = DateTime.UtcNow;
            }

            if (_nic == null)
                return null;

            var stats = _nic.GetIPStatistics();
            long rx = stats.BytesReceived, tx = stats.BytesSent;
            var now = DateTime.UtcNow;

            double down = 0, up = 0;
            if (_lastNetSample != default)
            {
                double secs = (now - _lastNetSample).TotalSeconds;
                if (secs > 0)
                {
                    down = Math.Max(0, (rx - _lastRx) / secs);
                    up = Math.Max(0, (tx - _lastTx) / secs);
                }
            }

            _lastRx = rx;
            _lastTx = tx;
            _lastNetSample = now;

            string? ip = _nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();

            return new NetworkInfo(_nic.Name, ip, down, up, rx, tx);
        }
        catch (Exception ex)
        {
            Log.Write($"Network read failed: {ex.Message}");
            _nic = null;
            return null;
        }
    }

    /// <summary>The adapter that is up and has a default gateway, preferring the busiest one.</summary>
    static NetworkInterface? PickAdapter()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        return candidates
                   .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                   .OrderByDescending(n => n.GetIPStatistics().BytesReceived)
                   .FirstOrDefault()
               ?? candidates.OrderByDescending(n => n.GetIPStatistics().BytesReceived).FirstOrDefault();
    }

    // ---------------------------------------------------------------- drives

    IReadOnlyList<DiskInfo> _disks = Array.Empty<DiskInfo>();
    DateTime _disksReadAt;

    public IReadOnlyList<DiskInfo> ReadDisks()
    {
        // Free space changes slowly; no need to hit the file system every second.
        if (DateTime.UtcNow - _disksReadAt < TimeSpan.FromSeconds(15))
            return _disks;

        _disksReadAt = DateTime.UtcNow;
        var list = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady)
                    continue;
                const double gb = 1024d * 1024 * 1024;
                list.Add(new DiskInfo(d.Name.TrimEnd('\\'), d.VolumeLabel, (d.TotalSize - d.TotalFreeSpace) / gb, d.TotalSize / gb));
            }
            catch
            {
                // Drive vanished or is locked (BitLocker) – skip it.
            }
        }
        return _disks = list;
    }

    // ---------------------------------------------------------------- disk activity

    PerformanceCounter? _diskRead, _diskWrite;
    bool _diskCountersFailed;

    /// <summary>Total read / write throughput across all physical disks.</summary>
    public DiskIoInfo? ReadDiskIo()
    {
        if (_diskCountersFailed)
            return null;
        try
        {
            if (_diskRead == null || _diskWrite == null)
            {
                _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
                _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            }
            return new DiskIoInfo(_diskRead.NextValue(), _diskWrite.NextValue());
        }
        catch (Exception ex)
        {
            Log.Write($"Disk activity counters unavailable: {ex.Message}");
            _diskCountersFailed = true;
            return null;
        }
    }

    // ---------------------------------------------------------------- battery

    public static BatteryInfo ReadBattery()
    {
        var p = System.Windows.Forms.SystemInformation.PowerStatus;
        bool has = !p.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                   && p.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
        return new BatteryInfo(
            has,
            Math.Clamp(p.BatteryLifePercent * 100, 0, 100),
            p.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging),
            p.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
            p.BatteryLifeRemaining > 0 ? TimeSpan.FromSeconds(p.BatteryLifeRemaining) : null);
    }

    // ---------------------------------------------------------------- processes

    readonly Dictionary<int, TimeSpan> _cpuTimes = new();
    readonly Dictionary<int, ulong> _ioBytes = new();
    // Processes Windows won't open for us (protected / elevated ones): asked once, not every sample.
    readonly HashSet<int> _noIo = new();
    DateTime _lastProcSample;
    IReadOnlyList<ProcessInfo> _topProcesses = Array.Empty<ProcessInfo>();
    ProcessTable? _table;
    long _samples;
    static readonly int OwnPid = Environment.ProcessId;

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The full table from the last <see cref="ReadTopProcesses"/> call made with table: true.</summary>
    public ProcessTable? ProcessTable => _table;

    /// <summary>
    /// Top processes by CPU, grouped by name (so 30 browser processes count as one). With <paramref name="table"/>
    /// (the Task manager tile) it samples every second instead of every two, also reads each process's read + write
    /// traffic, and fills <see cref="ProcessTable"/>.
    /// </summary>
    public IReadOnlyList<ProcessInfo> ReadTopProcesses(int count, bool table = false)
    {
        var now = DateTime.UtcNow;
        // Enumerating every process costs a few ms; twice a second at most is plenty for the list, the graph wants one a second.
        if (now - _lastProcSample < TimeSpan.FromMilliseconds(table ? 900 : 1900))
            return _topProcesses;

        double elapsed = _lastProcSample == default ? 0 : (now - _lastProcSample).TotalSeconds;
        _lastProcSample = now;

        var groups = new ProcessGroups(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0 || p.Id == OwnPid)
                    continue;
                try
                {
                    var cpuTime = p.TotalProcessorTime;
                    seen.Add(p.Id);
                    double cpu = 0;
                    if (elapsed > 0 && _cpuTimes.TryGetValue(p.Id, out var last))
                        cpu = (cpuTime - last).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
                    _cpuTimes[p.Id] = cpuTime;

                    double disk = 0;
                    if (table && ReadIo(p.Id) is ulong io)
                    {
                        if (elapsed > 0 && _ioBytes.TryGetValue(p.Id, out var lastIo) && io >= lastIo)
                            disk = (io - lastIo) / elapsed;
                        _ioBytes[p.Id] = io;
                    }

                    groups.Add(p.ProcessName, cpu, p.WorkingSet64 / 1048576d, disk);
                }
                catch
                {
                    // Protected/system process we can't query, or it exited meanwhile.
                }
            }
        }

        foreach (var pid in _cpuTimes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _cpuTimes.Remove(pid);
            _ioBytes.Remove(pid);
            _noIo.Remove(pid);
        }

        if (elapsed == 0)
            return _topProcesses; // first sample only primes the counters

        groups.Remove("Idle");
        if (table)
            _table = groups.Table(++_samples, DateTime.Now, 8);
        return _topProcesses = groups.Top(count);
    }

    /// <summary>Bytes the process has read and written so far (files, and on Windows also pipes and the network), or null.</summary>
    ulong? ReadIo(int pid)
    {
        if (_noIo.Contains(pid))
            return null;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
        {
            _noIo.Add(pid);
            return null;
        }
        try
        {
            return GetProcessIoCounters(h, out var c) ? c.ReadTransferCount + c.WriteTransferCount : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public void Dispose()
    {
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
    }
}
