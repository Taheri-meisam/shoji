using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// The Linux twin of the Windows Collector: a background loop that gathers a <see cref="Snapshot"/> every RefreshMs
/// and hands it to the UI (on this background thread; the UI marshals it). Only the data sources that an enabled tile needs are polled.
/// </summary>
public sealed class LinuxCollector : IDisposable
{
    readonly AppConfig _config;
    readonly Action<Snapshot> _publish;
    readonly CancellationTokenSource _cts = new();
    readonly HashSet<string> _modules;

    readonly bool _wantCpu, _wantGpu, _wantGpuReader, _wantHwmon, _historyMemory, _historyNetwork, _wantFocus;
    LinuxCpu? _cpu;
    LinuxGpu? _gpu;
    LinuxHwmon? _hwmon;
    LinuxFps? _fps;
    readonly bool _wantFps;
    readonly RecapTracker? _recap;
    readonly LinuxStats _system = new();
    readonly WeatherService? _weather;
    readonly PingMonitor? _ping;
    readonly PowerMeter? _power;
    readonly WebsiteMonitor? _sites;
    public LinuxMedia Media { get; } = new();

    Task? _loop;

    public LinuxCollector(AppConfig config, Action<Snapshot> publish)
    {
        _config = config;
        _publish = publish;
        // Every page is polled, so switching pages is instant and graphs on hidden pages keep going.
        var enabled = config.AllModules.Where(m => m.Enabled).ToList();
        _modules = enabled.Select(m => m.Type.ToLowerInvariant()).ToHashSet();
        // Demo mode (regression screenshots, Platform/DemoData.cs): the snapshots are made up, so nothing is polled or pinged.
        if (Demo.Enabled)
            return;

        // The status board and system orbit summarise several sources, so they need those polled too.
        bool summary = Has("status") || Has("overview");
        // A History tile needs the sources of the graphs it shows (default: CPU and GPU temperature).
        var series = enabled.Where(m => m.Type.Equals("history", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.OptList("Series") is { Count: > 0 } l ? l : new List<string> { "cpu.temp", "gpu.temp" })
            .Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        bool Graph(string prefix) => series.Any(x => x.StartsWith(prefix, StringComparison.Ordinal));
        _historyMemory = Graph("mem.");
        _historyNetwork = Graph("net.");
        _wantCpu = Has("cpu") || summary || Has("power") || Graph("cpu.") || Graph("power.");
        _wantGpu = Has("gpu") || summary || Has("power") || Graph("gpu.") || Graph("power.");
        // The temps and sensors tiles show the GPU too; NVIDIA's temperature only comes from nvidia-smi.
        _wantGpuReader = _wantGpu || Has("temps") || Has("sensors");
        _wantHwmon = Has("sensors") || Has("temps") || Has("fans");
        // Websites: every site from every Websites tile, checked once each (duplicates by URL are merged).
        var siteTiles = enabled.Where(m => m.Type.Equals("websites", StringComparison.OrdinalIgnoreCase)).ToList();
        var sites = siteTiles.SelectMany(m => m.Sites ?? new List<SiteRef>())
            .Where(x => WebsiteMonitor.Normalize(x.Url) != null)
            .GroupBy(x => WebsiteMonitor.Normalize(x.Url)!.ToString()).Select(g => g.First()).ToList();
        if (sites.Count > 0)
            _sites = new WebsiteMonitor(sites, siteTiles[0].OptInt("Interval", 60), config.Alerts.CertDays);
        if (enabled.FirstOrDefault(m => m.Type.Equals("power", StringComparison.OrdinalIgnoreCase)) is { } power)
            _power = new PowerMeter(power.OptInt("Other", 45), power.OptInt("Efficiency", 90) / 100.0);

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
        _wantFocus = NeedsFocus(config);
        // Frame rate from MangoHud's logs; the Last session tile pairs it with the full-screen app in focus.
        _wantFps = Has("fps") || Has("recap") || series.Contains("fps") || config.Web.Enabled;
        if (Has("recap"))
            _recap = new RecapTracker();
        if (Has("ping"))
            _ping = new PingMonitor(enabled.First(m => m.Type.Equals("ping", StringComparison.OrdinalIgnoreCase)).Host);
        else if (series.Contains("ping"))
            _ping = new PingMonitor(null);
    }

    /// <summary>A long-lived helper shared by tiles; see <see cref="ServiceStore"/>. It survives this collector.</summary>
    public T Service<T>(string key, Func<T> create) where T : class, IDisposable => ServiceStore.Get(key, create);

    bool Has(string type) => _modules.Contains(type);

    /// <summary>The app in focus is tracked only for scenes with an app or full-screen condition, and for the Last session tile (it spots games).</summary>
    static bool NeedsFocus(AppConfig c) =>
        c.ScenesEnabled && c.Scenes.Any(sc => sc.Enabled && (sc.Apps is { Count: > 0 } || sc.ExceptApps is { Count: > 0 } || sc.Fullscreen))
        || c.AllModules.Any(m => m.Enabled && m.Type.Equals("recap", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A key for everything that decides what this collector polls. If it hasn't changed (e.g. only the theme did),
    /// the dashboard keeps the running collector instead of restarting nvidia-smi.
    /// </summary>
    public static string KeyFor(AppConfig c) =>
        string.Join("|", c.RefreshMs, c.GpuIndex, c.Web.Enabled, c.Alerts.CertDays, c.City, NeedsFocus(c),
            string.Join(",", c.AllModules.Where(m => m.Enabled).Select(m =>
                $"{m.Type.ToLowerInvariant()}:{m.City}:{m.Host}:{m.OptBool("HideWeather")}:{m.Opt("Other")}:{m.Opt("Efficiency")}:{m.Opt("Interval")}:{m.Opt("Series")}:" +
                string.Join(";", m.Sites?.Select(x => $"{x.Url}|{x.Contains}") ?? Array.Empty<string>()) + ":" +
                string.Join(";", m.Sensors?.Select(s => s.Id) ?? Array.Empty<string>()))));

    /// <summary>Writes every hwmon / GPU sensor with its id to sensors.txt so users can pick some for the "sensors" tile.</summary>
    public string ExportSensorList()
    {
        var values = new Dictionary<string, SensorValue>(StringComparer.OrdinalIgnoreCase);
        new LinuxHwmon().ReadAll(values);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Shoji sensor list - copy an Id into a \"sensors\" module in config.json");
        sb.AppendLine($"Generated {DateTime.Now}.");
        sb.AppendLine();
        foreach (var (id, v) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"{id,-32} {v.Type,-12} {v.HardwareName,-12} {v.Name,-30} {v.Value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine();
        sb.AppendLine("Graphics cards (while a GPU tile is shown): gpu/<n>/load, temperature, hotspot, clock, memclock, vramused, vramtotal, fan, power");
        var path = System.IO.Path.Combine(AppConfig.Folder, "sensors.txt");
        System.IO.Directory.CreateDirectory(AppConfig.Folder);
        System.IO.File.WriteAllText(path, sb.ToString());
        return path;
    }

    public void Start()
    {
        if (Demo.Enabled)
        {
            Demo.Attach(_publish); // demo mode: the screenshot run publishes made-up snapshots at fixed moments
            return;
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    int RefreshMs => Math.Clamp(_config.RefreshMs, 250, 10_000);

    async Task RunAsync(CancellationToken ct)
    {
        // This loop owns the readers: it creates them and frees them in "finally", so a collector that's replaced
        // while nvidia-smi is still starting can't leak the process.
        try
        {
            try
            {
                if (_wantCpu)
                    _cpu = new LinuxCpu();
                if (_wantHwmon)
                    _hwmon = new LinuxHwmon();
                if (_wantGpuReader && !ct.IsCancellationRequested)
                {
                    _gpu = new LinuxGpu(RefreshMs);
                    _gpu.Start(ct); // waits up to 2 s for the first nvidia-smi sample (less if this collector is disposed)
                }
                if (_wantFps)
                    _fps = new LinuxFps();
            }
            catch (Exception ex)
            {
                Log.Write($"Sensor setup failed: {ex}");
            }
            if (ct.IsCancellationRequested)
                return;

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RefreshMs));
            do
            {
                try
                {
                    var snapshot = await CollectAsync().ConfigureAwait(false);
                    Record(snapshot);
                    _recap?.Sample(snapshot);
                    if (!ct.IsCancellationRequested)
                        _publish(_recap == null ? snapshot : snapshot with { Recap = _recap.Read() });
                }
                catch (Exception ex)
                {
                    Log.Write($"Collect failed: {ex}");
                }
            }
            while (await WaitAsync(timer, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.Write($"Collector loop ended: {ex}");
        }
        finally
        {
            CleanUp();
        }
    }

    /// <summary>Stops the readers and saves history / energy. Runs once, on the loop's thread (or in Dispose if the loop never started).</summary>
    void CleanUp()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1)
            return;
        try
        {
            _gpu?.Dispose();
            _ping?.Dispose();
            _sites?.Dispose();
            _power?.Save();
            _fps?.Dispose();
            _recap?.Flush(); // a game session that's still going is saved, not lost
            HistoryStore.Shared.Save();
        }
        catch (Exception ex)
        {
            Log.Write($"Collector clean-up failed: {ex.Message}");
        }
    }

    int _cleanedUp;

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    async Task<Snapshot> CollectAsync()
    {
        var mediaTask = Has("media") || Has("lyrics") ? Media.ReadAsync() : Task.FromResult<MediaInfo?>(null);
        var cpu = _wantCpu ? ReadCpu() : null;

        var gpus = _gpu?.ReadAll() ?? new List<(GpuInfo Info, bool Integrated, string Driver, bool Nvidia)>();
        if (gpus.Count > 0)
            PublishGpuNames(gpus.Select(g => g.Info.Name).ToList());
        GpuInfo? gpu = _wantGpu && gpus.Count > 0 ? gpus[Math.Clamp(_config.GpuIndex, 0, gpus.Count - 1)].Info : null;

        return new Snapshot
        {
            Time = DateTime.Now,
            Uptime = LinuxStats.ReadUptime(),
            Cpu = cpu,
            Power = _power?.Read(ReadPower(cpu, gpus), cpu),
            Sites = _sites?.Read() ?? (IReadOnlyList<SiteStatus>)Array.Empty<SiteStatus>(),
            Gpu = gpu,
            Memory = Has("memory") || Has("status") || Has("overview") || _historyMemory ? LinuxStats.ReadMemory() : null,
            Network = Has("network") || Has("overview") || _historyNetwork ? _system.ReadNetwork() : null,
            Disks = Has("disks") || Has("status") ? _system.ReadDisks() : Array.Empty<DiskInfo>(),
            DiskIo = Has("disks") ? _system.ReadDiskIo() : null,
            TopProcesses = Has("processes") ? _system.ReadTopProcesses(6) : Array.Empty<ProcessInfo>(),
            Sensors = Has("sensors") ? ReadSensors(gpus) : new Dictionary<string, SensorValue>(),
            Temps = Has("temps") ? ReadTemps(gpus) : Array.Empty<SensorReading>(),
            Fans = Has("fans") && _hwmon != null ? _hwmon.ReadFans() : Array.Empty<SensorReading>(),
            System = Has("system") ? LinuxSystemInfo.Current : null,
            Weather = _weather?.Read(),
            Ping = _ping?.Read(),
            Battery = Has("battery") || Has("status") ? LinuxStats.ReadBattery() : null,
            Fps = _fps?.Read(), // from MangoHud's frame-time log of the game that's running
            Foreground = _wantFocus ? LinuxFocus.Shared.Current : ForegroundInfo.None, // X11 / XWayland windows, native Wayland ones via GNOME's Window Calls
            IdleSeconds = IdleTracker.Seconds,
            Media = await mediaTask.ConfigureAwait(false),
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

    CpuInfo? ReadCpu()
    {
        if (_cpu == null)
            return null;
        var (load, threads) = _cpu.ReadLoad();
        double? temp = _cpu.ReadTemperature();
        double? power = _cpu.ReadPowerW();
        return new CpuInfo(
            Name: _cpu.Name,
            LoadPercent: load,
            TemperatureC: temp,
            ClockMhz: _cpu.ReadClockMhz(),
            PowerW: power,
            CoreLoads: threads,
            Cores: _cpu.Cores,
            Threads: _cpu.Threads,
            BaseMhz: _cpu.BaseMhz,
            VoltageV: null, // the core voltage needs a Super I/O driver and board-specific scaling
            SensorHint: _cpu.Hint(temp, power));
    }

    /// <summary>What the hardware measures for the power tile: CPU package (RAPL) and every add-in card. Integrated graphics is inside the CPU package.</summary>
    static HardwareSensors.PowerReadings ReadPower(CpuInfo? cpu, List<(GpuInfo Info, bool Integrated, string Driver, bool Nvidia)> gpus)
    {
        var discrete = gpus.Where(g => !g.Integrated).ToList();
        double? gpuW = null;
        foreach (var g in discrete)
            if (g.Info.PowerW is double w && w > 0 && w < 2000)
                gpuW = (gpuW ?? 0) + w;
        return new HardwareSensors.PowerReadings(cpu?.PowerW, gpuW, discrete.Count, null, null, null);
    }

    string? _gpuNamesKey;

    /// <summary>Graphics cards in GpuIndex order for the picker in Settings; same cards get "#1", "#2" like on Windows.</summary>
    void PublishGpuNames(List<string> names)
    {
        string key = string.Join("\n", names);
        if (key == _gpuNamesKey)
            return;
        _gpuNamesKey = key;
        HardwareSensors.GpuNames = names
            .Select((n, i) => names.Count(x => x == n) > 1 ? $"{n} #{names.Take(i + 1).Count(x => x == n)}" : n)
            .ToList();
    }

    /// <summary>Every hwmon temperature, plus the NVIDIA cards' (they have no hwmon chip with the proprietary driver).</summary>
    IReadOnlyList<SensorReading> ReadTemps(List<(GpuInfo Info, bool Integrated, string Driver, bool Nvidia)> gpus)
    {
        var list = new List<SensorReading>();
        var nvidia = gpus.Where(g => g.Nvidia).ToList();
        for (int i = 0; i < nvidia.Count; i++)
            if (nvidia[i].Info.TemperatureC is double t && t > 5 && t < 125)
                list.Add(new SensorReading(nvidia.Count > 1 ? $"GPU {i + 1}" : "GPU", "GPU", t));
        if (_hwmon != null)
            list.AddRange(_hwmon.ReadTemps());
        // CPU first, then GPU, drives and the rest, like the Windows tile.
        static int Rank(string g) => g switch { "CPU" => 0, "GPU" => 1, "Drive" => 2, "Board" => 3, _ => 4 };
        return list.Select((r, i) => (r, i)).OrderBy(x => Rank(x.r.Group)).ThenBy(x => x.i).Select(x => x.r).ToList();
    }

    /// <summary>Raw values for the "sensors" tile: hwmon inputs plus "gpu/&lt;n&gt;/&lt;value&gt;" for each card.</summary>
    Dictionary<string, SensorValue> ReadSensors(List<(GpuInfo Info, bool Integrated, string Driver, bool Nvidia)> gpus)
    {
        var d = new Dictionary<string, SensorValue>(StringComparer.OrdinalIgnoreCase);
        _hwmon?.ReadAll(d);
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i].Info;
            void Add(string id, string name, string type, double? v) =>
                d[$"gpu/{i}/{id}"] = new SensorValue(name, g.Name, type, v is double x ? (float)x : null);
            Add("load", "GPU Core", "Load", g.LoadPercent);
            Add("temperature", "GPU Core", "Temperature", g.TemperatureC);
            Add("hotspot", "GPU Hot Spot", "Temperature", g.HotSpotC);
            Add("clock", "GPU Core", "Clock", g.CoreClockMhz);
            Add("memclock", "GPU Memory", "Clock", g.MemoryClockMhz);
            Add("vramused", "GPU Memory Used", "SmallData", g.VramUsedMb);
            Add("vramtotal", "GPU Memory Total", "SmallData", g.VramTotalMb);
            Add("fan", "GPU Fan", "Control", g.FanPercent);
            Add("power", "GPU Package", "Power", g.PowerW);
        }
        return d;
    }

    /// <summary>Loops that were told to stop but hadn't finished yet; the process waits for them at exit so their saves complete.</summary>
    static readonly HashSet<Task> Ending = new();

    static LinuxCollector()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Task[] pending;
            lock (Ending)
                pending = Ending.ToArray();
            try { Task.WaitAll(pending, 3000); } catch { /* ignore */ }
        };
    }

    /// <summary>
    /// Returns at once (it's called on the UI thread when the config changes): the loop notices the cancellation within
    /// moments and frees the readers and saves in its "finally", off the UI thread. At exit the process waits for it.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _gpu?.Dispose(); // stops nvidia-smi now; doesn't wait
        var loop = _loop;
        if (loop == null)
        {
            CleanUp(); // never started
            return;
        }
        try { loop.Wait(50); } catch { /* ignore */ }
        if (loop.IsCompleted)
            return;
        lock (Ending)
            Ending.Add(loop);
        loop.ContinueWith(t => { lock (Ending) Ending.Remove(t); }, TaskScheduler.Default);
    }
}
