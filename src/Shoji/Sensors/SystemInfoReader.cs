using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>Static PC specs for the "system" module. Read once in the background (WMI is slow).</summary>
public static class SystemInfoReader
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
        var (os, build) = ReadOs();
        var (gpu, driver) = ReadGpu();
        var cpu = new WindowsCpu();
        string cores = cpu.Cores is int c ? L.F("{0} cores · {1} threads", c, cpu.Threads) : L.F("{0} threads", cpu.Threads);
        if (cpu.BaseMhz is double mhz)
            cores += " · " + L.F("{0:0.0} GHz base", mhz / 1000);
        cpu.Dispose();

        var screens = System.Windows.Forms.Screen.AllScreens;
        string displays = string.Join(", ", screens.Select(s => $"{s.Bounds.Width}×{s.Bounds.Height}"));

        return new SystemInfo(
            Os: os,
            OsBuild: build,
            Host: Environment.MachineName,
            Cpu: cpu.Name,
            CpuCores: cores,
            Gpu: gpu,
            GpuDriver: driver,
            Ram: ReadRam(),
            Board: Join(Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", "Manufacturer", "Product")),
            Bios: Join(Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion")),
            BootTime: DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            Displays: $"{screens.Length} · {displays}");
    }

    static (string os, string build) ReadOs()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string version = key?.GetValue("DisplayVersion") as string ?? "";
            string buildNo = key?.GetValue("CurrentBuildNumber") as string ?? "";
            // Windows 11 still says "Windows 10" in ProductName; the build number tells the truth.
            if (int.TryParse(buildNo, out int b) && b >= 22000)
                product = product.Replace("Windows 10", "Windows 11");
            string ubr = key?.GetValue("UBR") is int u ? "." + u : "";
            return ($"{product} {version}".Trim(), buildNo + ubr);
        }
        catch
        {
            return (Environment.OSVersion.VersionString, "");
        }
    }

    static (string name, string driver) ReadGpu()
    {
        var all = Wmi("SELECT Name, DriverVersion FROM Win32_VideoController", "Name", "DriverVersion");
        // Prefer a real GPU over virtual / remote-desktop adapters.
        var best = all
            .Where(r => r.Length == 2 && !string.IsNullOrEmpty(r[0]))
            .OrderBy(r => r[0].Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? 0
                        : r[0].Contains("Radeon", StringComparison.OrdinalIgnoreCase) || r[0].Contains("AMD", StringComparison.OrdinalIgnoreCase) ? 1
                        : r[0].Contains("Intel", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .FirstOrDefault();
        return best == null ? ("–", "") : (best[0].Replace("NVIDIA ", ""), best[1]);
    }

    static string ReadRam()
    {
        var modules = Wmi("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
                          "Capacity", "ConfiguredClockSpeed", "Speed", "SMBIOSMemoryType");
        if (modules.Count == 0)
            return "–";

        double gb = modules.Sum(m => double.TryParse(m[0], out var c) ? c : 0) / (1024d * 1024 * 1024);
        string speed = modules.Select(m => m[1] is { Length: > 0 } s && s != "0" ? s : m[2]).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
        string type = modules[0][3] switch
        {
            "24" => "DDR3",
            "26" => "DDR4",
            "30" => "LPDDR4",
            "34" => "DDR5",
            "35" => "LPDDR5",
            _ => "",
        };
        double each = gb / modules.Count;
        string kit = modules.Count > 1 ? $" ({modules.Count}×{each:0})" : "";
        string kind = (type, speed) switch
        {
            ("", "") => "",
            ("", _) => $" {speed} MT/s",
            (_, "") => " " + type,
            _ => $" {type}-{speed}",
        };
        return $"{gb:0} GB{kind}{kit}";
    }

    static string Join(List<string[]> rows) =>
        rows.Count == 0 ? "–" : string.Join(" ", rows[0].Where(s => !string.IsNullOrWhiteSpace(s) && s != "To Be Filled By O.E.M.")).Trim();

    static List<string[]> Wmi(string query, params string[] fields)
    {
        var rows = new List<string[]>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var o in searcher.Get())
                using (o)
                    rows.Add(fields.Select(f => o[f]?.ToString()?.Trim() ?? "").ToArray());
        }
        catch (Exception ex)
        {
            Log.Write($"WMI query failed ({query}): {ex.Message}");
        }
        return rows;
    }
}
