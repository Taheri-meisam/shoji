using System;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Shoji.Core;

public enum StartupMode
{
    Off,
    /// <summary>HKCU Run key – no admin, but no CPU temperatures.</summary>
    Normal,
    /// <summary>Scheduled task with highest privileges – full sensors, no UAC prompt at logon.</summary>
    Elevated,
}

/// <summary>Manages "start with Windows".</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Shoji";

    static string ExePath => AppPaths.ExePath;

    /// <summary>Whether this process runs as administrator (can't change while it runs, so it's read once).</summary>
    public static bool IsElevated { get; } = ReadElevated();

    static bool ReadElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static StartupMode Current
    {
        get
        {
            if (TaskExists())
                return StartupMode.Elevated;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) != null ? StartupMode.Normal : StartupMode.Off;
        }
    }

    /// <summary>
    /// Changes the startup mode. Slow (schtasks, maybe a UAC prompt): call it off the UI thread. Throws Win32Exception
    /// when the admin prompt is declined, InvalidOperationException when the task couldn't be (un)registered; in both
    /// cases the old setting stays as it was.
    /// </summary>
    public static void Set(StartupMode mode)
    {
        // The elevated part first, and only when it really worked is the Run key changed (else autostart would be lost).
        if (mode == StartupMode.Elevated)
        {
            var code = RunElevatedPowerShell(CreateTaskScript());
            if (code != 0 || !TaskExists())
                throw new InvalidOperationException($"Registering the admin logon task failed (exit code {code?.ToString() ?? "none, timed out"}).");
        }
        else if (TaskExists())
        {
            var code = RunElevatedPowerShell($"Unregister-ScheduledTask -TaskName '{Name}' -Confirm:$false");
            if (code != 0 || TaskExists())
                throw new InvalidOperationException($"Removing the admin logon task failed (exit code {code?.ToString() ?? "none, timed out"}).");
        }

        // Registry part needs no elevation.
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (mode == StartupMode.Normal)
            key.SetValue(Name, $"\"{ExePath}\" --autostart");
        else
            key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Logon task that runs with highest privileges. Defaults that would bite us are overridden:
    /// no 3-day time limit, runs on battery, and a short delay so the second monitor is ready.
    /// </summary>
    internal static string CreateTaskScript(string? exe = null)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}".Replace("'", "''"); // e.g. O'Brien
        return string.Join("; ",
            $"$a = New-ScheduledTaskAction -Execute '{(exe ?? ExePath).Replace("'", "''")}' -Argument '--autostart'",
            $"$t = New-ScheduledTaskTrigger -AtLogOn -User '{user}'",
            "$t.Delay = 'PT10S'",
            $"$p = New-ScheduledTaskPrincipal -UserId '{user}' -LogonType Interactive -RunLevel Highest",
            "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew",
            $"Register-ScheduledTask -TaskName '{Name}' -Action $a -Trigger $t -Principal $p -Settings $s -Force");
    }

    /// <summary>Runs a PowerShell command as admin (UAC prompt). Returns its exit code, or null if it didn't finish in time.</summary>
    internal static int? RunElevatedPowerShell(string command, bool visible = false, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // EncodedCommand (UTF-16LE base64) sidesteps every quoting problem with paths.
            Arguments = "-NoProfile -ExecutionPolicy Bypass " + (visible ? "" : "-WindowStyle Hidden ") + "-EncodedCommand "
                        + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)),
            UseShellExecute = true,
            Verb = "runas", // shows the UAC prompt
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi);
        if (p == null || !p.WaitForExit(timeoutMs))
            return null;
        return p.ExitCode;
    }

    internal static bool TaskExists(string name = Name)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{name}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The exe the Run key starts, or null.</summary>
    public static string? RunKeyPath(string name = Name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = (key?.GetValue(name) as string)?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        // "C:\path\Shoji.exe" --autostart  →  C:\path\Shoji.exe
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : value.Trim('"');
        }
        return value;
    }

    /// <summary>The Run key value without the --autostart flag (written by versions before 1.6).</summary>
    static bool RunKeyLacksFlag()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string v && !v.Contains("--autostart");
    }

    /// <summary>Sets (or with null removes) a Run key value.</summary>
    internal static void SetRunKey(string name, string? exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (exe == null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, $"\"{exe}\" --autostart");
    }

    /// <summary>The exe the admin logon task starts, or null (slow-ish: asks PowerShell; call off the UI thread).</summary>
    public static string? TaskPath()
    {
        if (!TaskExists())
            return null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-ScheduledTask -TaskName '{Name}').Actions[0].Execute\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return null;
            var path = p.StandardOutput.ReadToEnd().Trim().Trim('"');
            p.WaitForExit(10_000);
            return path.Length > 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Points autostart at this exe again when it went stale: the registered exe no longer exists (moved portable folder),
    /// or this is the installed copy and autostart still points at an old download. Never steals autostart from a copy
    /// that still exists. Returns a message for the user when it couldn't fix it by itself.
    /// </summary>
    public static string? Repair()
    {
        static bool Stale(string? path) =>
            path != null && !AppPaths.SamePath(path, ExePath) && (!System.IO.File.Exists(path) || AppPaths.IsInstalledCopy);

        try
        {
            if (Stale(RunKeyPath()) || AppPaths.SamePath(RunKeyPath(), ExePath) && RunKeyLacksFlag())
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.SetValue(Name, $"\"{ExePath}\" --autostart");
                Log.Write("Autostart now points to " + ExePath);
            }

            if (Stale(TaskPath()))
            {
                if (!IsElevated)
                    return L.T("Start with Windows (admin) still points to the old location. Tray → Start with Windows → On, as admin fixes it.");
                var code = RunElevatedPowerShell(CreateTaskScript()); // already admin: no prompt
                Log.Write(code == 0 ? "Admin autostart task now points to " + ExePath : $"Re-registering the admin autostart task failed (exit code {code})");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Autostart repair failed: {ex.Message}");
        }
        return null;
    }

    public static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>Restarts the app elevated (UAC prompt) so CPU temperatures become available.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas", Arguments = "--restarted" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user said no to UAC
        }
    }
}
