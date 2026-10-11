using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Wraps LibreHardwareMonitor. GPU sensors work as a normal user; CPU temperatures and
/// motherboard sensors need the app to run elevated (and the PawnIO driver on recent versions).
/// </summary>
public sealed class HardwareSensors : IDisposable
{
    readonly Computer _computer;
    readonly bool _allHardware;
    readonly int _gpuIndex;

    /// <summary>Graphics cards in GpuIndex order, from the last time sensors were opened (for the picker in Settings).</summary>
    public static IReadOnlyList<string> GpuNames { get; private set; } = Array.Empty<string>();
    IHardware? _cpu;
    IHardware? _gpu;

    /// <param name="allHardware">Also open motherboard/storage/controllers (needed by the "sensors" module).</param>
    readonly List<IHardware> _discreteGpus = new(), _psus = new();

    /// <param name="wantPsu">Also open "smart" power supplies (Corsair / MSI) that report their own power draw.</param>
    public HardwareSensors(bool wantCpu, bool wantGpu, bool allHardware, int gpuIndex, bool wantPsu = false)
    {
        _allHardware = allHardware;
        _gpuIndex = gpuIndex;
        _computer = new Computer
        {
            IsCpuEnabled = wantCpu || allHardware,
            IsGpuEnabled = wantGpu || allHardware,
            IsMotherboardEnabled = allHardware,
            IsStorageEnabled = allHardware,
            IsControllerEnabled = allHardware,
            IsMemoryEnabled = allHardware,
            IsPsuEnabled = wantPsu || allHardware,
        };

        // LHM decides once per process whether it has the driver (at the first Open), so remember what that was.
        lock (DriverLock)
            _driverAtOpen ??= CpuSensorSetup.Ready;
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Log.Write($"LibreHardwareMonitor failed to open: {ex}");
        }

        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

        var gpus = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderBy(h => Integrated(h) ? 2 : h.HardwareType == HardwareType.GpuNvidia ? 0 : 1) // integrated graphics last
            .ToList();
        _gpu = gpus.Count == 0 ? null : gpus[Math.Clamp(_gpuIndex, 0, gpus.Count - 1)];
        if (gpus.Count > 0)
            GpuNames = gpus.Select((g, i) => gpus.Count(x => x.Name == g.Name) > 1 ? $"{g.Name} #{gpus.Take(i + 1).Count(x => x.Name == g.Name)}" : g.Name).ToList();
        // Power adds up every add-in card; integrated graphics is already inside the CPU package.
        _discreteGpus.AddRange(gpus.Where(g => !Integrated(g)));
        _psus.AddRange(_computer.Hardware.Where(h => h.HardwareType == HardwareType.Psu));

        Log.Write("Hardware found: " + string.Join(", ", _computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}")));
    }

    // LibreHardwareMonitor isn't thread-safe: the collector updates while an export may run on another thread.
    readonly object _gate = new();

