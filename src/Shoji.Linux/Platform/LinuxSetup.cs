using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>What this PC lets Shoji read or do, with the one command that unlocks each missing piece.</summary>
public static class LinuxSetup
{
    /// <param name="Setup">Turns it on from Settings in one tap (when no command is needed), instead of a command to copy.</param>
    public sealed record Capability(string Name, string Icon, bool Ok, string Detail, string? Fix, Action? Setup = null);

    static List<Capability>? _cache;
    static DateTime _cachedAt;

    public static IReadOnlyList<Capability> Capabilities()
    {
        if (_cache != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(5))
            return _cache;
        var list = new List<Capability>();
        var hwmon = HwmonNames();
        bool cpuTemp = hwmon.Any(n => n is "coretemp" or "k10temp" or "zenpower" or "cpu_thermal" or "soc_thermal") || ThermalZones().Any(t => t is "x86_pkg_temp" or "cpu_thermal" or "cpu-thermal" or "acpitz");
        list.Add(new(L.T("CPU load, clock and temperature"), "memory", cpuTemp, L.T(cpuTemp ? "From /proc and the CPU's hwmon sensor" : "Temperature needs the CPU sensor driver"), cpuTemp ? null : "sudo modprobe coretemp   # or k10temp on AMD"));
        bool rapl = Readable("/sys/class/powercap/intel-rapl:0/energy_uj");
        bool hasRapl = File.Exists("/sys/class/powercap/intel-rapl:0/energy_uj");
        list.Add(new(L.T("CPU power"), "bolt", rapl, L.T(rapl ? "From RAPL energy counters" : hasRapl ? "The energy counter is readable by root only (a kernel default since 2020)" : "No RAPL counters on this CPU"),
            !rapl && hasRapl ? "echo 'SUBSYSTEM==\"powercap\", ACTION==\"add\", RUN+=\"/bin/chmod o+r /sys%p/energy_uj\"' | sudo tee /etc/udev/rules.d/90-shoji-rapl.rules && sudo chmod o+r /sys/class/powercap/intel-rapl:*/energy_uj" : null));
        bool nvidia = Which("nvidia-smi"), amd = Directory.Exists("/sys/class/drm") && Directory.GetDirectories("/sys/class/drm", "card*").Any(d => File.Exists(Path.Combine(d, "device", "gpu_busy_percent")));
        // Only an NVIDIA card needs a driver installed for its sensors; Intel and AMD graphics come with the kernel.
        bool nvidiaCard = !nvidia && PciVendors().Contains("0x10de");
        list.Add(new(L.T("GPU load, temperature, VRAM, power"), "developer_board", nvidia || amd,
            L.T(nvidia ? "From nvidia-smi" : amd ? "From the amdgpu driver" : nvidiaCard ? "The NVIDIA card needs its proprietary driver (nvidia-smi)" : "Intel graphics: clock only (the driver reports no load or temperature)"),
            nvidiaCard ? "sudo ubuntu-drivers install   " + L.T("# installs the recommended NVIDIA driver") : null));
        bool fans = hwmon.Count > 0 && Directory.GetDirectories("/sys/class/hwmon").Any(d => Directory.GetFiles(d, "fan*_input").Length > 0);
        list.Add(new(L.T("Fans"), "toys", fans, L.T(fans ? "From the motherboard's hwmon driver" : "No fan sensors exposed by the motherboard driver"), fans ? null : "sudo apt install lm-sensors && sudo sensors-detect --auto"));
        list.Add(new(L.T("Media (now playing)"), "play_circle", Which("busctl"), L.T("Any player that supports MPRIS (Spotify, Firefox, Chrome, VLC, Rhythmbox…)"), Which("busctl") ? null : "sudo apt install systemd"));
        bool wpctl = Which("wpctl"), pactl = Which("pactl");
        list.Add(new(L.T("Volume and microphone"), "volume_up", wpctl || pactl, L.T(wpctl ? "PipeWire, through wpctl" : pactl ? "Through pactl" : "Needs wpctl (PipeWire) or pactl"), wpctl || pactl ? null : "sudo apt install wireplumber"));
        // The app in focus (scene app / full-screen rules): X11 and XWayland windows through xprop; native Wayland windows
        // only through GNOME's "Window Calls" extension (Wayland itself doesn't tell other apps).
        bool xprop = LinuxFocus.HasX && Which("xprop"), waylandFocus = LinuxFocus.IsWayland, gnome = LinuxFocus.IsGnome;
        bool calls = waylandFocus && gnome && LinuxFocus.Shared.WindowCalls == true;
        list.Add(new(L.T("App in focus (scenes)"), "apps", xprop && (!waylandFocus || calls),
            L.T(!xprop ? LinuxFocus.HasX ? "Needs xprop to see X11 / XWayland apps" : "No X11 display, and Wayland keeps the focused window private"
            : !waylandFocus ? "From the X server, through xprop"
            : calls ? "X11 / XWayland apps through xprop, native Wayland apps through the Window Calls extension"
            : gnome ? "X11 / XWayland apps (most games) only; native Wayland apps need the GNOME extension \"Window Calls\""
            : "X11 / XWayland apps (most games) only; this desktop doesn't tell apps about native Wayland windows"),
            !xprop && LinuxFocus.HasX ? "sudo apt install x11-utils"
            : waylandFocus && gnome && !calls ? "xdg-open https://extensions.gnome.org/extension/4724/window-calls/   " + L.T("# install \"Window Calls\", then sign out and in") : null));
        // xdotool only reaches X11 apps; on Wayland, wtype works on wlroots desktops and ydotool everywhere (with ydotoold running).
        bool wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
        bool keys = wayland ? Which("wtype") || Which("ydotool") : Which("xdotool");
        list.Add(new(L.T("Deck hotkeys"), "keyboard", keys,
            L.T(keys ? wayland && !Which("wtype") ? "Through ydotool (its ydotoold service must be running)" : "Sends keys to the app in focus"
                 : wayland ? "Needed only for \"Press keys\" deck buttons; on Wayland needs ydotool (with ydotoold running) or wtype" : "Needed only for \"Press keys\" deck buttons"),
            keys ? null : wayland ? "sudo apt install ydotool && sudo usermod -aG input $USER && systemctl --user enable ydotool   " + L.T("# then sign out and in") : "sudo apt install xdotool"));
        bool shots = Which("gnome-screenshot") || Which("spectacle");
        list.Add(new(L.T("Deck screenshots"), "photo_camera", shots, L.T(shots ? "Screenshot buttons work" : "Needed only for screenshot deck buttons"), shots ? null : "sudo apt install gnome-screenshot"));
        // Frame rate: Linux has no system-wide frame timing, but MangoHud can log every game it runs into Shoji's folder.
        bool mango = MangoHud.Installed, logging = mango && MangoHud.Configured;
        list.Add(new(L.T("Frame rate (MangoHud)"), "speed", logging,
            L.T(logging ? "MangoHud logs frame times for Shoji. Start games with \"mangohud %command%\" in Steam's launch options (or MANGOHUD=1); Right Shift+F12 hides its in-game overlay"
            : mango ? "For the FPS and Last session tiles. \"Set up\" adds three lines to ~/.config/MangoHud/MangoHud.conf (yours stay) so MangoHud logs frame times; then start games with \"mangohud %command%\" in Steam's launch options (or MANGOHUD=1)"
            : "For the FPS and Last session tiles: Linux has no system-wide frame counter, MangoHud measures the games it runs"),
            mango ? null : "sudo apt install mangohud",
            mango && !logging ? () => { MangoHud.SetUp(); _cache = null; } : null));
        _cache = list;
        _cachedAt = DateTime.UtcNow;
        return list;
    }

