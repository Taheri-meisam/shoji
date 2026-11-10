using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoji.Sensors;

/// <summary>
/// Sums per-process readings into per-app rows (30 browser processes count as one app) and builds the snapshot's
/// top list and <see cref="ProcessTable"/>. Shared by the Windows and Linux samplers so both group and rank alike.
/// </summary>
public sealed class ProcessGroups
{
    readonly Dictionary<string, (double cpu, double mem, double disk, int n)> _apps;
    int _processes;
    double _cpu, _mem, _disk;

    public ProcessGroups(StringComparer comparer) => _apps = new(comparer);

    public void Add(string name, double cpu, double memMb, double diskBytesPerSec)
    {
        cpu = Math.Max(0, cpu);
        diskBytesPerSec = Math.Max(0, diskBytesPerSec);
        _apps.TryGetValue(name, out var g);
        _apps[name] = (g.cpu + cpu, g.mem + memMb, g.disk + diskBytesPerSec, g.n + 1);
        _processes++;
        _cpu += cpu;
        _mem += memMb;
        _disk += diskBytesPerSec;
    }

    /// <summary>Leaves an app out of the ranking and totals (Windows' "Idle" pseudo-process).</summary>
    public void Remove(string name)
    {
        if (!_apps.Remove(name, out var g))
            return;
        _processes -= g.n;
        _cpu -= g.cpu;
        _mem -= g.mem;
        _disk -= g.disk;
    }

    ProcessInfo Row(KeyValuePair<string, (double cpu, double mem, double disk, int n)> g) =>
        new(g.Key, g.Value.cpu, g.Value.mem, g.Value.disk, g.Value.n);

    /// <summary>The busiest apps by CPU (then RAM), for the Top processes tile.</summary>
    public IReadOnlyList<ProcessInfo> Top(int count) => _apps
        .OrderByDescending(g => g.Value.cpu)
        .ThenByDescending(g => g.Value.mem)
        .Take(count)
        .Select(Row)
        .ToList();

    /// <summary>The leaders of each view (CPU, RAM, disk), merged, with totals over every process.</summary>
    public ProcessTable Table(long sample, DateTime time, int perMetric)
    {
        var names = new HashSet<string>(_apps.Comparer);
        names.UnionWith(_apps.OrderByDescending(g => g.Value.cpu).Take(perMetric).Select(g => g.Key));
        names.UnionWith(_apps.OrderByDescending(g => g.Value.mem).Take(perMetric).Select(g => g.Key));
        names.UnionWith(_apps.Where(g => g.Value.disk > 0).OrderByDescending(g => g.Value.disk).Take(perMetric).Select(g => g.Key));
        var apps = _apps.Where(g => names.Contains(g.Key)).OrderByDescending(g => g.Value.cpu).Select(Row).ToList();
        return new ProcessTable(sample, time, _processes, Math.Max(0, _cpu), Math.Max(0, _mem), Math.Max(0, _disk), apps);
    }
}

/// <summary>What the Task manager tile charts.</summary>
public enum ProcessMetric { Cpu, Memory, Disk }

/// <summary>
/// The last couple of minutes of process tables, kept outside the tiles so a rebuilt dashboard (a settings change,
/// a theme switch) keeps its graph. Filled from the UI thread by the Task manager tiles, once per fresh sample.
/// </summary>
public sealed class ProcessHistory
{
    public static readonly ProcessHistory Shared = new();

    /// <summary>How many samples are kept (about one a second).</summary>
    public const int Capacity = 120;

    readonly List<ProcessTable> _samples = new();

    public IReadOnlyList<ProcessTable> Samples => _samples;

    /// <summary>Goes up whenever a sample is added (tiles compare it to know when to redraw).</summary>
    public long Version { get; private set; }

    /// <summary>Adds the table unless it is the one already added last (snapshots repeat it between samples).</summary>
    public bool Add(ProcessTable? table)
    {
        if (table == null || _samples.Count > 0 && _samples[^1].Sample == table.Sample && _samples[^1].Time == table.Time)
            return false;
        _samples.Add(table);
        if (_samples.Count > Capacity)
            _samples.RemoveRange(0, _samples.Count - Capacity);
        Version++;
        return true;
    }

    /// <summary>Replaces the history (demo pictures).</summary>
    public void Seed(IEnumerable<ProcessTable> tables)
    {
        _samples.Clear();
        foreach (var t in tables)
            Add(t);
    }

    public static double Value(ProcessInfo p, ProcessMetric m) => m switch
    {
        ProcessMetric.Memory => p.MemoryMb,
        ProcessMetric.Disk => p.DiskBytesPerSec,
        _ => p.CpuPercent,
    };

    public static double Total(ProcessTable t, ProcessMetric m) => m switch
    {
        ProcessMetric.Memory => t.MemoryMb,
        ProcessMetric.Disk => t.DiskBytesPerSec,
        _ => t.CpuPercent,
    };
}
