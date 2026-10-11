using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace Shoji.Core;

/// <summary>
/// Installs a downloaded (and already verified) update, Linux edition: the tarball install (install.sh's
/// ~/.local/opt/shoji) swaps its binary in place and restarts. A copy installed from the .deb is left to the
/// package manager, which owns /opt/shoji.
/// </summary>
public static class SelfUpdate
{
    /// <summary>Tests point this at a scratch folder, so an update never touches the real install.</summary>
    static string? OverrideDir => Environment.GetEnvironmentVariable("SHOJI_INSTALL_DIR") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    static string TarballDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "opt", "shoji");

    /// <summary>The binary an update replaces: the running single-file "shoji" (not "dotnet shoji.dll" while developing).</summary>
    static string? Target =>
        OverrideDir is { } dir ? Path.Combine(dir, "shoji")
        : Path.GetFileName(AppPaths.ExePath) == "shoji" ? AppPaths.ExePath : null;

    /// <summary>What kind of copy this is, for the Updates card.</summary>
    public static string Describe =>
        Linux.Installer.FromPackage && OverrideDir == null ? L.T("Installed from a package (.deb)")
        : Target is not { } t ? L.T("Development build")
        : AppPaths.SamePath(Path.GetDirectoryName(t), TarballDir) ? L.T("Installed in ~/.local/opt/shoji")
        : L.F("Running from {0}", Path.GetDirectoryName(t));

    /// <summary>Why this copy can't update itself (null = it can).</summary>
    public static string? Blocker =>
        Linux.Installer.FromPackage && OverrideDir == null ? L.T("Installed from a package: install the new .deb (or update it with your package manager).")
        : Target == null ? L.T("This is a development build (started with dotnet): download the new version instead.")
        : RuntimeInformation.OSArchitecture != Architecture.X64 ? L.T("Releases are built for x64 PCs only: build the new version from source.")
        : null;

    public static UpdateAsset? AssetFor(UpdateInfo info) => info.Linux;

    /// <summary>Puts the new binary in place and starts it once this process has exited. Null on success, else the reason.</summary>
    public static string? Install(string file, UpdateInfo info)
    {
        string target = Target ?? throw new UpdateException(Blocker ?? L.T("Can't update this copy."));
        string work = Path.Combine(Path.GetTempPath(), $"shoji-update-{Environment.ProcessId}");
        try
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            // .NET's tar reader refuses entries that would land outside the folder.
            using (var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
                TarFile.ExtractToDirectory(gz, work, overwriteFiles: true);
            string? binary = Directory.EnumerateFiles(work, "shoji", SearchOption.AllDirectories).FirstOrDefault();
            if (binary == null)
                return L.T("The download has no shoji program in it.");

            string dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);
            // Copy aside and rename into place (as install.sh does): writing over a running binary fails with
            // "Text file busy", while a rename just points future starts at the new file.
            string fresh = target + ".new";
            File.Copy(binary, fresh, overwrite: true);
            if (!OperatingSystem.IsWindows()) // always true here; tells the analyzer the call is fine
                File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(fresh, target, overwrite: true);
            // Licence texts of Shoji's fonts and icons travel with it.
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(binary)!)
                         .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("LICENSE") || n.StartsWith("OFL-") || n.StartsWith("NOTICE") || n.StartsWith("THIRD-PARTY"))))
            {
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true); }
                catch (Exception ex) { Log.Write($"Update: couldn't copy {Path.GetFileName(f)}: {ex.Message}"); }
            }
            Log.Write($"Updated {target} to {info.Version}; restarting");
            Restart(target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Log.Write($"Update install failed: {ex}");
            return L.F("Couldn't install the update: {0}", ex.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch { /* temp folder; best effort */ }
        }
    }

    /// <summary>
    /// Starts the new binary once this process is gone: its single-instance lock (a flock on shoji.lock) is only
    /// released at exit, and a copy started earlier would just hand over to this one and quit. A small shell waits
    /// for this PID (up to 30 s), then a second more; "--restarted" also makes the new copy wait for the lock.
    /// </summary>
    static void Restart(string target)
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i=0; while kill -0 \"$1\" 2>/dev/null && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done; sleep 1; exec \"$0\" --restarted");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi)?.Dispose();
    }
}
