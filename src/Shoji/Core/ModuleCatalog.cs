using System;
using System.Collections.Generic;
using System.Linq;

namespace Shoji.Core;

public enum OptionKind { Text, Secret, Bool }

/// <summary>A per-tile setting shown in Settings. Most live in <see cref="ModuleConfig.Options"/>; a few are typed fields.</summary>
public sealed record OptionSpec(string Key, string Label, string Hint, OptionKind Kind = OptionKind.Text)
{
    public string? Get(ModuleConfig m) => Key switch
    {
        "City" => m.City,
        "Host" => m.Host,
        "Zones" => m.Zones == null ? null : string.Join("; ", m.Zones.Select(z => z.Label == null ? z.Zone : $"{z.Zone}={z.Label}")),
        _ => m.Opt(Key),
    };

    public void Set(ModuleConfig m, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (Key)
        {
            case "City": m.City = value; break;
            case "Host": m.Host = value; break;
            case "Zones":
                // "Tokyo Standard Time=Tokyo; America/New_York=New York"
                m.Zones = value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(z => z.Split('=', 2, StringSplitOptions.TrimEntries))
                    .Select(p => new ZoneRef { Zone = p[0], Label = p.Length > 1 ? p[1] : null })
                    .ToList();
                break;
            default: m.SetOpt(Key, value); break;
        }
    }
}

/// <summary>A tile type the dashboard knows how to build.</summary>
public sealed record ModuleInfo(string Type, string Name, string Kanji, string Description, int DefaultSpan = 1, OptionSpec[]? Options = null)
{
    public OptionSpec[] OptionList => Options ?? Array.Empty<OptionSpec>();