    /// <summary>
    /// Graphics built into the processor: Intel's, and AMD's in Ryzen chips ("AMD Radeon(TM) Graphics", "Radeon 780M").
    /// Their power is already part of the CPU package, so they aren't added again.
    /// </summary>
    static bool Integrated(IHardware h) =>
        h.HardwareType == HardwareType.GpuIntel && !System.Text.RegularExpressions.Regex.IsMatch(h.Name, @"Arc\(TM\) [AB]\d{3}|Arc [AB]\d{3}") // Arc A770 / B580 are cards
        || h.HardwareType == HardwareType.GpuAmd && System.Text.RegularExpressions.Regex.IsMatch(h.Name,
            @"Radeon\(TM\) Graphics|Radeon Graphics|Radeon \d{3}M\b|Vega \d+ Graphics", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public void Update()
    {
        lock (_gate)
        {
            if (_allHardware)
            {
                foreach (var hw in _computer.Hardware)
                    UpdateTree(hw);
            }
            else
            {
                if (_cpu != null) UpdateTree(_cpu);
                if (_gpu != null) UpdateTree(_gpu);
                foreach (var g in _discreteGpus.Where(g => g != _gpu)) UpdateTree(g);
                foreach (var p in _psus) UpdateTree(p);
            }
        }
    }

    static void UpdateTree(IHardware hw)
    {
        try
        {
            hw.Update();
            foreach (var sub in hw.SubHardware)
                sub.Update();
        }
        catch (Exception ex)
        {
            Log.Write($"Update of {hw.Name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when LibreHardwareMonitor can really talk to the CPU / motherboard. Without the PawnIO driver
    /// (or without admin rights to open it) LHM doesn't fail: it quietly reports 0 °C, 0 W, 1.55 V and so on.
    /// So everything that comes from the driver is ignored unless this is true.
    /// It's the state when LHM was first opened: a PawnIO installed later only helps after a restart.
    /// </summary>
    public static bool DriverReady => _driverAtOpen ?? CpuSensorSetup.Ready;

    static readonly object DriverLock = new();
    static bool? _driverAtOpen;

    public static bool PawnIoInstalled => CpuSensorSetup.PawnIoInstalled;

    /// <summary>Short reason CPU temperature is unavailable, or null when it should work.</summary>
    public static string? DriverHint => DriverReady ? null
        : !PawnIoInstalled ? "needs PawnIO" : !Startup.IsElevated ? "needs admin" : "needs a restart of Shoji";

    /// <summary>What LibreHardwareMonitor knows about the CPU. Driver-only values are null when the driver isn't usable.</summary>
    public sealed record CpuSensors(string Name, double? LoadPercent, IReadOnlyList<double> CoreLoads,
                                    double? TemperatureC, double? ClockMhz, double? PowerW, double? VoltageV);

    public CpuSensors? ReadCpu()
    {
        if (_cpu == null)
            return null;

        var sensors = _cpu.Sensors;
        var coreLoads = sensors
            .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core", StringComparison.Ordinal))
            .OrderBy(s => s.Index)
            .Select(s => (double)(s.Value ?? 0))
            .ToList();
        var load = Pick(sensors, SensorType.Load, "CPU Total");

        if (!DriverReady)
            return new CpuSensors(CleanCpuName(_cpu.Name), load, coreLoads, null, null, null, null);

        // "Core #1", or "P-Core #1" / "E-Core #1" on hybrid Intel chips. "Effective" clocks are averaged over sleep time.
        var clocks = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.Value is > 100 and < 10_000
                        && s.Name.Contains("Core #", StringComparison.Ordinal)
                        && !s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value!.Value)
            .ToList();

        return new CpuSensors(
            Name: CleanCpuName(_cpu.Name),
            LoadPercent: load,
            CoreLoads: coreLoads,
            TemperatureC: Plausible(Pick(sensors, SensorType.Temperature,
                                         "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Tdie", "CPU Core", "Core Average", "Core Max")
                                    ?? Max(sensors, SensorType.Temperature), 1, 130),
            ClockMhz: clocks.Count > 0 ? clocks.Average() : Plausible(Pick(sensors, SensorType.Clock, "Cores (Average)"), 100, 10_000),
            PowerW: Plausible(Pick(sensors, SensorType.Power, "CPU Package", "Package"), 0.5, 1000),
            VoltageV: Plausible(Pick(sensors, SensorType.Voltage, "Core (SVI2 TFN)", "CPU Core", "Core (VID)", "Vcore", "Core #1 VID"), 0.3, 2));
    }

    static double? Plausible(double? v, double min, double max) =>
        v is double x && !double.IsNaN(x) && x >= min && x <= max ? x : null;

    public GpuInfo? ReadGpu()
    {
        if (_gpu == null)
            return null;

        var s = _gpu.Sensors;
        double? used = Pick(s, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        double? total = Pick(s, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");

        return new GpuInfo(
            Name: _gpu.Name.Replace("NVIDIA ", "").Replace("AMD ", "").Trim(),
            LoadPercent: Pick(s, SensorType.Load, "GPU Core", "D3D 3D"),
            TemperatureC: Pick(s, SensorType.Temperature, "GPU Core", "GPU VR SoC") ?? Max(s, SensorType.Temperature),
            HotSpotC: Pick(s, SensorType.Temperature, "GPU Hot Spot"),
            CoreClockMhz: Pick(s, SensorType.Clock, "GPU Core"),
            MemoryClockMhz: Pick(s, SensorType.Clock, "GPU Memory"),
            VramUsedMb: used,
            VramTotalMb: total,
            FanRpm: Max(s, SensorType.Fan),
            FanPercent: Max(s, SensorType.Control),
            PowerW: Pick(s, SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? Max(s, SensorType.Power));
    }

    /// <summary>What the hardware itself reports about power. Null where nothing measures it.</summary>
    public sealed record PowerReadings(double? CpuW, double? GpuW, int Gpus, double? PsuInW, double? PsuOutW, string? PsuName);

    public PowerReadings ReadPower()
    {
        double? cpu = ReadCpu()?.PowerW; // only with the driver; null otherwise
        double? gpu = null;
        foreach (var g in _discreteGpus)
        {
            var w = Pick(g.Sensors, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "Board Power") ?? Max(g.Sensors, SensorType.Power);
            if (w is double v && v > 0 && v < 2000)
                gpu = (gpu ?? 0) + v;
        }

        double? psuIn = null, psuOut = null;
        string? psuName = null;
        foreach (var p in _psus)
        {
            var power = p.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value is > 1 and < 3000).ToList();
            var input = power.FirstOrDefault(s => s.Name.Contains("Input", StringComparison.OrdinalIgnoreCase) || s.Name.Contains(" In", StringComparison.OrdinalIgnoreCase));
            var output = power.FirstOrDefault(s => s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Output", StringComparison.OrdinalIgnoreCase));
            if (input != null) psuIn = (psuIn ?? 0) + input.Value!.Value;
            if (output != null) psuOut = (psuOut ?? 0) + output.Value!.Value;
            if (input != null || output != null) psuName = p.Name;
        }
        return new PowerReadings(cpu, gpu, _discreteGpus.Count, psuIn, psuOut, psuName);
    }

    /// <summary>
    /// Every temperature worth showing, found automatically: CPU, GPU (core / hot spot / memory),
    /// one per drive, motherboard and RAM sensors. Implausible values (unused headers report -128 or 0) are skipped.
    /// </summary>
    public List<SensorReading> ReadTemps()
    {
        var list = new List<SensorReading>();
        bool driver = DriverReady;
        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu when driver:
                    var cpu = ReadCpu();
                    if (cpu?.TemperatureC is double t)
                        list.Add(new SensorReading("CPU", "CPU", t));
                    foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("CCD", StringComparison.Ordinal)
                                                            && !s.Name.Contains("Average") && !s.Name.Contains("Max")))
                        AddTemp(list, "CPU " + s.Name.Replace(" (Tdie)", ""), "CPU", s.Value);
                    break;

                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    AddTemp(list, "GPU", "GPU", Pick(hw.Sensors, SensorType.Temperature, "GPU Core", "GPU VR SoC"));
                    AddTemp(list, "GPU hot spot", "GPU", Pick(hw.Sensors, SensorType.Temperature, "GPU Hot Spot"));
                    AddTemp(list, "GPU memory", "GPU", Pick(hw.Sensors, SensorType.Temperature, "GPU Memory Junction", "GPU Memory"));
                    break;

                case HardwareType.Storage:
                    var first = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 120)
                                          .OrderBy(s => s.Index).FirstOrDefault();
                    if (first != null)
                        AddTemp(list, ShortName(hw.Name), "Drive", first.Value);
                    break;

                case HardwareType.Motherboard when driver:
                    foreach (var sub in hw.SubHardware)
                        foreach (var s in sub.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                            AddTemp(list, s.Name.StartsWith("Temperature #") ? "Board " + s.Name[13..] : s.Name, "Board", s.Value);
                    break;

                case HardwareType.Memory when driver:
                    foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                        AddTemp(list, s.Name, "RAM", s.Value);
                    break;

                case HardwareType.Cooler or HardwareType.EmbeddedController when driver || hw.HardwareType == HardwareType.Cooler:
                    foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                        AddTemp(list, s.Name, "Cooling", s.Value);
                    break;
            }
        }
        return list;
    }

    static void AddTemp(List<SensorReading> list, string label, string group, double? value)
    {
        if (value is double v && v > 5 && v < 125)
            list.Add(new SensorReading(label, group, v));
    }

    readonly HashSet<string> _spinningFans = new();

    /// <summary>Every fan / pump that has spun at least once this session (unused headers always read 0).</summary>
    public List<SensorReading> ReadFans()
    {
        var list = new List<SensorReading>();
        bool driver = DriverReady;
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType is HardwareType.Motherboard or HardwareType.Cpu or HardwareType.EmbeddedController && !driver)
                continue;
            CollectFans(hw, list, hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ? "GPU" : null);
        }
        return list;
    }

