using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoji.Core;

/// <summary>User settings, stored as JSON in %APPDATA%\Shoji\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Which monitor to land on:
    ///   "auto"      – the smallest non-primary monitor (falls back to primary if only one)
    ///   "primary"   – the primary monitor
    ///   "1", "2"…   – monitor number in the order Windows reports them
    ///   "\\.\DISPLAY2" – an exact device name (see tray menu → Monitors)
    /// </summary>
    public string Monitor { get; set; } = "auto";

    /// <summary>Sensor refresh interval in milliseconds.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Extra UI scale (1.0 = automatic sizing for the screen).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Number of tile columns; 0 = pick automatically from the screen shape.</summary>
    public int Columns { get; set; } = 0;

    public bool Topmost { get; set; } = false;

    /// <summary>Windows: hide the taskbar on the dashboard's screen while the dashboard is there (never the main taskbar).</summary>
    public bool CoverTaskbar { get; set; } = true;

    /// <summary>Nudges the layout a few pixels every few minutes to avoid burn-in on OLED panels.</summary>
    public bool PixelShift { get; set; } = true;

    public bool Use24HourClock { get; set; } = true;

    /// <summary>midnight, modern, futuristic, cyberpunk, tokyo, sumi, sakura, oled (see UI/Themes.cs).</summary>
    public string Theme { get; set; } = "midnight";

    /// <summary>"C" or "F". Thresholds below are always in °C.</summary>
    public string TemperatureUnit { get; set; } = "C";

    /// <summary>Optional colour overrides on top of the theme (null = use the theme's colour).</summary>
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? TileColor { get; set; }

    /// <summary>Temperatures (°C) at which values turn orange / red.</summary>
    public double TempWarn { get; set; } = 70;
    public double TempCrit { get; set; } = 85;

    /// <summary>Which GPU to show when there are several (0 = first discrete GPU found).</summary>
    public int GpuIndex { get; set; } = 0;

    /// <summary>Gentle theme animations (drifting petals, neon flicker, scan lines). Also helps against burn-in.</summary>
    public bool Animations { get; set; } = true;

    /// <summary>While something plays, its cover is shown blurred and dimmed behind the tiles of a page with a Now playing tile.</summary>
    public bool AlbumArtBackdrop { get; set; } = true;

    /// <summary>
    /// Where the weather is for: a city ("Tokyo", "Berlin, DE"), or "lat,lon". Empty = Windows location.
    /// A Weather, Hourly forecast or Clock tile can still set its own city, which then wins.
    /// </summary>
    public string? City { get; set; }

    /// <summary>Look of the Settings window: "auto" (follow Windows), "light" or "dark".</summary>
    public string SettingsLook { get; set; } = "auto";

    /// <summary>"auto" (the system's language), "en" or "nb" — see <see cref="L.Languages"/>.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Layout upgrades already applied to this file. 2 = the main page got the Recommended preset (once; afterwards
    /// it's yours to change). Missing in older files, so they read as 0.
    /// </summary>
    public int LayoutVersion { get; set; }

    public const int CurrentLayoutVersion = 2;

    /// <summary>Dashboard pages; swipe left/right on the screen (or tap the dots) to switch.</summary>
    public List<PageConfig> Pages { get; set; } = DefaultPages();

    /// <summary>Switch to the next page every N seconds (0 = off).</summary>
    public int PageCycleSeconds { get; set; } = 0;

    /// <summary>Holding a finger (or the mouse button) on a tile opens its quick edit sheet on the dashboard.</summary>
    public bool LongPressEdit { get; set; } = true;

    /// <summary>Version 1 had a single list of tiles; it becomes the first page on load.</summary>
    [JsonPropertyName("Modules")]
    public List<ModuleConfig>? LegacyModules { get; set; }

    /// <summary>Scenes switch page / theme automatically by the app in focus, full-screen games, idle time or time of day.</summary>
    public bool ScenesEnabled { get; set; } = true;
    public List<SceneConfig> Scenes { get; set; } = DefaultScenes();

    public DayNightConfig DayNight { get; set; } = new();
    public AlertsConfig Alerts { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public NightConfig Night { get; set; } = new();
    public ObsConfig Obs { get; set; } = new();
    /// <summary>Update check (see Core/UpdateChecker.cs).</summary>
    public UpdatesConfig Updates { get; set; } = new();

    /// <summary>iCal links shared by the Agenda, Clock and Calendar tiles (an Agenda tile can still list its own).</summary>
    public List<CalendarLink> Calendars { get; set; } = new();

    [JsonIgnore]
    public bool Fahrenheit => TemperatureUnit.StartsWith("F", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every tile on every page.</summary>
    [JsonIgnore]
    public IEnumerable<ModuleConfig> AllModules => Pages.SelectMany(p => p.Modules);

    public int PageIndex(string? nameOrNumber)
    {
        if (string.IsNullOrWhiteSpace(nameOrNumber))
            return -1;
        if (int.TryParse(nameOrNumber, out int n))
            return n >= 1 && n <= Pages.Count ? n - 1 : -1;
        return Pages.FindIndex(p => p.Name.Equals(nameOrNumber.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static List<PageConfig> DefaultPages() => new()
    {
        new() { Name = "Main", Modules = ModuleCatalog.Preset("main") },
        new() { Name = "Deck", Modules = ModuleCatalog.Preset("deck") },
        new() { Name = "Gaming", Modules = ModuleCatalog.Preset("gaming") },
        new() { Name = "Media", Modules = ModuleCatalog.Preset("media") },
        new() { Name = "Ambient", Modules = ModuleCatalog.Preset("ambient") },
    };

    public static List<SceneConfig> DefaultScenes() => new()
    {
        new()
        {
            Name = "Gaming", Fullscreen = true, Page = "Gaming",
            ExceptApps = new() { "chrome", "msedge", "firefox", "brave", "opera", "vlc", "mpv", "mpc-hc64", "PotPlayerMini64", "explorer" },
        },
        new() { Name = "Away", IdleMinutes = 10, Page = "Ambient" },
        new() { Name = "Music", Apps = new() { "spotify" }, Page = "Media" },
    };

    // ---------------------------------------------------------------- persistence

    /// <summary>%APPDATA%\Shoji, or the "data" folder next to the exe in portable mode.</summary>
    public static string Folder => AppPaths.DataFolder;

    public static string FilePath => Path.Combine(Folder, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// True when config.json exists but couldn't be read (typo while hand-editing): the app runs on defaults,
    /// and nothing is saved over the user's file until it's fixed.
    /// </summary>
    [JsonIgnore]
    public bool IsFallback { get; private set; }

    /// <summary>
    /// True when config.json was locked by another program (backup, antivirus, an editor) for too long, so these are
    /// the settings it had when last read. The file itself is fine; this copy just isn't saved over it.
    /// </summary>
    [JsonIgnore]
    public bool IsStale { get; private set; }

    static readonly object LastReadGate = new();
    static string? _lastRead;

    public static AppConfig Load()
    {
        for (int attempt = 0; ; attempt++)
        {
            string text;
            try
            {
                if (!File.Exists(FilePath))
                    break;
                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 10)
                {
                    // Another program (or our own Save) is still writing the file.
                    System.Threading.Thread.Sleep(200);
                    continue;
                }
                // Still locked after ~2 s. Nothing is wrong with the file, so don't flag or copy it: carry on with the
                // settings from the last good read (defaults at start-up) and don't save, so nothing gets overwritten.
                Log.Write($"Config is busy (locked by another program?), keeping the last read settings: {ex.Message}");
                string? last;
                lock (LastReadGate)
                    last = _lastRead;
                AppConfig stale;
                try { stale = last != null ? Parse(last) : new AppConfig { LayoutVersion = CurrentLayoutVersion }; }
                catch { stale = new AppConfig { LayoutVersion = CurrentLayoutVersion }; }
                stale.IsStale = true;
                stale.Migrate();
                return stale;
            }

            try
            {
                var loaded = Parse(text);
                if (loaded.Migrate())
                {
                    loaded.Save(); // keep the upgrade, so it happens once
                    text = JsonSerializer.Serialize(loaded, JsonOptions);
                }
                lock (LastReadGate)
                    _lastRead = text;
                return loaded;
            }
            catch (Exception ex)
            {
                Log.Write($"Config could not be read, using defaults until it's fixed: {ex.Message}");
                try { File.Copy(FilePath, Path.Combine(Folder, "config.invalid.json"), overwrite: true); } catch { /* best effort */ }
                return new AppConfig { IsFallback = true };
            }
        }

        var config = new AppConfig { LayoutVersion = CurrentLayoutVersion };
        config.Save();
        return config;
    }

    /// <summary>
    /// Loads the current file, applies a change and saves it, which triggers a live reload.
    /// Returns false (and changes nothing) when config.json is broken, so a click can't wipe the user's settings.
    /// </summary>
    public static bool Edit(Action<AppConfig> change)
    {
        var config = Load();
        if (config.IsFallback || config.IsStale)
            return false;
        change(config);
        return config.Save();
    }

    static AppConfig Parse(string text) => JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();

    /// <summary>Brings an older file up to date. Returns true when something changed that should be saved.</summary>
    bool Migrate()
    {
        bool changed = false;
        // Version 1 always wrote these three colours; they were just the old defaults, so let the theme decide now.
        if (string.Equals(AccentColor, "#4FC3F7", StringComparison.OrdinalIgnoreCase)
            && string.Equals(BackgroundColor, "#0B0E14", StringComparison.OrdinalIgnoreCase)
            && string.Equals(TileColor, "#151A24", StringComparison.OrdinalIgnoreCase))
        {
            AccentColor = BackgroundColor = TileColor = null;
        }
        Pages ??= DefaultPages();
        if (LegacyModules != null)
        {
            // Keep the user's tiles as page 1 and add the new pages after it.
            if (Pages.Count == 0)
                Pages.Add(new PageConfig { Name = "Main" });
            Pages[0].Modules = LegacyModules;
            LegacyModules = null;
        }
        if (Pages.Count == 0)
            Pages.Add(new PageConfig { Name = "Main", Modules = ModuleCatalog.Preset("default") });
        Scenes ??= new List<SceneConfig>();
        DayNight ??= new DayNightConfig();
        Alerts ??= new AlertsConfig();
        Web ??= new WebConfig();
        Night ??= new NightConfig();
        Updates ??= new UpdatesConfig();
        Normalize();
        if (Web.Key is { Length: > 0 and < 16 })
        {
            // A short (hand-typed) key is too easy to guess from the network: replace it. The phone needs the new link.
            Web.Key = WebServer.NewKey();
            Log.Write("Phone dashboard key was too short and has been replaced; see Settings → Phone dashboard for the new link.");
            changed = true;
        }

        if (LayoutVersion < 2)
        {
            // 1.8: the main page gets the Recommended layout. Tiles it doesn't use stay on the page, switched off.
            ModuleCatalog.ApplyPreset(Pages[0], "main");
            LayoutVersion = 2;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A hand-edited file can hold nulls where the app expects text or lists ("Type": null, [null]); JSON loads them
    /// as is. Drop empty entries and fill the defaults, so one typo can't crash the dashboard.
    /// Also run on imported layouts (LayoutShare), which can hold the same mistakes.
    /// </summary>
    internal void Normalize()
    {
        Monitor ??= "auto";
        Theme ??= "midnight";
        TemperatureUnit ??= "C";
        SettingsLook ??= "auto";
        Language ??= "auto";
        Pages.RemoveAll(p => p == null);
        if (Pages.Count == 0)
            Pages.Add(new PageConfig { Name = "Main", Modules = ModuleCatalog.Preset("default") });
        foreach (var p in Pages)
        {
            p.Name ??= "Page";
            p.Modules ??= new List<ModuleConfig>();
            p.Modules.RemoveAll(m => m == null || string.IsNullOrWhiteSpace(m.Type));
            foreach (var m in p.Modules)
            {
                m.Zones?.RemoveAll(z => z == null || string.IsNullOrWhiteSpace(z.Zone));
                m.Sites?.RemoveAll(x => x == null);
                m.Sites?.ForEach(x => x.Url ??= "");
                m.Sensors?.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Id));
                m.Buttons?.RemoveAll(b => b == null);
                m.Buttons?.ForEach(b =>
                {
                    b.Icon ??= "";
                    b.Label ??= "";
                    b.Action ??= "launch";
                });
            }
        }
        Scenes.RemoveAll(x => x == null);
        Scenes.ForEach(x => x.Name ??= "Scene");
        DayNight.DayTheme ??= "sakura";
        DayNight.NightTheme ??= "sumi";
        DayNight.DayStarts ??= "07:00";
        DayNight.NightStarts ??= "19:30";
        Web.Port = Math.Clamp(Web.Port, 1024, 65535);
        Night.From ??= "23:00";
        Night.To ??= "07:00";
        Night.Brightness = Math.Clamp(Night.Brightness, 0, 100);
        Night.WakeSeconds = Math.Clamp(Night.WakeSeconds, 5, 600);
        Obs ??= new ObsConfig();
        Obs.Host = string.IsNullOrWhiteSpace(Obs.Host) ? "localhost" : Obs.Host.Trim();
        Obs.Port = Obs.Port is > 0 and < 65536 ? Obs.Port : 4455;
        Calendars ??= new List<CalendarLink>();
        Calendars.RemoveAll(x => x == null);
        Calendars.ForEach(x => x.Url ??= "");
    }

    public bool Save()
    {
        if (IsFallback)
        {
            Log.Write("Not saving: config.json couldn't be read, fix it first (a copy is in config.invalid.json).");
            return false;
        }
        if (IsStale)
        {
            Log.Write("Not saving: config.json was locked by another program when it was last read.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            // Write a temporary file and swap it in, so a crash or power cut mid-save can't leave half a file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Config could not be saved: {ex.Message}");
            return false;
        }
    }
}

public sealed class ModuleConfig
{
    /// <summary>One of the types in <see cref="ModuleCatalog"/>.</summary>
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Optional custom tile title.</summary>
    public string? Title { get; set; }

    /// <summary>How many columns the tile spans (use a large number like 9 for full width).</summary>
    public int Span { get; set; } = 1;

    /// <summary>
    /// For the "sensors" module: list of LibreHardwareMonitor sensor identifiers to show,
    /// e.g. "/lpc/nct6798d/fan/1". Use tray menu → "Export sensor list" to find them.
    /// </summary>
    public List<SensorRef>? Sensors { get; set; }

    /// <summary>For "weather": a city name ("Tokyo", "Berlin, DE") or "lat,lon". Empty = Windows location.</summary>
    public string? City { get; set; }

    /// <summary>For "ping": host name or IP to ping (default 1.1.1.1).</summary>
    public string? Host { get; set; }

    /// <summary>For "worldclock": time zones to show.</summary>
    public List<ZoneRef>? Zones { get; set; }

    /// <summary>For "websites": the sites to check.</summary>
    public List<SiteRef>? Sites { get; set; }

    /// <summary>For "deck": the buttons, in order.</summary>
    public List<DeckButton>? Buttons { get; set; }

    /// <summary>Other per-tile options (see <see cref="ModuleCatalog"/> for which tile uses which).</summary>
    public Dictionary<string, string>? Options { get; set; }

    public string? Opt(string key) => Options != null && Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public int OptInt(string key, int fallback) => int.TryParse(Opt(key), out int v) ? v : fallback;

    public bool OptBool(string key) => Opt(key) is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>A comma-separated option as a list.</summary>
    public List<string> OptList(string key) =>
        (Opt(key) ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public void SetOpt(string key, string? value)
    {
        Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            Options.Remove(key);
        else
            Options[key] = value.Trim();
        if (Options.Count == 0)
            Options = null;
    }
}

public sealed class PageConfig
{
    public string Name { get; set; } = "Page";

    /// <summary>Tile columns for this page; 0 = use the global setting.</summary>
    public int Columns { get; set; } = 0;

    public List<ModuleConfig> Modules { get; set; } = new();
}

/// <summary>
/// "When these conditions hold, show this page (and optionally this theme)". All conditions that are set must
/// match; the first matching scene in the list wins. When none matches, the dashboard goes back to where you left it.
/// </summary>
public sealed class SceneConfig
{
    public string Name { get; set; } = "Scene";
    public bool Enabled { get; set; } = true;

    /// <summary>Process names ("spotify"), wildcards ("*launcher*") or path parts ("\\steamapps\\") of the app in focus.</summary>
    public List<string>? Apps { get; set; }

    /// <summary>Matches when the app in focus covers its whole monitor (games, full-screen video).</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Apps that never trigger this scene, e.g. browsers for a full-screen "Gaming" scene.</summary>
    public List<string>? ExceptApps { get; set; }

    /// <summary>Matches after this many minutes without keyboard / mouse input.</summary>
    public int? IdleMinutes { get; set; }

    /// <summary>Time window "HH:mm" (may wrap past midnight, e.g. 22:00–06:00).</summary>
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>Page name (or number) to show.</summary>
    public string? Page { get; set; }

    /// <summary>Theme to use while the scene is active (null = keep).</summary>
    public string? Theme { get; set; }
}

/// <summary>One button on a control deck tile.</summary>
public sealed class DeckButton
{
    /// <summary>An icon name ("mic", "play", "lock" … see DeckActions.Icons) or any text / emoji.</summary>
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>launch, url, command, powershell, keys, media, mic, mute, volume, output, powerplan, screenshot, lock, sleep, page, theme, scenes, timer.</summary>
    public string Action { get; set; } = "launch";

    /// <summary>What the action works on: a program, URL, key combo ("Ctrl+Shift+M"), command, page name…</summary>
    public string? Target { get; set; }

    /// <summary>Arguments for "launch".</summary>
    public string? Args { get; set; }
}

/// <summary>Automatic theme by time of day.</summary>
public sealed class DayNightConfig
{
    public bool Enabled { get; set; }
    public string DayTheme { get; set; } = "sakura";
    public string NightTheme { get; set; } = "sumi";
    public string DayStarts { get; set; } = "07:00";
    public string NightStarts { get; set; } = "19:30";
}

/// <summary>Thresholds that flash a banner on the dashboard. 0 switches a check off.</summary>
public sealed class AlertsConfig
{
    public bool Enabled { get; set; } = true;
    public double CpuTemp { get; set; } = 90;
    public double GpuTemp { get; set; } = 85;
    public double RamPercent { get; set; } = 95;
    public double DiskFreePercent { get; set; } = 5;
    public double PingMs { get; set; } = 200;
    /// <summary>Alert when a site on a Websites tile goes down.</summary>
    public bool SiteDown { get; set; } = true;
    /// <summary>Warn this many days before a site's SSL certificate (or domain, ×2) expires. 0 = off.</summary>
    public double CertDays { get; set; } = 14;
    public bool Sound { get; set; } = true;
}

/// <summary>Read-only dashboard served on the local network, for a phone or tablet.</summary>
public sealed class WebConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8787;
    /// <summary>Secret that has to be in the URL; generated when the server is first switched on.</summary>
    public string? Key { get; set; }
    /// <summary>The phone page also shows the control-deck buttons and can press them (not "Run a command" / scripts).</summary>
    public bool AllowDeck { get; set; }
}

/// <summary>
/// Night mode: between From and To the dashboard is dimmed (Brightness 0 = black, as good as off, and kind to OLED
/// panels). A tap brings it back to full brightness for WakeSeconds.
/// </summary>
public sealed class NightConfig
{
    public bool Enabled { get; set; }
    public string From { get; set; } = "23:00";
    public string To { get; set; } = "07:00";
    /// <summary>How bright the dashboard stays at night, in percent.</summary>
    public int Brightness { get; set; } = 20;
    public int WakeSeconds { get; set; } = 30;
}

/// <summary>OBS Studio's WebSocket server (OBS → Tools → WebSocket Server Settings), for the deck's "obs" buttons.</summary>
public sealed class ObsConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;
    /// <summary>The server password from OBS ("Show Connect Info"); empty when authentication is off.</summary>
    public string? Password { get; set; }
}

public sealed class SensorRef
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
}

public sealed class SiteRef
{
    public string Url { get; set; } = "";
    /// <summary>Display name (default: the host name).</summary>
    public string? Name { get; set; }
    /// <summary>The page must contain this text (case-insensitive); catches broken pages that still answer "200 OK".</summary>
    public string? Contains { get; set; }
}

/// <summary>A calendar's iCal (.ics) address, e.g. Google's "Secret address in iCal format". webcal:// works too.</summary>
public sealed class CalendarLink
{
    public string Url { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>#RRGGBB for its events on the tiles; null = a theme colour by its place in the list.</summary>
    public string? Color { get; set; }
}

public sealed class ZoneRef
{
    /// <summary>Windows ("Tokyo Standard Time") or IANA ("Asia/Tokyo") time zone id.</summary>
    public string Zone { get; set; } = "UTC";
    public string? Label { get; set; }
}
