using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Shoji.Core;

/// <summary>
/// Shoji used to be called WinDisplay. On the first start after the rename this takes over everything the old
/// name left behind (settings and history, Start with Windows including the admin task, the install, the Start menu
/// shortcut, the Settings → Apps entry and the phone dashboard's firewall rule), then removes the old pieces.
/// </summary>
public static class LegacyMigration
{
    const string OldName = "WinDisplay";
    const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinDisplay";

    static readonly string OldData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
    static readonly string OldInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", OldName);
    static readonly string OldShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldName + ".lnk");

    static bool HasNewConfig => File.Exists(Path.Combine(AppPaths.DataFolder, "config.json"));

    // Set once the move is done or the user said no, so neither asks again on every start (autostart included).
    const string MarkerKey = @"Software\Shoji", MarkerValue = "WinDisplayMigration";

    static bool Marked()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MarkerKey);
            return key?.GetValue(MarkerValue) != null;
        }
        catch
        {
            return false;
        }
    }

    static void Mark(string state)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MarkerKey);
            key.SetValue(MarkerValue, state);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't remember the WinDisplay move ({state}): {ex.Message}");
        }
    }

    /// <summary>Cheap checks only (it runs on every start): is there anything of WinDisplay left to take over?</summary>
    public static bool Needed
    {
        get
        {
            try
            {
                if (Marked())
                    return false;
                using var uninstall = Registry.CurrentUser.OpenSubKey(OldUninstallKey);
                return Directory.Exists(OldData) && !HasNewConfig
                       || Directory.Exists(OldInstallDir)
                       || uninstall != null
                       || File.Exists(OldShortcut)
                       || Startup.RunKeyPath(OldName) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Moves everything over. Returns false when this process should exit (installed Shoji was started, or the old app
    /// couldn't be closed). Saying no starts Shoji as it is and isn't asked again.
    /// </summary>
    public static bool Run()
    {
        bool wasInstalled = Directory.Exists(OldInstallDir);
        bool oldTask = Startup.TaskExists(OldName);
        bool oldRunKey = Startup.RunKeyPath(OldName) != null;

        string message = L.T("WinDisplay is now called Shoji.") + "\n\n" + L.T("Your settings, pages, deck buttons and history move over");
        if (oldTask || oldRunKey)
            message += L.T(", and Start with Windows will start Shoji instead");
        message += wasInstalled ? L.T(". The installed WinDisplay is replaced by Shoji.") : ".";
        if (oldTask && !Startup.IsElevated)
            message += "\n\n" + L.T("Windows will ask for admin permission once, to move the admin autostart.");
        message += "\n\n" + L.T("Cancel: start Shoji without moving anything (you won't be asked again).");
        if (MessageBox.Show(message, "Shoji", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            Mark("declined");
            Log.Write("Moving over from WinDisplay declined.");
            return true;
        }

        // 1. The old app has to be closed before its files can move (versions before 1.3 need the user to do it).
        if (!Installer.StopRunningCopy("continue", app: OldName))
            return false;

        try
        {
            // 2. Settings, pages, deck buttons, history, recap.
            if (Directory.Exists(OldData) && !HasNewConfig)
                Installer.CopyFolder(OldData, AppPaths.DataFolder);

            // 3. If WinDisplay was installed, Shoji takes its place as the installed app.
            string target = AppPaths.ExePath;
            if (wasInstalled && !AppPaths.IsInstalledCopy && !AppPaths.IsPortable)
            {
                Directory.CreateDirectory(AppPaths.InstallDir);
                Installer.CopyWithRetry(AppPaths.ExePath, AppPaths.InstalledExe);
                Installer.Register();
                target = AppPaths.InstalledExe;
            }

            // 4. Start with Windows: the Run key needs nothing special; the admin task and firewall rule need one UAC prompt.
            if (oldRunKey)
            {
                Startup.SetRunKey(OldName, null);
                Startup.SetRunKey("Shoji", target);
            }
            string script = "";
            if (oldTask)
                script += $"Unregister-ScheduledTask -TaskName '{OldName}' -Confirm:$false -ErrorAction SilentlyContinue; " + Startup.CreateTaskScript(target) + "; ";
            string oldExe = Path.Combine(OldInstallDir, OldName + ".exe").Replace("'", "''");
            script += $"Get-NetFirewallApplicationFilter -Program '{oldExe}' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule -ErrorAction SilentlyContinue";
            if (oldTask || UsedWebDashboard())
            {
                try { Startup.RunElevatedPowerShell(script); }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(L.T("Without admin permission the admin autostart couldn't be moved. " +
                                        "Turn it on again in the tray menu: Start with Windows → On, as admin."), "Shoji");
                }
            }

            // 5. Remove what's left of the old name.
            // Whatever can't be deleted stays (logged); the move itself is done, so it isn't offered again.
            Mark("done");
            TryDelete(() => File.Delete(OldShortcut));
            TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false));
            TryDelete(() => Directory.Delete(OldInstallDir, recursive: true));
            if (HasNewConfig)
                TryDelete(() => Directory.Delete(OldData, recursive: true));

            Log.Write($"Moved over from WinDisplay (was installed: {wasInstalled}, autostart: {(oldTask ? "admin" : oldRunKey ? "normal" : "off")}).");

            if (!AppPaths.SamePath(target, AppPaths.ExePath))
            {
                Installer.Launch(target);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Moving over from WinDisplay failed: {ex}");
            MessageBox.Show(L.F("Moving your WinDisplay settings over failed: {0}\nShoji starts anyway; the old files are untouched.", ex.Message),
                "Shoji", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }

    static bool UsedWebDashboard()
    {
        try
        {
            var config = Path.Combine(OldData, "config.json");
            return File.Exists(config) && File.ReadAllText(config).Contains("\"Key\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static void TryDelete(Action delete)
    {
        // The old exe can take a moment to be released after it exits.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 9)
            {
                System.Threading.Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                Log.Write($"Removing a WinDisplay leftover failed: {ex.Message}");
                return;
            }
        }
    }
}