    void CollectFans(IHardware hw, List<SensorReading> list, string? prefix)
    {
        foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue))
        {
            var id = s.Identifier.ToString();
            if (s.Value > 0 && s.Value < 20_000)
                _spinningFans.Add(id);
            if (!_spinningFans.Contains(id))
                continue;
            string label = s.Name.Replace("Fan #", "Fan ");
            if (prefix != null && !label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                label = prefix + " " + label.ToLowerInvariant();
            list.Add(new SensorReading(label, prefix ?? "Board", s.Value!.Value));
        }
        foreach (var sub in hw.SubHardware)
            CollectFans(sub, list, prefix);
    }

    static string ShortName(string name)
    {
        name = name.Replace("Samsung SSD ", "Samsung ").Replace("NVMe ", "").Replace(" SSD", "").Trim();
        return name.Length > 22 ? name[..22] + "…" : name;
    }

    /// <summary>
    /// Every sensor value keyed by identifier, for the configurable "sensors" module. Without the driver, values
    /// that come from it (CPU / board / RAM / embedded controller) are null rather than LHM's made-up zeros.
    /// </summary>
    public Dictionary<string, SensorValue> ReadAll()
    {
        var result = new Dictionary<string, SensorValue>(StringComparer.OrdinalIgnoreCase);
        bool driver = DriverReady;
        foreach (var hw in _computer.Hardware)
            Collect(hw, hw.HardwareType, driver, result);
        return result;
    }

    static void Collect(IHardware hw, HardwareType root, bool driver, Dictionary<string, SensorValue> into)
    {
        foreach (var s in hw.Sensors)
            into[s.Identifier.ToString()] = new SensorValue(s.Name, hw.Name, s.SensorType.ToString(),
                driver || !FromDriver(root, s.SensorType) ? s.Value : null);
        foreach (var sub in hw.SubHardware)
            Collect(sub, root, driver, into);
    }

    /// <summary>
    /// Whether a value is read through the driver. CPU load and the memory usage figures come from Windows itself,
    /// so they stay; temperatures, clocks, power and voltages of these parts don't.
    /// </summary>
    static bool FromDriver(HardwareType root, SensorType type) => root switch
    {
        HardwareType.Cpu => type != SensorType.Load,
        HardwareType.Memory => type is not (SensorType.Load or SensorType.Data or SensorType.SmallData),
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => true,
        _ => false,
    };

    /// <summary>Writes every sensor with its identifier to a text file so users can pick some for the "sensors" module.</summary>
    public string ExportSensorList()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            // Make sure everything is populated, even hardware this instance doesn't normally poll.
            foreach (var hw in _computer.Hardware)
                UpdateTree(hw);

            sb.AppendLine("Shoji sensor list - copy an Id into a \"sensors\" module in config.json");
            sb.AppendLine($"Generated {DateTime.Now}. Running elevated: {Startup.IsElevated}. PawnIO installed: {PawnIoInstalled}");
            sb.AppendLine();
            foreach (var hw in _computer.Hardware)
                Dump(hw, sb, 0);
        }

        var path = Path.Combine(AppConfig.Folder, "sensors.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    static void Dump(IHardware hw, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.AppendLine($"{pad}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            sb.AppendLine($"{pad}  {s.Identifier,-45} {s.SensorType,-12} {s.Name,-30} {s.Value?.ToString("0.##") ?? "-"}");
        foreach (var sub in hw.SubHardware)
            Dump(sub, sb, depth + 1);
        sb.AppendLine();
    }

    /// <summary>First sensor of the type whose name matches one of the candidates (in priority order).</summary>
    static double? Pick(ISensor[] sensors, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var s in sensors)
            {
                if (s.SensorType == type && s.Value.HasValue && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s.Value.Value;
            }
        }
        return null;
    }

    static double? Max(ISensor[] sensors, SensorType type)
    {
        double? max = null;
        foreach (var s in sensors)
        {
            if (s.SensorType == type && s.Value is float v && v > 0 && (max == null || v > max))
                max = v;
        }
        return max;
    }

    public static string CleanCpuName(string name) => name
        .Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "")
        .Replace("  ", " ").Trim();

    public void Dispose()
    {
        try { _computer.Close(); } catch { /* shutting down anyway */ }
    }
}
