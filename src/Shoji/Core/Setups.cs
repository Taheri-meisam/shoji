using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>One page of a setup: its name and tiles, written like a layout preset ("fps:2", "clock:9!").</summary>
public sealed record SetupPage(string Name, string[] Tiles);

/// <summary>Four pages that go together for one kind of use. The theme stays whatever is chosen.</summary>
public sealed record SetupDef(string Id, string Name, string Description, SetupPage[] Pages);

/// <summary>The layout someone had before they applied a setup, kept in the data folder so it can be brought back.</summary>
public sealed class SetupBackup
{
    public DateTime Saved { get; set; }
    public List<PageConfig> Pages { get; set; } = new();
    public string? Theme { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? TileColor { get; set; }
    public bool DayNight { get; set; }
    /// <summary>True for backups from 1.20 / 1.21, whose setups also changed the theme: only those give the theme back.</summary>
    public bool Themed { get; set; } = true;
    /// <summary>The pages as the setup left them: while they're untouched, trying another setup keeps this backup.</summary>
    public string? Applied { get; set; }
}

/// <summary>
/// Setups: ready-made dashboards of four pages each, for one kind of use (Settings → Appearance). Applying one
/// replaces the pages and leaves the theme alone, so any theme can be put on any setup; tiles keep their own settings
/// where the same kind of tile was already there (city, deck buttons, tokens), and the layout from before is saved
/// so it can be brought back.
/// </summary>
public static class Setups
{
    public static readonly IReadOnlyList<SetupDef> All = new SetupDef[]
    {
        new("everyday", "Everyday", "A bit of everything: the PC at a glance, your day, a control deck and the music", new SetupPage[]
        {
            new("Main", new[] { "clock:2", "cpu", "gpu", "memory", "power", "network", "status" }),
            new("Day", new[] { "clock:2", "weather", "calendar", "forecast:2", "agenda", "worldclock" }),
            new("Control", new[] { "deck:9", "mixer", "timer", "notifications" }),
            new("Music", new[] { "media:9!", "lyrics:9" }),
        }),
        new("gaming", "Gaming", "Frame rate and heat while you play, what happened afterwards, and a deck for the things you reach for", new SetupPage[]
        {
            new("Play", new[] { "fps:2", "cpu", "gpu", "temps", "power", "status", "ping" }),
            new("Session", new[] { "recap", "incidents:2", "network", "memory", "disks", "history:2" }),
            new("Control", new[] { "deck:9", "mixer", "media", "notifications" }),
            new("System", new[] { "taskmanager:2", "processes", "fans", "drivehealth", "system" }),
        }),
        new("developer", "Developer", "Tasks, agents and CI up front, the machine behind them, a focus page and a deck", new SetupPage[]
        {
            new("Code", new[] { "tasks:2", "incidents:2", "github", "aiusage", "cpu", "memory" }),
            new("System", new[] { "taskmanager:2", "gpu", "network", "disks", "temps" }),
            new("Focus", new[] { "timer", "agenda", "calendar", "media" }),
            new("Control", new[] { "deck:9", "mixer", "notifications", "websites" }),
        }),
        new("stream", "Streamer", "Going live: deck, chat and mixer in reach, the stream's health, your alerts and the music", new SetupPage[]
        {
            new("Live", new[] { "deck:2", "twitch", "mixer", "media", "status" }),
            new("Health", new[] { "fps", "cpu", "gpu", "network", "temps", "incidents" }),
            new("Chat", new[] { "twitch:2", "notifications", "clock" }),
            new("Music", new[] { "media:9!", "lyrics:9" }),
        }),
        new("office", "Home office", "The working day: agenda, tasks and a focus timer, then the PC, a deck and the wider world", new SetupPage[]
        {
            new("Today", new[] { "clock:2", "weather", "timer", "agenda", "calendar", "tasks:2" }),
            new("PC", new[] { "cpu", "memory", "network", "battery", "processes:2", "disks" }),
            new("Control", new[] { "deck:9", "mixer", "notifications", "media" }),
            new("World", new[] { "worldclock", "weather", "forecast:2", "ticker:9" }),
        }),
        new("home", "Family home", "For a screen in the house: the day ahead, lights and electricity price, a photo frame and the music", new SetupPage[]
        {
            new("Home", new[] { "clock:2", "weather", "photos", "forecast:2", "agenda", "calendar" }),
            new("House", new[] { "homeassistant:2", "clock", "weather", "powerprice:2", "forecast:2" }),
            new("Frame", new[] { "photos:9", "clock?HideWeather", "weather", "media" }),
            new("Music", new[] { "media:9!", "lyrics:9" }),
        }),
        new("server", "Server & monitoring", "For a machine that works for a living: sites and drives, who is using what, the hardware and the network", new SetupPage[]
        {
            new("Watch", new[] { "websites:2", "ping", "network", "status", "drivehealth", "disks" }),
            new("Load", new[] { "taskmanager:2", "cpu", "memory", "temps", "history:2" }),
            new("Hardware", new[] { "cpu", "gpu", "memory", "power", "temps", "fans", "disks", "system" }),
            new("Events", new[] { "incidents:2", "tasks:2", "notifications:2", "processes", "clock" }),
        }),
        new("bedside", "Bedside & ambient", "Quiet pages for a screen you glance at: a big clock, a photo frame, the weather and the music", new SetupPage[]
        {
            new("Clock", new[] { "clock:9!", "weather", "agenda" }),
            new("Frame", new[] { "photos:9", "clock?HideWeather", "weather", "media" }),
            new("Weather", new[] { "weather", "forecast:2", "calendar", "worldclock:2" }),
            new("Music", new[] { "media:9!", "lyrics:9" }),
        }),
    };

