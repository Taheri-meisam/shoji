using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>One line of the power breakdown.</summary>
public sealed record PowerPart(string Label, double Watts, bool Estimated);

/// <summary>
/// Whole-system power. <see cref="Measured"/> is true only when a smart PSU reports its input (wall) power;
/// otherwise it's the measured CPU and GPU power plus an estimate for everything else, divided by PSU efficiency.
/// </summary>
public sealed record PowerInfo(double TotalW, bool Measured, string Source, IReadOnlyList<PowerPart> Parts, double TodayWh, double SessionWh);

/// <summary>Adds up power and integrates it into energy (today and since start). Today's total survives restarts (power.json).</summary>
public sealed class PowerMeter
{
    static string FilePath => Path.Combine(AppConfig.Folder, "power.json");

    readonly double _otherW, _efficiency;
    double _todayWh, _sessionWh;
    DateTime _day = DateTime.Today, _lastSave = DateTime.UtcNow;
    long _last; // Stopwatch timestamp of the previous reading; the wall clock can jump (DST, time sync)

    sealed class Saved
    {
        public DateTime Day { get; set; }
        public double Wh { get; set; }
    }

    /// <param name="otherW">Estimate for the rest of the PC: motherboard, RAM, drives, fans, RGB.</param>
    /// <param name="efficiency">PSU efficiency, 0.5–1.</param>
    public PowerMeter(double otherW, double efficiency)
    {
        _otherW = Math.Clamp(otherW, 0, 500);
        _efficiency = Math.Clamp(efficiency, 0.5, 1);
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { } saved && saved.Day == DateTime.Today)
                _todayWh = saved.Wh;
        }
        catch
        {
            // start today's count from zero
        }
    }

    public PowerInfo Read(HardwareSensors.PowerReadings r, CpuInfo? cpu)
    {
        var parts = new List<PowerPart>();
        // Without the CPU driver there's no package power: estimate from load (idle ~12 W, about +1 W per % load).
        double cpuW = r.CpuW ?? 12 + (cpu?.LoadPercent ?? 10) * 1.0;
        parts.Add(new PowerPart("CPU", cpuW, r.CpuW == null));
        if (r.GpuW is double gpu)
            parts.Add(new PowerPart(r.Gpus > 1 ? "GPUs" : "GPU", gpu, false));
        parts.Add(new PowerPart("Rest of PC", _otherW, true));
        double dc = 0;
        foreach (var p in parts) dc += p.Watts;
        double losses = dc / _efficiency - dc;
        parts.Add(new PowerPart("PSU losses", losses, true));

        double total;
        bool measured;
        string source;
        if (r.PsuInW is double input)
        {
            (total, measured, source) = (input, true, L.F("measured by {0}", r.PsuName));
        }
        else if (r.PsuOutW is double output)
        {
            (total, measured, source) = (output / _efficiency, false, L.T("PSU output + losses"));
        }
        else
        {
            (total, measured, source) = (dc + losses, false, L.T("estimated at the wall"));
        }

        // Energy.
        var today = DateTime.Today;
        if (today != _day)
        {
            _day = today;
            _todayWh = 0;
        }
        long tick = Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            double hours = Math.Clamp(Stopwatch.GetElapsedTime(_last, tick).TotalSeconds, 0, 10) / 3600;
            _todayWh += total * hours;
            _sessionWh += total * hours;
        }
        _last = tick;
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(2))
            Save();

        return new PowerInfo(total, measured, source, parts, _todayWh, _sessionWh);
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Day = _day, Wh = _todayWh }));
        }
        catch (Exception ex)
        {
            Log.Write($"Power total could not be saved: {ex.Message}");
        }
    }
}
