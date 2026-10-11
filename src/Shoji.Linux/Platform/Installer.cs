using System;
using System.IO;
using Shoji.Core;

namespace Shoji.Linux;

/// <summary>Puts Shoji in the desktop's app menu (a .desktop entry and an icon in ~/.local/share), and takes it out again.</summary>
public static class Installer
{
    static string Share => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    static string DesktopFile => Path.Combine(Share, "applications", "shoji.desktop");
    static string IconFile => Path.Combine(Share, "icons", "hicolor", "256x256", "apps", "shoji.png");

    /// <summary>Installed from the .deb (scripts/build-deb.sh): /opt/shoji, with the app menu entry in /usr/share.</summary>
    public static bool FromPackage =>
        AppPaths.ExePath.StartsWith("/opt/shoji/", StringComparison.Ordinal) || AppPaths.ExePath.StartsWith("/usr/", StringComparison.Ordinal);

    public static bool InAppMenu => File.Exists(DesktopFile) || (FromPackage && File.Exists("/usr/share/applications/shoji.desktop"));

    /// <summary>False (and logged) when it failed.</summary>
    public static bool AddToAppMenu()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
            // Not AssetLoader: "--install" runs before (without) Avalonia, where that throws.
            using (var src = typeof(Installer).Assembly.GetManifestResourceStream("shoji.png") ?? throw new FileNotFoundException("The icon resource is missing"))
            using (var dst = File.Create(IconFile))
                src.CopyTo(dst);
            IconsChanged();
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, $"""
                [Desktop Entry]
                Type=Application
                Name=Shoji
                GenericName=PC dashboard
                GenericName[nb]=PC-dashbord
                Comment=Dashboard and control deck for a second screen
                Comment[nb]=Dashbord og knappepanel for en ekstra skjerm
                Exec="{AppPaths.ExePath}" --settings
                Icon=shoji
                Categories=System;Monitor;
                Keywords=dashboard;monitor;sensors;deck;weather;
                StartupNotify=false
                StartupWMClass=shoji
                """);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Adding to the app menu failed: {ex.Message}");
            return false;
        }
    }

    public static void RemoveFromAppMenu()
    {
        try
        {
            File.Delete(DesktopFile);
            File.Delete(IconFile);
            IconsChanged();
        }
        catch (Exception ex)
        {
            Log.Write($"Removing from the app menu failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Icon themes only rescan when the theme folder itself (hicolor) changes, not the 256x256/apps folder under it, so
    /// GNOME's app grid kept a gear for Shoji until the next login. Touching hicolor is what icon installers do.
    /// </summary>
    static void IconsChanged()
    {
        try
        {
            Directory.SetLastWriteTimeUtc(Path.Combine(Share, "icons", "hicolor"), DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not refresh the icon theme: {ex.Message}");
        }
    }
}