    /// <summary>Capabilities that are off but could be turned on (have a fix).</summary>
    public static IReadOnlyList<Capability> Missing() => Capabilities().Where(c => !c.Ok && (c.Fix != null || c.Setup != null)).ToList();

    static List<string> HwmonNames()
    {
        var names = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories("/sys/class/hwmon"))
                if (File.Exists(Path.Combine(d, "name")))
                    names.Add(File.ReadAllText(Path.Combine(d, "name")).Trim());
        }
        catch
        {
            // no hwmon
        }
        return names;
    }

    static List<string> ThermalZones()
    {
        var types = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
                if (File.Exists(Path.Combine(d, "type")))
                    types.Add(File.ReadAllText(Path.Combine(d, "type")).Trim());
        }
        catch
        {
            // no thermal zones
        }
        return types;
    }

    /// <summary>PCI vendor ids of the display controllers (class 0x03xxxx), e.g. "0x10de" for NVIDIA.</summary>
    static HashSet<string> PciVendors()
    {
        var vendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.GetDirectories("/sys/bus/pci/devices"))
            {
                string cls = File.Exists(Path.Combine(d, "class")) ? File.ReadAllText(Path.Combine(d, "class")).Trim() : "";
                if (cls.StartsWith("0x03", StringComparison.Ordinal) && File.Exists(Path.Combine(d, "vendor")))
                    vendors.Add(File.ReadAllText(Path.Combine(d, "vendor")).Trim());
            }
        }
        catch
        {
            // no PCI bus (ARM boards)
        }
        return vendors;
    }

    static bool Readable(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':').Any(dir => File.Exists(Path.Combine(dir, tool)));
}
