using System;
using System.Collections.Generic;

namespace Shoji.Sensors;

/// <summary>Everything the modules need for one refresh, gathered on a background thread.</summary>
public sealed record Snapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public TimeSpan Uptime { get; init; }

    public CpuInfo? Cpu { get; init; }
    public GpuInfo? Gpu { get; init; }
    public MemoryInfo? Memory { get; init; }
    public NetworkInfo? Network { get; init; }
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public IReadOnlyList<ProcessInfo> TopProcesses { get; init; } = Array.Empty<ProcessInfo>();
    public ActivityInfo? Activity { get; init; }
    public MediaInfo? Media { get; init; }
    public SystemInfo? System { get; init; }
    public WeatherState? Weather { get; init; }
    public PingInfo? Ping { get; init; }
    public DiskIoInfo? DiskIo { get; init; }
    public BatteryInfo? Battery { get; init; }
    public FpsInfo? Fps { get; init; }
    public PowerInfo? Power { get; init; }
    public IReadOnlyList<SiteStatus> Sites { get; init; } = Array.Empty<SiteStatus>();
    public RecapInfo? Recap { get; init; }

    /// <summary>The app in focus and how long the user has been away (always filled: scenes need them).</summary>
    public ForegroundInfo Foreground { get; init; } = ForegroundInfo.None;
    public double IdleSeconds { get; init; }

    /// <summary>Auto-discovered temperatures / fans (for the "temps" and "fans" modules).</summary>
    public IReadOnlyList<SensorReading> Temps { get; init; } = Array.Empty<SensorReading>();
    public IReadOnlyList<SensorReading> Fans { get; init; } = Array.Empty<SensorReading>();

    /// <summary>Raw sensor values by LibreHardwareMonitor identifier (for the "sensors" module).</summary>
    public IReadOnlyDictionary<string, SensorValue> Sensors { get; init; } = new Dictionary<string, SensorValue>();
}

public sealed record CpuInfo(
    string Name,
    double? LoadPercent,
    double? TemperatureC,
    double? ClockMhz,
    double? PowerW,
    IReadOnlyList<double> CoreLoads,
    int? Cores,
    int Threads,
    double? BaseMhz,
    double? VoltageV,
    // Why temperature/power are missing ("needs admin", "needs PawnIO"), or null.
    string? SensorHint);

public sealed record GpuInfo(
    string Name,
    double? LoadPercent,
    double? TemperatureC,
    double? HotSpotC,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? VramUsedMb,
    double? VramTotalMb,
    double? FanRpm,
    double? FanPercent,
    double? PowerW);

public sealed record MemoryInfo(double UsedGb, double TotalGb, double LoadPercent, double CommitUsedGb, double CommitTotalGb);

public sealed record NetworkInfo(
    string AdapterName,
    string? IpAddress,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    long TotalDownloadedBytes,
    long TotalUploadedBytes);

public sealed record DiskInfo(string Name, string Label, double UsedGb, double TotalGb);

public sealed record ProcessInfo(string Name, double CpuPercent, double MemoryMb);

public sealed record ActivityInfo(
    string ProcessName,
    string WindowTitle,
    TimeSpan FocusedFor,
    TimeSpan IdleFor,
    IReadOnlyList<AppUsage> TodayTopApps,
    TimeSpan TodayActive);

public sealed record AppUsage(string ProcessName, TimeSpan Duration);

public sealed record MediaInfo(
    string Title,
    string Artist,
    string App,
    bool IsPlaying,
    TimeSpan? Position,
    TimeSpan? Duration,
    byte[]? Thumbnail);

public sealed record SensorValue(string Name, string HardwareName, string Type, float? Value);

public sealed record SystemInfo(
    string Os,
    string OsBuild,
    string Host,
    string Cpu,
    string CpuCores,
    string Gpu,
    string GpuDriver,
    string Ram,
    string Board,
    string Bios,
    DateTime BootTime,
    string Displays);

public sealed record WeatherInfo(
    string Place,
    double TempC,
    double FeelsLikeC,
    int Humidity,
    double WindKmh,
    int Code,
    bool IsDay,
    IReadOnlyList<WeatherDay> Days,
    DateTime UpdatedAt,
    // Next 24 hours, starting with the current hour (in the location's local time).
    IReadOnlyList<WeatherHour> Hours);

public sealed record WeatherDay(DateTime Date, double MinC, double MaxC, int Code, int? RainChance);

public sealed record WeatherHour(DateTime Time, double TempC, int? RainChance, int Code, bool IsDay);

/// <summary>Either weather data, or a message explaining why there is none (no city set, offline…).</summary>
public sealed record WeatherState(WeatherInfo? Info, string? Message);

public sealed record PingInfo(string Host, double? LastMs, double? AverageMs, double? JitterMs, double LossPercent);

public sealed record DiskIoInfo(double ReadBytesPerSec, double WriteBytesPerSec);

public sealed record BatteryInfo(bool HasBattery, double Percent, bool Charging, bool PluggedIn, TimeSpan? Remaining);

/// <summary>One auto-discovered reading. Value is °C for temperatures, rpm for fans.</summary>
public sealed record SensorReading(string Label, string Group, double Value);

/// <summary>The window in focus (for scenes and the Activity tile).</summary>
public sealed record ForegroundInfo(int ProcessId, string ProcessName, string? Path, string Title, bool IsFullscreen)
{
    public static readonly ForegroundInfo None = new(0, "", null, "", false);
}

public sealed record FpsInfo(string App, double? Fps, double? OnePercentLow, double? FrameTimeMs, string? Error);
