using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;
#if SHOJI_LINUX
using System.Diagnostics;
using System.Text.RegularExpressions;
#else
using System.Management;
#endif

namespace Shoji.Sensors;

public enum DriveState { Unknown, Good, Watch, Failing }

/// <summary>What a drive says about its own health (SMART): the verdict, how worn it is, how long it has run.</summary>
/// <param name="Kind">"NVMe", "SSD", "HDD" or "".</param>
/// <param name="UsedPercent">Wear: 0 = new, 100 = the rated life is used up (SSDs).</param>
/// <param name="Problem">Why the state isn't Good, already translated ("3 bad sectors"), or null.</param>
public sealed record DriveHealth(string Name, string Kind, double SizeGb, DriveState State, double? UsedPercent,
    double? PowerOnHours, double? TemperatureC, double? WrittenTb, string? Problem);

/// <summary>The words the Drive health tile shows, shared by both editions.</summary>
public static class DriveHealthText
{
    public static string State(DriveState s) => s switch
    {
        DriveState.Good => L.T("Good"),
        DriveState.Watch => L.T("Keep an eye on it"),
        DriveState.Failing => L.T("Failing: back up now"),
        _ => L.T("No health data"),
    };

    /// <summary>"4 TB NVMe · 3% worn · 5 months on · 43° · 8.7 TB written".</summary>
    public static string Details(DriveHealth d)
    {
        var parts = new List<string>();
        string size = d.SizeGb >= 950 ? $"{d.SizeGb / 1000:0.#} TB" : $"{d.SizeGb:0} GB";
        parts.Add(d.Kind.Length > 0 ? $"{size} {d.Kind}" : size);
        if (d.UsedPercent is double used)
            parts.Add(L.F("{0:0}% worn", used));
        if (d.PowerOnHours is double h)
            parts.Add(L.F("{0} on", Age(h)));
        if (d.TemperatureC is double t)
            parts.Add($"{t:0}°");
        if (d.WrittenTb is double tb && tb >= 0.1)
            parts.Add(L.F("{0:0.#} TB written", tb));
        return string.Join(" · ", parts);
    }

    /// <summary>Running hours as "320 h", "5 months" or "4.1 years".</summary>
    public static string Age(double hours) =>
        hours < 1000 ? L.F("{0:0} h", hours)
        : hours < 8766 * 1.5 ? L.F("{0:0} months", hours / 730.5)
        : L.F("{0:0.#} years", hours / 8766);

    /// <summary>The line under the list: the worst drive decides.</summary>
    public static string Summary(IReadOnlyList<DriveHealth> drives)
    {
        if (drives.Count == 0)
            return L.T("No drives found");
        if (drives.Any(d => d.State == DriveState.Failing))
            return L.T("A drive is failing: back up now");
        if (drives.Any(d => d.State == DriveState.Watch))
            return L.T("A drive needs watching");
        return drives.All(d => d.State == DriveState.Unknown) ? Hint : L.T("All drives healthy");
    }

#if SHOJI_LINUX
    static string Hint => L.T("No SMART data (needs udisks2)");
#else
    static string Hint => L.T("No SMART data from these drives");
#endif
}

/// <summary>
/// Reads every drive's SMART verdict every ten minutes (it changes over months, and asking is slow). On Linux through
/// udisks2, which answers without root; on Windows through the Storage WMI classes (the verdict needs no admin, wear
/// and running hours do).
/// </summary>
public sealed class DriveHealthService : Poller<IReadOnlyList<DriveHealth>>
{
    public const string Key = "drivehealth";

    public DriveHealthService() : base(TimeSpan.FromMinutes(10)) => Start();

    protected override Task<IReadOnlyList<DriveHealth>> FetchAsync(CancellationToken ct) => Task.Run(() =>
    {
#if SHOJI_LINUX
        if (Demo.Enabled)
            return DemoDrives();
#endif
        IReadOnlyList<DriveHealth> list = Read(ct);
        return list;
    }, ct);

