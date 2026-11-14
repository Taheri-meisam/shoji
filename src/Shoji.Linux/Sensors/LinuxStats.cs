using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>The Linux twin of SystemStats: memory, network, drives, disk activity, battery and per-process CPU, all from procfs / sysfs.</summary>
internal sealed class LinuxStats
{
    const double Gb = 1024d * 1024 * 1024;

    // ---------------------------------------------------------------- memory

    /// <summary>/proc/meminfo as kB values.</summary>
    public static Dictionary<string, long> ReadMemInfo()
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var line in LinuxCli.ReadLines("/proc/meminfo"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var value = line[(colon + 1)..].Trim().Split(' ')[0];
                d[line[..colon]] = LinuxCli.ParseLong(value);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"/proc/meminfo unreadable: {ex.Message}");
        }
        return d;
    }

    public static MemoryInfo? ReadMemory()
    {
        var m = ReadMemInfo();
        if (!m.TryGetValue("MemTotal", out long totalKb) || totalKb <= 0)
            return null;
        long availKb = m.TryGetValue("MemAvailable", out var a) ? a : m.GetValueOrDefault("MemFree");
        double total = totalKb * 1024 / Gb;
        double used = (totalKb - availKb) * 1024 / Gb;

        // CommitLimit is only a real limit with strict overcommit (mode 2). In the default heuristic mode it's half the RAM
        // plus swap and the commit charge routinely exceeds it, so RAM + swap (Windows' "RAM + page file") is the fairer total.
        // Committed_AS counts promises, not use (it often passes RAM + swap), so outside strict mode show what is really
        // in use: RAM plus swap, out of RAM plus swap.
        bool strict = LinuxCli.ReadLine("/proc/sys/vm/overcommit_memory") == "2" && m.ContainsKey("CommitLimit");
        long swapTotal = m.GetValueOrDefault("SwapTotal"), swapUsed = Math.Max(0, swapTotal - m.GetValueOrDefault("SwapFree"));
        long commitUsedKb = strict ? m.GetValueOrDefault("Committed_AS") : totalKb - availKb + swapUsed;
        long commitTotalKb = strict ? m["CommitLimit"] : totalKb + swapTotal;
        return new MemoryInfo(used, total, used / total * 100, commitUsedKb * 1024 / Gb, commitTotalKb * 1024 / Gb);
    }

    // ---------------------------------------------------------------- network

    string? _iface;
    DateTime _ifacePickedAt;
    long _lastRx, _lastTx, _baseRx = -1, _baseTx = -1;
    DateTime _lastNetSample;
    string? _ip;

    public NetworkInfo? ReadNetwork()
    {
        try
        {
            // Re-pick the interface now and then (Wi-Fi ↔ Ethernet, VPN, cable unplugged…).
            if (_iface == null || DateTime.UtcNow - _ifacePickedAt > TimeSpan.FromSeconds(30))
            {
                var picked = PickInterface();
                if (picked != _iface)
                {
                    _lastNetSample = default;
                    _baseRx = _baseTx = -1;
                }
                _iface = picked;
                _ifacePickedAt = DateTime.UtcNow;
                _ip = _iface == null ? null : ReadIpv4(_iface);
            }
            if (_iface == null)
                return null;

            var counters = ReadNetDev();
            if (!counters.TryGetValue(_iface, out var c))
            {
                _iface = null;
                return null;
            }

            var now = DateTime.UtcNow;
            double down = 0, up = 0;
            if (_lastNetSample != default)
            {
                double secs = (now - _lastNetSample).TotalSeconds;
                if (secs > 0)
                {
                    down = Math.Max(0, (c.Rx - _lastRx) / secs);
                    up = Math.Max(0, (c.Tx - _lastTx) / secs);
                }
            }
            if (_baseRx < 0 || c.Rx < _baseRx || c.Tx < _baseTx)
                (_baseRx, _baseTx) = (c.Rx, c.Tx); // first sample, or the interface was reset
            _lastRx = c.Rx;
            _lastTx = c.Tx;
            _lastNetSample = now;

            return new NetworkInfo(FriendlyName(_iface), _ip, down, up, c.Rx - _baseRx, c.Tx - _baseTx);
        }
        catch (Exception ex)
        {
            Log.Write($"Network read failed: {ex.Message}");
            _iface = null;
            return null;
        }
    }

    static Dictionary<string, (long Rx, long Tx)> ReadNetDev()
    {
        var d = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        foreach (var line in LinuxCli.ReadLines("/proc/net/dev").Skip(2))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 9)
                d[line[..colon].Trim()] = (LinuxCli.ParseLong(f[0]), LinuxCli.ParseLong(f[8]));
        }
        return d;
    }

    /// <summary>The interface of the default route with the lowest metric; else the busiest interface that is up.</summary>
    static string? PickInterface()
    {
        string? best = null;
        long bestMetric = long.MaxValue;
        try
        {
            foreach (var line in LinuxCli.ReadLines("/proc/net/route").Skip(1))
            {
                var f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Destination 0.0.0.0 with mask 0.0.0.0 is the default route.
                if (f.Length >= 8 && f[1] == "00000000" && f[7] == "00000000")
                {
                    long metric = LinuxCli.ParseLong(f[6]);
                    if (metric < bestMetric)
                        (best, bestMetric) = (f[0], metric);
                }
            }
        }
        catch
        {
            // no IPv4 routing table (IPv6-only); fall through
        }
        if (best != null)
            return best;

        return ReadNetDev()
            .Where(kv => kv.Key != "lo" && LinuxCli.ReadLine($"/sys/class/net/{kv.Key}/operstate") == "up"
                         && Directory.Exists($"/sys/class/net/{kv.Key}/device")) // physical, not docker / veth bridges
            .OrderByDescending(kv => kv.Value.Rx)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    static string? ReadIpv4(string iface)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == iface)?
                .GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"Ethernet" / "Wi-Fi" like the Windows adapter names; anything else (VPN, bridge) keeps its interface name.</summary>
    static string FriendlyName(string iface)
    {
        if (Directory.Exists($"/sys/class/net/{iface}/wireless") || Directory.Exists($"/sys/class/net/{iface}/phy80211"))
            return "Wi-Fi";
        if (Directory.Exists($"/sys/class/net/{iface}/device") && LinuxCli.ReadLine($"/sys/class/net/{iface}/type") == "1")
            return "Ethernet";
        return iface;
    }

    // ---------------------------------------------------------------- drives

    IReadOnlyList<DiskInfo> _disks = Array.Empty<DiskInfo>();
    DateTime _disksReadAt;

    // Virtual, pseudo and read-only image file systems: nothing a user stores files on.
    static readonly HashSet<string> SkipFs = new(StringComparer.Ordinal)
    {
        "tmpfs", "devtmpfs", "ramfs", "overlay", "squashfs", "efivarfs", "proc", "sysfs", "cgroup", "cgroup2", "devpts",
        "securityfs", "pstore", "bpf", "debugfs", "tracefs", "configfs", "fusectl", "hugetlbfs", "mqueue", "autofs",
        "binfmt_misc", "nsfs", "iso9660", "udf", "fuse.portal", "fuse.gvfsd-fuse", "fuse.snapfuse", "erofs", "zram",
    };

    public IReadOnlyList<DiskInfo> ReadDisks()
    {
        // Free space changes slowly; no need to hit the file system every second.
        if (DateTime.UtcNow - _disksReadAt < TimeSpan.FromSeconds(15))
            return _disks;
        _disksReadAt = DateTime.UtcNow;

        var byDevice = new Dictionary<string, (string Mount, string Fs)>(StringComparer.Ordinal);
        try
        {
            foreach (var line in LinuxCli.ReadLines("/proc/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length < 3)
                    continue;
                string dev = f[0], mount = Unescape(f[1]), fs = f[2];
                // ZFS datasets have no /dev node ("rpool/ROOT/ubuntu"); the datasets of one pool share its space, so the
                // pool is shown once like a btrfs volume with subvolumes.
                bool zfs = fs == "zfs";
                if (zfs)
                    dev = "zfs:" + Unescape(dev).Split('/')[0];
                else if (SkipFs.Contains(fs) || !dev.StartsWith("/dev/", StringComparison.Ordinal) || dev.StartsWith("/dev/loop", StringComparison.Ordinal))
                    continue;
                if (mount.StartsWith("/snap/", StringComparison.Ordinal) || mount.StartsWith("/var/snap/", StringComparison.Ordinal)
                    || mount == "/boot" || mount.StartsWith("/boot/", StringComparison.Ordinal) || mount == "/efi"
                    || mount.StartsWith("/proc", StringComparison.Ordinal) || mount.StartsWith("/sys", StringComparison.Ordinal)
                    || mount.StartsWith("/run/", StringComparison.Ordinal) && !mount.StartsWith("/run/media/", StringComparison.Ordinal))
                    continue;
                if (!zfs && IsRemovable(dev))
                    continue; // USB sticks; Windows only lists fixed drives too
                // A device mounted several times (bind mounts, btrfs subvolumes) is shown once, at its shortest path.
                if (!byDevice.TryGetValue(dev, out var seen) || mount.Length < seen.Mount.Length)
                    byDevice[dev] = (mount, fs);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"/proc/mounts unreadable: {ex.Message}");
        }

        var list = new List<DiskInfo>();
        foreach (var (mount, _) in byDevice.Values.OrderBy(v => v.Mount == "/" ? 0 : 1).ThenBy(v => v.Mount, StringComparer.Ordinal))
        {
            try
            {
                var d = new DriveInfo(mount);
                if (d.TotalSize <= 0)
                    continue;
                list.Add(new DiskInfo(mount, Label(mount), (d.TotalSize - d.TotalFreeSpace) / Gb, d.TotalSize / Gb));
            }
            catch
            {
                // unmounted meanwhile, or no permission to stat it
            }
        }
        return _disks = list;
    }

    /// <summary>"/" → "System", "/home" → "Home", "/media/me/Backup" → "Backup".</summary>
    static string Label(string mount)
    {
        if (mount == "/")
            return L.T("System");
        string last = mount.TrimEnd('/').Split('/').Last();
        return mount == "/home" ? L.T("Home") : last;
    }

    /// <summary>/proc/mounts writes spaces and tabs as octal escapes ("\040").</summary>
    static string Unescape(string s) =>
        s.Contains('\\') ? System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()) : s;

    static bool IsRemovable(string dev)
    {
        try
        {
            // /dev/sdb1 → /sys/class/block/sdb1 → its parent disk's "removable" flag. Device-mapper / md have none.
            string name = Path.GetFileName(new FileInfo(dev).ResolveLinkTarget(true)?.FullName ?? dev);
            string sys = $"/sys/class/block/{name}";
            if (!Directory.Exists(sys))
                return false;
            string? disk = File.Exists(Path.Combine(sys, "partition"))
                ? Path.GetFileName(Path.GetDirectoryName(new DirectoryInfo(sys).ResolveLinkTarget(true)?.FullName ?? ""))
                : name;
            return disk != null && LinuxCli.ReadLine($"/sys/block/{disk}/removable") == "1";
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- disk activity

    long _lastRead = -1, _lastWritten = -1;
    DateTime _lastIoSample;
    string[]? _physicalDisks;

    /// <summary>Read / write throughput summed over whole physical disks (partitions, LVM and loop devices would count twice).</summary>
    public DiskIoInfo? ReadDiskIo()
    {
        try
        {
            _physicalDisks ??= LinuxCli.Dirs("/sys/block", "*")
                .Where(d => Directory.Exists(Path.Combine(d, "device")))
                .Select(Path.GetFileName).OfType<string>().ToArray();
            var disks = _physicalDisks.ToHashSet(StringComparer.Ordinal);
            long read = 0, written = 0;
            foreach (var line in LinuxCli.ReadLines("/proc/diskstats"))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // major minor name reads merged sectors_read ms writes merged sectors_written …
                if (f.Length >= 10 && disks.Contains(f[2]))
                {
                    read += LinuxCli.ParseLong(f[5]) * 512;
                    written += LinuxCli.ParseLong(f[9]) * 512;
                }
            }
            var now = DateTime.UtcNow;
            DiskIoInfo result = new(0, 0);
            if (_lastRead >= 0 && now > _lastIoSample)
            {
                double secs = (now - _lastIoSample).TotalSeconds;
                result = new DiskIoInfo(Math.Max(0, (read - _lastRead) / secs), Math.Max(0, (written - _lastWritten) / secs));
            }
            (_lastRead, _lastWritten, _lastIoSample) = (read, written, now);
            return result;
        }
        catch (Exception ex)
        {
            Log.Write($"Disk activity unavailable: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- battery

    public static BatteryInfo ReadBattery()
    {
        var bats = LinuxCli.Dirs("/sys/class/power_supply", "BAT*")
            .Where(b => LinuxCli.ReadLine(Path.Combine(b, "present")) != "0")
            .ToList();
        // A charger: the AC adapter ("Mains"), or a USB-C / USB power source ("USB", e.g. ucsi-source-psy-*) that is online.
        bool plugged = LinuxCli.Dirs("/sys/class/power_supply", "*")
            .Any(d => LinuxCli.ReadLine(Path.Combine(d, "type")) is "Mains" or "USB" && LinuxCli.ReadLine(Path.Combine(d, "online")) == "1");
        if (bats.Count == 0)
            return new BatteryInfo(false, 0, false, plugged, null);

        // Several batteries (some ThinkPads): add their energy up.
        double now = 0, full = 0, rate = 0, percentSum = 0;
        bool charging = false, discharging = false, haveEnergy = true;
        foreach (var b in bats)
        {
            long? Num(string file) => LinuxCli.ReadLong(Path.Combine(b, file));
            string status = LinuxCli.ReadLine(Path.Combine(b, "status")) ?? "";
            charging |= status == "Charging";
            discharging |= status == "Discharging";
            percentSum += Num("capacity") ?? 0;
            // energy_* is µWh with power_now in µW; charge_* is µAh with current_now in µA. Either ratio gives hours.
            long? n = Num("energy_now") ?? Num("charge_now");
            long? f = Num("energy_full") ?? Num("charge_full");
            long? r = Num("power_now") ?? Num("current_now");
            if (n == null || f == null)
                haveEnergy = false;
            now += n ?? 0;
            full += f ?? 0;
            rate += Math.Abs(r ?? 0);
        }

        double percent = haveEnergy && full > 0 ? now / full * 100 : percentSum / bats.Count;
        TimeSpan? remaining = null;
        if (haveEnergy && rate > 0)
        {
            double hours = discharging ? now / rate : charging ? (full - now) / rate : 0;
            if (hours > 0 && hours < 48)
                remaining = TimeSpan.FromHours(hours);
        }
        return new BatteryInfo(true, Math.Clamp(percent, 0, 100), charging, plugged || charging, remaining);
    }

    // ---------------------------------------------------------------- processes

    readonly Dictionary<int, long> _cpuTicks = new();
    readonly Dictionary<int, long> _ioBytes = new();
    // Processes whose /proc/<pid>/io can't be read (another user's, without root): tried once, not every sample.
    readonly HashSet<int> _noIo = new();
    DateTime _lastProcSample;
    IReadOnlyList<ProcessInfo> _topProcesses = Array.Empty<ProcessInfo>();
    ProcessTable? _table;
    long _samples;
    static readonly int OwnPid = Environment.ProcessId;
    static readonly double TicksPerSecond = ClockTicks();
    static readonly long PageSize = Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "sysconf")]
    static extern long SysConf(int name);

    static double ClockTicks()
    {
        try
        {
            long t = SysConf(2); // _SC_CLK_TCK
            return t > 0 ? t : 100;
        }
        catch
        {
            return 100; // USER_HZ is 100 on every mainstream architecture
        }
    }

    /// <summary>The full table from the last <see cref="ReadTopProcesses"/> call made with table: true.</summary>
    public ProcessTable? ProcessTable => _table;

    /// <summary>
    /// Top processes by CPU, grouped by name (so 30 browser processes count as one), CPU in % of the whole machine.
    /// With <paramref name="table"/> (the Task manager tile) it samples every second instead of every two, also reads
    /// each process's disk traffic, and fills <see cref="ProcessTable"/>.
    /// </summary>
    public IReadOnlyList<ProcessInfo> ReadTopProcesses(int count, bool table = false)
    {
        var now = DateTime.UtcNow;
        // Walking /proc costs a few ms; every two seconds is plenty for the list (same pace as Windows), the graph wants one a second.
        if (now - _lastProcSample < TimeSpan.FromMilliseconds(table ? 900 : 1900))
            return _topProcesses;

        double elapsed = _lastProcSample == default ? 0 : (now - _lastProcSample).TotalSeconds;
        _lastProcSample = now;

        var groups = new ProcessGroups(StringComparer.Ordinal);
        var seen = new HashSet<int>();
        int threads = Environment.ProcessorCount;

        foreach (var dir in LinuxCli.Dirs("/proc", "*"))
        {
            var leaf = dir.AsSpan(dir.LastIndexOf('/') + 1);
            if (leaf.Length == 0 || !char.IsAsciiDigit(leaf[0]) || !int.TryParse(leaf, out int pid) || pid == OwnPid)
                continue;
            try
            {
                if (LinuxCli.ReadText(dir + "/stat") is not { } stat)
                    continue; // exited meanwhile
                // "pid (comm) state ppid …": comm may contain spaces and parentheses, so split after the last ')'.
                int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
                if (open < 0 || close < open || close + 2 >= stat.Length)
                    continue;
                // Fields after the comm start at "state" (field 3): flags = 9, utime = 14, stime = 15, rss (pages) = 24.
                // Walked in place: this runs for every process, every second or two.
                long flags = 0, ticks = 0, rssPages = 0;
                int field = 0;
                var rest = stat.AsSpan(close + 2);
                while (rest.Length > 0 && field <= 21)
                {
                    int space = rest.IndexOf(' ');
                    var value = space < 0 ? rest : rest[..space];
                    if (field == 6)
                        long.TryParse(value, out flags);
                    else if (field is 11 or 12 && long.TryParse(value, out long t))
                        ticks += t;
                    else if (field == 21)
                        long.TryParse(value, out rssPages);
                    field++;
                    rest = space < 0 ? default : rest[(space + 1)..];
                }
                if (field < 13)
                    continue;
                bool kernelThread = (flags & 0x00200000) != 0; // PF_KTHREAD
                seen.Add(pid);

                double cpu = 0;
                bool known = _cpuTicks.TryGetValue(pid, out var last);
                if (elapsed > 0 && known)
                    cpu = (ticks - last) / TicksPerSecond / elapsed / threads * 100;
                // A process that used no processor time since the last look made no reads or writes either.
                bool idle = known && ticks == last;
                _cpuTicks[pid] = ticks;

                double rssMb = kernelThread ? 0 : rssPages * PageSize / 1048576d;

                double disk = 0;
                if (table && !kernelThread && !(idle && _ioBytes.ContainsKey(pid)) && ReadIo(dir, pid) is long io)
                {
                    if (elapsed > 0 && _ioBytes.TryGetValue(pid, out var lastIo))
                        disk = Math.Max(0, io - lastIo) / elapsed;
                    _ioBytes[pid] = io;
                }

                var comm = stat.AsSpan(open + 1, close - open - 1);
                string name = ProcessName(dir, pid, comm, kernelThread);
                groups.Add(name, cpu, rssMb, disk);
            }
            catch
            {
                // exited meanwhile
            }
        }

        foreach (var pid in _cpuTicks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _cpuTicks.Remove(pid);
            _ioBytes.Remove(pid);
            _names.Remove(pid);
            _noIo.Remove(pid);
        }

        if (elapsed == 0)
            return _topProcesses; // first sample only primes the counters

        if (table)
            _table = groups.Table(++_samples, DateTime.Now, 8);
        return _topProcesses = groups.Top(count);
    }

    /// <summary>Bytes the process has read from and written to storage so far, or null when it isn't ours to read.</summary>
    long? ReadIo(string dir, int pid)
    {
        if (_noIo.Contains(pid))
            return null;
        try
        {
            long read = 0, write = 0;
            if (LinuxCli.ReadText(dir + "/io") is not { } io)
            {
                _noIo.Add(pid);
                return null;
            }
            foreach (var line in io.Split('\n'))
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                    read = LinuxCli.ParseLong(line[11..].Trim());
                else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    write = LinuxCli.ParseLong(line[12..].Trim());
            }
            return read + write;
        }
        catch
        {
            _noIo.Add(pid);
            return null;
        }
    }

    /// <summary>"kworker/u64:2" and friends become one "kworker" row.</summary>
    static string KernelName(string comm)
    {
        int slash = comm.IndexOf('/');
        return slash > 0 ? comm[..slash] : comm;
    }

    readonly Dictionary<int, (string Comm, string Name)> _names = new();

    /// <summary>
    /// The name a process is listed under, worked out once per process: the kernel cuts names to 15 characters (the
    /// program file name from the command line is used then), and browser / Electron helpers ("Isolated Web Co",
    /// "Web Content", "RDD Process", chrome --type=renderer) count as the program that started them.
    /// </summary>
    string ProcessName(string dir, int pid, ReadOnlySpan<char> commNow, bool kernelThread)
    {
        if (_names.TryGetValue(pid, out var cached) && commNow.SequenceEqual(cached.Comm))
            return cached.Name;
        string comm = commNow.ToString();
        if (kernelThread)
        {
            _names[pid] = (comm, KernelName(comm));
            return _names[pid].Name;
        }
        string name = comm;
        try
        {
            var cmd = LinuxCli.ReadText(dir + "/cmdline") ?? "";
            int nul = cmd.IndexOf('\0');
            string argv0 = nul >= 0 ? cmd[..nul] : cmd;
            // Firefox rewrites its command line with spaces between the arguments, so the helper flag can follow either.
            bool helper = cmd.Contains("-contentproc", StringComparison.Ordinal) || cmd.Contains("--type=", StringComparison.Ordinal);
            if (helper && argv0.IndexOf(' ') is int space and > 0)
                argv0 = argv0[..space];
            string exe = Path.GetFileName(argv0);
            if (exe.Length > 0 && !exe.Contains(' ') && (helper || comm.Length >= 15 && exe.StartsWith(comm, StringComparison.Ordinal)))
                name = exe;
        }
        catch
        {
            // keep the kernel's name
        }
        _names[pid] = (comm, name);
        return name;
    }

    // ---------------------------------------------------------------- uptime

    public static TimeSpan ReadUptime() =>
        LinuxCli.ParseDouble(LinuxCli.ReadLine("/proc/uptime")?.Split(' ')[0]) is double s
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMilliseconds(Environment.TickCount64);
}
