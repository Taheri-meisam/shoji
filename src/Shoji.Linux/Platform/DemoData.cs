using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Sensors;

namespace Shoji;

/// <summary>
/// Demo mode for the theme regression screenshots (scripts/theme-regression.sh): "SHOJI_DEMO=1 shoji --screenshot …"
/// renders made-up, always identical data instead of this PC's sensors. Nothing is read from the hardware, the desktop
/// or the network: the collector doesn't start (LinuxCollector.Start hands its publish callback here), the web pollers
/// get canned answers (Poller.CreateClient), the mixer and notifications show fixed contents. The clock in the snapshots
/// is fixed; scripts/theme-regression.sh also freezes the process clock (tests/regression/fixedclock.c) so every other
/// DateTime.Now (calendars, "5 min ago") agrees with it.
/// Only honoured together with --screenshot, so a stray SHOJI_DEMO can't turn the real dashboard into a fake one.
/// </summary>
public static class Demo
{
    public static bool Enabled { get; private set; }

    /// <summary>The fixed moment every demo snapshot is taken at (local time).</summary>
    public static readonly DateTime Now = new(2026, 10, 8, 14, 3, 43, DateTimeKind.Local);

    /// <summary>Samples of made-up history in the graphs (the shared store keeps 120); step Steps-1 is "now".</summary>
    const int Steps = 40;
    const int Prefill = 80;

    const string Host = "demo.shoji.invalid";
    public const string CalendarUrl = "https://" + Host + "/calendar.ics";

    static Action<Snapshot>? _publish;

    public static void Enable()
    {
        Enabled = true;
        // The hub's own made-up notifications (Sensors/Notifications.cs); the desktop's real ones are never read.
        Environment.SetEnvironmentVariable("SHOJI_DEMO_NOTIFICATIONS", "1");
        LinuxAudio.Shared.UseDemo(0.62, micMuted: false,
            new[] { new LinuxAudioOutput(1, "Speakers", true), new LinuxAudioOutput(2, "Headphones", false) },
            new[] { ("Spotify", 0.8), ("Firefox", 0.45), ("Discord", 1.0) });

        // The graphs' history goes straight into the store. The dashboard itself only ever gets the last snapshot:
        // gauges with their own memory (a LED arc's peak hold, a tape's trend arrow) measure it in real time, which
        // would make the picture depend on how fast this machine is.
        var history = HistoryStore.Shared;
        for (int i = -Prefill; i < Steps; i++)
            Record(Step(i));
        // The History tile's per-minute graphs: six hours of a slow day.
        history.Seed("cpu.temp", Series(360, k => 52 + 9 * Math.Sin(k / 37.0) + 4 * Math.Sin(k / 6.3)));
        history.Seed("gpu.temp", Series(360, k => 47 + 13 * Math.Sin(k / 51.0 + 1) + 3 * Math.Sin(k / 4.1)));
        history.Seed("cpu.load", Series(360, k => 30 + 18 * Math.Sin(k / 29.0) + 7 * Math.Sin(k / 3.7)));
        history.Seed("fps", Series(360, k => 110 + 20 * Math.Sin(k / 17.0) + 6 * Math.Sin(k / 2.9)));
    }

    /// <summary>Called by LinuxCollector.Start instead of polling: the screenshot run decides when snapshots are published.</summary>
    internal static void Attach(Action<Snapshot> publish) => _publish = publish;

    /// <summary>
    /// Hands the dashboard the snapshot. The screenshot run calls this twice at fixed moments, the second time so tiles
    /// whose data arrived late (calendar, web value) show it too; always the same values, so the count can't matter.
    /// </summary>
    public static void Publish() => _publish?.Invoke(Step(Steps - 1));

