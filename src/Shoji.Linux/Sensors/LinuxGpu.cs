using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Graphics cards. NVIDIA through one long-running nvidia-smi (it samples on its own clock, so nothing is spawned per tick);
/// AMD and Intel straight from /sys/class/drm. Ordered like the Windows build: NVIDIA, other discrete cards, integrated last.
/// </summary>
internal sealed class LinuxGpu : IDisposable
{
    /// <summary>One card as found in sysfs (AMD, Intel, or an NVIDIA card without the proprietary driver).</summary>
    internal sealed record SysCard(string Name, string Vendor, string Driver, string Device, string? Hwmon, bool Integrated);

    /// <summary>One nvidia-smi sample; values are null where the card reports "[N/A]".</summary>
    sealed record SmiRow(int Index, string Name, double? Load, double? Temp, double? Clock, double? MemClock,
                         double? VramUsed, double? VramTotal, double? FanPercent, double? PowerW, string Driver);

    readonly int _intervalMs;
    readonly List<SysCard> _cards;
    readonly bool _hasNvidia;
    readonly SortedDictionary<int, SmiRow> _rows = new();
    readonly object _gate = new();
    readonly CancellationTokenSource _cts = new();
    Process? _smi;

    public LinuxGpu(int intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 10_000);
        var all = FindCards();
        // With the proprietary driver nvidia-smi knows more than sysfs; nouveau cards stay sysfs cards.
        _hasNvidia = all.Any(c => c.Vendor == "0x10de" && c.Driver == "nvidia") || Directory.Exists("/proc/driver/nvidia/gpus");
        _cards = all.Where(c => !(c.Vendor == "0x10de" && c.Driver == "nvidia")).ToList();
    }

    /// <summary>
    /// Starts nvidia-smi and waits briefly for its first sample, so the first snapshot already has the GPU. Stops waiting
    /// as soon as <paramref name="ct"/> (the collector's) is cancelled.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        if (!_hasNvidia)
            return;
        _ = Task.Run(() => RunSmiAsync(_cts.Token));
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && !_cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            lock (_gate)
                if (_rows.Count > 0)
                    break;
            Thread.Sleep(50);
        }
    }

    // ---------------------------------------------------------------- nvidia-smi

    async Task RunSmiAsync(CancellationToken ct)
    {
        // A broken driver install makes nvidia-smi fail at once, forever: retry after 15 s, doubling up to 10 min (a run
        // that delivered data starts over at 15 s), and log the problem at most hourly.
        var retry = TimeSpan.FromSeconds(15);
        void Problem(string key, string message) =>
            Log.WriteThrottled(key, $"{message}; retrying in {(retry.TotalMinutes >= 1 ? $"{retry.TotalMinutes:0} min" : $"{retry.TotalSeconds:0} s")} (logged at most hourly)", TimeSpan.FromHours(1));
        while (!ct.IsCancellationRequested)
        {
            bool gotData = false;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                         {
                             "--query-gpu=index,name,utilization.gpu,temperature.gpu,clocks.gr,clocks.mem,memory.used,memory.total,fan.speed,power.draw,driver_version",
                             "--format=csv,noheader,nounits", "-lms", _intervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         })
                    psi.ArgumentList.Add(a);
                psi.Environment["LC_ALL"] = "C";

                var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("nvidia-smi did not start");
                lock (_gate)
                    _smi = p;
                _ = p.StandardError.ReadToEndAsync(ct); // keep stderr drained
                while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (ParseRow(line) is { } row)
                    {
                        gotData = true;
                        lock (_gate)
                            _rows[row.Index] = row;
                    }
                }
                if (gotData)
                    retry = TimeSpan.FromSeconds(15);
                if (!ct.IsCancellationRequested)
                    Problem("nvidia-smi stopped", $"nvidia-smi stopped (exit {(p.WaitForExit(500) ? p.ExitCode : -1)})");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Problem("nvidia-smi unavailable", $"nvidia-smi unavailable: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    LinuxCli.Kill(_smi);
                    _smi?.Dispose();
                    _smi = null;
                    _rows.Clear();
                }
            }
            try { await Task.Delay(retry, ct); }
            catch (OperationCanceledException) { break; }
            if (!gotData)
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TimeSpan.FromMinutes(10).Ticks));
        }
    }

    static SmiRow? ParseRow(string line)
    {
        var f = line.Split(',').Select(s => s.Trim()).ToArray();
        if (f.Length < 11 || !int.TryParse(f[0], out int index))
            return null;
        // A name with a comma in it would add fields; everything but the name has a fixed position from the end.
        int n = f.Length;
        string name = string.Join(",", f[1..(n - 9)]);
        double? V(int fromEnd) => LinuxCli.ParseDouble(f[n - fromEnd]); // "[N/A]" and "[Not Supported]" parse as null
        return new SmiRow(index, name, V(9), V(8), V(7), V(6), V(5), V(4), V(3), V(2), f[n - 1]);
    }

    // ---------------------------------------------------------------- sysfs cards

    internal static List<SysCard> FindCards()
    {
        var list = new List<SysCard>();
        foreach (var card in LinuxCli.Dirs("/sys/class/drm", "card*").OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal))
        {
            if (!Regex.IsMatch(Path.GetFileName(card), @"^card\d+$"))
                continue; // connectors such as card1-DP-1
            string dev = Path.Combine(card, "device");
            string? vendor = LinuxCli.ReadLine(Path.Combine(dev, "vendor"));
            string? device = LinuxCli.ReadLine(Path.Combine(dev, "device"));
            if (vendor is not ("0x10de" or "0x1002" or "0x8086") || device == null)
                continue;
            string driver = "";
            try { driver = Path.GetFileName(new DirectoryInfo(Path.Combine(dev, "driver")).ResolveLinkTarget(true)?.FullName ?? ""); }
            catch { /* no driver bound */ }
            string? hwmon = LinuxCli.Dirs(Path.Combine(dev, "hwmon"), "hwmon*").FirstOrDefault();

            string name = LinuxCli.ReadLine(Path.Combine(dev, "product_name")) ?? NvidiaModel(dev) ?? PciName(vendor, device) ?? "GPU";
            bool integrated = vendor switch
            {
                "0x8086" => !Regex.IsMatch(name, @"Arc\(TM\) [AB]\d{3}|Arc [AB]\d{3}"), // Arc A770 / B580 are cards
                // APUs: Radeon 780M, "Radeon Graphics", Vega 8, or a small carve-out instead of real VRAM.
                "0x1002" => Regex.IsMatch(name, @"Radeon\(TM\) Graphics|Radeon Graphics|Radeon \d{3}M\b|Vega \d+ Graphics|Renoir|Cezanne|Rembrandt|Phoenix|Raphael|Lucienne|Picasso|Raven|Barcelo|Mendocino|Strix|Krackan|Granite Ridge|Hawk Point",
                                       RegexOptions.IgnoreCase)
                            || LinuxCli.ReadLong(Path.Combine(dev, "mem_info_vram_total")) is long vram && vram <= 2L << 30,
                _ => false,
            };
            list.Add(new SysCard(name, vendor, driver, dev, hwmon, integrated));
        }
        return list;
    }

    /// <summary>The proprietary driver's own name for the card ("NVIDIA RTX A6000"), from /proc without running nvidia-smi.</summary>
    static string? NvidiaModel(string dev)
    {
        try
        {
            string bus = Path.GetFileName(new DirectoryInfo(dev).ResolveLinkTarget(true)?.FullName ?? "");
            var info = LinuxCli.ReadText($"/proc/driver/nvidia/gpus/{bus}/information");
            var m = info == null ? null : Regex.Match(info, @"^Model:\s*(.+)$", RegexOptions.Multiline);
            return m is { Success: true } ? m.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Marketing name from the PCI ID database: "Navi 21 [Radeon RX 6800/6800 XT / 6900 XT]" → "Radeon RX 6800/6800 XT / 6900 XT".
    /// </summary>
    static string? PciName(string vendor, string device)
    {
        string v = vendor[2..].ToLowerInvariant(), d = "\t" + device[2..].ToLowerInvariant() + " ";
        foreach (var file in new[] { "/usr/share/misc/pci.ids", "/usr/share/hwdata/pci.ids", "/usr/share/pci.ids" })
        {
            if (!File.Exists(file))
                continue;
            try
            {
                bool inVendor = false;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    if (line[0] != '\t')
                    {
                        if (inVendor)
                            break;
                        inVendor = line.StartsWith(v + " ", StringComparison.Ordinal);
                    }
                    else if (inVendor && line.StartsWith(d, StringComparison.Ordinal))
                    {
                        string text = line[d.Length..].Trim();
                        var m = Regex.Match(text, @"\[([^\]]+)\]\s*$");
                        string prefix = vendor == "0x8086" ? "Intel " : "";
                        return prefix + (m.Success ? m.Groups[1].Value : text);
                    }
                }
            }
            catch
            {
                // try the next copy
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>All cards in GpuIndex order (NVIDIA, other discrete, integrated), read now.</summary>
    public List<(GpuInfo Info, bool Integrated, string Driver, bool Nvidia)> ReadAll()
    {
        var list = new List<(GpuInfo, bool, string, bool)>();
        lock (_gate)
        {
            foreach (var r in _rows.Values)
                list.Add((new GpuInfo(
                    Name: Clean(r.Name),
                    LoadPercent: r.Load,
                    TemperatureC: r.Temp,
                    HotSpotC: null, // nvidia-smi doesn't expose the hot spot
                    CoreClockMhz: r.Clock,
                    MemoryClockMhz: r.MemClock,
                    VramUsedMb: r.VramUsed,
                    VramTotalMb: r.VramTotal,
                    FanRpm: null,
                    FanPercent: r.FanPercent,
                    PowerW: r.PowerW), false, r.Driver, true));
        }
        foreach (var c in _cards.OrderBy(c => c.Integrated))
            list.Add((ReadCard(c), c.Integrated, c.Driver, false));
        return list;
    }

    static GpuInfo ReadCard(SysCard c)
    {
        string dev = c.Device;
        double? Hw(string file, double scale) =>
            c.Hwmon != null && LinuxCli.ReadLong(Path.Combine(c.Hwmon, file)) is long v ? v / scale : null;

        double? used = LinuxCli.ReadLong(Path.Combine(dev, "mem_info_vram_used")) is long u ? u / 1048576.0 : null;
        double? total = LinuxCli.ReadLong(Path.Combine(dev, "mem_info_vram_total")) is long t ? t / 1048576.0 : null;
        double? pwm = Hw("pwm1", 1);
        double? power = Hw("power1_average", 1e6) ?? Hw("power1_input", 1e6);

        double? clock = DpmMhz(Path.Combine(dev, "pp_dpm_sclk"));
        if (clock == null && c.Vendor == "0x8086")
        {
            // i915 puts the GT clock on the card; xe on the tile.
            string card = Path.GetDirectoryName(dev)!;
            clock = LinuxCli.ReadLong(Path.Combine(card, "gt_cur_freq_mhz"))
                    ?? LinuxCli.ReadLong(Path.Combine(dev, "tile0", "gt0", "freq0", "act_freq"))
                    ?? LinuxCli.ReadLong(Path.Combine(dev, "tile0", "gt0", "freq0", "cur_freq"));
        }

        return new GpuInfo(
            Name: Clean(c.Name),
            LoadPercent: LinuxCli.Plausible(LinuxCli.ReadLong(Path.Combine(dev, "gpu_busy_percent")), 0, 100),
            TemperatureC: LinuxCli.Plausible(Hw("temp1_input", 1000), 1, 130), // edge
            HotSpotC: LinuxCli.Plausible(Hw("temp2_input", 1000), 1, 130),     // junction
            CoreClockMhz: clock,
            MemoryClockMhz: DpmMhz(Path.Combine(dev, "pp_dpm_mclk")),
            VramUsedMb: used,
            VramTotalMb: total,
            FanRpm: LinuxCli.Plausible(Hw("fan1_input", 1), 1, 20_000),
            FanPercent: pwm is double p ? Math.Round(p / 255 * 100) : null,
            PowerW: LinuxCli.Plausible(power, 0.1, 2000));
    }

    /// <summary>The active level of an amdgpu DPM table, e.g. "1: 1800Mhz *".</summary>
    static double? DpmMhz(string path)
    {
        var text = LinuxCli.ReadText(path);
        if (text == null)
            return null;
        foreach (var line in text.Split('\n'))
        {
            if (!line.TrimEnd().EndsWith('*'))
                continue;
            var m = Regex.Match(line, @"(\d+)\s*Mhz", RegexOptions.IgnoreCase);
            if (m.Success)
                return LinuxCli.ParseDouble(m.Groups[1].Value);
        }
        return null;
    }

    static string Clean(string name) => name.Replace("NVIDIA ", "").Replace("AMD ", "").Trim();

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            LinuxCli.Kill(_smi); // the reader task disposes it when its read ends
        }
        // Not waited for: the read ends as soon as the process is gone, and the reader task cleans up after itself.
    }
}
