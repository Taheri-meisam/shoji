using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Shoji.Sensors;

/// <summary>
/// Every hwmon chip the kernel exposes (CPU, drives, Wi-Fi, motherboard Super I/O, AMD GPUs) for the temps, fans and
/// sensors tiles. The chip list is found once; values are re-read each tick (sysfs reads are cheap).
/// </summary>
internal sealed class LinuxHwmon
{
    sealed record Input(string Path, string Chip, string Key, string Kind, int Index, string Label, string Group);

    readonly List<Input> _inputs = new();
    readonly HashSet<string> _spinningFans = new(StringComparer.Ordinal);

    public LinuxHwmon()
    {
        var chipCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hw in LinuxCli.Dirs("/sys/class/hwmon", "hwmon*").OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal))
        {
            string chip = LinuxCli.ReadLine(Path.Combine(hw, "name")) ?? Path.GetFileName(hw);
            // hwmonN numbers change between boots, so keys use the chip name (and a counter when there are several, like two NVMe drives).
            int nth = chipCount[chip] = chipCount.GetValueOrDefault(chip) + 1;
            string key = nth == 1 ? chip : $"{chip}#{nth}";
            string group = GroupOf(chip);
            string? drive = group == "Drive" ? DriveName(hw) : null;
            foreach (var kind in new[] { "temp", "fan", "in", "power" })
            {
                var files = LinuxCli.Files(hw, kind + "*_input")
                    .Select(f => (f, m: Regex.Match(Path.GetFileName(f), $@"^{kind}(\d+)_input$")))
                    .Where(x => x.m.Success)
                    .Select(x => (x.f, index: int.Parse(x.m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
                    .OrderBy(x => x.index)
                    .ToList();
                foreach (var (file, index) in files)
                {
                    string? label = LinuxCli.ReadLine(file.Replace("_input", "_label"));
                    _inputs.Add(new Input(file, chip, key, kind, index, Tidy(label, chip, kind, index, files.Count, drive), group));
                }
            }
        }
    }

    /// <summary>Friendly tile groups like the Windows build's ("CPU", "GPU", "Drive", "Board"); unknown chips keep their name.</summary>
    static string GroupOf(string chip) => chip switch
    {
        "coretemp" or "k10temp" or "zenpower" or "cpu_thermal" => "CPU",
        "amdgpu" or "radeon" or "nouveau" or "i915" or "xe" => "GPU",
        "nvme" or "drivetemp" => "Drive",
        "acpitz" or "pch_cannonlake" or "pch_skylake" or "pch_cometlake" or "asus" or "asus_ec" or "asus_wmi_sensors" or "dell_smm"
            or "thinkpad" or "hp" or "it87" => "Board",
        _ when chip.StartsWith("nct", StringComparison.Ordinal) || chip.StartsWith("it8", StringComparison.Ordinal)
               || chip.StartsWith("w83", StringComparison.Ordinal) || chip.StartsWith("pch_", StringComparison.Ordinal) => "Board",
        _ when chip.StartsWith("iwlwifi", StringComparison.Ordinal) || chip.StartsWith("mt7", StringComparison.Ordinal)
               || chip.StartsWith("ath", StringComparison.Ordinal) => "Wi-Fi",
        _ => chip,
    };

    /// <summary>The drive model for nvme / drivetemp chips, shortened like the Windows build ("Samsung 990 PRO 4TB").</summary>
    static string? DriveName(string hw)
    {
        string? model = LinuxCli.ReadLine(Path.Combine(hw, "device", "model"));
        if (model == null)
            return null;
        model = Regex.Replace(model, @"\s+", " ").Replace("Samsung SSD ", "Samsung ").Replace("NVMe ", "").Replace(" SSD", "").Trim();
        return model.Length > 22 ? model[..22] + "…" : model;
    }

    static string Tidy(string? label, string chip, string kind, int index, int count, string? drive)
    {
        if (drive != null && kind == "temp")
            return label is null or "Composite" || count == 1 ? drive : $"{drive} {label.ToLowerInvariant()}";
        if (chip == "amdgpu" && kind == "temp")
            return label switch { "edge" => "GPU", "junction" => "GPU hot spot", "mem" => "GPU memory", _ => "GPU " + label };
        if (label != null)
            return label.StartsWith("Package id ", StringComparison.Ordinal) ? "CPU package" + (label.EndsWith(" 0", StringComparison.Ordinal) ? "" : " " + label[11..]) : label;
        string noun = kind switch { "temp" => chip, "fan" => "Fan", "in" => "Voltage", _ => "Power" };
        return count == 1 && kind == "temp" ? noun : $"{noun} {index}";
    }

    /// <summary>Every plausible temperature in °C (unused inputs read -128, 0 or fail).</summary>
    public List<SensorReading> ReadTemps()
    {
        var list = new List<SensorReading>();
        foreach (var i in _inputs.Where(i => i.Kind == "temp"))
            if (LinuxCli.ReadLong(i.Path) is long milli && milli / 1000.0 is var c && c > 5 && c < 125)
                list.Add(new SensorReading(i.Label, i.Group, c));
        return list;
    }

    /// <summary>Every fan that has spun at least once this session (unused headers always read 0).</summary>
    public List<SensorReading> ReadFans()
    {
        var list = new List<SensorReading>();
        foreach (var i in _inputs.Where(i => i.Kind == "fan"))
        {
            if (LinuxCli.ReadLong(i.Path) is not long rpm || rpm < 0 || rpm >= 20_000)
                continue;
            if (rpm > 0)
                _spinningFans.Add(i.Path);
            if (_spinningFans.Contains(i.Path))
                list.Add(new SensorReading(i.Group == "GPU" ? "GPU " + i.Label.ToLowerInvariant() : i.Label, i.Group, rpm));
        }
        return list;
    }

    /// <summary>Raw values for the "sensors" tile, keyed "hwmon/&lt;chip&gt;/&lt;input&gt;" (e.g. "hwmon/coretemp/temp1").</summary>
    public void ReadAll(Dictionary<string, SensorValue> into)
    {
        foreach (var i in _inputs)
        {
            if (LinuxCli.ReadLong(i.Path) is not long raw)
                continue;
            (string type, float value) = i.Kind switch
            {
                "temp" => ("Temperature", raw / 1000f),
                "fan" => ("Fan", (float)raw),
                "in" => ("Voltage", raw / 1000f),
                _ => ("Power", raw / 1e6f),
            };
            into[$"hwmon/{i.Key}/{i.Kind}{i.Index}"] = new SensorValue(i.Label, i.Chip, type, value);
        }
    }
}
