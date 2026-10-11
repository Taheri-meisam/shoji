using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Shoji.Core;

/// <summary>
/// Installs a downloaded (and already verified) update, Windows edition:
///  - installed copy: runs the new Shoji-Setup.exe with --install, the same path as "Update the installed app"
///    (it asks this copy to exit, copies itself over and starts the installed app);
///  - portable or loose copy: takes Shoji.exe out of the portable zip, and a hidden PowerShell swaps it in once this
///    process has exited and starts it again (like the uninstall clean-up in Installer.cs).
/// </summary>
public static class SelfUpdate
{
    /// <summary>Scoop keeps its apps in ...\scoop\apps\shoji\&lt;version&gt;; it updates them itself.</summary>
    static bool FromScoop => AppPaths.ExeDir.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>What kind of copy this is, for the Updates card.</summary>
    public static string Describe =>
        AppPaths.IsInstalledCopy ? L.T("Installed")
        : FromScoop ? L.T("Installed with Scoop")
        : AppPaths.IsPortable ? L.T("Portable")
        : L.T("Not installed");

    /// <summary>Why this copy can't update itself (null = it can).</summary>
    public static string? Blocker =>
        FromScoop ? L.T("Installed with Scoop: update it with \"scoop update shoji\".") : null;

    public static UpdateAsset? AssetFor(UpdateInfo info) => AppPaths.IsInstalledCopy ? info.Windows : info.WindowsPortable;

    /// <summary>Starts the installation; null when this copy should now exit (the new version starts by itself), else the reason.</summary>
    public static string? Install(string file, UpdateInfo info)
    {
        try
        {
            if (AppPaths.IsInstalledCopy)
            {
                // The setup asks this copy to exit through SingleInstance, and starts the installed app when done.
                Process.Start(new ProcessStartInfo(file) { Arguments = "--install", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! })?.Dispose();
                return null;
            }
            return ReplaceRunningExe(file, info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"Update install failed: {ex}");
            return L.F("Couldn't install the update: {0}", ex.Message);
        }
    }

    static string? ReplaceRunningExe(string file, UpdateInfo info)
    {
        // Next to the exe, so the final move stays on one drive (and a read-only folder shows up now, not after exiting).
        string fresh = AppPaths.ExePath + ".new";
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("Shoji.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return L.T("The portable zip has no Shoji.exe in it.");
            entry.ExtractToFile(fresh, overwrite: true);
        }
        else
        {
            File.Copy(file, fresh, overwrite: true);
        }

        // An exe can't be overwritten while it runs: a hidden PowerShell waits for this process to exit, moves the new
        // exe into place (retrying while Windows still holds the old one), then starts it. If the move keeps failing
        // the old exe starts again, so the dashboard comes back either way.
        string exe = AppPaths.ExePath.Replace("'", "''"), src = fresh.Replace("'", "''");
        string script =
            $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
            "for ($i = 0; $i -lt 30; $i++) { " +
            $"try {{ Move-Item -LiteralPath '{src}' -Destination '{exe}' -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
            $"Start-Process -FilePath '{exe}' -ArgumentList '--restarted'";
        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
        Log.Write($"Updating {AppPaths.ExePath} to {info.Version} once this copy has exited");
        return null;
    }
}