    /// <summary>Gauge styles this tile can be drawn in (first = the plain default). Chosen per tile ("Style" option) or by the theme.</summary>
    public (string Id, string Name)[] Styles { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>Every module type, with the names, descriptions and options shown in Settings, plus layout presets.</summary>
public static class ModuleCatalog
{
    static OptionSpec Large(string what) => new("Large", "Large", $"Fill the tile with a big {what}", OptionKind.Bool);

    public static readonly IReadOnlyList<ModuleInfo> All = new ModuleInfo[]
    {
        new("clock", "Clock", "今日", "Time, date, weather and PC uptime", 2, new[]
        {
            Large("clock"),
            new OptionSpec("HideWeather", "Weather", "Hide the weather next to the time", OptionKind.Bool),
            new OptionSpec("HideNextEvent", "Next event", "Hide the next calendar event (\"Next: Standup in 12 min\"; calendars are set in Settings → Weather & time)", OptionKind.Bool),
            new OptionSpec("City", "Own city", "only to differ from Settings → Weather & time; empty = the shared location"),
        }) { Styles = new (string, string)[] { ("classic", "Classic"), ("segments", "LED digits") } },
        new("cpu", "CPU", "処理装置", "Load, temperature, clock, power, cores, per-thread bars") { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("led", "LED arc"), ("speedometer", "Speedometer (clock)"), ("segments", "LED digits (temp)"), ("enso", "Ensō"), ("hex", "Hex threads") } },
        new("gpu", "GPU", "画像処理", "Load, temperatures, VRAM, clock, fan, power") { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("led", "LED arc"), ("speedometer", "Speedometer"), ("segments", "LED digits (temp)"), ("enso", "Ensō") } },
        new("power", "Power", "電力", "Whole-PC power draw (measured CPU / GPU + estimate, or a smart PSU), energy today and its cost", 1, new[]
        {
            new OptionSpec("Price", "Price per kWh", "e.g. 0.30 (shows what today's use cost)"),
            new OptionSpec("Currency", "Currency", "symbol, default from Windows region"),
            new OptionSpec("Other", "Rest of PC (W)", "estimate for motherboard, RAM, drives, fans, RGB; default 45"),
            new OptionSpec("Efficiency", "PSU efficiency %", "default 90 (80 PLUS Gold ≈ 90)"),
            new OptionSpec("Max", "Gauge maximum (W)", "for arc / speedometer, default 600"),
        }) { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("speedometer", "Speedometer"), ("segments", "LED digits") } },
#if SHOJI_LINUX
        new("fps", "FPS", "フレーム", "Frame rate, 1% low and frame times of the game, through MangoHud (Settings → Hardware)")
#else
        new("fps", "FPS", "フレーム", "Frame rate, 1% low and frame times of the app in focus (needs admin)")
#endif
            { Styles = new (string, string)[] { ("classic", "Classic"), ("speedometer", "Speedometer"), ("tape", "Flight tape"), ("segments", "LED digits"), ("arc", "Arc gauge") } },
        new("memory", "Memory", "記憶", "RAM in use and committed memory") { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("led", "LED arc"), ("rings", "Rings"), ("enso", "Ensō") } },
        new("network", "Network", "通信", "Download / upload speed, IP, session totals") { Styles = new (string, string)[] { ("classic", "Classic"), ("scope", "Oscilloscope") } },
        new("ping", "Ping", "応答", "Latency, jitter and packet loss to a host",
            Options: new[] { new OptionSpec("Host", "Host", "default 1.1.1.1") }) { Styles = new (string, string)[] { ("classic", "Classic"), ("scope", "Oscilloscope"), ("segments", "LED digits") } },
        new("disks", "Drives", "記憶装置", "Free space per drive, read / write speed"),
        new("processes", "Top processes", "プロセス", "Busiest apps by CPU, with RAM") { Styles = new (string, string)[] { ("classic", "List"), ("radar", "Radar") } },
        new("temps", "Temperatures", "温度", "Every temperature sensor, found automatically") { Styles = new (string, string)[] { ("classic", "List"), ("tapes", "Flight tapes"), ("tubes", "Coolant tubes"), ("segments", "LED digits (hottest)") } },
        new("fans", "Fans", "冷却", "Every fan and pump, found automatically"),
        new("history", "History", "履歴", "Graphs of the last hours (temperatures, load, FPS…)", 2, new[]
        {
            new OptionSpec("Series", "Graphs", "comma list: cpu.temp, gpu.temp, cpu.load, gpu.load, mem.load, net.down, net.up, ping, fps"),
            new OptionSpec("Hours", "Hours", "1–24, default 24"),
        }),
        new("status", "Status board", "警報", "Caution and warning lamps lit by your alert limits (CPU hot, ping high, mic live…)", 1),
        new("overview", "System orbit", "軌道", "Whole PC at a glance: CPU, GPU, RAM and network as orbiting planets or rings", 1)
            { Styles = new (string, string)[] { ("orbit", "Orbit"), ("rings", "Rings") } },
        new("recap", "Last session", "記録", "Summary of your last game: time played, FPS, peak temperatures"),
        new("websites", "Websites", "サイト", "Your sites: up / down, response time, SSL certificate and domain expiry, page content check", 1, new[]
        {
            new OptionSpec("Interval", "Check every (s)", "default 60; a failed check is retried after 15 s"),
        }),
        new("system", "System", "システム", "Windows version, motherboard, CPU / GPU / RAM specs"),
        new("deck", "Control deck", "操作盤", "Touch buttons: apps, websites, hotkeys, mic mute, audio output, scripts…", 9, new[]
        {
            new OptionSpec("Columns", "Columns", "buttons per row, default automatic"),
        }),
        new("mixer", "Volume mixer", "音量", "Master volume, microphone and per-app volume sliders"),
        new("media", "Now playing", "再生中", "Current song / video with touch controls", 1, new[] { Large("cover and title") }),
        new("lyrics", "Lyrics", "歌詞", "Synced lyrics for the song that's playing (lrclib.net)", 2),
        new("timer", "Focus timer", "集中", "Pomodoro timer with touch start / pause", 1, new[]
        {
            new OptionSpec("Focus", "Focus minutes", "default 25"),
            new OptionSpec("Break", "Break minutes", "default 5"),
        }) { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("enso", "Ensō") } },
        new("weather", "Weather", "天気", "Current weather and 3-day forecast (Open-Meteo, no account)", 1, new[]
        {
            new OptionSpec("City", "Own city", "only to differ from Settings → Weather & time; e.g. Tokyo, Berlin DE, or 35.68,139.76"),
        }),
        new("forecast", "Hourly forecast", "予報", "Temperature curve, sky and rain chance for the next 6–24 hours", 2, new[]
        {
            new OptionSpec("City", "Own city", "only to differ from Settings → Weather & time; empty = the shared location"),
            new OptionSpec("Hours", "Hours", "6–24, default 12"),
        }),
        new("calendar", "Calendar", "暦", "This month at a glance, with a dot under days that have events (calendars from Settings → Weather & time)"),
        new("agenda", "Agenda", "予定", "Upcoming events from Google / Outlook / iCloud calendars (iCal link)", 1, new[]
        {
            new OptionSpec("Calendars", "iCal links", "only to differ from Settings → Weather & time; secret .ics address(es), comma separated", OptionKind.Secret),
            new OptionSpec("Days", "Days ahead", "default 3"),
        }),
        new("worldclock", "World clock", "世界時計", "Time in other cities", 1, new[]
        {
            new OptionSpec("Zones", "Zones", "Tokyo Standard Time=Tokyo; America/New_York=New York"),
        }),
        new("battery", "Battery", "電池", "Charge, power source and time left (laptops)") { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("led", "LED arc") } },
        new("activity", "Activity", "活動", "App in focus and today's per-app usage"),
        new("photos", "Photo frame", "写真", "Your photos from a folder, one after another with a soft crossfade; tap for the next", 2, new[]
        {
            new OptionSpec("Folder", "Folder", "default your Pictures folder"),
            new OptionSpec("Every", "Every (s)", "seconds per photo, default 20, at least 5"),
            new OptionSpec("Fit", "Fit", "fill (crop to the tile, default) or fit (whole photo)"),
            new OptionSpec("Shuffle", "Shuffle", "Shuffle the photos", OptionKind.Bool),
            new OptionSpec("Caption", "Caption", "none (default), name, or date (when it was taken)"),
            new OptionSpec("Subfolders", "Subfolders", "Include subfolders", OptionKind.Bool),
        }),
