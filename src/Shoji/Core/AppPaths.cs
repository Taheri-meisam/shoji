using System;
using System.IO;
using System.Reflection;

namespace Shoji.Core;

/// <summary>
/// Where the app and its data live. Three ways to run:
///   installed – %LOCALAPPDATA%\Programs\Shoji\Shoji.exe, data in %APPDATA%\Shoji
///   portable  – a file called "portable" next to the exe; data in a "data" folder next to it, nothing in the registry
///   loose     – any other location (e.g. Downloads); works like installed, and offers to install on start
/// </summary>
public static class AppPaths
{
    public static string ExePath { get; } = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
    public static string ExeDir { get; } = Path.GetDirectoryName(ExePath)!;

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Programs", "Shoji");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, "Shoji.exe");

    public static string RoamingDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Shoji");

    public static string PortableDataFolder { get; } = Path.Combine(ExeDir, "data");

    /// <summary>"portable" or "portable.txt" (Notepad likes adding .txt) next to the exe.</summary>
    public static string PortableMarker => Path.Combine(ExeDir, "portable");

    public static bool IsPortable { get; } = File.Exists(PortableMarker) || File.Exists(PortableMarker + ".txt");

    public static bool IsInstalledCopy { get; } = SamePath(ExePath, InstalledExe);

    public static string DataFolder => IsPortable ? PortableDataFolder : RoamingDataFolder;

    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