    /// <summary>The verdict from what the drive reports. SSD wear only warns near the end: a worn drive still works.</summary>
    static (DriveState State, string? Problem) Judge(bool hasData, bool failing, double? usedPercent, long badSectors, long mediaErrors, string? warning)
    {
        if (!hasData)
            return (DriveState.Unknown, null);
        if (failing)
            return (DriveState.Failing, warning ?? L.T("the drive reports it is failing"));
        if (usedPercent >= 100)
            return (DriveState.Watch, L.T("rated life used up"));
        if (badSectors > 0)
            return (DriveState.Watch, L.F("{0} bad sectors", badSectors));
        if (mediaErrors > 0)
            return (DriveState.Watch, L.F("{0} media errors", mediaErrors));
        if (usedPercent >= 90)
            return (DriveState.Watch, null); // the wear is in the details already
        return (DriveState.Good, null);
    }

#if SHOJI_LINUX
    static IReadOnlyList<DriveHealth> DemoDrives() => new[]
    {
        new DriveHealth("Samsung SSD 990 PRO 2TB", "NVMe", 2000, DriveState.Good, 3, 3541, 43, 8.7, null),
        new DriveHealth("Crucial MX500 1TB", "SSD", 1000, DriveState.Watch, 91, 29800, 36, 61.4, null),
        new DriveHealth("ST8000DM004", "HDD", 8000, DriveState.Good, null, 10012, 40, null, null),
    };

