using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>Static PC specs for the "system" tile, read once in the background from /etc, /proc and /sys/class/dmi.</summary>
internal static class LinuxSystemInfo
{
    static Task<SystemInfo>? _task;

    /// <summary>The specs, or null while they are still being read.</summary>
    public static SystemInfo? Current
    {
        get
        {
            _task ??= Task.Run(Read);
            return _task.IsCompletedSuccessfully ? _task.Result : null;
        }
    }

    static SystemInfo Read()
    {
        var cpu = new LinuxCpu();
        string cores = cpu.Cores is int c ? L.F("{0} cores · {1} threads", c, cpu.Threads) : L.F("{0} threads", cpu.Threads);
        if (cpu.BaseMhz is double mhz)
            cores += L.F(" · {0:0.0} GHz base", mhz / 1000);
        var (gpu, driver) = ReadGpu();

        return new SystemInfo(
            Os: ReadOsName(),
            OsBuild: LinuxCli.ReadLine("/proc/sys/kernel/osrelease") ?? Environment.OSVersion.Version.ToString(),
            Host: LinuxCli.ReadLine("/proc/sys/kernel/hostname") ?? Environment.MachineName,
            Cpu: cpu.Name,
            CpuCores: cores,
            Gpu: gpu,
            GpuDriver: driver,
            Ram: ReadRam(),
            Board: Dmi("board_vendor", "board_name"),
            Bios: Dmi("bios_vendor", "bios_version", "bios_date"),
            BootTime: ReadBootTime(),
            Displays: "");
    }

    static string ReadOsName()
    {
        try
        {
            foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
            {
                if (!File.Exists(path))
                    continue;
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                        return line[12..].Trim().Trim('"', '\'');
            }
        }
        catch
        {
            // fall back below
        }
        return "Linux";
    }

    /// <summary>The card the dashboard shows first (NVIDIA, other discrete, integrated) and its driver version.</summary>
    static (string name, string driver) ReadGpu()
    {
        var card = LinuxGpu.FindCards()
            .OrderBy(c => c.Integrated ? 2 : c.Vendor == "0x10de" ? 0 : 1)
            .FirstOrDefault();
        if (card == null)
            return ("–", "");
        // The NVIDIA module carries its version; in-kernel drivers (amdgpu, i915) are versioned with the kernel.
        string driver = card.Driver.Length == 0 ? ""
            : LinuxCli.ReadLine($"/sys/module/{card.Driver}/version") ?? $"{card.Driver} {LinuxCli.ReadLine("/proc/sys/kernel/osrelease")}".Trim();
        return (card.Name.Replace("NVIDIA ", "").Replace("AMD ", "").Trim(), driver);
    }

    /// <summary>
    /// Installed RAM. The module list (type, speed) is root-only on Linux, and MemTotal is a little less than what is installed
    /// (firmware and kernel reservations), so it's rounded up to the size the modules most likely add up to.
    /// </summary>
    static string ReadRam()
    {
        var m = LinuxStats.ReadMemInfo();
        if (!m.TryGetValue("MemTotal", out long kb) || kb <= 0)
            return "–";
        double gb = kb / 1048576.0;
        double step = gb <= 16 ? 2 : gb <= 64 ? 4 : 8;
        return $"{Math.Ceiling(gb / step) * step:0} GB";
    }

    static string Dmi(params string[] fields)
    {
        var parts = fields
            .Select(f => LinuxCli.ReadLine($"/sys/class/dmi/id/{f}"))
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "To Be Filled By O.E.M." && s != "Default string" && s != "System Product Name")
            .ToList();
        return parts.Count == 0 ? "–" : string.Join(" ", parts);
    }

    static DateTime ReadBootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
                if (line.StartsWith("btime ", StringComparison.Ordinal))
                    return DateTimeOffset.FromUnixTimeSeconds(LinuxCli.ParseLong(line[6..].Trim())).LocalDateTime;
        }
        catch (Exception ex)
        {
            Log.Write($"Boot time unreadable: {ex.Message}");
        }
        return DateTime.Now - LinuxStats.ReadUptime();
    }
}
