using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Shoji.Core;

/// <summary>
/// The exe installs, updates and uninstalls itself, so it stays a single file:
///  - Install copies it to %LOCALAPPDATA%\Programs\Shoji (per user, no admin), adds a Start menu shortcut
///    and an entry in Settings → Apps with a proper uninstaller.
///  - Running a newer exe offers to update the installed one in place.
///  - Uninstall removes the app, shortcut, autostart, firewall rule and (if you want) your settings.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Shoji";
    const string Title = "Shoji";

    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Shoji.lnk");

    public static bool IsInstalled => File.Exists(AppPaths.InstalledExe);

    public static Version? InstalledVersion
    {
        get
        {
            if (!IsInstalled)
                return null;
            var v = FileVersionInfo.GetVersionInfo(AppPaths.InstalledExe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
    }

    const string SettingsKey = @"Software\Shoji";

    /// <summary>A copy named like an installer ("Shoji-Setup.exe") always shows the install dialog.</summary>
    public static bool IsSetupCopy =>
        System.IO.Path.GetFileNameWithoutExtension(AppPaths.ExePath).Contains("setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Offer install / update when started by hand from somewhere else (Downloads, a USB stick, the dist folder).
    /// Not in portable mode, not right after a restart, not when Windows starts it at logon, and not for a copy
    /// where the user ticked "Don't ask again".
    /// </summary>
    public static bool ShouldOffer(string[] args)
    {
#if DEBUG
        if (!IsSetupCopy) return false; // not while developing (dotnet run)
#endif
        if (AppPaths.IsInstalledCopy || args.Contains("--restarted") || args.Contains("--no-setup"))
            return false;
        if (IsSetupCopy)
            return true;
        if (AppPaths.IsPortable || args.Contains("--autostart"))
            return false;
        // Started by an autostart entry from before the --autostart flag existed: only right after logon.
        if (Environment.TickCount64 < 3 * 60_000 && AppPaths.SamePath(Startup.RunKeyPath(), AppPaths.ExePath))
            return false;
        return !Declined();
    }

    static bool Declined()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue("NoSetupPrompt") is string paths
                   && paths.Split('|').Any(p => AppPaths.SamePath(p, AppPaths.ExePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"Don't ask again for this copy".</summary>
    public static void Decline()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            var paths = (key.GetValue("NoSetupPrompt") as string ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!paths.Any(p => AppPaths.SamePath(p, AppPaths.ExePath)))
                paths.Add(AppPaths.ExePath);
            key.SetValue("NoSetupPrompt", string.Join("|", paths));
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't remember the setup choice: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- install / update

    /// <summary>
    /// Installs (or updates) from the running exe and starts the installed copy. True when it did.
    /// <paramref name="quiet"/> (winget's silent install): no dialogs, and the app is only started again when a
    /// running copy was stopped for the update.
    /// </summary>
    public static bool Install(bool quiet = false)
    {
        bool wasRunning = SingleInstance.IsRunning();
        if (!StopRunningCopy("install", quiet))
        {
            if (quiet)
                Log.Write("Quiet install: a running Shoji didn't exit, so nothing was installed.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(AppPaths.InstallDir);
            if (!AppPaths.IsInstalledCopy)
                CopyWithRetry(AppPaths.ExePath, AppPaths.InstalledExe);
            Register();
            MovePortableData();
        }
        catch (Exception ex)
        {
            Log.Write($"Install failed: {ex}");
            if (!quiet)
                MessageBox.Show(L.F("Installing failed: {0}", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        Log.Write($"Installed {AppPaths.VersionText} to {AppPaths.InstallDir}{(quiet ? " (quiet)" : "")}");
        if (!quiet || wasRunning)
            Launch(AppPaths.InstalledExe);
        return true;
    }

    /// <summary>Start menu shortcut + Settings → Apps entry. Cheap and idempotent; the installed copy calls it on start.</summary>
    public static void Register()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                string exe = AppPaths.InstalledExe;
                key.SetValue("DisplayName", "Shoji");
                key.SetValue("DisplayVersion", AppPaths.VersionText);
                key.SetValue("Publisher", "Shoji");
                key.SetValue("DisplayIcon", $"\"{exe}\",0");
                key.SetValue("InstallLocation", AppPaths.InstallDir);
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                if (File.Exists(exe))
                    key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                if (key.GetValue("InstallDate") == null)
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            if (!File.Exists(ShortcutPath))
                CreateShortcut(ShortcutPath, AppPaths.InstalledExe);
        }
        catch (Exception ex)
        {
            Log.Write($"Registering the install failed: {ex.Message}");
        }
    }

    static void CreateShortcut(string lnk, string target)
    {
        // WScript.Shell is on every Windows; it saves us declaring IShellLink by hand.
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell missing");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = L.T("Dashboard and control deck for a second screen");
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Installing from a portable copy brings its settings along (unless the PC already has some).</summary>
    static void MovePortableData()
    {
        if (!AppPaths.IsPortable || !Directory.Exists(AppPaths.PortableDataFolder))
            return;
        if (File.Exists(Path.Combine(AppPaths.RoamingDataFolder, "config.json")))
            return;
        CopyFolder(AppPaths.PortableDataFolder, AppPaths.RoamingDataFolder);
    }

    // ---------------------------------------------------------------- portable

    /// <summary>
    /// Makes this exe portable: drops the "portable" marker next to it, brings the current settings along,
    /// and restarts so they're used. False when the folder isn't writable (e.g. Program Files).
    /// </summary>
    public static bool MakePortable()
    {
        try
        {
            File.WriteAllText(AppPaths.PortableMarker,
                "This file makes Shoji portable: settings are kept in the \"data\" folder next to the exe.\r\nDelete it to use %APPDATA%\\Shoji instead.\r\n");
            if (!File.Exists(Path.Combine(AppPaths.PortableDataFolder, "config.json")) && Directory.Exists(AppPaths.RoamingDataFolder))
                CopyFolder(AppPaths.RoamingDataFolder, AppPaths.PortableDataFolder);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Making portable failed: {ex.Message}");
            MessageBox.Show(L.F("Can't make Shoji portable here: {0}\nCopy the exe to a folder you can write to (e.g. a USB stick) first.", ex.Message),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ---------------------------------------------------------------- uninstall

    /// <summary>"--uninstall" (from Settings → Apps). Asks, then removes everything it can.</summary>
    public static void Uninstall(bool quiet)
    {
        bool deleteData = false;
        if (!quiet)
        {
            if (MessageBox.Show(L.T("Uninstall Shoji?\n\nThis removes the app, its Start menu shortcut and Start with Windows."),
                    L.T("Uninstall Shoji"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var answer = MessageBox.Show(L.T("Also delete your settings (pages, deck buttons, scenes), history and logs?\n\n" +
                                             "Yes: delete them\nNo: keep them for a later reinstall"),
                L.T("Uninstall Shoji"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return;
            deleteData = answer == MessageBoxResult.Yes;
        }

        if (!StopRunningCopy("uninstall", quiet))
            return;

        // The admin logon task and the firewall rule (from the phone dashboard) need admin to remove: one UAC prompt.
        string exe = AppPaths.InstalledExe.Replace("'", "''");
        string script = "";
        if (Startup.TaskExists())
            script += "Unregister-ScheduledTask -TaskName 'Shoji' -Confirm:$false -ErrorAction SilentlyContinue; ";
        if (WebDashboardWasUsed())
            script += $"Get-NetFirewallApplicationFilter -Program '{exe}' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule -ErrorAction SilentlyContinue; ";
        bool leftovers = false;
        if (script.Length > 0)
        {
            try { Startup.RunElevatedPowerShell(script); }
            catch (System.ComponentModel.Win32Exception) { leftovers = true; } // UAC declined
        }

        TryDo(Startup.RemoveRunKey);
        TryDo(() => File.Delete(ShortcutPath));
        TryDo(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        TryDo(() => Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false));
        if (deleteData)
            TryDo(() => Directory.Delete(AppPaths.RoamingDataFolder, recursive: true));

        if (!quiet)
        {
            MessageBox.Show(L.T("Shoji has been removed.") +
                            (leftovers ? "\n\n" + L.T("The admin autostart task couldn't be removed without admin permission; " +
                                                       "delete \"Shoji\" in Task Scheduler if you like.") : "") +
                            "\n\n" + L.T("The PawnIO driver (if you installed it for CPU temperatures) is a separate program: " +
                                           "remove it in Settings → Apps if nothing else uses it."),
                L.T("Uninstall Shoji"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // An exe can't delete itself while running: a hidden PowerShell waits for this process to exit (we return
        // straight after this), then removes the folder, retrying while Windows still holds on to the exe.
        if (AppPaths.IsInstalledCopy)
        {
            string dir = AppPaths.InstallDir.Replace("'", "''");
            string cleanup =
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
                $"for ($i = 0; $i -lt 15; $i++) {{ Remove-Item -LiteralPath '{dir}' -Recurse -Force -ErrorAction SilentlyContinue; " +
                $"if (-not (Test-Path -LiteralPath '{dir}')) {{ break }}; Start-Sleep -Seconds 1 }}";
            TryDo(() => Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(cleanup)))
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            }));
        }
    }

    static bool WebDashboardWasUsed()
    {
        try
        {
            var path = Path.Combine(AppPaths.RoamingDataFolder, "config.json");
            return File.Exists(path) && File.ReadAllText(path).Contains("\"Key\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Asks a running copy to exit; if it's an old version that can't be asked, asks the user to close it.</summary>
    internal static bool StopRunningCopy(string what, bool quiet = false, string app = "Shoji")
    {
        while (!SingleInstance.RequestExit(TimeSpan.FromSeconds(10), app))
        {
            if (quiet || MessageBox.Show(L.F("{0} is still running. Close it (tray icon → Exit), then press OK to {1}.", app, L.T(what)),
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return false;
        }
        return true;
    }

    /// <summary>Starts the installed copy, as admin if autostart is set up that way (so CPU temperatures keep working).</summary>
    internal static void Launch(string exe)
    {
        bool admin = Startup.TaskExists() && !Startup.IsElevated;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--restarted --installed", Verb = admin ? "runas" : "" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--restarted --installed" }); // UAC declined
        }
    }

    internal static void CopyWithRetry(string from, string to)
    {
        // The old copy may need a moment to let go of its exe after exiting.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                // A downloaded exe carries "came from the internet" (Mark of the Web), and the copy keeps it, so
                // SmartScreen would warn again for the installed app. Drop it.
                try { File.Delete(to + ":Zone.Identifier"); }
                catch (Exception ex) { Log.Write($"Couldn't clear the download mark of {to}: {ex.Message}"); }
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    internal static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            if (!Path.GetFileName(file).Equals("log.txt", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    static void TryDo(Action a)
    {
        try { a(); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }
}
