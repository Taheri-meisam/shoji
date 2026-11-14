using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>One page of a setup: its name and tiles, written like a layout preset ("fps:2", "clock:9!").</summary>
public sealed record SetupPage(string Name, string[] Tiles);

/// <summary>A theme together with pages made for it.</summary>
public sealed record SetupDef(string Id, string Name, string Theme, string Description, SetupPage[] Pages);

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
    /// <summary>The pages as the setup left them: while they're untouched, trying another setup keeps this backup.</summary>
    public string? Applied { get; set; }
}

/// <summary>
/// Setups: ready-made dashboards, each a theme plus the pages that suit it (Settings → Appearance). Applying one
/// replaces the pages and the theme; tiles keep their own settings where the same kind of tile was already there
/// (city, deck buttons, tokens), and the layout from before is saved so it can be brought back.
/// </summary>
public static class Setups
{
    public static readonly IReadOnlyList<SetupDef> All = new SetupDef[]
    {
        new("cockpit", "Cockpit", "hypercar", "For gaming: an FPS speedometer, LED arcs and temperature tubes, and a pit page that shows what the PC is doing", new SetupPage[]
        {
            new("Drive", new[] { "fps:2", "cpu", "gpu", "temps", "power", "status", "ping" }),
            new("Pit", new[] { "taskmanager:2", "incidents:2", "network", "disks", "memory", "recap" }),
        }),
        new("mission", "Mission control", "mission", "Segment digits and scopes: the whole PC on one page, tasks, agents and the flight recorder on the next", new SetupPage[]
        {
            new("Flight", new[] { "clock:2", "cpu", "gpu", "memory", "power", "network", "status" }),
            new("Ops", new[] { "taskmanager:2", "tasks:2", "incidents:2", "temps", "ping" }),
        }),
        new("zen", "Zen desk", "sumi", "Ink on paper: the time, weather and calendar, a photo frame page and a quiet focus page", new SetupPage[]
        {
            new("Desk", new[] { "clock:2", "weather", "calendar", "forecast:2", "agenda", "worldclock" }),
            new("Frame", new[] { "photos:9", "clock?HideWeather", "weather", "media" }),
            new("Focus", new[] { "timer", "cpu", "memory", "media" }),
        }),
        new("developer", "Developer", "tokyonight", "Editor colours: tasks, agents, CI and AI usage up front, the machine on a second page", new SetupPage[]
        {
            new("Code", new[] { "tasks:2", "incidents:2", "github", "aiusage", "cpu", "memory" }),
            new("System", new[] { "taskmanager:2", "gpu", "network", "disks", "temps" }),
        }),
        new("living", "Living room", "aurora", "Northern lights: time, weather, photos and what's coming up, with a music page for the song and its lyrics", new SetupPage[]
        {
            new("Home", new[] { "clock:2", "weather", "photos", "forecast:2", "agenda", "calendar" }),
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
    /// Replaces the pages and the theme with the setup's. Throws when the layout from before can't be saved (then
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
                Theme = config.Theme, AccentColor = config.AccentColor, BackgroundColor = config.BackgroundColor, TileColor = config.TileColor,
                DayNight = config.DayNight.Enabled,
            };
        AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, Json));

        // Tiles of a kind that was already somewhere keep what was set on them, except their style: the setup is
        // about the theme's own gauges.
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
        config.Theme = setup.Theme;
        config.AccentColor = config.BackgroundColor = config.TileColor = null;
        config.DayNight.Enabled = false; // or the day / night themes would hide the setup's theme
        config.Normalize();

        backup.Applied = JsonSerializer.Serialize(config.Pages);
        AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, Json));
    }

    /// <summary>Brings back the pages, theme and colours from before the first setup. False when there was nothing saved.</summary>
    public static bool Restore(AppConfig config)
    {
        var backup = LoadBackup();
        if (backup == null || backup.Pages.Count == 0)
            return false;
        config.Pages = backup.Pages;
        if (backup.Theme != null)
            config.Theme = backup.Theme;
        config.AccentColor = backup.AccentColor;
        config.BackgroundColor = backup.BackgroundColor;
        config.TileColor = backup.TileColor;
        config.DayNight.Enabled = backup.DayNight;
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
