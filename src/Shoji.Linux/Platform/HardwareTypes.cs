using System;
using System.Collections.Generic;

namespace Shoji.Sensors;

/// <summary>
/// Names the shared code expects from the Windows sensor layer (PowerMeter reads <see cref="PowerReadings"/>).
/// On Linux the readings come from <see cref="LinuxCollector"/>.
/// </summary>
public static class HardwareSensors
{
    public sealed record PowerReadings(double? CpuW, double? GpuW, int Gpus, double? PsuInW, double? PsuOutW, string? PsuName);

    /// <summary>Graphics cards in GpuIndex order (for the picker in Settings).</summary>
    public static IReadOnlyList<string> GpuNames { get; set; } = Array.Empty<string>();

    /// <summary>On Linux the sensors need no special driver (Windows needs PawnIO); readings are simply there or not.</summary>
    public static bool DriverReady => true;
    public static string? DriverHint => null;
}