    static string BackupPath => Path.Combine(AppConfig.Folder, "before-setup.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Options a setup decides itself; everything else about a tile that was already there is kept.</summary>
    static readonly string[] Decided = { "Large", "HideWeather" };

    /// <summary>When the layout from before a setup was saved, or null when there is none to bring back.</summary>
    public static DateTime? BackupSaved => LoadBackup()?.Saved;

    static SetupBackup? LoadBackup()
    {
        try
        {
            return File.Exists(BackupPath) ? JsonSerializer.Deserialize<SetupBackup>(File.ReadAllText(BackupPath)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the pages with the setup's (the theme stays). Throws when the layout from before can't be saved (then
    /// nothing is changed).
    /// </summary>
    public static void Apply(AppConfig config, SetupDef setup)
    {
        // Trying one setup after another keeps the copy of your own layout, not of the setup in between.
        var backup = LoadBackup();
        if (backup?.Applied == null || backup.Applied != JsonSerializer.Serialize(config.Pages))
            backup = new SetupBackup
            {
                Saved = DateTime.Now,
                Pages = JsonSerializer.Deserialize<List<PageConfig>>(JsonSerializer.Serialize(config.Pages))!,
                Themed = false,
            };
        AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, Json));

        // Tiles of a kind that was already somewhere keep what was set on them, except their style: with a setup the
        // gauges follow whichever theme is picked next.
        var had = config.Pages.SelectMany(p => p.Modules).ToList();
        var pages = new List<PageConfig>();
        foreach (var page in setup.Pages)
        {
            var modules = new List<ModuleConfig>();
            foreach (var fresh in ModuleCatalog.Tiles(page.Tiles))
            {
                var m = had.FirstOrDefault(x => x.Type.Equals(fresh.Type, StringComparison.OrdinalIgnoreCase));
                if (m == null)
                {
                    modules.Add(fresh);
                    continue;
                }
                had.Remove(m);
                m.Enabled = true;
                m.Span = fresh.Span;
                m.SetOpt("Style", null);
                foreach (var key in Decided)
                    m.SetOpt(key, fresh.OptBool(key) ? "true" : null);
                modules.Add(m);
            }
            pages.Add(new PageConfig { Name = L.T(page.Name), Modules = modules });
        }
        config.Pages = pages;
        config.Normalize();

        backup.Applied = JsonSerializer.Serialize(config.Pages);
        AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, Json));
    }

    /// <summary>
    /// Brings back the pages from before the first setup. The theme is left as it is now (a setup doesn't change it;
    /// backups written by 1.20 / 1.21, whose setups did, still give their theme and colours back).
    /// </summary>
    public static bool Restore(AppConfig config)
    {
        var backup = LoadBackup();
        if (backup == null || backup.Pages.Count == 0)
            return false;
        config.Pages = backup.Pages;
        if (backup.Themed)
        {
            if (backup.Theme != null)
                config.Theme = backup.Theme;
            config.AccentColor = backup.AccentColor;
            config.BackgroundColor = backup.BackgroundColor;
            config.TileColor = backup.TileColor;
            config.DayNight.Enabled = backup.DayNight;
        }
        config.Normalize();
        try
        {
            File.Delete(BackupPath);
        }
        catch (Exception ex)
        {
            Log.Write($"The saved layout couldn't be removed: {ex.Message}");
        }
        return true;
    }
}
