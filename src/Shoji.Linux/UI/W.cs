using System;

namespace Shoji.UI;

/// <summary>Formatting helpers shared by the tiles (same output as on Windows).</summary>
public static partial class W
{
    public static string Format(double? value, string format, string unit) =>
        value.HasValue ? value.Value.ToString(format) + unit : "–";

    /// <summary>A °C reading in the configured unit, e.g. "61°" (or " °C" / " °F" with <paramref name="unit"/>).</summary>
    public static string Temp(double? celsius, bool unit = false)
    {
        if (celsius is not double c)
            return "–";
        double v = Theme.Fahrenheit ? c * 9 / 5 + 32 : c;
        return v.ToString("0") + (unit ? Theme.Fahrenheit ? " °F" : " °C" : "°");
    }

    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1000 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return bytes.ToString(bytes >= 100 || i == 0 ? "0" : "0.0") + " " + units[i];
    }

    public static string Rate(double bytesPerSecond)
    {
        // Network speeds read more naturally in bits (Mbps) like ISPs advertise.
        double bits = bytesPerSecond * 8;
        if (bits >= 1e9) return (bits / 1e9).ToString("0.00") + " Gbps";
        if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0") + " Mbps";
        if (bits >= 1e3) return (bits / 1e3).ToString("0") + " Kbps";
        return bits.ToString("0") + " bps";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
}
