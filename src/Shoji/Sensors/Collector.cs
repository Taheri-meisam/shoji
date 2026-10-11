using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Background loop that gathers a <see cref="Snapshot"/> every RefreshMs and hands it to the UI.
/// Only the data sources that an enabled module needs are polled.
/// </summary>
public sealed class Collector : IDisposable
{
    readonly AppConfig _config;
    readonly Action<Snapshot> _publish;
    readonly CancellationTokenSource _cts = new();
    readonly HashSet<string> _modules;

    readonly bool _wantCpu, _wantGpu, _wantAll;
    HardwareSensors? _hardware;
    WindowsCpu? _windowsCpu;
    readonly SystemStats _system = new();
    readonly ActivityTracker? _activity;
    readonly WeatherService? _weather;
    readonly PingMonitor? _ping;
    readonly ForegroundReader _foreground = new();
    readonly RecapTracker? _recap;
    readonly PowerMeter? _power;
    readonly WebsiteMonitor? _sites;
    readonly bool _wantFps, _historyMemory, _historyNetwork;
    FpsMonitor? _fps;
    public MediaService Media { get; } = new();

    Task? _loop;

    public Collector(AppConfig config, Action<Snapshot> publish)
    {
        _config = config;
        _publish = publish;
        // Every page is polled, so switching pages is instant and graphs on hidden pages keep going.
        var enabled = config.AllModules.Where(m => m.Enabled).ToList();
        _modules = enabled.Select(m => m.Type.ToLowerInvariant()).ToHashSet();

        // The status board and system orbit summarise several sources, so they need those polled too.
        bool summary = Has("status") || Has("overview");
        // A History tile needs the sources of the graphs it shows (default: CPU and GPU temperature).
        var series = enabled.Where(m => m.Type.Equals("history", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.OptList("Series") is { Count: > 0 } l ? l : new List<string> { "cpu.temp", "gpu.temp" })
            .Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        bool Graph(string prefix) => series.Any(x => x.StartsWith(prefix));
        _historyMemory = Graph("mem.");
        _historyNetwork = Graph("net.");
        _wantCpu = Has("cpu") || summary || Has("power") || Graph("cpu.") || Graph("power.");
        _wantGpu = Has("gpu") || summary || Has("power") || Graph("gpu.") || Graph("power.");
        // Websites: every site from every Websites tile, checked once each (duplicates by URL are merged).
        var siteTiles = enabled.Where(m => m.Type.Equals("websites", StringComparison.OrdinalIgnoreCase)).ToList();
        var sites = siteTiles.SelectMany(m => m.Sites ?? new List<SiteRef>())
            .Where(x => WebsiteMonitor.Normalize(x.Url) != null)
            .GroupBy(x => WebsiteMonitor.Normalize(x.Url)!.ToString()).Select(g => g.First()).ToList();
        if (sites.Count > 0)
            _sites = new WebsiteMonitor(sites, siteTiles[0].OptInt("Interval", 60), config.Alerts.CertDays);
        if (enabled.FirstOrDefault(m => m.Type.Equals("power", StringComparison.OrdinalIgnoreCase)) is { } power)
            _power = new PowerMeter(power.OptInt("Other", 45), power.OptInt("Efficiency", 90) / 100.0);
        _wantAll = Has("sensors") || Has("temps") || Has("fans");

        if (Has("activity"))
            _activity = new ActivityTracker();
        // Weather for the Weather tile and for clocks that show it; a tile's own city wins, then the shared one (Settings → Weather & time).
        bool IsType(ModuleConfig m, string t) => m.Type.Equals(t, StringComparison.OrdinalIgnoreCase);
        var clocksWithWeather = enabled.Where(m => IsType(m, "clock") && !m.OptBool("HideWeather")).ToList();
        if (Has("weather") || Has("forecast") || clocksWithWeather.Count > 0)
        {
            string? city = enabled.FirstOrDefault(m => IsType(m, "weather") && !string.IsNullOrWhiteSpace(m.City))?.City
                           ?? enabled.FirstOrDefault(m => IsType(m, "forecast") && !string.IsNullOrWhiteSpace(m.City))?.City
                           ?? clocksWithWeather.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.City))?.City
                           ?? config.City;
            _weather = new WeatherService(city);
        }
        if (Has("ping"))
            _ping = new PingMonitor(enabled.First(m => m.Type.Equals("ping", StringComparison.OrdinalIgnoreCase)).Host);
        else if (series.Contains("ping"))
            _ping = new PingMonitor(null);
        _wantFps = Has("fps") || Has("recap") || series.Contains("fps") || config.Web.Enabled;
        if (Has("recap"))
            _recap = new RecapTracker();
    }

    /// <summary>A long-lived helper shared by tiles; see <see cref="ServiceStore"/>. It survives this collector.</summary>
    public T Service<T>(string key, Func<T> create) where T : class, IDisposable => ServiceStore.Get(key, create);

    bool Has(string type) => _modules.Contains(type);

    /// <summary>
    /// A key for everything that decides what this collector polls. If it hasn't changed (e.g. only the theme did),
    /// the dashboard keeps the running collector instead of reopening LibreHardwareMonitor.
    /// </summary>
    public static string KeyFor(AppConfig c) =>
        string.Join("|", c.RefreshMs, c.GpuIndex, c.Web.Enabled, c.Alerts.CertDays, c.City,
            string.Join(",", c.AllModules.Where(m => m.Enabled).Select(m =>
                $"{m.Type.ToLowerInvariant()}:{m.City}:{m.Host}:{m.OptBool("HideWeather")}:{m.Opt("Other")}:{m.Opt("Efficiency")}:{m.Opt("Interval")}:{m.Opt("Series")}:" +
                string.Join(";", m.Sites?.Select(x => $"{x.Url}|{x.Contains}") ?? Array.Empty<string>()) + ":" +
                string.Join(";", m.Sensors?.Select(s => s.Id) ?? Array.Empty<string>()))));

    /// <summary>Opens LibreHardwareMonitor on demand to write sensors.txt.</summary>
    public string ExportSensorList()
    {
        if (_hardware != null)
            return _hardware.ExportSensorList();
        using var temp = new HardwareSensors(true, true, true, 0);
        return temp.ExportSensorList();
    }

    // The loop of the collector this one replaced. A new loop waits for it to free LibreHardwareMonitor before opening
    // its own (two open at once fight over the driver), so replacing a collector doesn't have to block the UI.
    static Task _previousLoop = Task.CompletedTask;

    /// <summary>Starts polling. Called on the UI thread.</summary>
    public void Start()
    {
        var previous = _previousLoop;
        _loop = Task.Run(async () =>
        {
            // Bounded, like the old blocking wait: a hung loop mustn't keep the dashboard empty for good.
            await Task.WhenAny(previous, Task.Delay(5000));
            await RunAsync(_cts.Token);
        });
        _previousLoop = _loop;
    }

    async Task RunAsync(CancellationToken ct)
    {
        // This loop owns the hardware handles: it creates them and frees them in "finally", so a collector that's
        // replaced while LibreHardwareMonitor is still opening can't leak them or free them while they're in use.
        try
        {
            // Opening LibreHardwareMonitor and the performance counters takes a second or two, so do it here.
            if ((_wantCpu || _wantGpu || _wantAll) && !ct.IsCancellationRequested) // replaced again while waiting
                _hardware = new HardwareSensors(_wantCpu, _wantGpu, _wantAll, _config.GpuIndex, wantPsu: _power != null);
            if (_wantCpu && !ct.IsCancellationRequested)
                _windowsCpu = new WindowsCpu();
            if (_wantFps && !ct.IsCancellationRequested)
                _fps = new FpsMonitor();
            if (ct.IsCancellationRequested)
                return;

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(_config.RefreshMs, 250, 10_000)));
            do
            {
                try
                {
                    var snapshot = await CollectAsync();
                    _recap?.Sample(snapshot);
                    Record(snapshot);
                    if (!ct.IsCancellationRequested)
                        _publish(_recap == null ? snapshot : snapshot with { Recap = _recap.Read() });
                }
                catch (Exception ex)
                {
                    Log.Write($"Collect failed: {ex}");
                }
            }
            while (await WaitAsync(timer, ct));
        }
        finally
        {
            _hardware?.Dispose();
            _windowsCpu?.Dispose();
            _fps?.Dispose();
            _system.Dispose();
            _ping?.Dispose();
            _sites?.Dispose();
            _activity?.Save();
            _power?.Save();
            _recap?.Flush(); // a game session that's still going is saved, not lost
            HistoryStore.Shared.Save();
        }
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    async Task<Snapshot> CollectAsync()
    {
        _hardware?.Update();

        var mediaTask = Has("media") || Has("lyrics") ? Media.ReadAsync() : Task.FromResult<MediaInfo?>(null);
        var foreground = _foreground.Read();
        var cpu = _wantCpu ? ReadCpu() : null;

        return new Snapshot
        {
            Time = DateTime.Now,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Cpu = cpu,
            Power = _power != null && _hardware != null ? _power.Read(_hardware.ReadPower(), cpu) : null,
            Sites = _sites?.Read() ?? (IReadOnlyList<SiteStatus>)Array.Empty<SiteStatus>(),
            Gpu = _wantGpu ? _hardware?.ReadGpu() : null,
            Memory = Has("memory") || Has("status") || Has("overview") || _historyMemory ? SystemStats.ReadMemory() : null,
            Network = Has("network") || Has("overview") || _historyNetwork ? _system.ReadNetwork() : null,
            Disks = Has("disks") || Has("status") ? _system.ReadDisks() : Array.Empty<DiskInfo>(),
            DiskIo = Has("disks") ? _system.ReadDiskIo() : null,
            TopProcesses = Has("processes") ? _system.ReadTopProcesses(6) : Array.Empty<ProcessInfo>(),
            Activity = _activity?.Sample(),
            Sensors = Has("sensors") && _hardware != null ? _hardware.ReadAll() : new Dictionary<string, SensorValue>(),
            Temps = Has("temps") && _hardware != null ? _hardware.ReadTemps() : Array.Empty<SensorReading>(),
            Fans = Has("fans") && _hardware != null ? _hardware.ReadFans() : Array.Empty<SensorReading>(),
            System = Has("system") ? SystemInfoReader.Current : null,
            Weather = _weather?.Read(),
            Ping = _ping?.Read(),
            Battery = Has("battery") ? SystemStats.ReadBattery() : null,
            Fps = _fps?.Read(foreground.ProcessId, foreground.ProcessName),
            Foreground = foreground,
            IdleSeconds = NativeMethods.IdleSeconds(),
            Media = await mediaTask,
        };
    }

    /// <summary>Feeds the shared graph history (small graphs, History tile, web dashboard).</summary>
    static void Record(Snapshot s)
    {
        var h = HistoryStore.Shared;
        h.Add("cpu.load", s.Cpu?.LoadPercent);
        h.Add("cpu.temp", s.Cpu?.TemperatureC);
        h.Add("cpu.power", s.Cpu?.PowerW);
        h.Add("gpu.load", s.Gpu?.LoadPercent);
        h.Add("gpu.temp", s.Gpu?.TemperatureC);
        h.Add("gpu.power", s.Gpu?.PowerW);
        h.Add("mem.load", s.Memory?.LoadPercent);
        h.Add("net.down", s.Network?.DownloadBytesPerSec);
        h.Add("net.up", s.Network?.UploadBytesPerSec);
        h.Add("ping", s.Ping?.LastMs);
        h.Add("fps", s.Fps?.Fps);
        h.Add("power.total", s.Power?.TotalW);
        h.SaveIfDue();
    }

    /// <summary>Windows gives name, load and clock without admin; LibreHardwareMonitor adds temperature, power and voltage when its driver works.</summary>
    CpuInfo? ReadCpu()
    {
        var lhm = _hardware?.ReadCpu();
        if (_windowsCpu == null && lhm == null)
            return null;

        var win = _windowsCpu?.Read();
        var threadLoads = lhm?.CoreLoads is { Count: > 0 } l ? l : win?.ThreadLoads ?? Array.Empty<double>();
        return new CpuInfo(
            Name: _windowsCpu?.Name ?? lhm!.Name,
            LoadPercent: lhm?.LoadPercent ?? win?.Load,
            TemperatureC: lhm?.TemperatureC,
            ClockMhz: lhm?.ClockMhz ?? win?.ClockMhz,
            PowerW: lhm?.PowerW,
            CoreLoads: threadLoads,
            Cores: _windowsCpu?.Cores,
            Threads: _windowsCpu?.Threads ?? Environment.ProcessorCount,
            BaseMhz: _windowsCpu?.BaseMhz,
            VoltageV: lhm?.VoltageV,
            SensorHint: lhm?.TemperatureC == null ? HardwareSensors.DriverHint ?? "not supported" : null);
    }

    /// <summary>
    /// Stops without waiting (the dashboard is replacing this collector). The loop frees everything itself when it
    /// ends (see RunAsync), and the next collector's loop waits for that.
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        if (_loop == null)
        {
            _ping?.Dispose();
            _sites?.Dispose();
            _system.Dispose();
        }
    }

    /// <summary>Stops and waits a little for the loop to save and free everything (on exit, before the process ends).</summary>
    public void Dispose()
    {
        Stop();
        try { _loop?.Wait(2000); } catch { /* ignore */ }
    }
}