    static IReadOnlyList<double> Series(int count, Func<int, double> f) =>
        Enumerable.Range(0, count).Select(k => Math.Round(f(k), 1)).ToArray();

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
    }

    /// <summary>Smooth made-up wobble around a value; step <see cref="Steps"/>-1 is what the picture shows.</summary>
    static double W(int i, double mid, double swing, double speed) =>
        Math.Round(mid + swing * Math.Sin((i - Steps + 1) * speed) + swing * 0.35 * Math.Sin((i - Steps + 1) * speed * 3.1 + 0.7), 1);

    /// <summary>The snapshot for step <paramref name="i"/> (negative: before the run-up).</summary>
    public static Snapshot Step(int i)
    {
        var cores = Enumerable.Range(0, 32).Select(k => Math.Round(Math.Clamp(45 + 40 * Math.Sin(k * 1.7 + i * 0.3), 2, 100), 1)).ToArray();
        double down = Math.Max(0, W(i, 4.2e6, 2.5e6, 0.45)), up = Math.Max(0, W(i, 380e3, 250e3, 0.6));
        var weatherDays = Enumerable.Range(0, 7).Select(d => new WeatherDay(Now.Date.AddDays(d),
            7 + d % 3, 13 + (d * 5) % 6, new[] { 2, 61, 3, 0, 80, 1, 45 }[d], new int?[] { 10, 80, 35, 0, 65, 5, 20 }[d])).ToArray();
        var hour0 = Now.Date.AddHours(Now.Hour);
        var weatherHours = Enumerable.Range(0, 24).Select(h => new WeatherHour(hour0.AddHours(h),
            Math.Round(12.4 - 4 * Math.Sin((h + 2) / 24.0 * Math.PI) * (h > 4 ? 1 : 0.4), 1), h is >= 3 and <= 6 ? 70 : 10,
            h is >= 3 and <= 6 ? 61 : h < 3 ? 2 : 3, hour0.AddHours(h).Hour is >= 7 and < 19)).ToArray();

        return new Snapshot
        {
            Time = Now,
            Uptime = new TimeSpan(3, 4, 12, 0),
            Cpu = new CpuInfo("AMD Ryzen 9 7950X 16-Core Processor", W(i, 47, 12, 0.35), W(i, 64, 5, 0.2), W(i, 4850, 250, 0.5),
                W(i, 88, 20, 0.35), cores, 16, 32, 4500, 1.21, null),
            Gpu = new GpuInfo("AMD Radeon RX 7900 XTX", W(i, 71, 15, 0.3), W(i, 58, 4, 0.15), W(i, 71, 5, 0.15), W(i, 2480, 120, 0.4),
                1250, 9830, 24560, 1420, 38, W(i, 286, 40, 0.3)),
            Memory = new MemoryInfo(18.6, 63.2, W(i, 29.4, 1.5, 0.1), 24.1, 72.0),
            Network = new NetworkInfo("enp6s0", "192.168.1.42", down, up, 12_400_000_000, 1_100_000_000),
            Disks = new[]
            {
                new DiskInfo("/", "System", 412, 931), new DiskInfo("/home", "Home", 1220, 1863), new DiskInfo("/mnt/games", "Games", 1710, 1863),
            },
            DiskIo = new DiskIoInfo(12_500_000, 3_200_000),
            TopProcesses = new[]
            {
                new ProcessInfo("cyberpunk2077", 38.1, 9800), new ProcessInfo("firefox", 12.4, 1840), new ProcessInfo("Discord", 4.2, 610),
                new ProcessInfo("spotify", 2.9, 420), new ProcessInfo("gnome-shell", 2.1, 380), new ProcessInfo("pipewire", 0.8, 44),
            },
            Media = new MediaInfo("Midnight City", "M83", "Spotify", true, TimeSpan.FromSeconds(102), TimeSpan.FromSeconds(243), null),
            System = new SystemInfo("Ubuntu 26.04 LTS", "Linux 7.0.0", "shoji-demo", "AMD Ryzen 9 7950X", "16 cores, 32 threads",
                "AMD Radeon RX 7900 XTX", "Mesa 26.1", "64 GB DDR5-6000", "ASUS ROG STRIX X670E-F", "3201", Now - new TimeSpan(3, 4, 12, 0),
                "1280×480 @ 60 Hz"),
            Weather = new WeatherState(new WeatherInfo("Oslo", 12.4, 10.9, 71, 14.0, 2, true, weatherDays, Now.AddMinutes(-4), weatherHours), null),
            Ping = new PingInfo("1.1.1.1", W(i, 14.2, 3, 0.9), 15.8, 2.1, 0),
            Battery = new BatteryInfo(true, 76, true, true, null),
            Fps = new FpsInfo("Cyberpunk 2077", W(i, 118, 9, 0.7), 92, Math.Round(1000 / W(i, 118, 9, 0.7), 1), null),
            Power = new PowerInfo(W(i, 412, 50, 0.3), false, "estimate", new[]
            {
                new PowerPart("CPU", 88, false), new PowerPart("GPU", 286, false), new PowerPart("Rest of PC", 45, true),
            }, 1840, 620),
            Recap = new RecapInfo("Elden Ring", Now.Date.AddDays(-1).AddHours(21).AddMinutes(10), new TimeSpan(1, 52, 0), 96, 71, 78, 74, 41, 88, 31, false),
            Temps = new[]
            {
                new SensorReading("CPU package", "k10temp", 64), new SensorReading("CPU CCD 1", "k10temp", 61),
                new SensorReading("GPU", "amdgpu", 58), new SensorReading("GPU hotspot", "amdgpu", 71),
                new SensorReading("NVMe", "nvme", 44), new SensorReading("Motherboard", "nct6798", 38),
            },
            Fans = new[]
            {
                new SensorReading("CPU fan", "nct6798", 1240), new SensorReading("Pump", "nct6798", 2650),
                new SensorReading("Case front", "nct6798", 880), new SensorReading("GPU fan", "amdgpu", 1420),
            },
            Sensors = new Dictionary<string, SensorValue>
            {
                ["hwmon/nct6798/fan1"] = new("CPU fan", "nct6798", "Fan", 1240f),
                ["hwmon/nct6798/in0"] = new("Vcore", "nct6798", "Voltage", 1.212f),
                ["hwmon/nvme/temp1"] = new("Composite", "nvme", "Temperature", 44f),
            },
        };
    }

    // ---------------------------------------------------------------- web

    /// <summary>For Poller's HttpClient: the demo calendar and web value, anything else fails as if offline.</summary>
    public static HttpMessageHandler HttpHandler() => new CannedHttp();

    sealed class CannedHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri && uri.Host == Host)
            {
                string? body = uri.AbsolutePath switch
                {
                    "/calendar.ics" => Calendar(),
                    "/value.json" => "{\"visitors\": 1287, \"outside\": {\"temp\": 12.4}, \"status\": \"All systems go\"}",
                    _ => null,
                };
                if (body != null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
            }
            throw new HttpRequestException("demo mode: no network");
        }
    }

    /// <summary>A few events around <see cref="Now"/> (floating local times), for the agenda, calendar and clock tiles.</summary>
    static string Calendar()
    {
        var day = Now.Date;
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Shoji//Demo//EN\r\n");
        int n = 0;
        void Event(DateTime start, TimeSpan? length, string title, string? location = null)
        {
            sb.Append("BEGIN:VEVENT\r\nUID:demo-").Append(++n).Append("@shoji\r\nDTSTAMP:20261001T000000Z\r\n");
            if (length == null)
                sb.Append("DTSTART;VALUE=DATE:").Append(start.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                  .Append("\r\nDTEND;VALUE=DATE:").Append(start.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n");
            else
                sb.Append("DTSTART:").Append(start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))
                  .Append("\r\nDTEND:").Append((start + length.Value).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("SUMMARY:").Append(title).Append("\r\n");
            if (location != null)
                sb.Append("LOCATION:").Append(location).Append("\r\n");
            sb.Append("END:VEVENT\r\n");
        }
        Event(day.AddHours(9), TimeSpan.FromMinutes(15), "Standup");
        Event(day.AddHours(15).AddMinutes(30), TimeSpan.FromHours(1), "Design review", "Room Fuji");
        Event(day.AddHours(18), TimeSpan.FromMinutes(90), "Climbing", "Oslo Klatresenter");
        Event(day.AddDays(1), null, "Mum's birthday");
        Event(day.AddDays(1).AddHours(10), TimeSpan.FromMinutes(45), "Dentist");
        Event(day.AddDays(2).AddHours(7).AddMinutes(45), TimeSpan.FromHours(3), "Flight to Tokyo", "OSL");
        Event(day.AddDays(-5).AddHours(12), TimeSpan.FromHours(1), "Lunch with Aiko");
        Event(day.AddDays(9).AddHours(19), TimeSpan.FromHours(2), "Concert");
        return sb.Append("END:VCALENDAR\r\n").ToString();
    }
}
