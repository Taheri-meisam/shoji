using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// CPU name, load and clock from Windows itself (registry + performance counters), so the CPU tile works
/// without admin rights or drivers. The clock is computed the same way Task Manager does it:
/// base clock × "% Processor Performance".
/// </summary>
public sealed class WindowsCpu : IDisposable
{
    const string Category = "Processor Information";

    readonly PerformanceCounter? _utility, _performance;
    readonly List<PerformanceCounter> _threads = new();

    public string Name { get; }
    public double? BaseMhz { get; }
    public int Threads { get; } = Environment.ProcessorCount;
    public int? Cores { get; }

    public WindowsCpu()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            Name = HardwareSensors.CleanCpuName(key?.GetValue("ProcessorNameString") as string ?? "CPU");
            BaseMhz = key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : null;
        }

        Cores = CountCores();

        try
        {
            _utility = Counter("% Processor Utility", "_Total") ?? Counter("% Processor Time", "_Total");
            _performance = Counter("% Processor Performance", "_Total");

            // Instances are "group,index" ("0,0", "0,1", …) plus per-group totals ("0,_Total").
            var instances = new PerformanceCounterCategory(Category).GetInstanceNames()
                .Select(n => (name: n, parts: n.Split(',')))
                .Where(x => x.parts.Length == 2 && int.TryParse(x.parts[0], out _) && int.TryParse(x.parts[1], out _))
                .OrderBy(x => int.Parse(x.parts[0])).ThenBy(x => int.Parse(x.parts[1]))
                .Select(x => x.name);
            foreach (var instance in instances)
            {
                var c = Counter("% Processor Utility", instance) ?? Counter("% Processor Time", instance);
                if (c != null)
                    _threads.Add(c);
            }
        }
        catch (Exception ex)
        {
            // Performance counters can be disabled or corrupted (fixable with "lodctr /r").
            Log.Write($"CPU performance counters unavailable: {ex.Message}");
        }
    }

    static PerformanceCounter? Counter(string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(Category, counter, instance, readOnly: true);
            c.NextValue(); // the first read only primes the counter
            return c;
        }
        catch
        {
            return null;
        }
    }

    static int? CountCores()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            int total = 0;
            foreach (var o in searcher.Get())
                using (o)
                    total += Convert.ToInt32(o["NumberOfCores"]);
            return total > 0 ? total : null;
        }
        catch
        {
            return null;
        }
    }

    public (double? Load, IReadOnlyList<double> ThreadLoads, double? ClockMhz) Read()
    {
        double? load = Next(_utility);
        var threads = _threads.Select(c => Next(c) ?? 0).ToList();
        double? clock = BaseMhz is double b && Next(_performance) is double perf && perf > 0 ? b * perf / 100 : null;
        return (load, threads, clock);
    }

    // "% Processor Utility" goes above 100 when the CPU boosts past its base clock; Task Manager caps it too.
    static double? Next(PerformanceCounter? c)
    {
        if (c == null)
            return null;
        try { return Math.Clamp(c.NextValue(), 0, c.CounterName == "% Processor Performance" ? 1000 : 100); }
        catch { return null; }
    }

    public void Dispose()
    {
        _utility?.Dispose();
        _performance?.Dispose();
        foreach (var c in _threads)
            c.Dispose();
    }
}