#if SHOJI_LINUX
        // On Linux the desktop's screen-sharing dialog picks the window or screen (tap the tile); only these apply.
        new("mirror", "Mirror", "鏡", "Live copy of a window or screen you pick (Discord, OBS, a minimap…)", 2, new[]
        {
            new OptionSpec("Crop", "Crop", "x,y,width,height inside the window or screen (optional)"),
            new OptionSpec("Fps", "Frames / second", "default 5, max 10"),
        }),
#else
        new("mirror", "Mirror", "鏡", "Live copy of your main screen, a window or a screen area (Discord, OBS, a minimap…)", 2, new[]
        {
            new OptionSpec("Screen", "Screen", "main, or a monitor number (1, 2…) to mirror a whole screen"),
            new OptionSpec("Window", "Or a window", "process name or part of the title, e.g. Discord"),
            new OptionSpec("Region", "Or screen area", "x,y,width,height on the main screen, e.g. 0,0,800,600"),
            new OptionSpec("Crop", "Crop window", "x,y,width,height inside the window (optional)"),
            new OptionSpec("Fps", "Frames / second", "default 5, max 30"),
        }),
#endif
        new("homeassistant", "Home Assistant", "家", "Lights, switches, sensors and cameras; tap to toggle", 1, new[]
        {
            new OptionSpec("Url", "Server", "e.g. http://homeassistant.local:8123"),
            new OptionSpec("Token", "Token", "long-lived access token (your profile → Security)", OptionKind.Secret),
            new OptionSpec("Entities", "Entities", "light.desk=Desk lamp, sensor.living_temperature=Living room, camera.door"),
        }),
        new("github", "GitHub", "開発", "Latest CI run and open pull requests per repository", 1, new[]
        {
            new OptionSpec("Repos", "Repositories", "owner/repo, owner/other"),
            new OptionSpec("Token", "Token", "optional; needed for private repos", OptionKind.Secret),
        }),
        new("twitch", "Twitch chat", "配信", "Live chat of a Twitch channel (read-only, no login)", 1, new[]
        {
            new OptionSpec("Channel", "Channel", "channel name"),
        }),
        new("notifications", "Notifications", "通知",
            "Discord, WhatsApp, mail… notifications from this PC, newest first; tap one to dismiss it. Kept in memory only, never sent to the phone page", 1, new[]
        {
            new OptionSpec("Only", "Only these apps", "comma list, e.g. Discord, WhatsApp; empty = all"),
            new OptionSpec("Hide", "Hide these apps", "comma list, e.g. Steam, OneDrive"),
            new OptionSpec("HideText", "Hide message text", "privacy: show only the app and title", OptionKind.Bool),
            new OptionSpec("Keep", "Keep for (minutes)", "default 60"),
        }),
        new("ticker", "Markets", "相場", "Stock and crypto prices with today's change", 1, new[]
        {
            new OptionSpec("Stocks", "Stocks", "AAPL, MSFT, ^GSPC (Yahoo Finance symbols)"),
            new OptionSpec("Coins", "Crypto", "bitcoin, ethereum (CoinGecko ids)"),
            new OptionSpec("Currency", "Currency", "for crypto, default usd"),
        }),
        new("webvalue", "Web value", "数値", "Any number or text from a web address or JSON API (a price, a counter, a sensor), with a sparkline", 1, new[]
        {
            new OptionSpec("Url", "Address", "https://… that answers with JSON or a plain value"),
            new OptionSpec("Field", "Field", "where the value is in the JSON, e.g. current.temperature_2m, data.items[0].price; empty for a plain answer"),
            new OptionSpec("Label", "Label", "shown under the value, e.g. Outside"),
            new OptionSpec("Unit", "Unit", "e.g. °C, $, users"),
            new OptionSpec("Decimals", "Decimals", "digits after the point; default automatic"),
            new OptionSpec("Every", "Check every (s)", "default 60, at least 10"),
            new OptionSpec("Min", "Gauge minimum", "for the arc gauge, default 0"),
            new OptionSpec("Max", "Gauge maximum", "for the arc gauge, default 100"),
            new OptionSpec("Header", "Request header", "optional, e.g. Authorization: Bearer abc123", OptionKind.Secret),
        }) { Styles = new (string, string)[] { ("classic", "Classic"), ("arc", "Arc gauge"), ("segments", "LED digits") } },
        new("sensors", "Sensors", "センサー", "Hand-picked LibreHardwareMonitor sensors (advanced, IDs in config.json)"),
    };

    public static ModuleInfo? Find(string type) =>
        All.FirstOrDefault(m => m.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    public static readonly IReadOnlyList<(string Id, string Name, string Description)> Presets = new[]
    {
        ("main", "Recommended", "Clock + weather, CPU, GPU, memory, power, network, status lamps, media"),
        ("default", "Classic", "The original nine tiles"),
        ("essentials", "Essentials", "Clock, CPU, GPU, memory, network, media"),
        ("gaming", "Gaming", "FPS, temperatures, status lamps, power, ping, last session"),
        ("deck", "Control", "Control deck, volume mixer, focus timer"),
        ("media", "Media", "Big now playing with synced lyrics"),
        ("ambient", "Ambient", "Big clock, weather, hourly forecast, system orbit, calendar, world clock"),
        ("desk", "Desk", "Weather, calendar, world clock and focus time"),
        ("everything", "Everything", "Every built-in tile that needs no setup"),
    };

    /// <summary>The module list for a preset. "type:2" spans two columns, "type:9" the full width, "type!" turns on Large.</summary>
    public static List<ModuleConfig> Preset(string id)
    {
        string[] types = id switch
        {
            "main" => new[] { "clock:2", "cpu", "gpu", "memory", "power", "network", "status", "media:2" },
            "essentials" => new[] { "clock:2", "cpu", "gpu", "memory", "network", "media:2" },
            "gaming" => new[] { "fps", "cpu", "gpu", "temps", "status", "power", "ping", "processes", "recap" },
            "deck" => new[] { "deck:9", "mixer", "timer" },
            "media" => new[] { "media:9!", "lyrics:9" },
            "ambient" => new[] { "clock:9!", "weather", "forecast:2", "overview", "calendar", "worldclock" },
            "desk" => new[] { "clock", "weather", "forecast:2", "calendar", "worldclock", "cpu", "memory", "timer", "activity", "media:2" },
            "everything" => new[]
            {
                "clock", "weather", "forecast:2", "cpu", "gpu", "fps", "memory", "network", "temps", "fans", "ping", "disks",
                "processes", "status", "power", "overview", "history:2", "system", "calendar", "worldclock", "activity", "media", "timer", "mixer", "battery",
            },
            _ => new[] { "clock:2", "cpu", "gpu", "memory", "network", "disks", "processes", "activity", "media" },
        };
        return types.Select(t =>
        {
            bool large = t.EndsWith('!');
            var parts = t.TrimEnd('!').Split(':');
            var m = new ModuleConfig { Type = parts[0], Span = parts.Length > 1 ? int.Parse(parts[1]) : 1 };
            if (large)
                m.SetOpt("Large", "true");
            if (m.Type == "deck")
                m.Buttons = DeckDefaults.Buttons();
            return m;
        }).ToList();
    }

    /// <summary>
    /// Replaces a page's layout with a preset but keeps each tile's own settings (city, buttons, sensors…).
    /// Tiles the preset doesn't use stay on the page, switched off, so nothing configured is lost.
    /// </summary>
    public static void ApplyPreset(PageConfig page, string id)
    {
        var old = page.Modules;
        var result = new List<ModuleConfig>();
        foreach (var p in Preset(id))
        {
            var existing = old.FirstOrDefault(m => m.Type.Equals(p.Type, StringComparison.OrdinalIgnoreCase) && !result.Contains(m));
            var m = existing ?? p;
            m.Enabled = true;
            m.Span = p.Span;
            if (p.OptBool("Large"))
                m.SetOpt("Large", "true");
            result.Add(m);
        }
        foreach (var m in old.Where(m => !result.Contains(m)))
        {
            m.Enabled = false;
            result.Add(m);
        }
        page.Modules = result;
    }

    public static ModuleConfig New(string type)
    {
        var info = Find(type);
        var m = new ModuleConfig { Type = type, Span = info?.DefaultSpan ?? 1 };
        if (type == "deck")
            m.Buttons = DeckDefaults.Buttons();
        if (type == "photos")
        {
            // On by default; a switch that is off simply has no value, so a new tile starts with them set.
            m.SetOpt("Shuffle", "true");
            m.SetOpt("Subfolders", "true");
        }
        return m;
    }
}