    static List<DriveHealth> Read(CancellationToken ct)
    {
        var list = new List<DriveHealth>();
        string? dump = Run("udisksctl", "dump", ct);
        if (dump == null)
            return list;

        // "/org/…/drives/NAME:" starts a drive; "  org.freedesktop.UDisks2.X:" an interface; "    Key:  value" a property.
        string? path = null, iface = null;
        var drives = new List<(string Path, Dictionary<string, string> Props)>();
        Dictionary<string, string>? props = null;
        foreach (string line in dump.Split('\n'))
        {
            if (line.StartsWith('/'))
            {
                path = line.TrimEnd(':', ' ');
                props = path.Contains("/UDisks2/drives/") ? new Dictionary<string, string>() : null;
                if (props != null)
                    drives.Add((path, props));
            }
            else if (props != null && line.StartsWith("  org.freedesktop.UDisks2."))
            {
                iface = line.Trim().TrimEnd(':')["org.freedesktop.UDisks2.".Length..];
                props[iface] = "";
            }
            else if (props != null && iface != null && line.StartsWith("    ") && line.IndexOf(':') is > 0 and var colon)
            {
                string key = line[..colon].Trim();
                if (key.Length > 0 && !key.Contains(' '))
                    props[iface + "." + key] = line[(colon + 1)..].Trim();
            }
        }

        foreach (var (drivePath, p) in drives)
        {
            double size = Num(p, "Drive.Size") / 1e9;
            bool ata = p.GetValueOrDefault("Drive.Ata.SmartSupported") == "true" && p.GetValueOrDefault("Drive.Ata.SmartEnabled") == "true";
            bool nvme = p.ContainsKey("NVMe.Controller");
            // Empty card readers and optical drives say nothing; USB sticks have no SMART and would only be noise.
            if (size <= 0 || p.GetValueOrDefault("Drive.Optical") == "true" || !ata && !nvme && p.GetValueOrDefault("Drive.ConnectionBus") is "usb" or "sdio")
                continue;

            string name = p.GetValueOrDefault("Drive.Model") ?? "";
            if (name.Length == 0)
                name = drivePath[(drivePath.LastIndexOf('/') + 1)..];
            bool spins = Num(p, "Drive.RotationRate") > 0;
            string kind = nvme ? "NVMe" : spins ? "HDD" : ata ? "SSD" : "";

            double? used = null, hours = null, temp = null, written = null;
            long bad = 0, media = 0;
            bool failing = false;
            string? warning = null;
            if (nvme)
            {
                if (p.TryGetValue("NVMe.Controller.SmartPowerOnHours", out string? h) && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out double hv))
                    hours = hv;
                temp = Kelvin(Num(p, "NVMe.Controller.SmartTemperature"));
                string critical = p.GetValueOrDefault("NVMe.Controller.SmartCriticalWarning") ?? "";
                // "temperature" only says it is hot right now; the others mean the drive itself is in trouble.
                var flags = critical.Split(new[] { ',', ' ', '[', ']', '\'' }, StringSplitOptions.RemoveEmptyEntries).Where(f => f != "temperature").ToList();
                if (flags.Count > 0)
                {
                    failing = true;
                    warning = string.Join(", ", flags).Replace('_', ' ');
                }
                string attrs = Run("gdbus", $"call --system --dest org.freedesktop.UDisks2 --object-path {drivePath} --method org.freedesktop.UDisks2.NVMe.Controller.SmartGetAttributes {{}}", ct) ?? "";
                if (Regex.Match(attrs, @"'percent_used': <byte 0x([0-9a-f]+)>") is { Success: true } pu)
                    used = Convert.ToInt32(pu.Groups[1].Value, 16);
                if (Regex.Match(attrs, @"'total_data_written': <uint64 (\d+)>") is { Success: true } tw)
                    written = double.Parse(tw.Groups[1].Value, CultureInfo.InvariantCulture) / 1e12;
                if (Regex.Match(attrs, @"'media_errors': <uint64 (\d+)>") is { Success: true } me)
                    media = long.Parse(me.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (ata)
            {
                failing = p.GetValueOrDefault("Drive.Ata.SmartFailing") == "true";
                double seconds = Num(p, "Drive.Ata.SmartPowerOnSeconds");
                hours = seconds > 0 ? seconds / 3600 : null;
                temp = Kelvin(Num(p, "Drive.Ata.SmartTemperature"));
                bad = (long)Num(p, "Drive.Ata.SmartNumBadSectors");
                if (!spins)
                    used = AtaWear(Run("gdbus", $"call --system --dest org.freedesktop.UDisks2 --object-path {drivePath} --method org.freedesktop.UDisks2.Drive.Ata.SmartGetAttributes {{}}", ct));
            }

            var (state, problem) = Judge(ata || nvme, failing, used, bad, media, warning);
            list.Add(new DriveHealth(name, kind, size, state, used, hours, temp, written, problem));
        }
        return list;
    }

    /// <summary>
    /// SATA SSDs have no standard wear attribute; the common ones count down from 100 in their normalised value:
    /// 231 "SSD life left", 233 "media wearout" (Intel), 177 "wear levelling" (Samsung), 202 "lifetime remaining" (Crucial).
    /// </summary>
    static double? AtaWear(string? attributes)
    {
        if (attributes == null)
            return null;
        var values = new Dictionary<int, int>();
        foreach (Match m in Regex.Matches(attributes, @"\((?:byte )?0x([0-9a-f]{2}), '[^']*', (?:uint16 )?\d+, (-?\d+),"))
            values[Convert.ToInt32(m.Groups[1].Value, 16)] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        foreach (int id in new[] { 231, 233, 177, 202 })
            if (values.TryGetValue(id, out int left) && left is >= 0 and <= 100)
                return 100 - left;
        return null;
    }

    static double Num(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    static double? Kelvin(double k) => k > 200 ? k - 273.15 : null;

    static string? Run(string file, string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null)
                return null;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            if (!p.WaitForExit(8000))
            {
                p.Kill();
                return null;
            }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // udisks2 isn't installed: the tile says so
        }
    }
#else
    static List<DriveHealth> Read(CancellationToken ct)
    {
        var list = new List<DriveHealth>();
        const string scope = @"root\Microsoft\Windows\Storage";

        // Wear, running hours and temperature: Windows only hands these to an elevated process.
        var counters = new Dictionary<string, (double? Wear, double? Hours, double? Temp)>();
        try
        {
            using var search = new ManagementObjectSearcher(scope, "SELECT DeviceId, Wear, PowerOnHours, Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject o in search.Get())
                using (o)
                    counters[Convert.ToString(o["DeviceId"], CultureInfo.InvariantCulture) ?? ""] = (Value(o["Wear"]), Value(o["PowerOnHours"]), Value(o["Temperature"]) is > 0 and var t ? t : null);
        }
        catch (Exception)
        {
            // not elevated: the verdict below still works
        }

        using var disks = new ManagementObjectSearcher(scope, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
        foreach (ManagementObject o in disks.Get())
            using (o)
            {
                ct.ThrowIfCancellationRequested();
                string id = Convert.ToString(o["DeviceId"], CultureInfo.InvariantCulture) ?? "";
                int bus = (int)(Value(o["BusType"]) ?? 0), media = (int)(Value(o["MediaType"]) ?? 0);
                double size = (Value(o["Size"]) ?? 0) / 1e9;
                int health = (int)(Value(o["HealthStatus"]) ?? 5); // 0 healthy, 1 warning, 2 unhealthy, 5 unknown
                if (size <= 0 || bus is 7 or 12 or 13 && health == 5) // USB sticks and card readers that report nothing
                    continue;
                counters.TryGetValue(id, out var c);
                string kind = bus == 17 ? "NVMe" : media == 3 ? "HDD" : media == 4 ? "SSD" : "";
                var (state, problem) = Judge(health != 5, health == 2, c.Wear, 0, 0, null);
                if (health == 1 && state == DriveState.Good)
                    (state, problem) = (DriveState.Watch, L.T("Windows reports a warning"));
                list.Add(new DriveHealth((Convert.ToString(o["FriendlyName"], CultureInfo.InvariantCulture) ?? "").Trim(), kind, size, state, c.Wear, c.Hours, c.Temp, null, problem));
            }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static double? Value(object? o) => o == null ? null : Convert.ToDouble(o, CultureInfo.InvariantCulture);
#endif
}
