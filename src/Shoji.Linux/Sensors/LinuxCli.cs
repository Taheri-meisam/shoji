using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shoji.Sensors;

/// <summary>Small helpers shared by the Linux sensor readers: short-lived CLI calls and tolerant sysfs / procfs reads.</summary>
internal static class LinuxCli
{
    /// <summary>
    /// Runs a tool and returns its exit code and stdout, or (-1, "") if it can't start or takes longer than
    /// <paramref name="timeoutMs"/> (then it is killed). Never throws.
    /// </summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 1500)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            // Tools print numbers the C way regardless of the user's locale.
            psi.Environment["LC_ALL"] = "C";

            p = Process.Start(psi);
            if (p == null)
                return (-1, "");
            using var cts = new CancellationTokenSource(timeoutMs);
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token); // drained so a chatty tool can't block on a full pipe
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); } catch { /* ignore */ }
            return (p.ExitCode, output);
        }
        catch (Exception)
        {
            // Missing tool, timeout or a broken pipe: callers treat all of these as "no data".
            Kill(p);
            return (-1, "");
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>Starts a command without waiting for it (volume / media buttons). Never throws.</summary>
    public static void Fire(string file, params string[] args) => _ = RunAsync(file, args, 3000);

    public static void Kill(Process? p)
    {
        try
        {
            if (p != null && !p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }

    // ---------------------------------------------------------------- files

    public static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>First line of a sysfs attribute, trimmed; null if missing, unreadable or empty.</summary>
    public static string? ReadLine(string path)
    {
        var s = ReadText(path)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static long? ReadLong(string path) =>
        long.TryParse(ReadLine(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    public static double? ParseDouble(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) ? v : null;

    public static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

    public static string[] Dirs(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static string[] Files(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static double? Plausible(double? v, double min, double max) =>
        v is double x && !double.IsNaN(x) && x >= min && x <= max ? x : null;

    /// <summary>Same tidy-up as the Windows build, so both show "Intel Core i9-9900X @ 3.50GHz".</summary>
    public static string CleanCpuName(string name) => name
        .Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "")
        .Replace("  ", " ").Replace("  ", " ").Trim();
}