/// <summary>The buttons a new control deck starts with.</summary>
public static class DeckDefaults
{
    public static List<DeckButton> Buttons() => new()
    {
        new() { Icon = "mic", Label = "Mic", Action = "mic" },
        new() { Icon = "mute", Label = "Sound", Action = "mute" },
        new() { Icon = "headphones", Label = "Output", Action = "output" },
        new() { Icon = "prev", Label = "Previous", Action = "media", Target = "previous" },
        new() { Icon = "play", Label = "Play", Action = "media", Target = "playpause" },
        new() { Icon = "next", Label = "Next", Action = "media", Target = "next" },
        new() { Icon = "voldown", Label = "Vol −", Action = "volume", Target = "-5" },
        new() { Icon = "volume", Label = "Vol +", Action = "volume", Target = "+5" },
        new() { Icon = "camera", Label = "Screenshot", Action = "screenshot" },
#if SHOJI_LINUX
        new() { Icon = "chart", Label = "System Monitor", Action = "launch", Target = "gnome-system-monitor" },
        new() { Icon = "folder", Label = "Downloads", Action = "url", Target = "~/Downloads" },
#else
        new() { Icon = "chart", Label = "Task Manager", Action = "launch", Target = "taskmgr.exe" },
        new() { Icon = "folder", Label = "Downloads", Action = "url", Target = "shell:Downloads" },
#endif
        new() { Icon = "theme", Label = "Theme", Action = "theme", Target = "next" },
        new() { Icon = "pages", Label = "Next page", Action = "page", Target = "next" },
        new() { Icon = "scenes", Label = "Auto scenes", Action = "scenes" },
        new() { Icon = "lock", Label = "Lock PC", Action = "lock" },
    };
}
