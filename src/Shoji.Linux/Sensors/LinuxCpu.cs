using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// CPU from procfs / sysfs: load from /proc/stat, clock from cpufreq, temperature from hwmon and package power
/// from RAPL. Everything but RAPL works as a normal user.
/// </summary>
internal sealed class LinuxCpu
{
    public string Name { get; }
    public int? Cores { get; }
    public int Threads { get; }
    public double? BaseMhz { get; }

    readonly string? _tempPath;
    readonly string[] _freqPaths;

    public LinuxCpu()
    {
        (Name, Cores, Threads) = ReadCpuInfo();
        _freqPaths = LinuxCli.Dirs("/sys/devices/system/cpu", "cpu*")
            .Where(d => Path.GetFileName(d)[3..].All(char.IsDigit))
            .Select(d => Path.Combine(d, "cpufreq", "scaling_cur_freq"))
            .Where(File.Exists)
            .ToArray();
        if (LinuxCli.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency") is long baseKhz && baseKhz > 0)
            BaseMhz = baseKhz / 1000.0;
        _tempPath = FindTemperature();
        _rapl = FindRapl();
    }

    // ---------------------------------------------------------------- static facts

    static (string name, int? cores, int threads) ReadCpuInfo()
    {
        string name = "CPU";
        int threads = 0;
        var cores = new HashSet<(string, string)>();
        string physical = "0";
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;
                string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "processor": threads++; break;
                    case "model name" when name == "CPU": name = value; break;
                    case "physical id": physical = value; break;
                    // "core id" comes after "physical id" in each block, so the pair is complete here.
                    case "core id": cores.Add((physical, value)); break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"/proc/cpuinfo unreadable: {ex.Message}");
        }
        if (threads == 0)
            threads = Environment.ProcessorCount;
        return (LinuxCli.CleanCpuName(name), cores.Count > 0 ? cores.Count : null, threads);
    }

    /// <summary>
    /// Path of the best CPU temperature input: Intel package, AMD Tdie/Tctl, or the first core as a last resort; then the SoC
    /// sensor of ARM boards (Raspberry Pi's cpu_thermal); then the thermal zones (x86_pkg_temp, else the ACPI one).
    /// </summary>
    static string? FindTemperature()
    {
        var hwmons = LinuxCli.Dirs("/sys/class/hwmon", "hwmon*").OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => (dir: d, name: LinuxCli.ReadLine(Path.Combine(d, "name")))).ToList();
        foreach (var (hw, name) in hwmons)
        {
            if (name is not ("coretemp" or "k10temp" or "zenpower"))
                continue;
            var inputs = LinuxCli.Files(hw, "temp*_input")
                .Select(f => (file: f, label: LinuxCli.ReadLine(f.Replace("_input", "_label")) ?? ""))
                .ToList();
            // Tdie is the real die temperature; Tctl carries a fan-curve offset on some Ryzen / Threadripper parts.
            foreach (var want in new[] { "Package id 0", "Tdie", "Tctl" })
                if (inputs.FirstOrDefault(i => i.label == want).file is { } f)
                    return f;
            if (inputs.Count > 0)
                return inputs.OrderBy(i => i.file, StringComparer.Ordinal).First().file;
        }
        foreach (var (hw, name) in hwmons)
            if (name is "cpu_thermal" or "soc_thermal" && LinuxCli.Files(hw, "temp*_input").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() is { } f)
                return f;
        var zones = LinuxCli.Dirs("/sys/class/thermal", "thermal_zone*").OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal)
            .Select(d => (dir: d, type: LinuxCli.ReadLine(Path.Combine(d, "type")))).ToList();
        foreach (var want in new[] { "x86_pkg_temp", "cpu_thermal", "cpu-thermal", "soc_thermal", "acpitz" })
            if (zones.FirstOrDefault(z => z.type == want).dir is { } zone && LinuxCli.ReadLong(Path.Combine(zone, "temp")) is long t && t > 0)
                return Path.Combine(zone, "temp");
        return null;
    }

    // ---------------------------------------------------------------- load

    ulong[]? _lastTotal, _lastIdle;

    /// <summary>Total load and per-thread loads (%) since the previous call; null on the first call.</summary>
    public (double? Total, IReadOnlyList<double> Threads) ReadLoad()
    {
        var totals = new List<ulong>();
        var idles = new List<ulong>();
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal))
                    break; // the cpu lines come first
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5)
                    continue;
                // user nice system idle iowait irq softirq steal (guest time is already inside user / nice).
                ulong total = 0;
                for (int i = 1; i < Math.Min(f.Length, 9); i++)
                    total += ulong.TryParse(f[i], out var v) ? v : 0;
                ulong idle = ulong.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture) + (f.Length > 5 && ulong.TryParse(f[5], out var w) ? w : 0);
                totals.Add(total);
                idles.Add(idle);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"/proc/stat unreadable: {ex.Message}");
            return (null, Array.Empty<double>());
        }

        double? overall = null;
        var threads = new List<double>();
        if (_lastTotal != null && _lastIdle != null && _lastTotal.Length == totals.Count)
        {
            for (int i = 0; i < totals.Count; i++)
            {
                double dt = totals[i] - (double)_lastTotal[i];
                double di = idles[i] - (double)_lastIdle[i];
                double load = dt > 0 ? Math.Clamp((dt - di) / dt * 100, 0, 100) : 0;
                if (i == 0)
                    overall = load;
                else
                    threads.Add(load);
            }
        }
        _lastTotal = totals.ToArray();
        _lastIdle = idles.ToArray();
        return (overall, threads);
    }

    // ---------------------------------------------------------------- clock / temperature

    /// <summary>Average current clock of all threads in MHz (scaling_cur_freq is in kHz).</summary>
    public double? ReadClockMhz()
    {
        double sum = 0;
        int n = 0;
        foreach (var path in _freqPaths)
        {
            if (LinuxCli.ReadLong(path) is long khz && khz > 0)
            {
                sum += khz;
                n++;
            }
        }
        return n > 0 ? LinuxCli.Plausible(sum / n / 1000, 100, 10_000) : null;
    }

    public double? ReadTemperature() =>
        _tempPath != null && LinuxCli.ReadLong(_tempPath) is long milli ? LinuxCli.Plausible(milli / 1000.0, 1, 130) : null;

    // ---------------------------------------------------------------- RAPL power

    sealed class RaplDomain
    {
        public required string EnergyPath;
        public long MaxRange;
        public long Last = -1;
    }

    readonly List<RaplDomain> _rapl;
    readonly Stopwatch _raplClock = new();
    DateTime _raplRetryAt;

    /// <summary>The RAPL folder whose energy_uj is unreadable (for the hint), or null.</summary>
    public string? RaplBlocked { get; private set; }

    /// <summary>Package domains ("package-0", one per socket); their sub-domains (core, uncore, dram) are inside them.</summary>
    static List<RaplDomain> FindRapl()
    {
        var list = new List<RaplDomain>();
        foreach (var dir in LinuxCli.Dirs("/sys/class/powercap", "intel-rapl:*").OrderBy(d => d, StringComparer.Ordinal))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.Count(c => c == ':') != 1)
                continue;
            if (LinuxCli.ReadLine(Path.Combine(dir, "name")) is not { } n || !n.StartsWith("package", StringComparison.Ordinal))
                continue;
            list.Add(new RaplDomain
            {
                EnergyPath = Path.Combine(dir, "energy_uj"),
                MaxRange = LinuxCli.ReadLong(Path.Combine(dir, "max_energy_range_uj")) ?? 0,
            });
        }
        return list;
    }

    /// <summary>Package power in W from the energy counter's change since the last call. Null on the first call or without access.</summary>
    public double? ReadPowerW()
    {
        if (_rapl.Count == 0)
            return null;
        // energy_uj is root-only on most distributions; retrying now and then picks up a later chmod.
        if (RaplBlocked != null && DateTime.UtcNow < _raplRetryAt)
            return null;

        double seconds = _raplClock.Elapsed.TotalSeconds;
        _raplClock.Restart();
        double joules = 0;
        bool complete = true;
        foreach (var d in _rapl)
        {
            long now;
            try
            {
                now = LinuxCli.ParseLong(File.ReadAllText(d.EnergyPath).Trim());
            }
            catch (UnauthorizedAccessException)
            {
                RaplBlocked = Path.GetDirectoryName(d.EnergyPath);
                _raplRetryAt = DateTime.UtcNow.AddSeconds(15);
                foreach (var x in _rapl) x.Last = -1;
                return null;
            }
            catch (Exception)
            {
                complete = false;
                d.Last = -1;
                continue;
            }
            RaplBlocked = null;
            if (d.Last < 0)
                complete = false;
            else
            {
                long delta = now - d.Last;
                if (delta < 0)
                    delta += d.MaxRange > 0 ? d.MaxRange : 0; // the counter wrapped around
                joules += Math.Max(0, delta) / 1e6;
            }
            d.Last = now;
        }
        return complete && seconds > 0.05 ? LinuxCli.Plausible(joules / seconds, 0.1, 2000) : null;
    }

    /// <summary>Short reason the temperature or power is missing, or null when both work.</summary>
    public string? Hint(double? temperature, double? power)
    {
        if (temperature == null && _tempPath == null)
            return L.T("not supported");
        if (power == null && RaplBlocked != null)
            return L.F("CPU power: run `sudo chmod o+r {0}/energy_uj` (or see README)", RaplBlocked);
        return null;
    }
}
