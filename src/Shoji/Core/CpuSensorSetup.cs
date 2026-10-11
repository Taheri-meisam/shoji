using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Shoji.Core;

/// <summary>
/// One-click fix for "CPU temperature shows –": installs the PawnIO driver (via winget) if it's missing,
/// makes autostart use the admin task, and restarts Shoji as administrator.
/// </summary>
public static class CpuSensorSetup
{
    const string PawnIoKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    static bool? _installed;
    static DateTime _checkedAt;

    /// <summary>
    /// Whether PawnIO is installed. Read from the registry (LibreHardwareMonitor only checks once at startup,
    /// so it wouldn't notice an install made while we're running). Cached for a few seconds: it's asked every refresh.
    /// </summary>
    public static bool PawnIoInstalled
    {
        get
        {
            if (_installed == null || DateTime.UtcNow - _checkedAt > TimeSpan.FromSeconds(10))
            {
                _installed = HasKey(RegistryView.Registry64) || HasKey(RegistryView.Registry32);
                _checkedAt = DateTime.UtcNow;
            }
            return _installed.Value;
        }
    }

    static bool HasKey(RegistryView view)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(PawnIoKey);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Ready => Startup.IsElevated && PawnIoInstalled;

    /// <summary>
    /// Runs the whole fix. Blocks while winget runs, so call it off the UI thread.
    /// Returns true when an elevated copy was started and this one should exit.
    /// </summary>
    public static bool Run(Action<string> notify)
    {
        if (!PawnIoInstalled)
        {
            notify(L.T("Installing the PawnIO driver. Approve the Windows prompt; a window shows the progress."));
            const string install =
                "winget install -e --id namazso.PawnIO --accept-package-agreements --accept-source-agreements; " +
                "if ($LASTEXITCODE -ne 0) { Write-Host ''; Read-Host 'Install did not finish. Press Enter to close' }";
            try
            {
                Startup.RunElevatedPowerShell(install, visible: true, timeoutMs: 10 * 60_000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                notify(L.T("Cancelled. Admin permission is needed to install the driver."));
                return false;
            }

            _installed = null;
            if (!PawnIoInstalled)
            {
                notify(L.T("PawnIO couldn't be installed automatically. Opening pawnio.eu: install it, then choose Enable CPU temperature again."));
                try { Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); } catch { /* no browser */ }
                return false;
            }
        }

        // Keep it working after a reboot too: autostart through the elevated logon task.
        if (Startup.Current == StartupMode.Normal)
        {
            try { Startup.Set(StartupMode.Elevated); }
            catch (Exception ex) { Log.Write($"Switching autostart to admin failed: {ex.Message}"); }
        }

        // Even if we're already elevated, LibreHardwareMonitor only looks for the driver when it starts.
        if (Startup.RestartElevated())
            return true;
        notify(L.T("Cancelled. Shoji needs to run as administrator to read CPU temperatures."));
        return false;
    }
}
